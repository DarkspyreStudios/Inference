using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// The Antigravity conversation the Host holds. It owns the saved history and the current AGY conversation, measures
/// before each turn, and compacts by opening a new AGY conversation with the compacted history as its opening. AGY runs
/// its own tools inside a turn, so the only decision point is before a turn's input is sent. AGY's own compaction, a
/// <c>checkpoint</c> step, is reported as it happens.
/// </summary>
internal sealed class AntigravityConversation : ILlmAgentSession
{
    private readonly LlmConnection _connection;
    private readonly AntigravityCliLlmClient _chat;
    // Opens an AGY session: a new conversation with the history as its opening, or the conversation the ID names.
    private readonly Func<LlmAgentContext, string?, CancellationToken, Task<AntigravityAgentSession>> _open;
    private readonly string _modelId;
    private readonly LlmAgentExecutionOptions _execution;
    // The AGY conversation a resume names; the first session reopens it.
    private readonly string? _resumeConversationId;
    private readonly SavedConversation _history;
    private readonly ConversationCompactor _compactor;
    private readonly NoAgentResources _resources = new(LlmAgentResourceStopStatus.Unknown);
    // One turn or one compaction at a time.
    private readonly SemaphoreSlim _slot = new(1, 1);
    // Guards _inner, _turn and _retirement.
    private readonly Lock _sync = new();
    // Null when no AGY session is open: the next turn or compaction opens one with the saved history.
    private AntigravityAgentSession? _inner;
    private ActiveTurn? _turn;
    private Task? _retirement;

    internal AntigravityConversation(string connectionId, LlmConnection connection, AntigravityCliLlmClient chat,
        LearnedModelContext learned, string modelId, LlmAgentExecutionOptions? execution, ILlmConversationTranscript? transcript,
        LlmAgentContext history, string? resumeConversationId,
        Func<LlmAgentContext, string?, CancellationToken, Task<AntigravityAgentSession>> open)
    {
        _connection = connection;
        _chat = chat;
        _open = open;
        _modelId = modelId;
        _execution = execution ?? new();
        _resumeConversationId = resumeConversationId;
        // AGY takes text only, so no caller resource is counted; the instructions are counted once AGY reports usage.
        _history = new SavedConversation(connectionId, connection, chat, learned, _execution, framing: null, resources: null,
            AntigravityModelLimits.Get, history);
        _compactor = new(MeasureForCompactionAsync, SideRequestAsync, EstimateTextAsync, transcript);
    }

    /// <summary>The first AGY conversation's ID, fixed for this conversation.</summary>
    public string ThreadId { get; private set; } = string.Empty;

    public string SessionId => ThreadId;

    public LlmCompactionResponsibility CompactionResponsibility => LlmCompactionResponsibility.Provider;

    private bool Preserve => _execution.ContextPolicy.PreserveSuppliedContext;

