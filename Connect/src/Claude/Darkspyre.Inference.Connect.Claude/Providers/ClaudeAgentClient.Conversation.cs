using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed partial class ClaudeAgentClient
{
    private ClaudeCliLlmClient Chat => chat;

    private LearnedModelContext Learned => learned;

    private IClaudeCliSessionProcessFactory Processes => processFactory;

    /// <summary>
    /// The Claude conversation the Host holds. It owns the saved history and the current CLI session, measures before
    /// each send, and compacts by opening a new CLI session with the compacted history as its opening.
    /// </summary>
    private sealed class ClaudeConversation : ILlmAgentSession
    {
        private const string ContinueLine =
            "Continue from where you stopped. The tool results above are complete; do not repeat finished tool calls.";

        private readonly ClaudeAgentClient _owner;
        private readonly string _connectionId;
        private readonly LlmConnection _connection;
        private readonly string _workingDirectory;
        private readonly bool _ephemeral;
        // The thread id a resume names; the first CLI session opens under it.
        private readonly string? _resumeThreadId;
        private readonly LlmAgentToolHandler? _toolHandler;
        private readonly Dictionary<string, LlmAgentToolDefinition> _toolsByName;
        // The session instructions followed by the tool list the bridge sends, as the CLI receives both.
        private readonly string? _framing;
        private readonly LlmAgentExecutionOptions _execution;
        private readonly ConversationCompactor _compactor;
        private readonly NoAgentResources _resources = new(LlmAgentResourceStopStatus.Unknown);
        // One turn or one compaction at a time.
        private readonly SemaphoreSlim _slot = new(1, 1);
        // Guards everything below; the inner sessions call Record and BeforeResultsAsync from their own threads.
        private readonly Lock _sync = new();
        // ModelId follows the model the turns select, so a reopened CLI starts on it.
        private LlmAgentSessionOptions _options;
        private readonly SavedConversation _history;
        // Null when no CLI session is open: the next turn or compaction opens one with the saved history.
        private ClaudeAgentSession? _inner;
        private IReadOnlyList<LlmModel>? _catalog;
        // The open inner session's number; events of any other session are not recorded.
        private int _generation = -1;
        private int _nextGeneration;
        private ActiveTurn? _turn;
        private Task? _retirement;

        internal ClaudeConversation(ClaudeAgentClient owner, string connectionId, LlmConnection connection,
            LlmAgentSessionOptions options, string workingDirectory, bool ephemeral, LlmAgentContext history, string? resumeThreadId)
        {
            _resumeThreadId = resumeThreadId;
            _owner = owner;
            _connectionId = connectionId;
            _connection = connection;
            _workingDirectory = workingDirectory;
            _ephemeral = ephemeral;
            _toolHandler = options.ToolHandler;
            // The first definition of a duplicated name is the one used.
            LlmAgentToolDefinition[] tools = [.. ClientTools.Functions(options.Tools).DistinctBy(tool => tool.Name, StringComparer.Ordinal)];
            _toolsByName = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
            string? toolList = tools.Length == 0 ? null : ClaudeAgentSession.McpToolsListResult(tools,
                LlmToolNames.Create(tools.Select(tool => tool.Name), McpToolBridge.IsValidToolName)).ToJsonString();
            _framing = toolList is null ? options.Instructions
                : string.IsNullOrEmpty(options.Instructions) ? toolList : $"{options.Instructions}\n\n{toolList}";
            _execution = options.ExecutionOptions ?? new();
            _options = options with { Context = null, ToolHandler = options.ToolHandler is null ? null : HandleToolAsync };
            _history = new SavedConversation(connectionId, connection, owner.Chat, owner.Learned, _execution, _framing,
                options.CallerServices?.Resources, Model, history);
            _compactor = new(MeasureForCompactionAsync, SideRequestAsync, EstimateText, options.CallerServices?.Transcript);
        }

        /// <summary>The first CLI session's id, fixed for this conversation.</summary>
        public string ThreadId { get; private set; } = string.Empty;

        public string SessionId { get; private set; } = string.Empty;

        public LlmCompactionResponsibility CompactionResponsibility => LlmCompactionResponsibility.Provider;

        private bool Preserve => _execution.ContextPolicy.PreserveSuppliedContext;

        /// <summary>
        /// Opens the first CLI session. A resume whose session the CLI saved for this working folder opens it with
        /// <c>--resume</c> and sends no opening; otherwise the saved history is the opening, under the resumed id when
        /// there is one. The first turn's decision point (a) measures and compacts the saved history.
        /// </summary>
        internal async Task OpenAsync(CancellationToken cancellationToken)
        {
            if (_resumeThreadId is { } id)
            {
                bool native = !_ephemeral
                    && _owner.Processes.SessionSaved(CliInfrastructure.NormalizeWorkingDirectory(_workingDirectory), id);
                _ = await OpenAndInstallAsync(native ? new() : _history.Saved, replaceHistory: false, cancellationToken, (id, native));
            }
            else
            {
                await EnsureOpenAsync(cancellationToken);
            }
            ThreadId = _inner!.ThreadId;
            SessionId = _options.SessionId ?? ThreadId;
        }

        /// <summary>
        /// The Claude marks. SupportsNativeResume is Supported only while the open CLI session is the one ThreadId names
        /// and the CLI saves it: a compaction, a reopen or an unanswered tool call moves the conversation to another CLI
        /// session, which a resume by ThreadId does not reach.
        /// </summary>
        public async Task<LlmAgentCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            ClaudeAgentSession? inner;
            lock (_sync) { inner = _inner; }
            LlmAgentCapabilities marks = inner is null ? NativeAgentContract.Claude : await inner.GetCapabilitiesAsync(cancellationToken);
            return !_ephemeral && inner is not null && inner.ThreadId == ThreadId
                ? marks
                : marks with { SupportsNativeResume = InferenceSupport.Unsupported };
        }

        /// <summary>
        /// Sends <paramref name="input"/> to the running inner turn. The saved history records it where the CLI takes it
        /// in, when its lifecycle reports it started.
        /// </summary>
        public async Task SteerAsync(string turnId, IReadOnlyList<LlmAgentContent> input, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(input);
            ObjectDisposedException.ThrowIf(_retirement is not null, this);
            string steerId = Guid.NewGuid().ToString();
            ActiveTurn turn;
            ClaudeAgentSession inner;
            string innerTurnId;
            lock (_sync)
            {
                // The inner session accepts only its own turn id.
                if (_turn is not { } active || active.Id != turnId || active.InnerTurnId is not { } running || _inner is not { } open)
                    throw new ArgumentException("The requested turn is not active.", nameof(turnId));
                (turn, inner, innerTurnId) = (active, open, running);
                turn.Steers.Add(steerId, ManagedAgentSession.UserMessage(new LlmAgentTurnRequest { Input = input }));
            }
            try
            {
                await inner.SteerAsync(innerTurnId, steerId, input, cancellationToken);
            }
            catch
            {
                lock (_sync) { turn.Steers.Remove(steerId); }
                throw;
            }
        }

        public Task<LlmRequestAccounting> MeasureContextAsync(LlmAgentContext proposedContext, CancellationToken cancellationToken = default) =>
            (_inner ?? throw NativeAgentContract.UnsupportedContext(cancellationToken)).MeasureContextAsync(proposedContext, cancellationToken);

        public Task<LlmAgentContext> CaptureContextAsync(LlmAgentContextCaptureOptions? options = null,
            CancellationToken cancellationToken = default) =>
            (_inner ?? throw NativeAgentContract.UnsupportedContext(cancellationToken)).CaptureContextAsync(options, cancellationToken);

        public Task ReplaceContextAsync(LlmAgentContext context, long expectedRevision, CancellationToken cancellationToken = default) =>
            (_inner ?? throw NativeAgentContract.UnsupportedContext(cancellationToken)).ReplaceContextAsync(context, expectedRevision, cancellationToken);

        public IAsyncEnumerable<LlmAgentEvent> ContinueTurnAsync(LlmAgentTurnContinuation continuation,
            CancellationToken cancellationToken = default) =>
            _inner?.ContinueTurnAsync(continuation, cancellationToken) ?? NativeAgentContract.UnsupportedContinuation(cancellationToken);

        public IAsyncEnumerable<LlmAgentEvent> ObserveAsync(CancellationToken cancellationToken = default) =>
            _resources.ObserveAsync(cancellationToken);

        public Task<IReadOnlyList<LlmAgentResource>> GetResourcesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return NoAgentResources.GetAsync();
        }

        public Task<LlmAgentResourceOutput> ReadResourceOutputAsync(string resourceId, string? cursor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
            return NoAgentResources.ReadAsync();
        }

        public Task<LlmAgentResourceStopStatus> StopResourceAsync(string resourceId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
            return _resources.StopAsync();
        }

        /// <summary>The 6.5.4 measure of the saved history and the prospective turn's input. The CLI is not asked.</summary>
        public async Task<LlmAgentContextStatus> GetContextStatusAsync(LlmAgentTurnRequest? prospectiveTurn = null,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_retirement is not null, this);
            string modelId;
            lock (_sync) { modelId = prospectiveTurn?.ModelId ?? _turn?.ModelId ?? _options.ModelId; }
            LlmChatMessage? input = prospectiveTurn is null ? null : ManagedAgentSession.UserMessage(ClientTools.Snapshot(prospectiveTurn));
            return await _history.StatusAsync(modelId, input, observe: false, cancellationToken);
        }

        public async Task InterruptAsync(string turnId, CancellationToken cancellationToken = default)
        {
            ActiveTurn? turn;
            ClaudeAgentSession? inner;
            string? innerTurnId;
            lock (_sync)
            {
                turn = _turn;
                if (turn is null || turn.Id != turnId)
                    throw new ArgumentException("The requested turn is not active.", nameof(turnId));
                turn.Interrupted = true;
                inner = _inner;
                innerTurnId = turn.InnerTurnId;
            }
            // The inner session accepts only its own turn id.
            if (inner is not null && innerTurnId is not null)
            {
                try { await inner.InterruptAsync(innerTurnId, cancellationToken); }
                catch (ArgumentException) { /* The inner turn ended as the interrupt arrived. */ }
            }
            try { await turn.Cancellation.CancelAsync(); }
            catch (ObjectDisposedException) { /* The turn ended as the interrupt arrived. */ }
        }

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
            string turnId = Guid.NewGuid().ToString("N");
            var turn = new ActiveTurn(turnId, new TurnRecord(turnId, _options.ToolHandling == LlmAgentToolHandling.Execute),
                events.Writer, cancellation);
            Task producer;
            try
            {
                lock (_sync)
                {
                    if (!string.IsNullOrWhiteSpace(request.ModelId)) _options = _options with { ModelId = request.ModelId };
                    turn.ModelId = _options.ModelId;
                    _turn = turn;
                }
                producer = RunAsync(turn, request, cancellationToken);
            }
            catch
            {
                cancellation.Dispose();
                _slot.Release();
                throw;
            }
            try
            {
                await foreach (LlmAgentEvent item in events.Reader.ReadAllAsync(cancellationToken))
                    yield return item;
            }
            finally
            {
                await cancellation.CancelAsync();
                await producer;
                lock (_sync) { _turn = null; }
                cancellation.Dispose();
                _slot.Release();
            }
        }

        /// <summary>Runs one turn into its event stream, which it always completes: with the turn's failure, if any.</summary>
        [SuppressMessage("Design", "CA1031", Justification = "The failure completes the turn's stream, so its reader receives it.")]
        private async Task RunAsync(ActiveTurn turn, LlmAgentTurnRequest request, CancellationToken consumer)
        {
            Exception? failure = null;
            try
            {
                await RunTurnCoreAsync(turn, request);
            }
            catch (OperationCanceledException) when (turn.Interrupted && !consumer.IsCancellationRequested && _retirement is null)
            {
                // A cancelled measure, compaction or opening leaves the saved history unchanged.
                turn.Write(new LlmAgentTurnCompletedEvent(ThreadId, turn.Id, LlmAgentCompletionReason.Interruption, null));
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
            await EnsureOpenAsync(token);
            LlmChatMessage input = ManagedAgentSession.UserMessage(request);
            turn.PendingInput = input;
            // Decision point (a): before the turn's input is written. The first turn's measure includes the opening.
            if (!await DecideAsync(turn)) return;
            RecordInput(turn, input);
            LlmAgentTurnRequest next = request;
            while (true)
            {
                LlmAgentTurnCompletedEvent? completion = await ForwardAsync(turn, next);
                if (completion is null)
                {
                    // Decision point (b) stopped the inner turn with the step's last result unwritten. A steer waits
                    // for that result, so the closing CLI never started it.
                    RecordUnstartedSteers(turn);
                    if (!await ReopenAsync(turn)) return;
                    next = Continue(request);
                    continue;
                }
                if (completion.IsContextOverflow && !Preserve && !turn.Refused)
                {
                    // The Host never sees the first refusal: compact, open a new CLI session and send once more.
                    turn.Refused = true;
                    bool midTurn = turn.Record.HasSteps;
                    if (!midTurn)
                    {
                        _history.Remove(turn.Record.InputId);
                        turn.PendingInput = input;
                    }
                    await ObserveRefusalAsync(turn, completion.Error);
                    if (!await ReopenAsync(turn)) return;
                    if (!midTurn) RecordInput(turn, input);
                    next = midTurn ? Continue(request) : request;
                    continue;
                }
                if (completion.Reason == LlmAgentCompletionReason.ToolRequests)
                {
                    // The CLI holds an unanswered call; the next send opens a new CLI session from the saved history.
                    await CloseInnerAsync();
                }
                turn.Write(completion with { ThreadId = ThreadId, TurnId = turn.Id });
                return;
            }
        }

        /// <summary>The turn's input and settings with the continuation line in place of its input.</summary>
        private static LlmAgentTurnRequest Continue(LlmAgentTurnRequest request) =>
            request with { Input = [new LlmAgentTextContent(ContinueLine)], Attachments = [] };

        /// <summary>
        /// Forwards one inner turn with this conversation's thread id and the outer turn id. Returns its completion, or
        /// null when decision point (b) ended it.
        /// </summary>
        private async Task<LlmAgentTurnCompletedEvent?> ForwardAsync(ActiveTurn turn, LlmAgentTurnRequest request)
        {
            ClaudeAgentSession inner = _inner ?? throw new ObjectDisposedException(nameof(ClaudeConversation));
            LlmAgentTurnCompletedEvent? completion = null;
            try
            {
                await foreach (LlmAgentEvent item in inner.RunTurnAsync(request, turn.Token))
                {
                    switch (item)
                    {
                        case LlmAgentTurnStartedEvent started:
                            lock (_sync) { turn.InnerTurnId = started.TurnId; }
                            break;
                        case LlmAgentTurnCompletedEvent completed:
                            completion = completed;
                            break;
                        case LlmAgentUsageEvent usage:
                            // Each CLI process reports totals for its own turn only.
                            turn.Usage.InnerTotals = usage.RunTotals;
                            turn.Write(usage with { ThreadId = ThreadId, TurnId = turn.Id, RunTotals = turn.Usage.Totals(usage.RunTotals) });
                            break;
                        default:
                            turn.Write(item with { ThreadId = ThreadId, TurnId = turn.Id });
                            break;
                    }
                }
            }
            finally
            {
                lock (_sync) { turn.InnerTurnId = null; }
                turn.Usage.FoldInner();
            }
            if (completion is null && !turn.TakeReopen())
                throw new LlmProviderException("Claude CLI ended the turn without a result.", LlmTransport.AnthropicClaudeCli,
                    providerCode: "claude_cli_agent_protocol");
            return completion;
        }

        /// <summary>Decision point (a): measures, reports and compacts at or above the trigger. False when the turn ended.</summary>
        private async Task<bool> DecideAsync(ActiveTurn turn)
        {
            LlmAgentContextStatus measured = await _history.StatusAsync(turn.ModelId, turn.PendingInput, observe: true, turn.Token);
            turn.Write(new LlmAgentContextStatusEvent(ThreadId, turn.Id, measured));
            return measured.NeedsCompaction is not true || Preserve || await ReopenAsync(turn);
        }

        /// <summary>
        /// Decision point (b), called by the inner session when a model step's last bridge call settles. True asks the
        /// session to stop the turn with that result unwritten, so the conversation compacts and reopens.
        /// </summary>
        private async ValueTask<bool> BeforeResultsAsync(int generation, CancellationToken cancellationToken)
        {
            ActiveTurn? turn;
            lock (_sync)
            {
                turn = generation == _generation ? _turn : null;
            }
            if (turn is null) return false;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, turn.Token);
            LlmAgentContextStatus measured;
            try { measured = await _history.StatusAsync(turn.ModelId, null, observe: false, linked.Token); }
            catch (OperationCanceledException) when (turn.Token.IsCancellationRequested) { return false; }
            turn.Write(new LlmAgentContextStatusEvent(ThreadId, turn.Id, measured));
            if (measured.NeedsCompaction is not true || Preserve) return false;
            turn.RequestReopen();
            return true;
        }

        /// <summary>
        /// Disposes the inner session, compacts the saved history with this turn's entries so far as the current turn,
        /// and opens the next CLI session with the result as its opening. False when compaction failed and the turn ended.
        /// </summary>
        private async Task<bool> ReopenAsync(ActiveTurn turn)
        {
            CancellationToken token = turn.Token;
            CompactionLimits? limits = _history.Limits(turn.ModelId);
            await CloseInnerAsync();
            LlmAgentContext source = _history.Saved;
            HashSet<string> protectedIds = ConversationCompactor.ProtectedEntries(source, turn.Id, _toolsByName);
            string itemId = Guid.NewGuid().ToString("N");
            CompactionResult? result = null;
            if (limits is not null)
            {
                try
                {
                    result = await _compactor.CompactAsync(source, turn.ModelId, turn.Id, protectedIds, limits,
                        (text, stageToken) => { turn.Write(new LlmAgentActivityEvent(ThreadId, turn.Id, itemId, text)); return ValueTask.CompletedTask; },
                        token);
                }
                catch (Exception error) when (!token.IsCancellationRequested && error is CompactionFailedException or LlmProviderException)
                {
                    LlmUsage? failedUsage = error is CompactionFailedException failed ? failed.Usage : ((LlmProviderException)error).Usage;
                    if (failedUsage is not null)
                    {
                        turn.Usage.Add(failedUsage);
                        turn.Write(new LlmAgentUsageEvent(ThreadId, turn.Id, Guid.NewGuid().ToString("N"), failedUsage, turn.Usage.Totals(null)));
                    }
                    turn.Write(new LlmAgentTurnCompletedEvent(ThreadId, turn.Id, LlmAgentCompletionReason.ProviderFailure, error.Message));
                    return false;
                }
            }
            bool changed = result is { Changed: true };
            LlmAgentContext opening = changed ? result!.Context : source;
            if (changed) turn.Write(new LlmAgentActivityEvent(ThreadId, turn.Id, itemId, "Applying the compacted context"));
            long revision = await OpenAndInstallAsync(opening, changed, token);
            turn.Record.Close();
            if (limits is not null) turn.Write(new LlmAgentActivityEvent(ThreadId, turn.Id, itemId, string.Empty));
            if (changed)
            {
                string requestId = Guid.NewGuid().ToString("N");
                turn.Write(Compacted(turn.Id, requestId, revision, result!));
                if (result!.Usage is not null)
                {
                    turn.Usage.Add(result.Usage);
                    turn.Write(new LlmAgentUsageEvent(ThreadId, turn.Id, requestId, result.Usage, turn.Usage.Totals(null)));
                }
            }
            // The next send is the first step of the new CLI session: its measure is reported and observed.
            LlmAgentContextStatus measured = await _history.StatusAsync(turn.ModelId, turn.PendingInput, observe: true, token);
            turn.Write(new LlmAgentContextStatusEvent(ThreadId, turn.Id, measured));
            return true;
        }

        public async Task<LlmAgentCompactedEvent?> CompactNowAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_retirement is not null, this);
            string selectedModelId;
            lock (_sync) { selectedModelId = _options.ModelId; }
            if (ClaudeCliLlmClient.ResolveCapabilities(_connection, selectedModelId).Agent.SupportsCompactNow
                == InferenceSupport.Unsupported)
                throw new NotSupportedException("The selected connection disables this agent operation.");
            if (!_slot.Wait(0, cancellationToken)) throw new InvalidOperationException("The session is running or paused.");
            try
            {
                await EnsureOpenAsync(cancellationToken);
                LlmAgentContext source = _history.Saved;
                string modelId;
                lock (_sync) { modelId = _options.ModelId; }
                CompactionLimits? limits = _history.Limits(modelId);
                if (source.Entries.Count == 0 || limits is null) return null;
                // No turn is running, so no result is protected and the last turn stays whole.
                string lastTurnId = source.Entries[^1].TurnId;
                CompactionResult result = await _compactor.CompactAsync(source, modelId, lastTurnId,
                    new HashSet<string>(StringComparer.Ordinal), limits, static (_, _) => ValueTask.CompletedTask, cancellationToken);
                if (!result.Changed) return null;
                await CloseInnerAsync();
                long revision = await OpenAndInstallAsync(result.Context, replaceHistory: true, cancellationToken);
                return Compacted(lastTurnId, Guid.NewGuid().ToString("N"), revision, result);
            }
            finally
            {
                _slot.Release();
            }
        }

        private LlmAgentCompactedEvent Compacted(string turnId, string requestId, long revision, CompactionResult result) =>
            result.ToEvent(ThreadId, turnId, requestId, revision);

        public ValueTask DisposeAsync()
        {
            lock (_sync) { _retirement ??= RetireAsync(); }
            return new(_retirement);
        }

        private async Task RetireAsync()
        {
            ActiveTurn? turn;
            ClaudeAgentSession? inner;
            lock (_sync)
            {
                turn = _turn;
                inner = _inner;
                _inner = null;
                _generation = -1;
            }
            if (turn is not null)
            {
                try { await turn.Cancellation.CancelAsync(); }
                catch (ObjectDisposedException) { /* The turn ended as retirement began. */ }
            }
            _resources.Dispose();
            if (inner is not null) await inner.DisposeAsync();
        }

        /// <summary>Opens a CLI session with the saved history when none is open.</summary>
        private async Task EnsureOpenAsync(CancellationToken cancellationToken)
        {
            if (_inner is not null) return;
            _ = await OpenAndInstallAsync(_history.Saved, replaceHistory: false, cancellationToken);
        }

        /// <summary>
        /// Opens a CLI session with <paramref name="opening"/> and makes it current. With <paramref name="replaceHistory"/>,
        /// the opening becomes the next revision of the saved history, only once the session opened. Returns the revision.
        /// </summary>
        private async Task<long> OpenAndInstallAsync(LlmAgentContext opening, bool replaceHistory, CancellationToken cancellationToken,
            (string Id, bool Resume)? identity = null)
        {
            int generation;
            lock (_sync) { generation = ++_nextGeneration; }
            ClaudeAgentSession session = await _owner.OpenInnerAsync(_connectionId, _connection, _options, _workingDirectory,
                _ephemeral, opening, identity, item => Record(generation, item), (steerId, started) => SteerSettled(generation, steerId, started),
                token => BeforeResultsAsync(generation, token),
                cancellationToken);
            bool retired;
            long revision;
            lock (_sync)
            {
                retired = _retirement is not null;
                if (!retired)
                {
                    _inner = session;
                    _generation = generation;
                    _catalog = session.Connection.Catalog;
                }
                revision = retired ? _history.Saved.Revision : _history.Install(opening, replaceHistory);
            }
            if (retired)
            {
                await session.DisposeAsync();
                throw new ObjectDisposedException(nameof(ClaudeConversation));
            }
            return revision;
        }

        private async Task CloseInnerAsync()
        {
            ClaudeAgentSession? inner;
            lock (_sync)
            {
                inner = _inner;
                _inner = null;
                _generation = -1;
            }
            if (inner is not null) await inner.DisposeAsync();
        }

        /// <summary>The Host's tool handler, called with this conversation's thread id and the outer turn id.</summary>
        private Task<LlmToolOutput> HandleToolAsync(LlmAgentToolCall call, CancellationToken cancellationToken)
        {
            string? turnId;
            lock (_sync) { turnId = _turn?.Id; }
            return _toolHandler!(call with { ThreadId = ThreadId, TurnId = turnId ?? call.TurnId }, cancellationToken);
        }

        /// <summary>Records one event of a host turn of the open inner session into the saved history.</summary>
        private void Record(int generation, LlmAgentEvent item)
        {
            lock (_sync)
            {
                if (generation != _generation || _turn is not { } turn) return;
                if (item is LlmAgentUsageEvent)
                {
                    if (_inner?.LastStepUsage is { InputTokens: int input } step)
                        _history.ObserveReport(turn.ModelId, input, step.OutputTokens);
                    return;
                }
                foreach (LlmAgentContextEntry entry in turn.Record.Apply(item)) _history.Upsert(entry);
            }
        }

        /// <summary>
        /// A steer of the open inner session settled. One that started is recorded at this point of the turn, where the
        /// CLI took it in; one the CLI ended unstarted is dropped.
        /// </summary>
        private void SteerSettled(int generation, string steerId, bool started)
        {
            lock (_sync)
            {
                if (generation != _generation || _turn is not { } turn || !turn.Steers.Remove(steerId, out LlmChatMessage? message)) return;
                if (started) _history.Upsert(new LlmAgentContextEntry(turn.Record.Steer(), turn.Id, message));
            }
        }

        /// <summary>
        /// Records the turn's steers the closing inner session never started, so the next CLI session receives them in
        /// its opening.
        /// </summary>
        private void RecordUnstartedSteers(ActiveTurn turn)
        {
            lock (_sync)
            {
                foreach (LlmChatMessage message in turn.Steers.Values)
                    _history.Upsert(new LlmAgentContextEntry(turn.Record.Steer(), turn.Id, message));
                turn.Steers.Clear();
            }
        }

        private void RecordInput(ActiveTurn turn, LlmChatMessage input)
        {
            lock (_sync)
            {
                _history.Upsert(new LlmAgentContextEntry(turn.Record.InputId, turn.Id, input));
                turn.PendingInput = null;
            }
        }

        /// <summary>The corrected size of the whole request a CLI session opened with <paramref name="context"/> receives.</summary>
        private ValueTask<long> MeasureForCompactionAsync(LlmAgentContext context, CancellationToken cancellationToken)
        {
            string modelId;
            LlmChatMessage[] added;
            lock (_sync)
            {
                modelId = _turn?.ModelId ?? _options.ModelId;
                added = _turn?.PendingInput is { } input ? [input] : [];
            }
            return _history.MeasureAsync(context, modelId, added, cancellationToken);
        }

        /// <summary>The refused request's local size and the sizes the refusal names teach the model's rate and limit.</summary>
        private async Task ObserveRefusalAsync(ActiveTurn turn, string? detail)
        {
            LlmChatMessage[] added;
            lock (_sync) { added = turn.PendingInput is { } input ? [input] : []; }
            long estimate = await _history.EstimateAsync(_history.Saved, turn.ModelId, added, turn.Token);
            (long? reportedTokens, long? reportedLimit) = ReadRefusalSizes(detail);
            _owner.Learned.ObserveRefusal(_connectionId, turn.ModelId, estimate, reportedTokens, reportedLimit,
                ClaudeModelLimits.Get(turn.ModelId).ContextTokens);
        }

        /// <summary>Sends one summary request in print mode on the conversation's connection and returns its result.</summary>
        private Task<LlmChatResult> SideRequestAsync(LlmChatRequest request, CancellationToken cancellationToken) =>
            SummaryRequest.ReadAsync(_owner.Chat.StreamAsync(_connection, request, cancellationToken), cancellationToken);

        /// <summary>The corrected size of a text sent as one user message.</summary>
        private long? EstimateText(string text)
        {
            string modelId;
            lock (_sync) { modelId = _turn?.ModelId ?? _options.ModelId; }
            return _history.EstimateText(modelId, text);
        }

        /// <summary>The model's window and output limit: the open session's catalog entry when it has a size, else the
        /// published table.</summary>
        private LlmModel Model(string modelId)
        {
            if (Volatile.Read(ref _catalog)?.FirstOrDefault(model => model.Id == modelId && model.MaxContextTokens is not null) is { } listed)
                return listed;
            ClaudeModelLimit limit = ClaudeModelLimits.Get(modelId);
            return new(modelId, modelId) { MaxContextTokens = limit.ContextTokens, MaxOutputTokens = limit.OutputTokens };
        }

        /// <summary>One running outer turn: its id, record, stream and cancellation, and the inner turn it forwards.</summary>
        private sealed class ActiveTurn(string id, TurnRecord record, ChannelWriter<LlmAgentEvent> writer,
            CancellationTokenSource cancellation)
        {
            private int _reopen;

            public string Id { get; } = id;
            public TurnRecord Record { get; } = record;
            public ChannelWriter<LlmAgentEvent> Writer { get; } = writer;
            public CancellationTokenSource Cancellation { get; } = cancellation;
            public CancellationToken Token { get; } = cancellation.Token;
            public string ModelId { get; set; } = string.Empty;
            public string? InnerTurnId { get; set; }
            // Steers sent in this turn and not yet settled, by id, as the saved history records them; _sync guards it.
            public Dictionary<string, LlmChatMessage> Steers { get; } = new(StringComparer.Ordinal);
            public bool Interrupted { get; set; }
            public bool Refused { get; set; }
            // The turn's input before it is recorded; measures and compaction count it.
            public LlmChatMessage? PendingInput { get; set; }
            public TurnUsage Usage { get; } = new();

            public void Write(LlmAgentEvent item) => Writer.TryWrite(item);

            public void RequestReopen() => Volatile.Write(ref _reopen, 1);

            public bool TakeReopen() => Interlocked.Exchange(ref _reopen, 0) == 1;
        }
    }
}
