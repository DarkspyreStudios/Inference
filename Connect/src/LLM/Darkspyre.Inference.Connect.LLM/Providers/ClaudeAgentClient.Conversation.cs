using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// The Claude conversation the Host holds. It owns the saved history and the current CLI session, measures before
    /// each send, and compacts by opening a new CLI session with the compacted history as its opening.
    /// </summary>
    private sealed partial class ClaudeConversation : ILlmAgentSession
    {
        private const string ContinueLine =
            "Continue from where you stopped. The tool results above are complete; do not repeat finished tool calls.";

        private readonly ClaudeAgentClient _owner;
        private readonly string _connectionId;
        private readonly LlmConnection _connection;
        private readonly string _workingDirectory;
        private readonly bool _ephemeral;
        private readonly LlmAgentToolHandler? _toolHandler;
        private readonly Dictionary<string, LlmAgentToolDefinition> _toolsByName;
        private readonly LlmAgentExecutionOptions _execution;
        private readonly ConversationCompactor _compactor;
        private readonly NoAgentResources _resources = new(LlmAgentResourceStopStatus.Unknown);
        // One turn or one compaction at a time.
        private readonly SemaphoreSlim _slot = new(1, 1);
        // Guards everything below; the inner sessions call Record and BeforeResultsAsync from their own threads.
        private readonly Lock _sync = new();
        // ModelId follows the model the turns select, so a reopened CLI starts on it.
        private LlmAgentSessionOptions _options;
        private LlmAgentContext _saved;
        // Null when no CLI session is open: the next turn or compaction opens one with the saved history.
        private ClaudeAgentSession? _inner;
        private IReadOnlyList<LlmModel>? _catalog;
        // The open inner session's number; events of any other session are not recorded.
        private int _generation = -1;
        private int _nextGeneration;
        private ActiveTurn? _turn;
        // The last step's reported input and output, and the record sequence at that report.
        private long? _reportedTokens;
        private long _reportedMark;
        private long _sequence;
        private readonly Dictionary<string, long> _recordedAt = new(StringComparer.Ordinal);
        // The local estimate of the first step of the open inner session, observed against its reported input.
        private long? _firstStepEstimate;
        private Task? _retirement;

        internal ClaudeConversation(ClaudeAgentClient owner, string connectionId, LlmConnection connection,
            LlmAgentSessionOptions options, string workingDirectory, bool ephemeral, LlmAgentContext history)
        {
            _owner = owner;
            _connectionId = connectionId;
            _connection = connection;
            _workingDirectory = workingDirectory;
            _ephemeral = ephemeral;
            _toolHandler = options.ToolHandler;
            // The first definition of a duplicated name is the one used.
            _toolsByName = ClientTools.Functions(options.Tools).DistinctBy(tool => tool.Name, StringComparer.Ordinal)
                .ToDictionary(tool => tool.Name, StringComparer.Ordinal);
            _execution = options.ExecutionOptions ?? new();
            _options = options with { Context = null, ToolHandler = options.ToolHandler is null ? null : HandleToolAsync };
            _saved = history;
            _compactor = new(MeasureForCompactionAsync, SideRequestAsync, EstimateText, options.CallerServices?.Transcript);
        }

        /// <summary>The first CLI session's id, fixed for this conversation.</summary>
        public string ThreadId { get; private set; } = string.Empty;

        public string SessionId { get; private set; } = string.Empty;

        public LlmCompactionResponsibility CompactionResponsibility => LlmCompactionResponsibility.Provider;

        private bool Preserve => _execution.ContextPolicy.PreserveSuppliedContext;

        /// <summary>Opens the first CLI session with the saved history as its opening. The first turn's decision point
        /// (a) measures and compacts it.</summary>
        internal async Task OpenAsync(CancellationToken cancellationToken)
        {
            await EnsureOpenAsync(cancellationToken);
            ThreadId = _inner!.ThreadId;
            SessionId = _options.SessionId ?? ThreadId;
        }

        public Task<LlmAgentCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            _inner is { } inner ? inner.GetCapabilitiesAsync(cancellationToken) : Task.FromResult(NativeAgentContract.Claude);

        public Task SteerAsync(string turnId, IReadOnlyList<LlmAgentContent> input, CancellationToken cancellationToken = default) =>
            _inner?.SteerAsync(turnId, input, cancellationToken) ?? throw new NotSupportedException(ClaudeAgentSession.SteeringUnsupported);

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
            return (await MeasureAsync(modelId, input, observe: false, cancellationToken)).Status;
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
                    // Decision point (b) stopped the inner turn with the step's last result unwritten.
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
                        RemoveEntry(turn.Record.InputId);
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
                            turn.InnerTotals = usage.RunTotals;
                            turn.Write(usage with { ThreadId = ThreadId, TurnId = turn.Id, RunTotals = turn.Totals(usage.RunTotals) });
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
                turn.FoldInnerTotals();
            }
            if (completion is null && !turn.TakeReopen())
                throw new LlmProviderException("Claude CLI ended the turn without a result.", LlmTransport.AnthropicClaudeCli,
                    providerCode: "claude_cli_agent_protocol");
            return completion;
        }

        /// <summary>Decision point (a): measures, reports and compacts at or above the trigger. False when the turn ended.</summary>
        private async Task<bool> DecideAsync(ActiveTurn turn)
        {
            Measurement measured = await MeasureAsync(turn.ModelId, turn.PendingInput, observe: true, turn.Token);
            turn.Write(new LlmAgentContextStatusEvent(ThreadId, turn.Id, measured.Status));
            return measured.Status.NeedsCompaction is not true || Preserve || await ReopenAsync(turn);
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
            Measurement measured;
            try { measured = await MeasureAsync(turn.ModelId, null, observe: false, linked.Token); }
            catch (OperationCanceledException) when (turn.Token.IsCancellationRequested) { return false; }
            turn.Write(new LlmAgentContextStatusEvent(ThreadId, turn.Id, measured.Status));
            if (measured.Status.NeedsCompaction is not true || Preserve) return false;
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
            CompactionLimits? limits = Limits(turn.ModelId);
            await CloseInnerAsync();
            LlmAgentContext source;
            HashSet<string> protectedIds;
            lock (_sync)
            {
                source = _saved;
                protectedIds = ConversationCompactor.ProtectedEntries(source, turn.Id, _toolsByName);
            }
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
                        turn.AddUsage(failedUsage);
                        turn.Write(new LlmAgentUsageEvent(ThreadId, turn.Id, Guid.NewGuid().ToString("N"), failedUsage, turn.Totals(null)));
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
                    turn.AddUsage(result.Usage);
                    turn.Write(new LlmAgentUsageEvent(ThreadId, turn.Id, requestId, result.Usage, turn.Totals(null)));
                }
            }
            // The next send is the first step of the new CLI session: its measure is reported and observed.
            Measurement measured = await MeasureAsync(turn.ModelId, turn.PendingInput, observe: true, token);
            turn.Write(new LlmAgentContextStatusEvent(ThreadId, turn.Id, measured.Status));
            return true;
        }

        public async Task<LlmAgentCompactedEvent?> CompactNowAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_retirement is not null, this);
            if (!_slot.Wait(0, cancellationToken)) throw new InvalidOperationException("The session is running or paused.");
            try
            {
                await EnsureOpenAsync(cancellationToken);
                LlmAgentContext source;
                string modelId;
                lock (_sync)
                {
                    source = _saved;
                    modelId = _options.ModelId;
                }
                CompactionLimits? limits = Limits(modelId);
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
            new(ThreadId, turnId, requestId, revision, result.DroppedMessageCount, result.Usage)
            {
                ShortenedToolOutputCount = result.ShortenedToolOutputCount,
                RemovedImageCount = result.RemovedImageCount
            };

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
            LlmAgentContext saved;
            lock (_sync) { saved = _saved; }
            _ = await OpenAndInstallAsync(saved, replaceHistory: false, cancellationToken);
        }

        /// <summary>
        /// Opens a CLI session with <paramref name="opening"/> and makes it current. With <paramref name="replaceHistory"/>,
        /// the opening becomes the next revision of the saved history, only once the session opened. Returns the revision.
        /// </summary>
        private async Task<long> OpenAndInstallAsync(LlmAgentContext opening, bool replaceHistory, CancellationToken cancellationToken)
        {
            int generation;
            lock (_sync) { generation = ++_nextGeneration; }
            ClaudeAgentSession session = await _owner.OpenInnerAsync(_connectionId, _connection, _options, _workingDirectory,
                _ephemeral, opening, item => Record(generation, item), token => BeforeResultsAsync(generation, token),
                cancellationToken);
            bool retired;
            long revision;
            lock (_sync)
            {
                retired = _retirement is not null;
                if (!retired)
                {
                    if (replaceHistory) _saved = opening with { Revision = checked(_saved.Revision + 1) };
                    _inner = session;
                    _generation = generation;
                    _catalog = session.Connection.Catalog;
                    _reportedTokens = null;
                    _recordedAt.Clear();
                    _firstStepEstimate = null;
                }
                revision = _saved.Revision;
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
                    if (_inner?.LastStepUsage is not { InputTokens: int input } step) return;
                    _reportedTokens = (long)input + (step.OutputTokens ?? 0);
                    _reportedMark = _sequence;
                    if (_firstStepEstimate is long estimate && Limits(turn.ModelId) is { } limits)
                        _owner.Learned.ObserveReport(_connection.Transport, turn.ModelId, estimate, input, limits.UsableWindow);
                    _firstStepEstimate = null;
                    return;
                }
                foreach (LlmAgentContextEntry entry in turn.Record.Apply(item)) Upsert(entry);
            }
        }

        private void RecordInput(ActiveTurn turn, LlmChatMessage input)
        {
            lock (_sync)
            {
                Upsert(new LlmAgentContextEntry(turn.Record.InputId, turn.Id, input));
                turn.PendingInput = null;
            }
        }

        /// <summary>Replaces the entry with the same id in place, or appends it. Caller holds _sync.</summary>
        private void Upsert(LlmAgentContextEntry entry)
        {
            var entries = _saved.Entries.ToList();
            int index = entries.FindIndex(existing => existing.Id == entry.Id);
            if (index >= 0) entries[index] = entry;
            else entries.Add(entry);
            _saved = _saved with { Entries = entries, Revision = checked(_saved.Revision + 1) };
            _recordedAt[entry.Id] = ++_sequence;
        }

        private void RemoveEntry(string id)
        {
            lock (_sync)
            {
                _saved = _saved with { Entries = [.. _saved.Entries.Where(entry => entry.Id != id)], Revision = checked(_saved.Revision + 1) };
                _recordedAt.Remove(id);
            }
        }

        /// <summary>
        /// The corrected size of the next send. With a reported step, its input and output plus the entries recorded
        /// after that report and the new input; otherwise the whole request the CLI would receive.
        /// </summary>
        private async Task<Measurement> MeasureAsync(string modelId, LlmChatMessage? input, bool observe,
            CancellationToken cancellationToken)
        {
            LlmAgentContext saved;
            long? reported;
            LlmChatMessage[] after;
            lock (_sync)
            {
                saved = _saved;
                reported = _reportedTokens;
                after = [.. saved.Entries.Where(entry => _recordedAt.TryGetValue(entry.Id, out long at) && at > _reportedMark)
                    .Select(entry => entry.Message)];
            }
            LlmChatMessage[] added = input is null ? [] : [input];
            var resources = await ResolveAsync(saved, added, cancellationToken);
            ContextEstimate estimate;
            if (reported is long tokens)
            {
                long local = Local(modelId, null, [.. after, .. added], resources);
                estimate = new(tokens + Correct(modelId, local), IncludesProviderReport: true);
            }
            else
            {
                long local = Local(modelId, _options.Instructions, [.. saved.Entries.Select(entry => entry.Message), .. added],
                    resources);
                if (observe) lock (_sync) { _firstStepEstimate = local; }
                estimate = new(Correct(modelId, local));
            }
            LlmModel model = Model(modelId);
            LlmAgentContextPolicy policy = ManagedAgentPolicy.Normalize(_execution, model).ContextPolicy;
            return new(ManagedAgentPolicy.Status(modelId, saved.Revision, estimate, policy, model,
                _owner.Learned.Limit(_connection.Transport, modelId)));
        }

        /// <summary>The corrected size of the whole request a CLI session opened with <paramref name="context"/> receives.</summary>
        private async ValueTask<long> MeasureForCompactionAsync(LlmAgentContext context, CancellationToken cancellationToken)
        {
            string modelId;
            LlmChatMessage[] added;
            lock (_sync)
            {
                modelId = _turn?.ModelId ?? _options.ModelId;
                added = _turn?.PendingInput is { } input ? [input] : [];
            }
            var resources = await ResolveAsync(context, added, cancellationToken);
            return Correct(modelId, Local(modelId, _options.Instructions,
                [.. context.Entries.Select(entry => entry.Message), .. added], resources));
        }

        /// <summary>The refused request's local size and the sizes the refusal names teach the model's rate and limit.</summary>
        private async Task ObserveRefusalAsync(ActiveTurn turn, string? detail)
        {
            LlmAgentContext saved;
            LlmChatMessage[] added;
            lock (_sync)
            {
                saved = _saved;
                added = turn.PendingInput is { } input ? [input] : [];
            }
            var resources = await ResolveAsync(saved, added, turn.Token);
            long estimate = Local(turn.ModelId, _options.Instructions, [.. saved.Entries.Select(entry => entry.Message), .. added],
                resources);
            Match sizes = RefusalSizes().Match(detail ?? string.Empty);
            long? reportedTokens = sizes.Success ? long.Parse(sizes.Groups[1].Value, CultureInfo.InvariantCulture) : null;
            long? reportedLimit = sizes.Success ? long.Parse(sizes.Groups[2].Value, CultureInfo.InvariantCulture) : null;
            _owner.Learned.ObserveRefusal(_connection.Transport, turn.ModelId, estimate, reportedTokens, reportedLimit,
                ClaudeModelLimits.Get(turn.ModelId).ContextTokens);
        }

        [GeneratedRegex(@"(\d+) tokens > (\d+)")]
        private static partial Regex RefusalSizes();

        /// <summary>Sends one summary request in print mode on the conversation's connection and returns its result.</summary>
        private async Task<LlmChatResult> SideRequestAsync(LlmChatRequest request, CancellationToken cancellationToken)
        {
            LlmChatResult? result = null;
            await foreach (LlmStreamEvent item in _owner.Chat.StreamAsync(_connection, request, cancellationToken).WithCancellation(cancellationToken))
                if (item is LlmChatCompletionEvent completion) result = completion.Result;
            return result ?? throw new InvalidOperationException("The summary request ended without completion.");
        }

        /// <summary>The corrected size of a text sent as one user message.</summary>
        private long? EstimateText(string text)
        {
            string modelId;
            lock (_sync) { modelId = _turn?.ModelId ?? _options.ModelId; }
            return Correct(modelId, Local(modelId, null, [new("user", text)], ResourceSizes.None));
        }

        private long Correct(string modelId, long local) => _owner.Learned.Correct(_connection.Transport, modelId, local);

        /// <summary>
        /// The local count of a request with these messages, as the Claude CLI client counts it. The chat route refuses
        /// tool definitions, so the session tools are framing the learned correction covers.
        /// </summary>
        private long Local(string modelId, string? instructions, IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyDictionary<InferenceResourceReference, InferenceResourceDescriptor?> resources)
        {
            if (messages.Count == 0 && string.IsNullOrWhiteSpace(instructions)) return 0;
            return _owner.Chat.EstimateInputTokens(_connection, new LlmChatRequest
            {
                ModelId = modelId,
                Instructions = instructions,
                Messages = messages
            }, resources)?.Tokens ?? 0;
        }

        private async Task<IReadOnlyDictionary<InferenceResourceReference, InferenceResourceDescriptor?>> ResolveAsync(
            LlmAgentContext context, IReadOnlyList<LlmChatMessage> added, CancellationToken cancellationToken)
        {
            InferenceResourceReference[] references = [.. context.Entries.SelectMany(entry => ResourceSizes.References(entry.Message))
                .Concat(context.SelectedResources).Concat(added.SelectMany(ResourceSizes.References))];
            return _options.CallerServices?.Resources is { } access && references.Length > 0
                ? await ResourceSizes.ResolveAsync(access, references, cancellationToken)
                : ResourceSizes.None;
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

        private CompactionLimits? Limits(string modelId)
        {
            LlmModel model = Model(modelId);
            return CompactionLimits.For(model.MaxContextTokens, _owner.Learned.Limit(_connection.Transport, modelId),
                model.MaxOutputTokens, ManagedAgentPolicy.Normalize(_execution, model).ContextPolicy);
        }

        private sealed record Measurement(LlmAgentContextStatus Status);

        /// <summary>One running outer turn: its id, record, stream and cancellation, and the inner turn it forwards.</summary>
        private sealed class ActiveTurn(string id, TurnRecord record, ChannelWriter<LlmAgentEvent> writer,
            CancellationTokenSource cancellation)
        {
            private readonly Lock _usage = new();
            private LlmUsage? _base;
            private bool _hasBase;
            private LlmUsage? _innerTotals;
            private bool _hasInnerTotals;
            private int _reopen;

            public string Id { get; } = id;
            public TurnRecord Record { get; } = record;
            public ChannelWriter<LlmAgentEvent> Writer { get; } = writer;
            public CancellationTokenSource Cancellation { get; } = cancellation;
            public CancellationToken Token { get; } = cancellation.Token;
            public string ModelId { get; set; } = string.Empty;
            public string? InnerTurnId { get; set; }
            public bool Interrupted { get; set; }
            public bool Refused { get; set; }
            // The turn's input before it is recorded; measures and compaction count it.
            public LlmChatMessage? PendingInput { get; set; }

            public void Write(LlmAgentEvent item) => Writer.TryWrite(item);

            public void RequestReopen() => Volatile.Write(ref _reopen, 1);

            public bool TakeReopen() => Interlocked.Exchange(ref _reopen, 0) == 1;

            public LlmUsage? InnerTotals
            {
                set { lock (_usage) { _innerTotals = value; _hasInnerTotals = true; } }
            }

            /// <summary>The turn's totals so far plus an inner session's own totals.</summary>
            public LlmUsage? Totals(LlmUsage? inner)
            {
                lock (_usage)
                {
                    if (inner is null) return _hasBase ? _base : null;
                    return _hasBase ? UsageTotals.Add(_base, inner, first: false) : inner;
                }
            }

            public void AddUsage(LlmUsage? usage)
            {
                lock (_usage)
                {
                    _base = UsageTotals.Add(_base, usage, first: !_hasBase);
                    _hasBase = true;
                }
            }

            /// <summary>Adds an ended inner session's final totals to the turn's totals.</summary>
            public void FoldInnerTotals()
            {
                LlmUsage? inner;
                lock (_usage)
                {
                    if (!_hasInnerTotals) return;
                    inner = _innerTotals;
                    _hasInnerTotals = false;
                    _innerTotals = null;
                }
                AddUsage(inner);
            }
        }

        /// <summary>
        /// One turn's entries in the shape the Host's native transcript uses: the input as one user message, each model
        /// step as one assistant message with its calls, followed by one user message with their results in call order.
        /// Entry ids are <c>{turnId}-{n}</c>, counted from 0 within the turn.
        /// </summary>
        private sealed class TurnRecord(string turnId, bool recordsCalls)
        {
            private int _next = 1;
            private Step? _step;

            public string InputId { get; } = $"{turnId}-0";

            /// <summary>The turn recorded a model step.</summary>
            public bool HasSteps { get; private set; }

            /// <summary>The next arrival starts a new step: the current one is in a compacted history.</summary>
            public void Close() => _step = null;

            /// <summary>The entries an event changed.</summary>
            public IReadOnlyList<LlmAgentContextEntry> Apply(LlmAgentEvent item)
            {
                switch (item)
                {
                    case LlmAgentMessageCompletedEvent { Text.Length: > 0 } message:
                        {
                            Step step = Open();
                            step.Text.Add(message.Text);
                            return step.Entries(turnId);
                        }
                    case LlmAgentToolEvent { Status: "inProgress" } call when recordsCalls:
                        {
                            Step step = Open();
                            if (step.Calls.Any(existing => existing.Id == call.ItemId)) return [];
                            step.ResultsId ??= NextId();
                            step.Calls.Add(new LlmToolCall(call.ItemId, call.Tool, call.Arguments.GetRawText()));
                            return step.Entries(turnId);
                        }
                    case LlmAgentToolEvent ended when recordsCalls && _step is { } current
                        && current.Calls.Any(existing => existing.Id == ended.ItemId):
                        current.Results[ended.ItemId] = ended.Output;
                        current.HasResult = true;
                        return current.Entries(turnId);
                    default:
                        return [];
                }
            }

            /// <summary>The current step; an arrival after one of its results starts the next.</summary>
            private Step Open()
            {
                if (_step is null || _step.HasResult) _step = new Step(NextId());
                HasSteps = true;
                return _step;
            }

            private string NextId() => $"{turnId}-{_next++}";

            private sealed class Step(string assistantId)
            {
                public List<string> Text { get; } = [];
                public List<LlmToolCall> Calls { get; } = [];
                public Dictionary<string, LlmToolOutput?> Results { get; } = new(StringComparer.Ordinal);
                public string? ResultsId { get; set; }
                public bool HasResult { get; set; }

                public IReadOnlyList<LlmAgentContextEntry> Entries(string turnId)
                {
                    var assistant = new LlmAgentContextEntry(assistantId, turnId,
                        new LlmChatMessage("assistant", string.Join('\n', Text)) { ToolCalls = [.. Calls] });
                    if (ResultsId is null) return [assistant];
                    return [assistant, new LlmAgentContextEntry(ResultsId, turnId, new LlmChatMessage("user", string.Empty)
                    {
                        ToolResults = [.. Calls.Select(call => new LlmToolResult(call.Id,
                            Results.TryGetValue(call.Id, out LlmToolOutput? output) && output is not null ? output
                                : LlmToolOutput.FromText("No result was delivered for this call.", isError: true)))]
                    })];
                }
            }
        }
    }
}
