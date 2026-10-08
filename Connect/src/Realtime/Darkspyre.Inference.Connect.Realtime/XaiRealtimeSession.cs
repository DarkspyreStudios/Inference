using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Realtime;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.Realtime;

internal sealed class XaiRealtimeSession : IRealtimeVoiceSession
{
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly LlmCleanupOutcomes _cleanup = new();
    private readonly SemaphoreSlim _send = new(1);
    private readonly Lock _gate = new();
    private readonly Channel<(RealtimeEvent Event, long Bytes)> _events;
    private readonly InferenceStreamLimits _limits;
    private readonly InferenceResourceSelectionVersion? _scope;
    private readonly Dictionary<OutputKey, Output> _outputs = [];
    private readonly Dictionary<string, RealtimeAudioConfig?> _responses = new(StringComparer.Ordinal);
    private readonly List<RealtimeAudioArtifact> _committed = [];
    private WebSocket? _socket;
    private IInferenceResourceAccess? _resources;
    private Func<string, string?, RealtimeConversationReference>? _issue;
    private Action<RealtimeConversationReference, string>? _observeReceipt;
    private Action<RealtimeConversationReference>? _invalidateReceipt;
    private readonly string? _expectedConversation;
    private RealtimeConversationReference? _resumptionReceipt;
    private readonly string? _expectedModel;
    private string[]? _secrets;
    private RealtimeAudioConfig? _input;
    private RealtimeAudioConfig? _observedOutput;
    private RealtimeTurnDetection? _turn;
    private string? _observedModel;
    private string? _routingModel;
    private bool _routingChanged;
    private readonly HashSet<string> _attemptedModels = new(StringComparer.Ordinal);
    private long _attemptedModelBytes;
    private RealtimeConversationReference? _conversation;
    private Task _receiver = Task.CompletedTask;
    private Exception? _terminalFailure;
    private readonly TaskCompletionSource _completion = Signal();
    private bool _completionStarted;
    private bool _explicitRetirement;
    private RealtimeEventIdentity? _pendingIdentity;
    private string? _pendingStatus;
    private long _terminalMetadata;
    private TaskCompletionSource _space = Signal();
    private long _buffered;
    private long _pendingMetadata;
    private long _committedBytes;
    private int _reader;

