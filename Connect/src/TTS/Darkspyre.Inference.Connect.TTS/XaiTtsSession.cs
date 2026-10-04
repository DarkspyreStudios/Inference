using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.TTS;

internal sealed class XaiTtsSession : ITtsStreamingSession
{
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly SemaphoreSlim _send = new(1);
    private readonly Channel<(TtsStreamEvent Event, long Bytes)> _events;
    private readonly InferenceStreamLimits _limits;
    private readonly Lock _sync = new();
    private readonly List<Exception> _cleanup = [];
    private TaskCompletionSource _space = NewSignal();
    private WebSocket? _socket;
    private IInferenceResourceAccess? _resources;
    private readonly InferenceResourceSelectionVersion? _scope;
    private SpeechResourceWriter.Writer? _artifact;
    private Task _receiver = Task.CompletedTask;
    private Exception? _terminalResourceFailure;
    private long _buffered;
    private int _reader;
    private int _utterance;

    private XaiTtsSession(WebSocket socket, InferenceStreamLimits limits, IInferenceResourceAccess? resources, InferenceResourceSelectionVersion? scope)
    {
        _socket = socket; _limits = limits; _resources = resources; _scope = scope;
        _events = Channel.CreateBounded<(TtsStreamEvent, long)>(new BoundedChannelOptions(limits.MaximumBufferedEvents)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    }
    internal static async Task<XaiTtsSession> OpenAsync(Func<Uri, HttpRequestMessage, Action, Action<Exception>, CancellationToken, Task<WebSocket>> connect,
        LlmConnection snapshot, TtsConnection connection, TtsStreamOptions options, InferenceStreamLimits limits,
        IInferenceResourceAccess? resources, Action openingFence, Action<Exception> recordOpeningCleanup, CancellationToken token)
    {
        if (limits.MaximumBufferedEvents <= 0 || limits.MaximumBufferedPayloadBytes <= 0 || limits.MaximumMessageBytes <= 0) throw XaiTtsProtocol.Invalid();
        openingFence();
        InferenceResourceSelectionVersion? scope = resources is null ? null : await SpeechResourceWriter.ReadScopeAsync(resources, openingFence, token).ConfigureAwait(false);
        openingFence();
        using var headers = new HttpRequestMessage(); XaiRequestHeaders.Apply(headers, snapshot, false); headers.Headers.Accept.Clear();
        foreach (var header in connection.Headers) if (!headers.Headers.TryAddWithoutValidation(header.Key, header.Value)) throw XaiTtsProtocol.Invalid();
        WebSocket? socket = null; XaiTtsSession? session = null;
        try
        {
            socket = await connect(XaiTtsProtocol.StreamUri(connection.Endpoint, options), headers, openingFence, recordOpeningCleanup, token).ConfigureAwait(false);
            openingFence(); if (socket is null) throw XaiTtsProtocol.Protocol();
            session = new(socket, limits, resources, scope);
            if (options.Xai?.Replace is { } replacements) await session.UpdateReplacementsAsync(replacements, token).ConfigureAwait(false);
            openingFence();
            session._receiver = session.ReceiveAsync();
            return session;
        }
        catch (Exception original)
        {
            try { if (session is not null) await session.DisposeAsync().ConfigureAwait(false); else socket?.Dispose(); }
            catch (Exception error)
            {
                Exception cleanup = LlmResourcePreparation.AsCleanupFailure(error, refusedOwner: (object?)session ?? socket);
                recordOpeningCleanup(cleanup);
                throw new AggregateException(original, cleanup);
            }
            throw;
        }
    }
    public Task AppendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return SendAsync("text.delta", writer => writer.WriteString("delta", text), state => state is 0 or 1, 1, cancellationToken);
    }
    public Task CompleteUtteranceAsync(CancellationToken cancellationToken = default) => SendAsync("text.done", null, state => state is 0 or 1, 2, cancellationToken);
    public Task ClearUtteranceAsync(CancellationToken cancellationToken = default) => SendAsync("text.clear", null, state => state != 3, 3, cancellationToken);
    public Task UpdateReplacementsAsync(IReadOnlyDictionary<string, string> replacements, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        return SendAsync("session.update", writer => XaiTtsProtocol.WriteReplace(writer, TtsData.Copy(replacements)!), _ => true, null, cancellationToken);
    }
    private async Task SendAsync(string type, Action<Utf8JsonWriter>? fields, Func<int, bool> allowed, int? next, CancellationToken caller)
    {
        using var operation = _lifetime.Enter(caller); CancellationToken token = operation.Token;
        byte[] bytes = XaiTtsProtocol.Json(writer => { writer.WriteString("type", type); fields?.Invoke(writer); });
        if (bytes.LongLength > _limits.MaximumMessageBytes) { _ = RetireAsync(); throw Capacity(); }
        await _send.WaitAsync(token).ConfigureAwait(false);
        bool dispatched = false;
        try
        {
            Fence(token); lock (_sync) { if (!allowed(_utterance)) throw new InvalidOperationException("The speech utterance is awaiting its actual terminal observation."); if (next is { } state) _utterance = state; }
            dispatched = true; await _socket!.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token).ConfigureAwait(false); Fence(token);
        }
        catch
        {
            // A refused in-flight send has unknown remote effects. Retire framing; never replay.
            if (dispatched) _ = RetireAsync();
            throw;
        }
        finally { _send.Release(); }
    }
    public IAsyncEnumerable<TtsStreamEvent> Events => ReadAsync();
    private async IAsyncEnumerable<TtsStreamEvent> ReadAsync([EnumeratorCancellation] CancellationToken token = default)
    {
        using (_lifetime.Enter(token))
        {
            if (Interlocked.Exchange(ref _reader, 1) != 0) throw new InvalidOperationException("A speech session has one Events consumer.");
        }
        await foreach (var item in _events.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            Release(item.Bytes);
            if (!IsStopping()) yield return item.Event;
        }
    }
    private async Task ReceiveAsync()
    {
        using var operation = _lifetime.Enter(default); CancellationToken token = operation.Token;
        Exception? failure = null;
        try
        {
            var buffer = new byte[8192];
            while (true)
            {
                using var assembled = new MemoryStream(); ValueWebSocketReceiveResult frame;
                do
                {
                    frame = await _socket!.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false); Fence(token);
                    if (frame.MessageType == WebSocketMessageType.Close) throw XaiTtsProtocol.Protocol();
                    if (frame.MessageType != WebSocketMessageType.Text) throw XaiTtsProtocol.Protocol();
                    if (assembled.Length + frame.Count > _limits.MaximumMessageBytes || assembled.Length + frame.Count > int.MaxValue) throw Capacity();
                    assembled.Write(buffer, 0, frame.Count);
                } while (!frame.EndOfMessage);
                using JsonDocument document = JsonDocument.Parse(assembled.GetBuffer().AsMemory(0, checked((int)assembled.Length)));
                JsonElement root = document.RootElement; string type = XaiTtsProtocol.RequiredString(root, "type");
                switch (type)
                {
                    case "audio.delta":
                        byte[] audio = XaiTtsProtocol.Audio(root, "delta");
                        var chunk = new TtsAudioChunk(audio, Duration: XaiTtsProtocol.Seconds(root, "audio_duration"), Timestamps: XaiTtsProtocol.Timings(root));
                        lock (_sync) { if (_utterance == 0) throw XaiTtsProtocol.Protocol(); }
                        if (_resources is not null)
                        {
                            _artifact ??= await SpeechResourceWriter.OpenAsync(_resources, _scope!, null, null, () => Fence(token), RecordCleanup, token).ConfigureAwait(false);
                            await _artifact.WriteAsync(audio, token).ConfigureAwait(false); Fence(token);
                        }
                        await PublishAsync(chunk, token).ConfigureAwait(false); break;
                    case "audio.done":
                        var completed = new TtsUtteranceCompleted(XaiTtsProtocol.OptionalString(root, "trace_id"));
                        lock (_sync) { if (_utterance == 0) throw XaiTtsProtocol.Protocol(); }
                        var artifact = _artifact;
                        try
                        {
                            if (artifact is not null)
                            {
                                completed = completed with { Resource = await artifact.CommitAsync(token).ConfigureAwait(false) };
                                await DisposeArtifactAsync().ConfigureAwait(false); artifact.Fence(token);
                            }
                            Fence(token); lock (_sync) _utterance = 0;
                            await PublishAsync(completed, token).ConfigureAwait(false);
                        }
                        catch (Exception error)
                        {
                            ExceptionDispatchInfo.Capture(LlmResourcePreparation.WithGeneratedResources(error,
                                artifact?.ObservedCommit is { } receipt ? [receipt] : [])).Throw();
                            throw;
                        }
                        break;
                    case "audio.clear":
                        lock (_sync) { if (_utterance != 3) throw XaiTtsProtocol.Protocol(); }
                        await DisposeArtifactAsync().ConfigureAwait(false); Fence(token);
                        lock (_sync) _utterance = 0;
                        await PublishAsync(new TtsUtteranceCleared(), token).ConfigureAwait(false); break;
                    case "session.updated":
                        if (!root.TryGetProperty("replace", out var map) || map.ValueKind != JsonValueKind.Object) throw XaiTtsProtocol.Protocol();
                        var replace = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var item in map.EnumerateObject()) { if (item.Value.ValueKind != JsonValueKind.String) throw XaiTtsProtocol.Protocol(); replace.Add(item.Name, item.Value.GetString()!); }
                        await PublishAsync(new TtsReplacementsUpdated(replace), token).ConfigureAwait(false); break;
                    case "error":
                        _ = XaiTtsProtocol.RequiredString(root, "message");
                        await PublishAsync(new TtsStreamError(new(InferenceFailureCode.ExecutionFailed, null, "xAI speech synthesis reported a failure.")), token).ConfigureAwait(false);
                        // Published replacement errors can leave the session open. The provider owns subsequent lifecycle.
                        break;
                    default: throw XaiTtsProtocol.Protocol();
                }
            }
        }
        catch (OperationCanceledException error) when (IsStopping() && !LlmResourcePreparation.IsResourceFailure(error)) { }
        catch (ChannelClosedException) when (IsStopping()) { }
        catch (Exception error) { failure = error is JsonException or ArgumentException or InvalidOperationException ? XaiTtsProtocol.Protocol() : error; }
        try { await DisposeArtifactAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { failure = failure is null ? cleanup : new AggregateException(failure, cleanup); }
        if (failure is not null && HasTerminalResourceFailure(failure)) _terminalResourceFailure = failure;
        _events.Writer.TryComplete(failure);
        if (failure is not null) _ = RetireAsync();
    }
    private async Task DisposeArtifactAsync()
    {
        var artifact = _artifact; _artifact = null;
        if (artifact is not null) await artifact.DisposeAsync().ConfigureAwait(false);
    }
    private async Task PublishAsync(TtsStreamEvent observation, CancellationToken token)
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
    private void Release(long size)
    {
        TaskCompletionSource signal;
        lock (_sync) { _buffered -= size; signal = _space; _space = NewSignal(); }
        signal.TrySetResult();
    }
    private static long Size(TtsStreamEvent value) => value switch
    {
        TtsAudioChunk audio => audio.Audio.Length + (audio.Timestamps?.Sum(item => (long)Encoding.UTF8.GetByteCount(item.Text) + 16) ?? 0),
        TtsReplacementsUpdated map => map.Replace.Sum(pair => (long)Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value)),
        TtsUtteranceCompleted completed => Bytes(completed.ProviderTraceId) + (completed.Resource is { } resource ? Bytes(resource.Reference.WorkspaceId) + Bytes(resource.Reference.Id) + Bytes(resource.SessionId) + Bytes(resource.ContentType) + Bytes(resource.FileName) : 0),
        TtsStreamError error => Bytes(error.Error.Message),
        _ => 0
    };
    private static int Bytes(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static InferenceException Capacity() => new(InferenceFailureCode.CapacityExhausted, "The configured speech session buffer capacity was exceeded.");
    private void Fence(CancellationToken token) { token.ThrowIfCancellationRequested(); if (IsStopping()) throw new OperationCanceledException(token); }
    private bool IsStopping() { try { _lifetime.ThrowIfStopping(); return false; } catch (ObjectDisposedException) { return true; } }
    private void RecordCleanup(Exception error) { lock (_cleanup) _cleanup.Add(error); }
    public ValueTask DisposeAsync() => new(RetireAsync());
    private Task RetireAsync()
    {
        _events.Writer.TryComplete();
        return _lifetime.RetireAsync(async () =>
        {
            await _receiver.ConfigureAwait(false);
            _resources = null;
            WebSocket? socket = _socket; _socket = null;
            try { socket?.Dispose(); } catch (Exception error) { RecordCleanup(LlmResourcePreparation.AsCleanupFailure(error, refusedOwner: socket)); }
            _send.Dispose();
            var failures = new List<Exception>();
            if (_terminalResourceFailure is { } terminal) failures.Add(terminal);
            foreach (Exception cleanup in _cleanup)
                if (!failures.Any(error => Contains(error, cleanup))) failures.Add(cleanup);
            if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException(failures);
        });
    }
    internal static bool Contains(Exception original, Exception candidate) => ReferenceEquals(original, candidate)
        || original is AggregateException aggregate && aggregate.InnerExceptions.Any(error => Contains(error, candidate));
    internal static bool HasTerminalResourceFailure(Exception error) => error switch
    {
        InferenceResourceOperationException resource => resource.Failure.Phase != InferenceResourceOperationPhase.Cleanup,
        InferenceResourceOperationCanceledException resource => resource.Failure.Phase != InferenceResourceOperationPhase.Cleanup,
        AggregateException aggregate => aggregate.InnerExceptions.Any(HasTerminalResourceFailure),
        _ => false
    };
}