    /// <summary>
    /// Opens the first AGY session: the conversation a resume names, with no opening, or a new conversation with the
    /// saved history as its opening.
    /// </summary>
    internal async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_resumeConversationId is { } id)
            _ = await InstallAsync(await _open(new(), id, cancellationToken).ConfigureAwait(false), new(), replaceHistory: false,
                resumedNatively: true)
                .ConfigureAwait(false);
        else
            await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        ThreadId = Inner().ThreadId;
    }

    /// <summary>
    /// The Antigravity marks. SupportsNativeResume is Supported only while the open AGY conversation is the one ThreadId
    /// names: a compaction moves the conversation to another AGY conversation, which a resume by ThreadId does not reach.
    /// </summary>
    public Task<LlmAgentCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AntigravityAgentSession? inner;
        lock (_sync) { inner = _inner; }
        LlmAgentCapabilities marks = AntigravityNativeAgent.Capabilities;
        return Task.FromResult(inner is not null && inner.ThreadId == ThreadId
            ? marks
            : marks with { SupportsNativeResume = InferenceSupport.Unsupported });
    }

    /// <summary>The measure of the saved history and the prospective turn's input. AGY is not asked.</summary>
    public Task<LlmAgentContextStatus> GetContextStatusAsync(LlmAgentTurnRequest? prospectiveTurn = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_retirement is not null, this);
        string modelId = string.IsNullOrWhiteSpace(prospectiveTurn?.ModelId) ? _modelId : prospectiveTurn.ModelId;
        LlmChatMessage? input = prospectiveTurn is null ? null : ManagedAgentSession.UserMessage(ClientTools.Snapshot(prospectiveTurn));
        return _history.StatusAsync(modelId, input, observe: false, cancellationToken);
    }

    public Task<LlmRequestAccounting> MeasureContextAsync(LlmAgentContext proposedContext, CancellationToken cancellationToken = default) =>
        Inner().MeasureContextAsync(proposedContext, cancellationToken);

    public Task<LlmAgentContext> CaptureContextAsync(LlmAgentContextCaptureOptions? options = null,
        CancellationToken cancellationToken = default) => throw NativeAgentContract.UnsupportedContext(cancellationToken);

    public Task ReplaceContextAsync(LlmAgentContext context, long expectedRevision, CancellationToken cancellationToken = default) =>
        throw NativeAgentContract.UnsupportedContext(cancellationToken);

    public IAsyncEnumerable<LlmAgentEvent> ContinueTurnAsync(LlmAgentTurnContinuation continuation,
        CancellationToken cancellationToken = default) => NativeAgentContract.UnsupportedContinuation(cancellationToken);

    public Task SteerAsync(string turnId, IReadOnlyList<LlmAgentContent> input, CancellationToken cancellationToken = default) =>
        Inner().SteerAsync(turnId, input, cancellationToken);

    public Task InterruptAsync(string turnId, CancellationToken cancellationToken = default) =>
        Inner().InterruptAsync(turnId, cancellationToken);

    public IAsyncEnumerable<LlmAgentEvent> ObserveAsync(CancellationToken cancellationToken = default) =>
        _resources.ObserveAsync(cancellationToken);

    public Task<IReadOnlyList<LlmAgentResource>> GetResourcesAsync(CancellationToken cancellationToken = default) =>
        Inner().GetResourcesAsync(cancellationToken);

    public Task<LlmAgentResourceOutput> ReadResourceOutputAsync(string resourceId, string? cursor,
        CancellationToken cancellationToken = default) => Inner().ReadResourceOutputAsync(resourceId, cursor, cancellationToken);

    public Task<LlmAgentResourceStopStatus> StopResourceAsync(string resourceId, CancellationToken cancellationToken = default) =>
        Inner().StopResourceAsync(resourceId, cancellationToken);

    public IAsyncEnumerable<LlmAgentEvent> RunTurnAsync(LlmAgentTurnRequest request, CancellationToken cancellationToken = default) =>
        ProduceTurnAsync(request, cancellationToken);

    private async IAsyncEnumerable<LlmAgentEvent> ProduceTurnAsync(LlmAgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = ClientTools.Snapshot(request);
        ObjectDisposedException.ThrowIf(_retirement is not null, this);
        if (!_slot.Wait(0, cancellationToken)) throw new InvalidOperationException("The session is running or paused.");
        Channel<LlmAgentEvent> events = Channel.CreateUnbounded<LlmAgentEvent>(new UnboundedChannelOptions { SingleReader = true });
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var turn = new ActiveTurn(Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(request.ModelId) ? _modelId : request.ModelId, events.Writer, cancellation.Token);
        Task producer;
        try
        {
            lock (_sync) { _turn = turn; }
            producer = RunAsync(turn, request);
        }
        catch
        {
            cancellation.Dispose();
            _slot.Release();
            throw;
        }
        try
        {
            await foreach (LlmAgentEvent item in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            await producer.ConfigureAwait(false);
            lock (_sync) { _turn = null; }
            cancellation.Dispose();
            _slot.Release();
        }
    }

    /// <summary>Runs one turn into its event stream, which it always completes: with the turn's failure, if any.</summary>
    [SuppressMessage("Design", "CA1031", Justification = "The failure completes the turn's stream, so its reader receives it.")]
    private async Task RunAsync(ActiveTurn turn, LlmAgentTurnRequest request)
    {
        Exception? failure = null;
        try
        {
            await RunTurnCoreAsync(turn, request).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }
        turn.Writer.TryComplete(failure);
    }

    private async Task RunTurnCoreAsync(ActiveTurn turn, LlmAgentTurnRequest request)
    {
        CancellationToken token = turn.Token;
        turn.Write(new LlmAgentTurnStartedEvent(ThreadId, turn.Id));
        await EnsureOpenAsync(token).ConfigureAwait(false);
        turn.PendingInput = ManagedAgentSession.UserMessage(request);
        // The decision point: before the turn's input is sent. The first turn's measure includes the opening.
        LlmAgentContextStatus status = await _history.StatusAsync(turn.ModelId, turn.PendingInput, observe: true, token)
            .ConfigureAwait(false);
        turn.Write(new LlmAgentContextStatusEvent(ThreadId, turn.Id, status));
        if (status.NeedsCompaction is true && !Preserve && !await CompactAsync(turn).ConfigureAwait(false)) return;
        _history.Upsert(new LlmAgentContextEntry(turn.Record.InputId, turn.Id, turn.PendingInput!));
        turn.PendingInput = null;

        AntigravityAgentSession inner = Inner();
        LlmAgentTurnCompletedEvent? completion = null;
        try
        {
            await foreach (LlmAgentEvent item in inner.RunTurnAsync(request, token).ConfigureAwait(false))
            {
                foreach (LlmAgentContextEntry entry in turn.Record.Apply(item)) _history.Upsert(entry);
                switch (item)
                {
                    case LlmAgentTurnStartedEvent:
                        break;
                    case LlmAgentTurnCompletedEvent completed:
                        completion = completed;
                        break;
                    case LlmAgentUsageEvent usage:
                        // Each AGY session reports totals for its own turn only.
                        turn.Usage.InnerTotals = usage.RunTotals;
                        turn.Write(usage with { ThreadId = ThreadId, TurnId = turn.Id, RunTotals = turn.Usage.Totals(usage.RunTotals) });
                        break;
                    case LlmAgentCompactedEvent compacted:
                        turn.Write(compacted with { ThreadId = ThreadId, TurnId = turn.Id, ContextRevision = _history.Saved.Revision });
                        break;
                    default:
                        turn.Write(item with { ThreadId = ThreadId, TurnId = turn.Id });
                        break;
                }
            }
        }
        finally
        {
            turn.Usage.FoldInner();
        }
        // The next measure starts from the last response's report; the first response's report teaches the rate.
        if (inner.FirstStepUsage is { InputTokens: int firstInput } first)
            _history.ObserveReport(turn.ModelId, firstInput, first.OutputTokens);
        if (inner.LastStepUsage is { InputTokens: int lastInput } last)
            _history.ObserveReport(turn.ModelId, lastInput, last.OutputTokens);
        if (completion is null)
            throw new LlmProviderException("Antigravity CLI ended the turn without a result.", LlmTransport.GoogleAntigravityCli,
                providerCode: "antigravity_cli_invalid_response");
        turn.Write(completion with { ThreadId = ThreadId, TurnId = turn.Id });
    }

    /// <summary>
    /// Compacts the saved history with the turn's pending input as the current turn and, when compaction changed it,
    /// replaces the AGY conversation with a new one opened with the result. A failed compaction keeps the open AGY
    /// conversation and ends the turn. False when the turn ended.
    /// </summary>
    private async Task<bool> CompactAsync(ActiveTurn turn)
    {
        CancellationToken token = turn.Token;
        // A trigger exists only under known limits.
        CompactionLimits limits = _history.Limits(turn.ModelId)!;
        string itemId = Guid.NewGuid().ToString("N");
        CompactionResult result;
        try
        {
            result = await _compactor.CompactAsync(_history.Saved, turn.ModelId, turn.Id, new HashSet<string>(StringComparer.Ordinal),
                limits, (text, _) => { turn.Write(new LlmAgentActivityEvent(ThreadId, turn.Id, itemId, text)); return ValueTask.CompletedTask; },
                token).ConfigureAwait(false);
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is CompactionFailedException or LlmProviderException)
        {
            LlmUsage? failedUsage = error is CompactionFailedException failed ? failed.Usage : ((LlmProviderException)error).Usage;
            if (failedUsage is not null)
            {
                turn.Usage.Add(failedUsage);
                turn.Write(new LlmAgentUsageEvent(ThreadId, turn.Id, Guid.NewGuid().ToString("N"), failedUsage, turn.Usage.Totals(null)));
            }
            turn.Write(new LlmAgentActivityEvent(ThreadId, turn.Id, itemId, string.Empty));
            turn.Write(new LlmAgentTurnCompletedEvent(ThreadId, turn.Id, LlmAgentCompletionReason.ProviderFailure, error.Message));
            return false;
        }
        if (result.Changed)
        {
            turn.Write(new LlmAgentActivityEvent(ThreadId, turn.Id, itemId, "Applying the compacted context"));
            await CloseInnerAsync().ConfigureAwait(false);
            long revision = await OpenNewAsync(result.Context, replaceHistory: true, token).ConfigureAwait(false);
            string requestId = Guid.NewGuid().ToString("N");
            turn.Write(result.ToEvent(ThreadId, turn.Id, requestId, revision, carriesContext: true));
            if (result.Usage is not null)
            {
                turn.Usage.Add(result.Usage);
                turn.Write(new LlmAgentUsageEvent(ThreadId, turn.Id, requestId, result.Usage, turn.Usage.Totals(null)));
            }
        }
        turn.Write(new LlmAgentActivityEvent(ThreadId, turn.Id, itemId, string.Empty));
        // The next send is the first step of the new AGY conversation: its measure is reported and observed.
        LlmAgentContextStatus measured = await _history.StatusAsync(turn.ModelId, turn.PendingInput, observe: true, token)
            .ConfigureAwait(false);
        turn.Write(new LlmAgentContextStatusEvent(ThreadId, turn.Id, measured));
        return true;
    }

    public async Task<LlmAgentCompactedEvent?> CompactNowAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_retirement is not null, this);
        if (AntigravityCliLlmClient.ResolveCapabilities(_connection, _modelId).Agent.SupportsCompactNow == InferenceSupport.Unsupported)
            throw new NotSupportedException("The selected connection disables this agent operation.");
        if (!_slot.Wait(0, cancellationToken)) throw new InvalidOperationException("The session is running or paused.");
        try
        {
            LlmAgentContext source = _history.Saved;
            CompactionLimits? limits = _history.Limits(_modelId);
            if (source.Entries.Count == 0 || limits is null) return null;
            // No turn is running, so the last turn stays whole.
            string lastTurnId = source.Entries[^1].TurnId;
            CompactionResult result = await _compactor.CompactAsync(source, _modelId, lastTurnId,
                new HashSet<string>(StringComparer.Ordinal), limits, static (_, _) => ValueTask.CompletedTask, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Changed) return null;
            await CloseInnerAsync().ConfigureAwait(false);
            long revision = await OpenNewAsync(result.Context, replaceHistory: true, cancellationToken).ConfigureAwait(false);
            return result.ToEvent(ThreadId, lastTurnId, Guid.NewGuid().ToString("N"), revision, carriesContext: true);
        }
        finally
        {
            _slot.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync) { _retirement ??= RetireAsync(); }
        return new(_retirement);
    }

    private async Task RetireAsync()
    {
        AntigravityAgentSession? inner;
        lock (_sync)
        {
            inner = _inner;
            _inner = null;
        }
        _resources.Dispose();
        if (inner is not null) await inner.DisposeAsync().ConfigureAwait(false);
    }

    private AntigravityAgentSession Inner()
    {
        lock (_sync) { return _inner ?? throw new ObjectDisposedException(nameof(AntigravityConversation)); }
    }

    /// <summary>Opens a new AGY conversation with the saved history when none is open.</summary>
    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        lock (_sync) { if (_inner is not null) return; }
        _ = await OpenNewAsync(_history.Saved, replaceHistory: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a new AGY conversation with <paramref name="opening"/> and makes it current. With
    /// <paramref name="replaceHistory"/>, the opening becomes the next revision of the saved history, only once the
    /// conversation opened. Returns the revision.
    /// </summary>
    private async Task<long> OpenNewAsync(LlmAgentContext opening, bool replaceHistory, CancellationToken cancellationToken) =>
        await InstallAsync(await _open(opening, null, cancellationToken).ConfigureAwait(false), opening, replaceHistory,
            resumedNatively: false)
            .ConfigureAwait(false);

    private async Task<long> InstallAsync(AntigravityAgentSession session, LlmAgentContext opening, bool replaceHistory,
        bool resumedNatively)
    {
        lock (_sync)
        {
            if (_retirement is null)
            {
                _inner = session;
                return _history.Install(opening, replaceHistory, resumedNatively);
            }
        }
        await session.DisposeAsync().ConfigureAwait(false);
        throw new ObjectDisposedException(nameof(AntigravityConversation));
    }

    private async Task CloseInnerAsync()
    {
        AntigravityAgentSession? inner;
        lock (_sync)
        {
            inner = _inner;
            _inner = null;
        }
        if (inner is not null) await inner.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The corrected size of the whole request a new AGY conversation opened with <paramref name="context"/> receives.</summary>
    private ValueTask<long> MeasureForCompactionAsync(LlmAgentContext context, CancellationToken cancellationToken)
    {
        ActiveTurn? turn;
        lock (_sync) { turn = _turn; }
        return _history.MeasureAsync(context, turn?.ModelId ?? _modelId,
            turn?.PendingInput is { } input ? [input] : [], cancellationToken);
    }

    /// <summary>Sends one summary request in print mode on the conversation's connection and returns its result.</summary>
    private Task<LlmChatResult> SideRequestAsync(LlmChatRequest request, CancellationToken cancellationToken) =>
        SummaryRequest.ReadAsync(_chat.StreamAsync(_connection, request, cancellationToken), cancellationToken);

    /// <summary>The corrected size of a text sent as one user message.</summary>
    private ValueTask<long?> EstimateTextAsync(string text, CancellationToken cancellationToken)
    {
        ActiveTurn? turn;
        lock (_sync) { turn = _turn; }
        return _history.EstimateTextAsync(turn?.ModelId ?? _modelId, text, cancellationToken);
    }

    /// <summary>One running turn: its id, model, record, stream and cancellation.</summary>
    private sealed class ActiveTurn(string id, string modelId, ChannelWriter<LlmAgentEvent> writer, CancellationToken token)
    {
        public string Id { get; } = id;
        public string ModelId { get; } = modelId;
        // AGY runs its own tools, so only the input and the final answer are recorded.
        public TurnRecord Record { get; } = new(id, recordsCalls: false);
        public ChannelWriter<LlmAgentEvent> Writer { get; } = writer;
        public CancellationToken Token { get; } = token;
        public TurnUsage Usage { get; } = new();
        // The turn's input before it is recorded; measures and compaction count it.
        public LlmChatMessage? PendingInput { get; set; }

        public void Write(LlmAgentEvent item) => Writer.TryWrite(item);
    }
}