    internal XaiRealtimeSession(WebSocket socket, InferenceStreamLimits limits, IInferenceResourceAccess? resources,
        InferenceResourceSelectionVersion? scope, RealtimeSessionConfig options,
        Func<string, string?, RealtimeConversationReference> issue, string[] secrets,
        Action<RealtimeConversationReference, string> observeReceipt, Action<RealtimeConversationReference> invalidateReceipt, string? expectedModel)
    {
        _socket = socket; _limits = limits; _resources = resources; _scope = scope; _issue = issue; _secrets = secrets;
        _input = options.InputAudio; _turn = options.TurnDetection;
        _observeReceipt = observeReceipt;
        _invalidateReceipt = invalidateReceipt;
        _routingModel = options.ModelId;
        _expectedConversation = options.Resumption?.Conversation?.ConversationId;
        _resumptionReceipt = options.Resumption?.Conversation;
        _expectedModel = expectedModel;
        _events = Channel.CreateBounded<(RealtimeEvent, long)>(new BoundedChannelOptions(limits.MaximumBufferedEvents)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    }
    internal async Task InitializeAsync(byte[] update, CancellationToken token)
    {
        try { await _socket!.SendAsync(update.AsMemory(), WebSocketMessageType.Text, true, token).ConfigureAwait(false); }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException) { throw XaiRealtimeProvider.TransportFailure(error); }
        token.ThrowIfCancellationRequested();
        _receiver = ReceiveAsync();
    }
    public Task UpdateSessionAsync(RealtimeSessionConfig config, CancellationToken cancellationToken = default) => CommandAsync(() =>
    {
        var snapshot = XaiRealtimeProtocol.Config(config);
        return async token =>
        {
            lock (_gate) { if (snapshot.Resumption?.Conversation is { } supplied && !ReferenceEquals(supplied, _conversation) && !ReferenceEquals(supplied, _resumptionReceipt)) throw XaiRealtimeProtocol.Unsupported(); }
            var protectedValues = new List<string>();
            byte[] bytes = await XaiRealtimeProtocol.UpdateAsync(snapshot, () => Fence(token), token, protectedValues.Add).ConfigureAwait(false);
            lock (_gate) { _secrets = _secrets!.Concat(protectedValues).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray(); }
            if (snapshot.ModelId is { } model) CheckModelCapacity(model);
            return new([new(bytes)], () => { lock (_gate) { if (snapshot.InputAudio is not null) _input = snapshot.InputAudio; if (snapshot.TurnDetection is not null) _turn = snapshot.TurnDetection; } },
                snapshot.ModelId is { } selected ? () => AttemptModel(selected) : null);
        };
    }, cancellationToken);
    public Task AppendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken = default) => CommandAsync(() =>
    {
        byte[] bytes = audio.ToArray();
        return token =>
        {
            Fence(token);
            RealtimeAudioConfig? input; lock (_gate) { input = _input; }
            return Task.FromResult(new Command([input?.Transport == RealtimeAudioTransport.Binary ? new(bytes, WebSocketMessageType.Binary)
            : new(XaiRealtimeProtocol.Message("input_audio_buffer.append", writer => writer.WriteBase64String("audio", bytes)))]));
        };
    }, cancellationToken);
    public Task CommitAudioAsync(CancellationToken cancellationToken = default) => CommandAsync(() => _ =>
    {
        lock (_gate) { if (_turn is not RealtimeManualTurnDetection) throw XaiRealtimeProtocol.Unsupported(); }
        return Task.FromResult(new Command([new(XaiRealtimeProtocol.Message("input_audio_buffer.commit"))]));
    }, cancellationToken);
    public Task ClearAudioAsync(CancellationToken cancellationToken = default) => SimpleAsync("input_audio_buffer.clear", null, cancellationToken);
    public Task AddItemAsync(RealtimeConversationItem item, string? previousItemId = null, CancellationToken cancellationToken = default) => CommandAsync(() =>
    {
        var snapshot = XaiRealtimeProtocol.Item(item);
        return async token =>
        {
            Fence(token);
            string? output = snapshot is RealtimeFunctionOutputItem result
                ? await XaiRealtimeToolOutput.PrepareAsync(result.Output, _resources, _limits.MaximumMessageBytes, () => Fence(token), _cleanup.Record, token).ConfigureAwait(false) : null;
            return new([new(XaiRealtimeProtocol.ItemMessage(snapshot, previousItemId, output))]);
        };
    }, cancellationToken);
    public Task DeleteItemAsync(string itemId, CancellationToken cancellationToken = default) => SimpleAsync("conversation.item.delete", writer =>
    { ArgumentException.ThrowIfNullOrWhiteSpace(itemId); writer.WriteString("item_id", itemId); }, cancellationToken);
    public Task TruncateItemAsync(string itemId, int contentIndex, TimeSpan audioEnd, CancellationToken cancellationToken = default) => SimpleAsync("conversation.item.truncate", writer =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        if (contentIndex < 0 || audioEnd < TimeSpan.Zero || audioEnd.Ticks % TimeSpan.TicksPerMillisecond != 0) throw XaiRealtimeProtocol.Invalid();
        writer.WriteString("item_id", itemId); writer.WriteNumber("content_index", contentIndex); writer.WriteNumber("audio_end_ms", audioEnd.Ticks / TimeSpan.TicksPerMillisecond);
    }, cancellationToken);
    public Task CreateResponseAsync(RealtimeResponseOptions? options = null, CancellationToken cancellationToken = default) => CommandAsync(() =>
    {
        byte[] bytes = XaiRealtimeProtocol.Response(options);
        return _ => Task.FromResult(new Command([new(bytes)]));
    }, cancellationToken);
    public Task CancelResponseAsync(string? responseId = null, CancellationToken cancellationToken = default) => SimpleAsync("response.cancel", writer => XaiRealtimeProtocol.WriteString(writer, "response_id", responseId), cancellationToken);
    public Task SubmitToolResultAsync(RealtimeToolResult result, CancellationToken cancellationToken = default) => CommandAsync(() =>
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(result.CallId);
        LlmToolOutput snapshot = XaiRealtimeToolOutput.Snapshot(result.Output);
        string callId = result.CallId;
        return async token =>
        {
            string output = await XaiRealtimeToolOutput.PrepareAsync(snapshot, _resources, _limits.MaximumMessageBytes, () => Fence(token), _cleanup.Record, token).ConfigureAwait(false);
            return new([new(XaiRealtimeProtocol.ItemMessage(new RealtimeFunctionOutputItem(callId, snapshot), null, output)), new(XaiRealtimeProtocol.Response(null))]);
        };
    }, cancellationToken);
    private Task SimpleAsync(string type, Action<Utf8JsonWriter>? fields, CancellationToken token) => CommandAsync(() =>
    {
        byte[] bytes = XaiRealtimeProtocol.Message(type, fields);
        return _ => Task.FromResult(new Command([new(bytes)]));
    }, token);
    private sealed record Frame(byte[] Bytes, WebSocketMessageType Type = WebSocketMessageType.Text);
    private sealed record Command(Frame[] Frames, Action? Sent = null, Action? Dispatch = null);
    private async Task CommandAsync(Func<Func<CancellationToken, Task<Command>>> snapshot, CancellationToken caller)
    {
        using var operation = _lifetime.Enter(caller); CancellationToken token = operation.Token;
        Fence(token); var prepare = snapshot(); Fence(token);
        await _send.WaitAsync(token).ConfigureAwait(false);
        bool dispatched = false;
        try
        {
            Fence(token); Command command = await prepare(token).ConfigureAwait(false); Fence(token);
            if (command.Frames.Any(frame => frame.Bytes.LongLength > _limits.MaximumMessageBytes)) throw XaiRealtimeProtocol.Capacity();
            Fence(token); command.Dispatch?.Invoke();
            foreach (Frame frame in command.Frames)
            {
                Fence(token); dispatched = true;
                try { await _socket!.SendAsync(frame.Bytes.AsMemory(), frame.Type, true, token).ConfigureAwait(false); }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException) { throw XaiRealtimeProvider.TransportFailure(error); }
                Fence(token);
            }
            command.Sent?.Invoke(); Fence(token);
        }
        catch (Exception error)
        {
            if (dispatched)
            {
                lock (_gate) { _terminalFailure = Combine(_terminalFailure, error); }
                _ = RetireAsync(explicitRetirement: true);
            }
            throw;
        }
        finally { _send.Release(); }
    }

    public IAsyncEnumerable<RealtimeEvent> Events => ReadAsync();
    private async IAsyncEnumerable<RealtimeEvent> ReadAsync([EnumeratorCancellation] CancellationToken caller = default)
    {
        caller.ThrowIfCancellationRequested();
        lock (_gate) { ObjectDisposedException.ThrowIf(_explicitRetirement, this); }
        if (Interlocked.Exchange(ref _reader, 1) != 0) throw new InvalidOperationException("A realtime session has one Events consumer.");
        await foreach (var item in _events.Reader.ReadAllAsync(caller).ConfigureAwait(false))
        {
            Release(item.Bytes); caller.ThrowIfCancellationRequested();
            bool retired; lock (_gate) { retired = _explicitRetirement; }
            if (retired) break;
            yield return item.Event;
        }
        await _completion.Task.WaitAsync(caller).ConfigureAwait(false);
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
                using var message = new MemoryStream(); ValueWebSocketReceiveResult frame; WebSocketMessageType? type = null;
                do
                {
                    try { frame = await _socket!.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false); }
                    catch (Exception error) when (error is WebSocketException or OperationCanceledException) { throw XaiRealtimeProvider.TransportFailure(error); }
                    Fence(token);
                    if (frame.MessageType == WebSocketMessageType.Close) return;
                    if (type is not null && type != frame.MessageType) throw XaiRealtimeProtocol.Malformed(); type = frame.MessageType;
                    if (message.Length + frame.Count > _limits.MaximumMessageBytes || message.Length + frame.Count > int.MaxValue) throw XaiRealtimeProtocol.Capacity();
                    message.Write(buffer, 0, frame.Count);
                } while (!frame.EndOfMessage);
                if (type == WebSocketMessageType.Binary)
                {
                    var possible = _outputs.Values.Where(output => !output.AudioDone).ToArray();
                    if (possible.Length != 1) throw XaiRealtimeProtocol.Malformed();
                    var audio = new RealtimeAudioDeltaEvent(possible[0].Identity with { EventId = null }, message.ToArray());
                    await ObserveAsync(audio, message.Length, token).ConfigureAwait(false);
                    continue;
                }
                if (type != WebSocketMessageType.Text) throw XaiRealtimeProtocol.Malformed();
                RealtimeEvent? observation;
                using (JsonDocument document = Parse(message))
                {
                    JsonElement root = document.RootElement;
                    string[] secrets; lock (_gate) { secrets = _secrets!; }
                    if (XaiRealtimeReader.Required(root, "type") == "conversation.created")
                    {
                        XaiRealtimeReader.ValidateMetadata(root, secrets);
                        var conversation = XaiRealtimeReader.Object(root, "conversation"); string id = XaiRealtimeReader.Required(conversation, "id");
                        if (_expectedConversation is not null && _expectedConversation != id) throw XaiRealtimeProtocol.Malformed();
                        RealtimeConversationReference? receipt; string? model;
                        lock (_gate) { receipt = _conversation; model = _observedModel; }
                        receipt ??= _issue!(id, model);
                        if (receipt.ConversationId != id) throw XaiRealtimeProtocol.Malformed();
                        bool changed;
                        lock (_gate) { _conversation = receipt; changed = _routingChanged; }
                        if (changed) _invalidateReceipt!(receipt);
                        observation = new RealtimeConversationCreatedEvent(XaiRealtimeReader.Identity(root), receipt);
                    }
                    else
                    {
                        try { observation = XaiRealtimeReader.Parse(root, secrets); if (observation is not null) XaiRealtimeReader.ValidateMetadata(root, secrets); }
                        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException)
                        { throw XaiRealtimeProtocol.Malformed(); }
                    }
                }
                if (observation is not null) await ObserveAsync(observation, message.Length, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (Stopping() && _committed.Count == 0 && !LlmResourcePreparation.IsResourceFailure(error)) { }
        catch (ChannelClosedException) when (Stopping() && _committed.Count == 0) { }
        catch (Exception error) { failure = error; }
        finally
        {
            foreach (var output in _outputs.Values)
                if (output.Writer is { } writer)
                {
                    output.Writer = null;
                    try { await writer.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanup)
                    {
                        _cleanup.Record(cleanup);
                        if (failure is null) failure = cleanup;
                        else if (!Contains(failure, cleanup)) failure = new AggregateException(failure, cleanup);
                    }
                }
            _outputs.Clear();
            lock (_gate) { _terminalFailure = Combine(_terminalFailure, failure); }
            _events.Writer.TryComplete();
            _ = RetireAsync();
        }
    }
    private static JsonDocument Parse(MemoryStream message)
    {
        try { return JsonDocument.Parse(message.GetBuffer().AsMemory(0, checked((int)message.Length))); }
        catch (JsonException) { throw XaiRealtimeProtocol.Malformed(); }
    }
    private async Task ObserveAsync(RealtimeEvent observation, long size, CancellationToken token)
    {
        Fence(token);
        if (observation is RealtimeSessionEvent { Configuration: { } config })
        {
            if (config.ModelId is { } model)
            {
                if (SecretRedactor.ContainsProtectedValue(model, null, _secrets!)) throw XaiRealtimeProtocol.Malformed();
                bool requested; bool contradiction; bool routingChanged;
                RealtimeConversationReference? resumed; RealtimeConversationReference? conversation;
                lock (_gate)
                {
                    requested = _attemptedModels.Contains(model);
                    routingChanged = _routingChanged;
                    bool changed = _observedModel is not null && _observedModel != model;
                    contradiction = !requested && (changed || !routingChanged && _expectedModel is not null && _expectedModel != model);
                    resumed = _resumptionReceipt; conversation = _conversation;
                    if (!contradiction) _observedModel = model;
                }
                if (contradiction)
                {
                    if (resumed is not null) _invalidateReceipt!(resumed);
                    if (conversation is not null) _invalidateReceipt!(conversation);
                    throw XaiRealtimeProtocol.Malformed();
                }
                // An attempted selector classifies this observation, not an acknowledgment.
                // Routing-changing dispatch already invalidates the immutable opening proof.
                if (!routingChanged)
                {
                    if (resumed is not null) _observeReceipt!(resumed, model);
                    if (conversation is not null) _observeReceipt!(conversation, model);
                }
            }
            lock (_gate) { if (config.InputAudio is not null) _input = config.InputAudio; if (config.TurnDetection is not null) _turn = config.TurnDetection; }
            if (config.OutputAudio is not null) _observedOutput = config.OutputAudio;
        }
        if (observation is RealtimeResponseEvent { Stage: "response.created" } created)
        {
            string id = created.Identity.ResponseId ?? throw XaiRealtimeProtocol.Malformed();
            long metadata = StringSize(id);
            lock (_gate)
            {
                if (_responses.Count + _outputs.Count >= _limits.MaximumBufferedEvents || !FitsMetadata(metadata)
                    || !_responses.TryAdd(id, _observedOutput)) throw XaiRealtimeProtocol.Capacity();
                _pendingMetadata += metadata;
            }
        }
        if (observation is RealtimeContentPartEvent { Stage: "response.content_part.added", Part: RealtimeAudioContent } content)
            _ = RequireOutput(content.Identity);
        if (observation is RealtimeAudioDeltaEvent audio)
        {
            Output output = RequireOutput(audio.Identity);
            if (output.AudioDone) throw XaiRealtimeProtocol.Malformed();
            if (_resources is not null)
            {
                output.Writer ??= await SpeechResourceWriter.OpenAsync(_resources, _scope!, null, null, () => Fence(token), _cleanup.Record, token).ConfigureAwait(false);
                await output.Writer.WriteAsync(audio.Audio, token).ConfigureAwait(false); Fence(token);
            }
        }
        if (observation is RealtimeAudioDoneEvent audioDone) RequireOutput(audioDone.Identity).AudioDone = true;
        if (observation is RealtimeContentPartEvent { Stage: "response.content_part.done", Part: RealtimeAudioContent } finishedPart)
            RequireOutput(finishedPart.Identity).AudioDone = true;
        if (observation is RealtimeResponseEvent { Stage: "response.done" } completed)
        {
            string? responseId = completed.Identity.ResponseId;
            if (responseId is null) throw XaiRealtimeProtocol.Malformed();
            lock (_gate)
            {
                _terminalMetadata = StringSize(completed.Identity.EventId) + StringSize(responseId)
                    + StringSize(completed.Identity.ItemId) + StringSize(completed.ProviderStatus) + 32;
                if (!FitsMetadata(_terminalMetadata)) throw XaiRealtimeProtocol.Capacity();
                _pendingMetadata += _terminalMetadata;
                _pendingIdentity = completed.Identity; _pendingStatus = completed.ProviderStatus;
            }
            var artifacts = new List<RealtimeAudioArtifact>();
            foreach (var pair in _outputs.Where(pair => pair.Key.ResponseId == responseId).ToArray())
            {
                Output output = pair.Value;
                if (output.Writer is { } writer)
                {
                    try { _ = await writer.CommitAsync(token).ConfigureAwait(false); }
                    catch
                    {
                        if (writer.ObservedCommit is not null) RetainArtifact(output, writer);
                        throw;
                    }
                    artifacts.Add(RetainArtifact(output, writer));
                    if (!FitsMetadata(0)) throw XaiRealtimeProtocol.Capacity();
                    output.Writer = null;
                    await writer.DisposeAsync().ConfigureAwait(false); writer.Fence(token);
                }
                lock (_gate) { _pendingMetadata -= output.Size; _outputs.Remove(pair.Key); }
            }
            lock (_gate) { if (_responses.Remove(responseId)) _pendingMetadata -= StringSize(responseId); }
            observation = completed with { AudioArtifacts = _resources is null ? null : Array.AsReadOnly(artifacts.ToArray()) };
            size = checked(size + artifacts.Sum(ArtifactSize));
            // The terminal event owns the immutable receipts now; no unbounded committed history.
        }
        await PublishAsync(observation, size, token).ConfigureAwait(false);
        if (observation is RealtimeResponseEvent { Stage: "response.done" })
        {
            _committed.Clear();
            lock (_gate)
            {
                _committedBytes = 0; _pendingMetadata -= _terminalMetadata; _terminalMetadata = 0;
                _pendingIdentity = null; _pendingStatus = null;
            }
        }
    }
    private RealtimeAudioArtifact RetainArtifact(Output output, SpeechResourceWriter.Writer writer)
    {
        InferenceResourceCommitResult receipt = writer.ObservedCommit!;
        var artifact = new RealtimeAudioArtifact(output.Identity, receipt.Resource, output.Audio)
        { SelectionVersion = new(receipt.Selection!.WorkspaceId, receipt.Selection.SessionId, receipt.Selection.Revision) };
        _committed.Add(artifact);
        lock (_gate)
        {
            _committedBytes = checked(_committedBytes + ArtifactSize(artifact));
        }
        return artifact;
    }
    private sealed record OutputKey(string? ResponseId, string? ItemId, int? OutputIndex, int? ContentIndex);
    private sealed class Output(RealtimeEventIdentity identity, RealtimeAudioConfig? audio, long size)
    {
        internal RealtimeEventIdentity Identity { get; } = identity;
        internal RealtimeAudioConfig? Audio { get; } = audio;
        internal long Size { get; } = size;
        internal bool AudioDone { get; set; }
        internal SpeechResourceWriter.Writer? Writer { get; set; }
    }
    private Output RequireOutput(RealtimeEventIdentity identity)
    {
        var key = new OutputKey(identity.ResponseId, identity.ItemId, identity.OutputIndex, identity.ContentIndex);
        if (_outputs.TryGetValue(key, out Output? output)) return output;
        if (identity.ResponseId is null) throw XaiRealtimeProtocol.Malformed();
        long size = StringSize(identity.ResponseId) + StringSize(identity.ItemId) + 32;
        lock (_gate)
        {
            if (_outputs.Count + _responses.Count >= _limits.MaximumBufferedEvents || !FitsMetadata(size)) throw XaiRealtimeProtocol.Capacity();
            _responses.TryGetValue(identity.ResponseId, out RealtimeAudioConfig? audio);
            output = new(identity with { EventId = null }, audio, size); _outputs.Add(key, output); _pendingMetadata += size; return output;
        }
    }
    private static long ArtifactSize(RealtimeAudioArtifact artifact) => StringSize(artifact.Resource.Reference.WorkspaceId) + StringSize(artifact.Resource.Reference.Id)
        + StringSize(artifact.Resource.SessionId) + StringSize(artifact.Resource.FileName) + StringSize(artifact.Resource.ContentType) + 64;
    private static int StringSize(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);
    private void CheckModelCapacity(string model)
    {
        lock (_gate)
            if (!_attemptedModels.Contains(model) && model != _routingModel
                && (_attemptedModels.Count >= _limits.MaximumBufferedEvents || !FitsMetadata(StringSize(model)))) throw XaiRealtimeProtocol.Capacity();
    }
    private void AttemptModel(string model)
    {
        bool invalidate; RealtimeConversationReference? resumed; RealtimeConversationReference? conversation;
        lock (_gate)
        {
            CheckModelCapacity(model);
            invalidate = model != _routingModel;
            if (invalidate)
            {
                _routingModel = model; _routingChanged = true;
                if (_attemptedModels.Add(model)) _attemptedModelBytes += StringSize(model);
            }
            resumed = _resumptionReceipt; conversation = _conversation;
        }
        if (invalidate)
        {
            if (resumed is not null) _invalidateReceipt!(resumed);
            if (conversation is not null) _invalidateReceipt!(conversation);
        }
    }
    private bool FitsMetadata(long additional)
    {
        lock (_gate)
        {
            return additional <= _limits.MaximumBufferedPayloadBytes
            && _pendingMetadata <= _limits.MaximumBufferedPayloadBytes - additional
            && _committedBytes <= _limits.MaximumBufferedPayloadBytes - additional - _pendingMetadata
            && _attemptedModelBytes <= _limits.MaximumBufferedPayloadBytes - additional - _pendingMetadata - _committedBytes
            && _buffered <= _limits.MaximumBufferedPayloadBytes - additional - _pendingMetadata - _committedBytes - _attemptedModelBytes;
        }
    }
    private async Task PublishAsync(RealtimeEvent observation, long size, CancellationToken token)
    {
        if (size > _limits.MaximumBufferedPayloadBytes) throw XaiRealtimeProtocol.Capacity();
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_pendingMetadata > _limits.MaximumBufferedPayloadBytes - size
                    || _attemptedModelBytes > _limits.MaximumBufferedPayloadBytes - size - _pendingMetadata) throw XaiRealtimeProtocol.Capacity();
                if (_buffered <= _limits.MaximumBufferedPayloadBytes - size - _pendingMetadata - _attemptedModelBytes) { _buffered += size; break; }
                wait = _space.Task;
            }
            await wait.WaitAsync(token).ConfigureAwait(false); Fence(token);
        }
        try { await _events.Writer.WriteAsync((observation, size), token).ConfigureAwait(false); }
        catch { Release(size); throw; }
        Fence(token);
    }
    private void Release(long size)
    {
        TaskCompletionSource signal;
        lock (_gate) { _buffered -= size; signal = _space; _space = Signal(); }
        signal.TrySetResult();
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool Stopping() { try { _lifetime.ThrowIfStopping(); return false; } catch (ObjectDisposedException) { return true; } }
    private void Fence(CancellationToken token) { token.ThrowIfCancellationRequested(); if (Stopping()) throw new OperationCanceledException(token); }
    public ValueTask DisposeAsync() => new(RetireAsync(explicitRetirement: true));
    private Task RetireAsync(bool explicitRetirement = false)
    {
        bool start;
        lock (_gate)
        {
            _explicitRetirement |= explicitRetirement;
            start = !_completionStarted; _completionStarted = true;
        }
        if (explicitRetirement) _events.Writer.TryComplete();
        if (start) _ = CompleteAsync();
        return _completion.Task;
    }
    [SuppressMessage("Design", "CA1031", Justification = "The cached completion preserves the settled failure for Events and every disposer.")]
    private async Task CompleteAsync()
    {
        Exception? releaseFailure = null;
        try
        {
            await _lifetime.RetireAsync(async () =>
            {
                await _receiver.ConfigureAwait(false);
                _resources = null; _issue = null; _observeReceipt = null; _invalidateReceipt = null; _resumptionReceipt = null; _secrets = null;
                WebSocket? socket = _socket; _socket = null;
                try { socket?.Dispose(); } catch (Exception error) { _cleanup.Record(XaiRealtimeProvider.Cleanup(error, socket!)); }
                _send.Dispose();
                _responses.Clear(); _outputs.Clear(); _attemptedModels.Clear(); _input = null; _turn = null; _observedOutput = null;
                var failures = new List<Exception>();
                if (_terminalFailure is { } terminal) failures.Add(terminal);
                try { await _cleanup.ReleaseAsync().ConfigureAwait(false); }
                catch (Exception error) { AddUnseen(failures, error); }
                releaseFailure = failures.Count switch { 0 => null, 1 => failures[0], _ => new AggregateException(failures) };
                if (releaseFailure is not null) ExceptionDispatchInfo.Capture(releaseFailure).Throw();
            }).ConfigureAwait(false);
            _completion.TrySetResult();
        }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            if (releaseFailure is not null) failures.Add(releaseFailure);
            AddUnseen(failures, error);
            Exception cause = failures.Count == 1 ? failures[0] : new AggregateException(failures);
            if (_committed.Count != 0)
            {
                var observation = new RealtimeErrorEvent(_pendingIdentity ?? _committed[0].Identity,
                    new(InferenceFailureCode.ExecutionFailed, null, "The realtime operation could not deliver committed audio.", ResourceFailure(cause)))
                { AudioArtifacts = _committed, ProviderStatus = _pendingStatus };
                cause = cause is OperationCanceledException canceled && !LlmResourcePreparation.IsCleanupFailure(cause)
                    ? new RealtimeOperationCanceledException(observation, canceled)
                    : new RealtimeOperationException(observation, cause);
            }
            _completion.TrySetException(cause);
        }
        finally
        {
            _committed.Clear(); _pendingIdentity = null; _pendingStatus = null;
            lock (_gate) { _committedBytes = 0; _pendingMetadata = 0; _terminalMetadata = 0; }
        }
    }
    private static InferenceResourceOperationFailure? ResourceFailure(Exception error) => error switch
    {
        InferenceResourceOperationException resource => resource.Failure,
        InferenceResourceOperationCanceledException resource => resource.Failure,
        AggregateException aggregate => aggregate.InnerExceptions.Select(ResourceFailure).FirstOrDefault(failure => failure is not null),
        _ => null
    };
    private static Exception? Combine(Exception? original, Exception? next)
    {
        if (next is null) return original;
        if (original is null) return next;
        var failures = new List<Exception> { original }; AddUnseen(failures, next);
        return failures.Count == 1 ? original : new AggregateException(failures);
    }
    private static void AddUnseen(List<Exception> failures, Exception error)
    {
        if (failures.Any(root => Contains(root, error))) return;
        if (error is AggregateException aggregate && aggregate.InnerExceptions.Any(child => failures.Any(root => Contains(root, child))))
        { foreach (Exception child in aggregate.InnerExceptions) AddUnseen(failures, child); }
        else failures.Add(error);
    }
    private static bool Contains(Exception root, Exception target)
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var pending = new Stack<Exception>(); pending.Push(root);
        while (pending.TryPop(out Exception? item))
        {
            if (ReferenceEquals(item, target)) return true;
            if (!seen.Add(item)) continue;
            if (item is AggregateException aggregate) foreach (Exception child in aggregate.InnerExceptions) pending.Push(child);
            else if (item.InnerException is { } inner) pending.Push(inner);
        }
        return false;
    }
}
