using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.STT;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.STT;

internal sealed class XaiSttSession : ISpeechTranscriptionSession
{
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly SemaphoreSlim _send = new(1);
    private readonly Channel<(SpeechTranscriptEvent Event, long Bytes)> _events;
    private readonly InferenceStreamLimits _limits;
    private readonly Lock _sync = new();
    private readonly List<Exception> _cleanup = [];
    private readonly HashSet<int> _done = [];
    private readonly TaskCompletionSource _ready = Signal();
    private TaskCompletionSource _space = Signal();
    private WebSocket? _socket;
    private Task _receiver = Task.CompletedTask;
    private readonly int _channels;
    private readonly bool _multichannel;
    private long _buffered;
    private int _reader;
    private bool _inputComplete;
    private bool _created;
    private bool _reportedError;

    private XaiSttSession(WebSocket socket, SpeechStreamOptions options, InferenceStreamLimits limits)
    {
        _socket = socket; _limits = limits; _multichannel = options.Multichannel == true; _channels = _multichannel ? options.Channels!.Value : 1;
        _events = Channel.CreateBounded<(SpeechTranscriptEvent, long)>(new BoundedChannelOptions(limits.MaximumBufferedEvents)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    }
    internal static async Task<XaiSttSession> OpenAsync(Func<Uri, HttpRequestMessage, Action, Action<Exception>, CancellationToken, Task<WebSocket>> connect,
        LlmConnection snapshot, SttConnection connection, SpeechStreamOptions options, InferenceStreamLimits limits,
        Action openingFence, Action<Exception> recordCleanup, CancellationToken token)
    {
        WebSocket? socket = null; XaiSttSession? session = null;
        using var headers = new HttpRequestMessage(); XaiRequestHeaders.Apply(headers, snapshot, false); headers.Headers.Accept.Clear();
        foreach (var header in connection.AdditionalHeaders) if (!headers.Headers.TryAddWithoutValidation(header.Key, header.Value)) throw XaiSttProtocol.Invalid();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(connection.RequestTimeout);
        try
        {
            openingFence();
            socket = await connect(XaiSttProtocol.StreamUri(connection.Endpoint, options), headers, openingFence, recordCleanup, timeout.Token).ConfigureAwait(false);
            openingFence(); ArgumentNullException.ThrowIfNull(socket);
            session = new(socket, options, limits); session._receiver = session.ReceiveAsync();
            await session._ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false); openingFence();
            return session;
        }
        catch (Exception original)
        {
            try { if (session is not null) await session.DisposeAsync().ConfigureAwait(false); else socket?.Dispose(); }
            catch (Exception error)
            { var cleanup = LlmResourcePreparation.AsCleanupFailure(error, refusedOwner: (object?)session ?? socket); recordCleanup(cleanup); throw new AggregateException(original, cleanup); }
            throw;
        }
    }
    public Task AppendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken = default)
        => SendAsync(audio.ToArray(), WebSocketMessageType.Binary, false, null, cancellationToken);
    public Task FinalizeAsync(int? channel = null, CancellationToken cancellationToken = default)
    {
        if (channel is { } index && (!_multichannel || index < 0 || index >= _channels)) throw XaiSttProtocol.Invalid();
        return SendAsync(XaiSttProtocol.Command("finalize", channel), WebSocketMessageType.Text, false, channel, cancellationToken);
    }
    public Task CompleteAudioAsync(CancellationToken cancellationToken = default)
        => SendAsync(XaiSttProtocol.Command("audio.done"), WebSocketMessageType.Text, true, null, cancellationToken);
    private async Task SendAsync(byte[] bytes, WebSocketMessageType type, bool complete, int? channel, CancellationToken caller)
    {
        using var operation = _lifetime.Enter(caller); CancellationToken token = operation.Token;
        if (bytes.LongLength > _limits.MaximumMessageBytes) throw Capacity();
        await _send.WaitAsync(token).ConfigureAwait(false); bool dispatched = false;
        try
        {
            Fence(token);
            lock (_sync)
            {
                if (_inputComplete) throw new InvalidOperationException("The transcription audio input is complete.");
                if (complete) _inputComplete = true;
            }
            dispatched = true;
            await _socket!.SendAsync(bytes.AsMemory(), type, true, token).ConfigureAwait(false); Fence(token);
        }
        catch { if (dispatched) _ = RetireAsync(); throw; }
        finally { _send.Release(); }
    }
    public IAsyncEnumerable<SpeechTranscriptEvent> Events => ReadAsync();
    private async IAsyncEnumerable<SpeechTranscriptEvent> ReadAsync([EnumeratorCancellation] CancellationToken token = default)
    {
        if (Interlocked.Exchange(ref _reader, 1) != 0) throw new InvalidOperationException("A transcription session has one Events consumer.");
        await foreach (var item in _events.Reader.ReadAllAsync(token).ConfigureAwait(false))
        { Release(item.Bytes); if (!IsStopping()) yield return item.Event; }
    }
    private async Task ReceiveAsync()
    {
        using var operation = _lifetime.Enter(default); CancellationToken token = operation.Token; Exception? failure = null;
        try
        {
            byte[] buffer = new byte[8192];
            while (true)
            {
                using var assembled = new MemoryStream(); ValueWebSocketReceiveResult frame;
                do
                {
                    frame = await _socket!.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false); Fence(token);
                    if (frame.MessageType == WebSocketMessageType.Close)
                    {
                        lock (_sync) if (!_reportedError && (!_inputComplete || _done.Count != _channels)) throw XaiSttProtocol.Protocol();
                        if (!_created) throw XaiSttProtocol.Protocol();
                        _events.Writer.TryComplete(); return;
                    }
                    if (frame.MessageType != WebSocketMessageType.Text) throw XaiSttProtocol.Protocol();
                    if (assembled.Length + frame.Count > _limits.MaximumMessageBytes || assembled.Length + frame.Count > int.MaxValue) throw Capacity();
                    assembled.Write(buffer, 0, frame.Count);
                } while (!frame.EndOfMessage);
                using JsonDocument document = JsonDocument.Parse(assembled.GetBuffer().AsMemory(0, checked((int)assembled.Length)));
                JsonElement root = document.RootElement; string type = XaiSttProtocol.RequiredString(root, "type");
                if (!_created && type is not ("transcript.created" or "error")) throw XaiSttProtocol.Protocol();
                switch (type)
                {
                    case "transcript.created":
                        if (_created) throw XaiSttProtocol.Protocol();
                        string id = XaiSttProtocol.RequiredString(root, "id"); if (id.Length == 0) throw XaiSttProtocol.Protocol();
                        _created = true; await PublishAsync(new SpeechTranscriptCreatedEvent(id), token).ConfigureAwait(false); _ready.TrySetResult(); break;
                    case "transcript.partial":
                        int? partialChannel = ReadChannel(root);
                        double? confidence = XaiSttProtocol.OptionalNumber(root, "end_of_turn_confidence"); if (confidence is > 1) throw XaiSttProtocol.Protocol();
                        await PublishAsync(new SpeechTranscriptPartialEvent(XaiSttProtocol.RequiredString(root, "text"),
                            XaiSttProtocol.Seconds(root, "start") ?? throw XaiSttProtocol.Protocol(),
                            XaiSttProtocol.Seconds(root, "duration") ?? throw XaiSttProtocol.Protocol(),
                            XaiSttProtocol.RequiredBoolean(root, "is_final"), XaiSttProtocol.RequiredBoolean(root, "speech_final"),
                            partialChannel, XaiSttProtocol.Words(root) ?? throw XaiSttProtocol.Protocol(), confidence), token).ConfigureAwait(false);
                        break;
                    case "transcript.done":
                        int? doneChannel = ReadChannel(root);
                        lock (_sync) { if (!_inputComplete || !_done.Add(doneChannel ?? 0)) throw XaiSttProtocol.Protocol(); }
                        await PublishAsync(new SpeechTranscriptDoneEvent(XaiSttProtocol.RequiredString(root, "text"),
                            XaiSttProtocol.Seconds(root, "duration") ?? throw XaiSttProtocol.Protocol(), doneChannel,
                            XaiSttProtocol.Words(root) ?? throw XaiSttProtocol.Protocol()), token).ConfigureAwait(false);
                        break;
                    case "error":
                        _ = XaiSttProtocol.RequiredString(root, "message"); _reportedError = true;
                        await PublishAsync(new SpeechTranscriptErrorEvent(new(InferenceFailureCode.ExecutionFailed, null, "xAI transcription reported a failure.")), token).ConfigureAwait(false);
                        if (!_created) throw new InferenceException(InferenceFailureCode.ExecutionFailed, "xAI transcription could not become ready.");
                        break;
                    default: throw XaiSttProtocol.Protocol();
                }
            }
        }
        catch (OperationCanceledException) when (IsStopping()) { }
        catch (ChannelClosedException) when (IsStopping()) { }
        catch (Exception error) { failure = error is JsonException or ArgumentException or InvalidOperationException ? XaiSttProtocol.Protocol() : error; }
        if (failure is not null) _ready.TrySetException(failure);
        _events.Writer.TryComplete(failure); if (failure is not null) _ = RetireAsync();
    }
    private int? ReadChannel(JsonElement root)
    {
        int? channel = XaiSttProtocol.OptionalInteger(root, "channel_index");
        if (_multichannel && channel is null || channel is { } index && index >= _channels) throw XaiSttProtocol.Protocol();
        return channel;
    }
    private async Task PublishAsync(SpeechTranscriptEvent observation, CancellationToken token)
    {
        long size = Size(observation); if (size > _limits.MaximumBufferedPayloadBytes) throw Capacity();
        while (true)
        {
            Task wait;
            lock (_sync) { if (_buffered <= _limits.MaximumBufferedPayloadBytes - size) { _buffered += size; break; } wait = _space.Task; }
            await wait.WaitAsync(token).ConfigureAwait(false); Fence(token);
        }
        try { await _events.Writer.WriteAsync((observation, size), token).ConfigureAwait(false); }
        catch { Release(size); throw; }
        Fence(token);
    }
    private void Release(long bytes)
    {
        TaskCompletionSource signal;
        lock (_sync) { _buffered -= bytes; signal = _space; _space = Signal(); }
        signal.TrySetResult();
    }
    private static long Size(SpeechTranscriptEvent observation) => observation switch
    {
        SpeechTranscriptCreatedEvent created => Bytes(created.SessionId) + 16,
        SpeechTranscriptPartialEvent partial => Bytes(partial.Text) + Words(partial.Words) + 64,
        SpeechTranscriptDoneEvent done => Bytes(done.Text) + Words(done.Words) + 32,
        SpeechTranscriptErrorEvent error => Bytes(error.Error.Message) + 16,
        _ => 0
    };
    private static long Words(IReadOnlyList<SpeechWord>? words) => words?.Sum(word => (long)Bytes(word.Text) + 32) ?? 0;
    private static int Bytes(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static InferenceException Capacity() => new(InferenceFailureCode.CapacityExhausted, "The configured transcription session capacity is exhausted.");
    private void Fence(CancellationToken token) { token.ThrowIfCancellationRequested(); if (IsStopping()) throw new OperationCanceledException(token); }
    private bool IsStopping() { try { _lifetime.ThrowIfStopping(); return false; } catch (ObjectDisposedException) { return true; } }
    public ValueTask DisposeAsync() => new(RetireAsync());
    private Task RetireAsync()
    {
        _events.Writer.TryComplete();
        return _lifetime.RetireAsync(async () =>
        {
            await _receiver.ConfigureAwait(false); WebSocket? socket = _socket; _socket = null;
            try { socket?.Dispose(); } catch (Exception error) { _cleanup.Add(LlmResourcePreparation.AsCleanupFailure(error, refusedOwner: socket)); }
            _send.Dispose();
            if (_cleanup.Count == 1) ExceptionDispatchInfo.Capture(_cleanup[0]).Throw();
            if (_cleanup.Count > 1) throw new AggregateException(_cleanup);
        });
    }
}
