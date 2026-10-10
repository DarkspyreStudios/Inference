using Darkspyre.Inference.Common;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>One live context owner. No database, host filesystem tools, or durable run recovery.</summary>
internal sealed class ManagedAgentSession : ILlmAgentSession
{
    private readonly object _sync = new();
    private readonly LlmOperationLifetime _resourceCallbacks = new();
    private readonly bool _supportsResources;
    private InferenceResourceSelectionVersion? _resourceScope;
    private readonly LlmConnection _connection;
    private readonly LlmAgentSessionOptions _options;
    private readonly LlmAgentExecutionOptions _execution;
    private readonly IReadOnlyList<LlmModel> _models;
    private readonly ILlmContextEstimator _estimator;
    private readonly Func<string, LlmProviderCapabilities> _capabilities;
    private readonly Func<LlmChatRequest, CancellationToken, IAsyncEnumerable<LlmStreamEvent>> _stream;
    private readonly Dictionary<string, LlmAgentToolDefinition> _tools;
    private LlmAgentToolHandler? _toolHandler;
    private LlmAgentCallerServices? _callerServices;
    private Func<LlmChatRequest, CancellationToken, Task<ProviderCompactionResult>>? _compactor;
    private readonly bool _automaticProviderCompaction;
    private IReadOnlyList<LlmHostedTool> _hostedTools;
    private Task? _retirement;
    private LlmAgentContext _context;
    // Messages whose continuation state was left out at start; reported once, on the first turn.
    private int _startOmittedContinuationStates;
    private LiveRun? _run;
    private bool _segmentActive;
    private bool _disposed;
    private Task _producer = Task.CompletedTask;
    private LlmAgentContextStatus? _latestStatus;

    // Managed runs host-executed tools; their processes belong to the host, not this runtime.
    private readonly NoAgentResources _resources = new(LlmAgentResourceStopStatus.Unsupported);

    internal static async Task<ManagedAgentSession> CreateAsync(LlmConnection connection, LlmAgentSessionOptions options,
        IReadOnlyList<LlmModel> models, ILlmContextEstimator estimator,
        Func<LlmChatRequest, CancellationToken, IAsyncEnumerable<LlmStreamEvent>> stream,
        Func<string, LlmProviderCapabilities> capabilities, Action? openingFence = null,
        Func<LlmChatRequest, CancellationToken, Task<ProviderCompactionResult>>? compactor = null,
        bool automaticProviderCompaction = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        openingFence?.Invoke();
        options = ClientTools.Snapshot(options);
        bool supportsResources = estimator is ILlmResourcePreparationClient { CanPrepareResources: true }
            && (capabilities(options.ModelId).SupportsPromptDocuments != InferenceSupport.Unsupported);
        cancellationToken.ThrowIfCancellationRequested();
        openingFence?.Invoke();
        var session = new ManagedAgentSession(connection, options, models, estimator, stream, capabilities,
            supportsResources, compactor, automaticProviderCompaction);
        try
        {
            using var operation = session._resourceCallbacks.Enter(cancellationToken);
            void Fence() { session.FenceResourceCallback(operation.Token); openingFence?.Invoke(); }
            if (options.CallerServices?.Resources is { } access)
            {
                InferenceResourceSelection selection = await LlmResourcePreparation.ReadSelectionAsync(access, Fence, operation.Token).ConfigureAwait(false);
                LlmAgentContext context = session._context;
                if (options.Context is null)
                    context = WithSelection(context, selection);
                await LlmResourcePreparation.ValidateContextAsync(context, selection, access, Fence, operation.Token).ConfigureAwait(false);
                Fence();
                session._resourceScope = new(selection.WorkspaceId, selection.SessionId, selection.Revision);
                session._context = context;
            }
            Fence();
            return session;
        }
        catch (Exception original)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            ExceptionDispatchInfo.Capture(original).Throw();
            throw;
        }
    }

    private ManagedAgentSession(LlmConnection connection, LlmAgentSessionOptions options,
        IReadOnlyList<LlmModel> models, ILlmContextEstimator estimator,
        Func<LlmChatRequest, CancellationToken, IAsyncEnumerable<LlmStreamEvent>> stream,
        Func<string, LlmProviderCapabilities> capabilities, bool supportsResources,
        Func<LlmChatRequest, CancellationToken, Task<ProviderCompactionResult>>? compactor,
        bool automaticProviderCompaction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelId);
        AgentResourceOptions.Validate(ManagedAgentPolicy.Capabilities, allowRuntimeBackgroundWork: false, options.ResourceOutputRetentionBytes);
        _supportsResources = supportsResources;
        _compactor = compactor;
        _automaticProviderCompaction = automaticProviderCompaction;
        if (automaticProviderCompaction && compactor is null)
            throw new ArgumentException("Automatic provider compaction requires a compactor.");
        _connection = connection;
        _models = Array.AsReadOnly(models.ToArray());
        _estimator = estimator;
        _stream = stream;
        _capabilities = capabilities;
        HostedTools.Validate(options.HostedTools, options.MaxHostedTurns, capabilities(options.ModelId).HostedTools);
        ClientTools.ValidateManagedOpening(options, capabilities(options.ModelId), supportsResources);
        _hostedTools = options.HostedTools;
        _execution = ManagedAgentPolicy.Normalize(options.ExecutionOptions ?? new(), Model(options.ModelId));
        _toolHandler = options.ToolHandler;
        _callerServices = options.CallerServices;
        _options = options with
        {
            ToolHandler = null,
            CallerServices = null,
            HostedTools = [],
            Context = null
        };
        // The first definition of a duplicated name is the one used.
        _tools = _options.Tools.OfType<LlmAgentToolDefinition>().DistinctBy(tool => tool.Name, StringComparer.Ordinal)
            .ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        (_context, _startOmittedContinuationStates) = ManagedAgentContext.Import(
            options.Context ?? new(), connection, options.ModelId, supportsResources);
        ThreadId = options.ThreadId ?? Guid.NewGuid().ToString();
        SessionId = options.SessionId ?? ThreadId;
        ArgumentException.ThrowIfNullOrWhiteSpace(ThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SessionId);
        _ = Measure(_context, BaseTurn());
    }

    public string ThreadId { get; }
    public string SessionId { get; }

    public LlmCompactionResponsibility CompactionResponsibility => LlmCompactionResponsibility.Host;

    public Task<LlmAgentCompactedEvent?> CompactNowAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("The selected model does not support compacting now.");
    }

    public Task<LlmAgentCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            return Task.FromResult(Capabilities);
        }
    }

    public Task<LlmAgentContextStatus> GetContextStatusAsync(LlmAgentTurnRequest? prospectiveTurn = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireCapability(Capabilities.SupportsContextStatus);
            if (prospectiveTurn is not null)
            {
                RequireIdle();
                var turn = FreezeTurn(prospectiveTurn);
                var preview = Append(_context, new("preview", "preview", UserMessage(turn)), increment: false);
                return Task.FromResult(Measure(preview, turn));
            }
            return Task.FromResult(_segmentActive && _latestStatus is not null
                ? _latestStatus : Measure(_run is null ? _context : WithQueuedSteering(_context, _run), _run?.Turn ?? BaseTurn()));
        }
    }

    public Task<LlmRequestAccounting> MeasureContextAsync(LlmAgentContext proposedContext,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireCapability(Capabilities.SupportsContextStatus);
            if (_segmentActive) { throw new InvalidOperationException("Proposal accounting requires idle or paused execution."); }
            var turn = _run?.Turn ?? BaseTurn();
            var proposal = ManagedAgentContext.Import(proposedContext, _connection, turn.ModelId!, _supportsResources).Context;
            if (_run is not null) { proposal = WithQueuedSteering(proposal, _run); }
            ContextEstimate? used = EstimateInputTokens(proposal, turn);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ManagedAgentPolicy.Account(turn.ModelId!, used,
                _execution.ContextPolicy, Model(turn.ModelId!), learnedLimit: null) with
            {
                ContextRevision = _context.Revision,
                OutputLimitEnforcement = _capabilities(turn.ModelId!).OutputLimitEnforcement
            });
        }
    }

    public async Task<LlmAgentContext> CaptureContextAsync(LlmAgentContextCaptureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = _resourceCallbacks.Enter(cancellationToken);
        cancellationToken = operation.Token;
        LlmAgentContext capture;
        IInferenceResourceAccess? access;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireCapability(Capabilities.SupportsContextCapture);
            if (_segmentActive) { throw new InvalidOperationException("Capture requires settled execution."); }
            capture = ManagedAgentContext.Copy(_context, options?.IncludeAttachmentData == true);
            access = _callerServices?.Resources;
        }
        if (access is not null)
        {
            InferenceResourceSelection selection = await ReadAdmittedSelectionAsync(access, cancellationToken).ConfigureAwait(false);
            capture = WithSelection(capture, selection);
            await LlmResourcePreparation.ValidateContextAsync(capture, selection, access,
                () => FenceResourceCallback(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        lock (_sync)
        {
            FenceResourceCallback(cancellationToken);
            if (_segmentActive || _context.Revision != capture.Revision)
                throw new InvalidOperationException("Execution changed while the context capture was admitted.");
            return capture;
        }
    }

    public async Task<LlmAgentContext> CompactWithProviderAsync(LlmProviderCompactionRequest request,
        CancellationToken cancellationToken = default)
    {
        using var operation = _resourceCallbacks.Enter(cancellationToken);
        cancellationToken = operation.Token;
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(request.ExpectedContextRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(request.RetainedUserTokens);
        LlmAgentContext source;
        LlmAgentTurnRequest turn;
        LiveRun? run;
        HashSet<string> protectedIds;
        IInferenceResourceAccess? access;
        Func<LlmChatRequest, CancellationToken, Task<ProviderCompactionResult>> compactor;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireCapability(_automaticProviderCompaction ? InferenceSupport.Unsupported : Capabilities.SupportsProviderCompaction);
            compactor = _compactor ?? throw new NotSupportedException("This session has no provider compactor.");
            run = RequireCompactionPosition(request);
            source = ManagedAgentContext.Copy(_context, includeAttachmentData: true);
            turn = run?.Turn ?? BaseTurn();
            protectedIds = run is null ? new(StringComparer.Ordinal) : new(run.Protected, StringComparer.Ordinal);
            access = _callerServices?.Resources;
        }
        await ValidateCompactionResourcesAsync(source, access, cancellationToken).ConfigureAwait(false);
        FenceResourceCallback(cancellationToken);
        lock (_sync)
        {
            FenceResourceCallback(cancellationToken);
            if (!ReferenceEquals(run, RequireCompactionPosition(request)))
                throw new InvalidOperationException("The run changed during compaction admission.");
        }
        ProviderCompactionResult result = await compactor(ClientTools.Snapshot(Request(source, turn)), cancellationToken).ConfigureAwait(false);
        FenceResourceCallback(cancellationToken);
        LlmAgentContext proposal = ProviderContext(source, turn, result);
        var retained = new List<LlmAgentContextEntry>();
        foreach (LlmAgentContextEntry entry in source.Entries.Reverse())
        {
            if (protectedIds.Contains(entry.Id) || entry.Message.Role != "user" || entry.Message.ToolResults.Count > 0) continue;
            var candidate = source with { Entries = new[] { entry }.Concat(retained).ToArray() };
            ContextEstimate? cost = EstimateInputTokens(candidate, turn);
            if (cost is null || cost.Value.Tokens > request.RetainedUserTokens) break;
            retained.Insert(0, entry);
        }
        proposal = ManagedAgentContext.Import(proposal with
        {
            Entries = [.. retained, .. proposal.Entries, .. source.Entries.Where(entry => protectedIds.Contains(entry.Id))]
        }, _connection, turn.ModelId!, _supportsResources).Context;
        await ValidateCompactionResourcesAsync(proposal, access, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            FenceResourceCallback(cancellationToken);
            if (!ReferenceEquals(run, RequireCompactionPosition(request)))
                throw new InvalidOperationException("The run changed during provider compaction.");
            if (run is not null) AddUsage(run, result.Usage);
            return proposal;
        }
    }

    private LiveRun? RequireCompactionPosition(LlmProviderCompactionRequest request)
    {
        if (_segmentActive) throw new InvalidOperationException("Provider proposals require idle or paused execution.");
        RequireRevision(request.ExpectedContextRevision);
        if (_run is null)
        {
            if (request.TurnId is not null || request.PauseId is not null)
                throw new InvalidOperationException("Idle compaction has no turn or pause identity.");
        }
        else if (_run.PauseId is null || request.TurnId != _run.Id || request.PauseId != _run.PauseId)
            throw new InvalidOperationException("The run or pause is stale or consumed.");
        return _run;
    }

    private async Task ValidateCompactionResourcesAsync(LlmAgentContext context, IInferenceResourceAccess? access,
        CancellationToken token)
    {
        if (access is null) return;
        InferenceResourceSelection selection = await ReadAdmittedSelectionAsync(access, token).ConfigureAwait(false);
        await LlmResourcePreparation.ValidateContextAsync(context, selection, access,
            () => FenceResourceCallback(token), token).ConfigureAwait(false);
        FenceResourceCallback(token);
    }

    private LlmAgentContext ProviderContext(LlmAgentContext source, LlmAgentTurnRequest turn, ProviderCompactionResult result)
    {
        string id = NewId();
        return ManagedAgentContext.Import(source with
        {
            Entries = [new(id, id, new("assistant", "") { ContinuationState = result.ContinuationState })]
        }, _connection, turn.ModelId!, _supportsResources).Context;
    }

    public async Task ReplaceContextAsync(LlmAgentContext context, long expectedRevision, CancellationToken cancellationToken = default)
    {
        using var operation = _resourceCallbacks.Enter(cancellationToken);
        cancellationToken = operation.Token;
        LlmAgentContext replacement;
        IInferenceResourceAccess? access;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireCapability(Capabilities.SupportsContextReplacement);
            RequireIdle();
            RequireRevision(expectedRevision);
            replacement = ManagedAgentContext.Import(context, _connection, _options.ModelId, _supportsResources).Context;
            access = _callerServices?.Resources;
        }
        if (access is not null)
        {
            InferenceResourceSelection selection = await ReadAdmittedSelectionAsync(access, cancellationToken).ConfigureAwait(false);
            await LlmResourcePreparation.ValidateContextAsync(replacement, selection, access,
                () => FenceResourceCallback(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        lock (_sync)
        {
            FenceResourceCallback(cancellationToken);
            RequireIdle();
            RequireRevision(expectedRevision);
            _ = Measure(replacement, BaseTurn());
            _context = replacement with { Revision = checked(_context.Revision + 1) };
            _latestStatus = null;
        }
    }

    public async IAsyncEnumerable<LlmAgentEvent> RunTurnAsync(LlmAgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var admission = _resourceCallbacks.Enter(cancellationToken);
        IInferenceResourceAccess? access;
        InferenceResourceSelectionVersion? scope;
        lock (_sync) { ThrowIfDisposed(); access = _callerServices?.Resources; scope = _resourceScope; }
        if (access is not null && scope is not null)
            LlmResourcePreparation.ValidateScope(access, scope, () => FenceResourceCallback(admission.Token), admission.Token);
        LiveRun run;
        Segment segment;
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            RequireIdle();
            var turn = FreezeTurn(request);
            // Validate the entire projected request before mutating context or emitting start.
            _ = ManagedAgentContext.Import(_context, _connection, turn.ModelId!, _supportsResources);
            string turnId = NewId();
            // Empty input appends no user entry: the turn sends the context as it is.
            LlmAgentContextEntry? user = turn.Input.Count == 0 && turn.Attachments.Count == 0
                ? null : new LlmAgentContextEntry(NewId(), turnId, UserMessage(turn));
            var projected = user is null ? _context : Append(_context, user);
            _latestStatus = Measure(projected, turn);
            run = new(turn, turnId);
            _context = projected;
            if (!_automaticProviderCompaction && user is not null)
            {
                run.Protected.Add(user.Id);
                run.UserEntryId = user.Id;
            }
            _run = run;
            _segmentActive = true;
            segment = StartSegment(run, null, cancellationToken);
        }
        admission.Dispose();
        await foreach (var item in ReadSegmentAsync(run, segment, cancellationToken)) { yield return item; }
    }

    public async IAsyncEnumerable<LlmAgentEvent> ContinueTurnAsync(LlmAgentTurnContinuation continuation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CancellationToken turnCancellation = cancellationToken;
        using var operation = _resourceCallbacks.Enter(cancellationToken);
        cancellationToken = operation.Token;
        LiveRun run;
        LiveRun admittedRun;
        Segment segment;
        LlmAgentContext source;
        LlmAgentContext replacement;
        IInferenceResourceAccess? access;
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            run = _run ?? throw new InvalidOperationException("There is no paused run.");
            RequireCapability(Capabilities.SupportsCompactionHandoff);
            if (_segmentActive || run.Id != continuation.TurnId || run.PauseId != continuation.PauseId)
            {
                throw new InvalidOperationException("The run or pause is stale, consumed, or active.");
            }
            RequireRevision(continuation.ExpectedContextRevision);
            admittedRun = run;
            source = _context;
            replacement = ManagedAgentContext.Import(continuation.ReplacementContext, _connection, run.Turn.ModelId!, _supportsResources).Context;
            access = _callerServices?.Resources;
        }
        var validator = _estimator as ILlmCompactionValidator;
        validator?.ValidateCompaction(_connection, Request(source, admittedRun.Turn), Request(replacement, admittedRun.Turn));
        FenceResourceCallback(cancellationToken);
        if (access is not null)
        {
            InferenceResourceSelection selection = await ReadAdmittedSelectionAsync(access, cancellationToken).ConfigureAwait(false);
            await LlmResourcePreparation.ValidateContextAsync(replacement, selection, access,
                () => FenceResourceCallback(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        validator?.ValidateCompaction(_connection, Request(source, admittedRun.Turn), Request(replacement, admittedRun.Turn));
        FenceResourceCallback(cancellationToken);
        lock (_sync)
        {
            FenceResourceCallback(cancellationToken);
            run = _run ?? throw new InvalidOperationException("There is no paused run.");
            if (_segmentActive || !ReferenceEquals(run, admittedRun) || !ReferenceEquals(_context, source)
                || run.Id != continuation.TurnId || run.PauseId != continuation.PauseId)
                throw new InvalidOperationException("The run or pause changed during context admission.");
            RequireRevision(continuation.ExpectedContextRevision);
            var oldProtected = _context.Entries.Where(entry => run.Protected.Contains(entry.Id)).ToArray();
            var newProtected = replacement.Entries.Where(entry => run.Protected.Contains(entry.Id)).ToArray();
            if (oldProtected.Length != newProtected.Length
                || oldProtected.Where((entry, index) => !ManagedAgentContext.Equivalent(entry, newProtected[index])
                    && !ManagedAgentContext.IsReducedToolResults(entry, newProtected[index])).Any())
            {
                throw new ArgumentException("Compaction must retain protected records intact or reduce only their tool-result parts, in order.");
            }
            var projected = WithQueuedSteering(replacement, run);
            var before = Measure(WithQueuedSteering(_context, run), run.Turn);
            var after = Measure(projected, run.Turn);
            bool reduced = before.UsedContextTokens is long prior && after.UsedContextTokens is long next
                ? next < prior
                : _context.Entries.Any(entry => !run.Protected.Contains(entry.Id)
                    && !replacement.Entries.Any(candidate => candidate.Id == entry.Id));
            if (!reduced || after.NeedsCompaction is true)
            {
                throw new ArgumentException("Replacement must reduce context and fit the known compaction threshold.");
            }
            long nextRevision = checked(_context.Revision + 1);
            run.Protected.UnionWith(projected.Entries.Skip(replacement.Entries.Count).Select(entry => entry.Id));
            run.Steering.Clear();
            _context = projected with { Revision = nextRevision };
            run.PauseId = null;
            _segmentActive = true;
            segment = StartSegment(run, continuation.PauseId, turnCancellation);
        }
        operation.Dispose();
        await foreach (var item in ReadSegmentAsync(run, segment, turnCancellation)) { yield return item; }
    }

    public Task SteerAsync(string turnId, IReadOnlyList<LlmAgentContent> input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string text = TextContent(input);
        if (text.Length == 0) { throw new ArgumentException("Steering requires nonempty text."); }
        lock (_sync)
        {
            ThrowIfDisposed();
            LiveRun run = RequireRun(turnId);
            if (Capabilities.SupportsSteering != InferenceSupport.Supported)
                throw new NotSupportedException("The selected model does not support steering.");
            run.Steering.Enqueue(text);
            return Task.CompletedTask;
        }
    }

    public async Task InterruptAsync(string turnId, CancellationToken cancellationToken = default)
    {
        Task producer;
        Task cancellation;
        LiveRun run;
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            run = RequireRun(turnId);
            RequireCapability(Capabilities.SupportsInterrupt);
            run.Steering.Clear();
            producer = _producer;
            if (!_segmentActive) { _run = null; run.Dispose(); return; }
            cancellation = run.Cancellation.CancelAsync();
        }
        Task interruption = Task.WhenAll(cancellation, producer.WaitAsync(cancellationToken));
        try { await interruption.ConfigureAwait(false); }
        catch
        {
            if (interruption.Exception is { InnerExceptions.Count: > 1 } failures) throw failures;
            throw;
        }
    }

    public async IAsyncEnumerable<LlmAgentEvent> ObserveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await foreach (LlmAgentEvent item in _resources.ObserveAsync(cancellationToken))
        {
            yield return item;
        }
    }

    public Task<IReadOnlyList<LlmAgentResource>> GetResourcesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return NoAgentResources.GetAsync();
    }

    public Task<LlmAgentResourceOutput> ReadResourceOutputAsync(string resourceId, string? cursor,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        cancellationToken.ThrowIfCancellationRequested();
        return NoAgentResources.ReadAsync();
    }

    public Task<LlmAgentResourceStopStatus> StopResourceAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        cancellationToken.ThrowIfCancellationRequested();
        return _resources.StopAsync();
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync) { return new(_retirement ??= RetireAsync()); }
    }

    private async Task RetireAsync()
    {
        LiveRun? run;
        Task producer;
        Task cancellation;
        Task callbacks;
        lock (_sync)
        {
            _disposed = true;
            _resources.Dispose();
            run = _run;
            producer = _producer;
            cancellation = run?.Cancellation.CancelAsync() ?? Task.CompletedTask;
            callbacks = _resourceCallbacks.RetireAsync();
        }
        Task retirement = Task.WhenAll(cancellation, producer, callbacks);
        try { await retirement.ConfigureAwait(false); }
        catch
        {
            if (retirement.Exception is { InnerExceptions.Count: > 1 } failures) throw failures;
            throw;
        }
        finally
        {
            lock (_sync)
            {
                _hostedTools = [];
                _toolHandler = null;
                _callerServices = null;
                _compactor = null;
                _run = null;
                if (!_segmentActive) { run?.Dispose(); }
            }
        }
    }

    // Called while admission owns _sync, before interrupt/disposal can observe the run.
    private Segment StartSegment(LiveRun run, string? continuedPause, CancellationToken cancellationToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Cancellation.Token);
        var channel = Channel.CreateBounded<LlmAgentEvent>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _producer = completion.Task;
        _ = Task.Run(async () =>
        {
            try
            {
                await ProduceAsync(run, continuedPause, channel.Writer, cancellation.Token).ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
        }, CancellationToken.None);
        return new(channel.Reader, cancellation, _producer);
    }

    private async IAsyncEnumerable<LlmAgentEvent> ReadSegmentAsync(LiveRun run, Segment segment,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        bool drained = false;
        try
        {
            await foreach (var item in segment.Reader.ReadAllAsync(cancellationToken)) { yield return item; }
            drained = true;
        }
        finally { await DrainSegmentAsync(run, segment, drained).ConfigureAwait(false); }
    }

    private async Task DrainSegmentAsync(LiveRun run, Segment segment, bool drained)
    {
        Task retirement = Task.WhenAll(segment.Cancellation.CancelAsync(), segment.Producer);
        try { await retirement.ConfigureAwait(false); }
        catch
        {
            if (retirement.Exception is { InnerExceptions.Count: > 1 } failures) throw failures;
            throw;
        }
        finally
        {
            lock (_sync)
            {
                _segmentActive = false;
                if (!drained || _disposed || run.Cancellation.IsCancellationRequested || run.PauseId is null)
                {
                    _run = null;
                    run.Dispose();
                }
            }
            segment.Cancellation.Dispose();
        }
    }

    private async Task ProduceAsync(LiveRun run, string? continuedPause, ChannelWriter<LlmAgentEvent> writer, CancellationToken token)
    {
        bool executingMixedTools = false;
        try
        {
            await writer.WriteAsync(continuedPause is null
                ? new LlmAgentTurnStartedEvent(ThreadId, run.Id)
                : new LlmAgentTurnContinuedEvent(ThreadId, run.Id, continuedPause), token);
            if (_context.Entries.Count == 0)
            {
                // Empty input on an empty context leaves nothing to send.
                await CompleteAsync(run, writer, LlmAgentCompletionReason.Completed, token);
                return;
            }
            if (_context.Entries.Any(entry => entry.Message.OmittedAttachments.Count > 0))
            {
                await writer.WriteAsync(new LlmAgentWarningEvent(ThreadId, run.Id,
                    "Some restored attachments are unavailable; omission descriptors are not sent to the model."), token);
            }
            if (continuedPause is null && Interlocked.Exchange(ref _startOmittedContinuationStates, 0) is int omitted and > 0)
            {
                await writer.WriteAsync(new LlmAgentWarningEvent(ThreadId, run.Id,
                    $"Response state from {omitted} earlier messages was not carried to this model."), token);
            }
            while (run.Iterations < _execution.MaxModelIterations)
            {
                LlmChatRequest request;
                LlmAgentContextStatus status;
                lock (_sync)
                {
                    AdmitSteering(run);
                    request = Request(_context, run.Turn, firstStep: run.Iterations == 0);
                    status = _latestStatus = Measure(_context, run.Turn);
                }
                await writer.WriteAsync(new LlmAgentContextStatusEvent(ThreadId, run.Id, status), token);
                if (_automaticProviderCompaction && ExceedsHardWindow(status, run.Turn))
                {
                    await CompleteAsync(run, writer, LlmAgentCompletionReason.ProviderFailure, token,
                        "Input exceeds the known model context window.");
                    return;
                }
                if (status.NeedsCompaction is true)
                {
                    if (!_automaticProviderCompaction)
                    {
                        await PauseAsync(run, status, LlmAgentCompactionReason.Threshold, writer, token);
                        return;
                    }
                    (ProviderCompactionResult compacted, long revision) = await CompactAutomaticallyAsync(run, token).ConfigureAwait(false);
                    await writer.WriteAsync(new LlmAgentCompactedEvent(ThreadId, run.Id, compacted.RequestId,
                        revision, compacted.DroppedMessageCount, compacted.Usage), token);
                    await writer.WriteAsync(new LlmAgentUsageEvent(ThreadId, run.Id, compacted.RequestId,
                        compacted.Usage, run.Usage), token);
                    lock (_sync)
                    {
                        request = Request(_context, run.Turn);
                        status = _latestStatus = Measure(_context, run.Turn);
                    }
                    if (ExceedsHardWindow(status, run.Turn))
                    {
                        await CompleteAsync(run, writer, LlmAgentCompletionReason.ProviderFailure, token,
                            "Input exceeds the known model context window.");
                        return;
                    }
                }
                string stepId = NewId();
                LlmChatResult? result = null;
                try
                {
                    for (int attempt = 1; ; attempt++)
                    {
                        bool replacementStarted = false;
                        try
                        {
                            await foreach (var item in StreamModelAsync(run, request, token))
                            {
                                if (request.Resources is not null)
                                {
                                    try { FenceResourceCallback(token); }
                                    catch (Exception error) { ExceptionDispatchInfo.Capture(LlmResourcePreparation.AsResourceFailure(error, token)).Throw(); throw; }
                                }
                                if (result is not null) { throw new InvalidOperationException("Model emitted an event after completion."); }
                                if (attempt > 1 && !replacementStarted)
                                {
                                    await writer.WriteAsync(new LlmAgentActivityEvent(ThreadId, run.Id, stepId, string.Empty), token);
                                    replacementStarted = true;
                                }
                                switch (item)
                                {
                                    case LlmUsageDeltaEvent usage:
                                        LlmUsage? runningTotal = run.UsageUnknown ? null
                                            : UsageTotals.Add(run.Usage, usage.Cumulative, run.UsageCalls == 0);
                                        await writer.WriteAsync(new LlmAgentUsageEvent(ThreadId, run.Id, stepId,
                                            usage.Cumulative, runningTotal) { ModelId = request.ModelId }, token);
                                        break;
                                    case LlmTextDeltaEvent text:
                                        await writer.WriteAsync(new LlmAgentTextDeltaEvent(ThreadId, run.Id, stepId, text.Delta), token);
                                        break;
                                    case LlmStructuredOutputSnapshotEvent snapshot:
                                        await writer.WriteAsync(new LlmAgentStructuredOutputSnapshotEvent(
                                            ThreadId, run.Id, stepId, snapshot.Content.Clone()), token);
                                        break;
                                    case LlmReasoningDeltaEvent reasoning:
                                        await writer.WriteAsync(new LlmAgentReasoningDeltaEvent(ThreadId, run.Id, stepId,
                                            reasoning.Delta, reasoning.IsSummary), token);
                                        break;
                                    case LlmHostedToolCallStartedEvent hosted:
                                        await writer.WriteAsync(new LlmAgentHostedToolCallStartedEvent(
                                            ThreadId, run.Id, stepId, hosted.CallId, hosted.Kind, hosted.Name), token);
                                        break;
                                    case LlmHostedToolCallProgressEvent hosted:
                                        await writer.WriteAsync(new LlmAgentHostedToolCallProgressEvent(
                                            ThreadId, run.Id, stepId, hosted.CallId, hosted.Phase), token);
                                        break;
                                    case LlmHostedToolCallCompletedEvent hosted:
                                        await writer.WriteAsync(new LlmAgentHostedToolCallCompletedEvent(
                                            ThreadId, run.Id, stepId, HostedTools.Snapshot(hosted.Call)), token);
                                        break;
                                    case LlmCitationEvent citation:
                                        await writer.WriteAsync(new LlmAgentCitationEvent(
                                            ThreadId, run.Id, stepId, citation.Citation), token);
                                        break;
                                    case LlmChatCompletionEvent completion:
                                        result = completion.Result;
                                        await writer.WriteAsync(new LlmAgentCitationsSnapshotEvent(
                                            ThreadId, run.Id, stepId, result.Citations), token);
                                        break;
                                }
                            }
                            break;
                        }
                        catch (Exception error) when (result is null && !token.IsCancellationRequested &&
                            (error is LlmProviderException { IsResponseStreamInterrupted: true }
                            || error is InferenceResourceOperationException
                            {
                                Failure: { Phase: InferenceResourceOperationPhase.Materialize, CommitOutcome: InferenceResourceCommitOutcome.NotAttempted },
                                InnerException: LlmProviderException { IsResponseStreamInterrupted: true }
                            }))
                        {
                            bool hostedTools = request.HostedTools.Count > 0;
                            int? nextAttempt = hostedTools || attempt == 3 ? null : attempt + 1;
                            run.UsageUnknown = true;
                            await writer.WriteAsync(new LlmAgentResponseRecoveryEvent(ThreadId, run.Id, stepId, nextAttempt), token);
                            if (nextAttempt is null)
                            {
                                string message = hostedTools
                                    ? "The model response stream was interrupted. Provider-side work may already have run. This request was not replayed because provider-hosted tools are enabled. Completed application-tool results are retained."
                                    : "The model response stream was interrupted. Recovery failed after 3 attempts. Completed tool results are retained. Resume to continue.";
                                await CompleteAsync(run, writer, LlmAgentCompletionReason.ProviderFailure, token, message);
                                return;
                            }
                            await Task.Delay(TimeSpan.FromSeconds(attempt), token);
                            stepId = NewId();
                        }
                    }
                }
                catch (LlmProviderException error) when (error.IsContextOverflow)
                {
                    if (_automaticProviderCompaction)
                    {
                        await CompleteAsync(run, writer, LlmAgentCompletionReason.ProviderFailure, token, error.Message);
                        return;
                    }
                    if (++run.ProviderOverflowCount > 2)
                    {
                        await CompleteAsync(run, writer, LlmAgentCompletionReason.ProviderFailure, token,
                            "The context could not be reduced enough for this step.");
                        return;
                    }
                    await PauseAsync(run, status with { NeedsCompaction = true }, LlmAgentCompactionReason.ProviderOverflow, writer, token);
                    return;
                }
                if (request.Resources is not null)
                {
                    try { FenceResourceCallback(token); }
                    catch (Exception error) { ExceptionDispatchInfo.Capture(LlmResourcePreparation.AsResourceFailure(error, token)).Throw(); throw; }
                }
                if (result is null) { throw new InvalidOperationException("Model stream ended without completion."); }
                run.ProviderOverflowCount = 0;
                run.Iterations++;
                AddUsage(run, result.Usage);
                await writer.WriteAsync(new LlmAgentUsageEvent(ThreadId, run.Id, stepId, result.Usage, run.Usage)
                {
                    ModelId = result.ModelId ?? request.ModelId
                }, token);
                if (result.Status is not (LlmCompletionStatus.Completed or LlmCompletionStatus.RequiresAction or LlmCompletionStatus.Unknown))
                {
                    if (result.Status == LlmCompletionStatus.Incomplete
                        && result.ContinuationState is not null && result.ToolCalls.Count == 0)
                    {
                        AppendAssistant(stepId, run, result);
                        await writer.WriteAsync(new LlmAgentMessageCompletedEvent(ThreadId, run.Id, stepId, result.Text), token);
                    }
                    LlmAgentCompletionReason ended = result.Status switch
                    {
                        LlmCompletionStatus.Refused => LlmAgentCompletionReason.Refusal,
                        LlmCompletionStatus.Incomplete => LlmAgentCompletionReason.Truncation,
                        _ => LlmAgentCompletionReason.ProviderFailure
                    };
                    await CompleteAsync(run, writer, ended, token, structuredOutput: AnswerCheck(run, result, ended));
                    return;
                }
                if (result.Status == LlmCompletionStatus.Unknown && result.ToolCalls.Count > 0)
                {
                    AppendAssistant(stepId, run, result);
                    await writer.WriteAsync(new LlmAgentMessageCompletedEvent(ThreadId, run.Id, stepId, result.Text), token);
                    await CompleteAsync(run, writer, LlmAgentCompletionReason.Unknown, token);
                    return;
                }
                if (result.ToolCalls.Count > 0 && _options.ToolHandling == LlmAgentToolHandling.ReturnRequests)
                {
                    await ReturnRequestsAsync(run, stepId, result, writer, token);
                    return;
                }
                LlmAgentContextEntry assistant = AppendAssistant(stepId, run, result);
                // Mixed calls retain actual completed results; interruption leaves the remaining tail unresolved.
                if (result.ToolCalls.Count > 0)
                {
                    executingMixedTools = result.ToolCalls.Any(call => call is LlmShellCall);
                    try { await writer.WriteAsync(new LlmAgentMessageCompletedEvent(ThreadId, run.Id, stepId, result.Text), token); }
                    finally { await ExecuteToolsAsync(run, result.ToolCalls, assistant.Id, writer, token); }
                    executingMixedTools = false;
                    token.ThrowIfCancellationRequested();
                    continue;
                }
                await writer.WriteAsync(new LlmAgentMessageCompletedEvent(ThreadId, run.Id, stepId, result.Text), token);
                if (result.ToolCalls.Count == 0)
                {
                    if (result.Status == LlmCompletionStatus.RequiresAction)
                    {
                        throw new InvalidOperationException("Model requested tools without returning calls.");
                    }
                    lock (_sync)
                    {
                        if (run.Steering.Count > 0) { continue; }
                        run.Terminal = true;
                    }
                    await CompleteAsync(run, writer, result.Status == LlmCompletionStatus.Unknown
                        ? LlmAgentCompletionReason.Unknown : LlmAgentCompletionReason.Completed, token,
                        structuredOutput: AnswerCheck(run, result, null));
                    return;
                }
            }
            await CompleteAsync(run, writer, LlmAgentCompletionReason.IterationLimit, token);
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested && !LlmResourcePreparation.IsResourceFailure(error))
        {
            // Best effort only: a cancelled/abandoned reader must never block tool settlement.
            writer.TryWrite(new LlmAgentTurnCompletedEvent(ThreadId, run.Id, LlmAgentCompletionReason.Interruption, null));
            if (executingMixedTools) writer.TryComplete(error);
        }
        catch (Exception error)
        {
            // An unclassified failure reports its type and its message with credentials removed.
            try
            {
                if (error is not LlmProviderException)
                {
                    // Runtime type and managed frames contain no exception messages or provider payloads.
                    await writer.WriteAsync(new LlmAgentWarningEvent(ThreadId, run.Id,
                        $"Managed execution exception: {error.GetType().FullName}\n{new System.Diagnostics.StackTrace(error, fNeedFileInfo: false)}"), token);
                }
                await writer.WriteAsync(new LlmAgentTurnCompletedEvent(ThreadId, run.Id, LlmAgentCompletionReason.ProviderFailure,
                    error is LlmProviderException provider ? provider.Message
                        : $"{error.GetType().Name}: {SecretRedactor.Sanitize(error.Message, LlmCredential.Parse(_connection.Credential))}"), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            if (executingMixedTools || LlmResourcePreparation.IsResourceFailure(error)) writer.TryComplete(error);
            if (LlmResourcePreparation.IsCleanupFailure(error)) throw;
        }
        finally
        {
            lock (_sync) { if (run.PauseId is null) { run.Terminal = true; } }
            writer.TryComplete();
        }
    }

    private LlmAgentContextEntry AppendAssistant(string stepId, LiveRun run, LlmChatResult result)
    {
        var assistant = new LlmAgentContextEntry(stepId, run.Id, ManagedAgentContext.CopyMessage(new("assistant", result.Text)
        {
            ToolCalls = result.ToolCalls,
            ContinuationState = result.ContinuationState
        }));
        ValidateCalls(result.ToolCalls);
        lock (_sync) { _context = Append(_context, assistant); }
        return assistant;
    }

    private async Task ExecuteToolsAsync(LiveRun run, IReadOnlyList<LlmClientToolCall> returnedCalls, string assistantId,
        ChannelWriter<LlmAgentEvent> writer, CancellationToken token)
    {
        if (returnedCalls.Any(call => call is LlmShellCall))
        {
            string? entryId = null;
            foreach (LlmClientToolCall call in returnedCalls)
            {
                FenceResourceCallback(token);
                LlmClientToolResult result = call switch
                {
                    LlmToolCall function => await InvokeFunctionAsync(run, function, writer,
                        message => throw new InvalidOperationException(message), () => { }, () =>
                        {
                            lock (_sync)
                            {
                                if (_disposed || !ReferenceEquals(_run, run)) throw new OperationCanceledException(token);
                            }
                        }, token),
                    LlmShellCall shell => await InvokeShellAsync(run, shell, token),
                    _ => throw new NotSupportedException("Unknown client tool call.")
                };
                lock (_sync)
                {
                    if (_disposed || !ReferenceEquals(_run, run)) throw new OperationCanceledException(token);
                    if (entryId is null)
                    {
                        entryId = NewId();
                        _context = Append(_context, new(entryId, run.Id, new("user", "") { ToolResults = [result] }));
                    }
                    else
                    {
                        _context = _context with
                        {
                            Revision = _context.Revision + 1,
                            Entries = _context.Entries.Select(entry => entry.Id == entryId
                                ? entry with { Message = entry.Message with { ToolResults = [.. entry.Message.ToolResults, result] } }
                                : entry).ToArray()
                        };
                    }
                    if (!_automaticProviderCompaction)
                    {
                        run.Protected.Clear();
                        run.Protected.UnionWith([run.UserEntryId, assistantId, entryId]);
                    }
                }
            }
            return;
        }
        IReadOnlyList<LlmToolCall> calls = ClientTools.Functions(returnedCalls);
        bool parallel = _execution.ToolExecutionMode == LlmAgentToolExecutionMode.Parallel
            && !calls.Any(call => _tools.TryGetValue(call.Name, out var tool) && tool.RequiresSequentialExecution);
        using var concurrency = new SemaphoreSlim(parallel ? _execution.MaxConcurrentTools : 1);
        async Task<LlmToolResult> InvokeAsync(LlmToolCall call)
        {
            bool entered = false;
            bool started = false;
            async Task<LlmToolResult> FailedAsync(string message)
            {
                if (!token.IsCancellationRequested)
                {
                    try
                    {
                        await writer.WriteAsync(new LlmAgentToolEvent(ThreadId, run.Id, call.Id, null,
                        call.Name, RecordedArguments(call.ArgumentsJson), false, "failed",
                        LlmToolOutput.FromText(message, isError: true)), token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                }
                return new(call.Id, LlmToolOutput.FromText(message, isError: true));
            }
            try
            {
                await concurrency.WaitAsync(token);
                entered = true;
                return await InvokeFunctionAsync(run, call, writer, FailedAsync, () => started = true, () => { }, token);
            }
            catch (Exception error) when (LlmResourcePreparation.IsResourceFailure(error)) { throw; }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new(call.Id, LlmToolOutput.FromText(started ? "Tool interrupted; external outcome is unknown. Do not automatically replay."
                    : "Tool was not executed because the run was cancelled.", isError: true));
            }
            catch (Exception error)
            {
                return await FailedAsync(HandlerFailure(error));
            }
            finally { if (entered) { concurrency.Release(); } }
        }
        LlmToolResult[] results;
        if (parallel)
        {
            Task<LlmToolResult[]> completion = Task.WhenAll(calls.Select(InvokeAsync));
            try { results = await completion; }
            catch
            {
                if (completion.Exception is { InnerExceptions.Count: > 1 } failures) throw failures;
                throw;
            }
        }
        else
        {
            var ordered = new List<LlmToolResult>();
            foreach (var call in calls) { ordered.Add(await InvokeAsync(call)); }
            results = [.. ordered];
        }
        lock (_sync)
        {
            var entry = new LlmAgentContextEntry(NewId(), run.Id, new("user", "") { ToolResults = results });
            _context = Append(_context, entry);
            if (!_automaticProviderCompaction)
            {
                run.Protected.Clear();
                run.Protected.UnionWith([run.UserEntryId, assistantId, entry.Id]);
            }
        }
    }

    private async Task<LlmToolResult> InvokeFunctionAsync(LiveRun run, LlmToolCall call,
        ChannelWriter<LlmAgentEvent> writer, Func<string, Task<LlmToolResult>> failed,
        Action started, Action observeCompleted, CancellationToken token)
    {
        if (!_tools.TryGetValue(call.Name, out var definition)) return await failed(UnknownToolResult);
        if (ArgumentRejection(definition, call.ArgumentsJson, out JsonElement arguments) is { } rejection)
            return await failed(rejection);
        if (_toolHandler is not { } handler) return await failed($"No handler is available for tool '{call.Name}'.");
        JsonElement recorded = RecordedArguments(call.ArgumentsJson);
        await writer.WriteAsync(new LlmAgentToolEvent(ThreadId, run.Id, call.Id, null, call.Name,
            recorded, null, "started"), token);
        FenceResourceCallback(token);
        started();
        var returned = await handler(new(ThreadId, run.Id, call.Id, null, call.Name, arguments), token);
        observeCompleted();
        var output = ManagedAgentContext.CopyOutput(returned);
        if (output.Parts.OfType<LlmToolOutputSessionResource>().Any())
        {
            try
            {
                using var callback = _resourceCallbacks.Enter(token);
                IInferenceResourceAccess access;
                lock (_sync)
                {
                    FenceResourceCallback(callback.Token);
                    access = _callerServices?.Resources ?? throw new NotSupportedException("Tool resources require caller-owned access.");
                }
                if (_resourceScope is { } scope)
                    LlmResourcePreparation.ValidateScope(access, scope, () => FenceResourceCallback(callback.Token), callback.Token);
                await LlmResourcePreparation.AuthorizeToolOutputAsync(output, access,
                    () => FenceResourceCallback(callback.Token), callback.Token).ConfigureAwait(false);
                FenceResourceCallback(callback.Token);
            }
            catch (Exception error) { ExceptionDispatchInfo.Capture(LlmResourcePreparation.AsResourceFailure(error, token)).Throw(); throw; }
        }
        var mapped = new LlmToolResult(call.Id, output);
        if (!token.IsCancellationRequested)
        {
            try
            {
                await writer.WriteAsync(new LlmAgentToolEvent(ThreadId, run.Id, call.Id, null, call.Name,
                    recorded, !output.IsError, "completed", mapped.Output), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
        return mapped;
    }

    /// <summary>A handler failure as the model sees it, with the connection's credential and header values removed.</summary>
    private string HandlerFailure(Exception error) =>
        $"{error.GetType().Name}: {SecretRedactor.Sanitize(error.Message, LlmCredential.Parse(_connection.Credential), _connection.AdditionalHeaders.Values)} External outcome is unknown. Do not automatically replay.";

    private async Task<LlmShellCallResult> InvokeShellAsync(LiveRun run, LlmShellCall call, CancellationToken token)
    {
        using var callback = _resourceCallbacks.Enter(token);
        ILlmAgentShellExecutor executor;
        lock (_sync)
        {
            FenceResourceCallback(callback.Token);
            executor = _callerServices?.ShellExecutor ?? throw new NotSupportedException("Shell calls require a borrowed caller executor.");
        }
        LlmShellCallResult returned = await executor.ExecuteAsync(new(new(ThreadId, SessionId, run.Id, null, null),
            (LlmShellCall)ClientTools.Snapshot(call)), callback.Token).ConfigureAwait(false);
        lock (_sync)
        {
            if (_disposed || !ReferenceEquals(_run, run)) throw new OperationCanceledException(callback.Token);
        }
        ArgumentNullException.ThrowIfNull(returned);
        LlmShellCallResult result = ClientTools.Snapshot(returned);
        if (result.ToolCallId != call.Id || result.Output.Any(output => output.Outcome is not (LlmShellExitedOutcome or LlmShellTimedOutOutcome)))
            throw new InvalidOperationException("Shell executor returned an invalid result identity or outcome.");
        return result;
    }

    private async Task PauseAsync(LiveRun run, LlmAgentContextStatus status, LlmAgentCompactionReason reason,
        ChannelWriter<LlmAgentEvent> writer, CancellationToken token)
    {
        RequireCapability(Capabilities.SupportsCompactionHandoff);
        LlmAgentCompactionRequiredEvent pause;
        lock (_sync)
        {
            run.PauseId = NewId();
            var protectedOnly = _context with { Entries = _context.Entries.Where(entry => run.Protected.Contains(entry.Id)).ToArray() };
            bool? needsCompaction = Measure(protectedOnly, run.Turn).NeedsCompaction;
            bool compactable = protectedOnly.Entries.Count < _context.Entries.Count
                || protectedOnly.Entries.SelectMany(entry => entry.Message.ToolResults).OfType<LlmToolResult>()
                    .SelectMany(result => result.Output.Parts)
                    .Any(part => part is LlmToolOutputText { Text: { Length: > 2000 } } or LlmToolOutputBinary);
            pause = new(ThreadId, run.Id, run.PauseId, status, reason, compactable, run.Protected.ToArray())
            {
                ProtectedFits = needsCompaction is { } known ? !known : null
            };
        }
        await writer.WriteAsync(pause, token);
    }

    private async Task CompleteAsync(LiveRun run, ChannelWriter<LlmAgentEvent> writer, LlmAgentCompletionReason reason,
        CancellationToken token, string? error = null, LlmStructuredOutputValidation? structuredOutput = null)
    {
        lock (_sync) { run.Terminal = true; }
        await writer.WriteAsync(new LlmAgentTurnCompletedEvent(ThreadId, run.Id, reason,
            reason is LlmAgentCompletionReason.Completed or LlmAgentCompletionReason.ToolRequests ? null : error ?? reason.ToString())
        {
            StructuredOutput = structuredOutput
        }, token);
    }

    /// <summary>
    /// The final answer's check against the turn's schema, or null when the turn requested none. An answer that ended
    /// with <paramref name="unevaluated"/> is reported as not evaluated.
    /// </summary>
    private static LlmStructuredOutputValidation? AnswerCheck(LiveRun run, LlmChatResult result,
        LlmAgentCompletionReason? unevaluated)
    {
        if (run.Turn.OutputSchema is not { } schema) return null;
        LlmStructuredOutputEnforcement enforcement = result.StructuredOutput?.Enforcement
            ?? LlmStructuredOutputEnforcement.LocalValidationOnly;
        return unevaluated is { } reason
            ? new(enforcement, false, false, [$"The answer was not evaluated: {reason}."])
            : StructuredOutput.Validate(result.Text, new() { Name = "agent_output", Schema = schema }, enforcement);
    }

    /// <summary>
    /// Ends the turn with the step's calls unrun (B7). The context keeps the assistant text only, so it stays at a
    /// settled boundary; each call is reported with its validation result.
    /// </summary>
    private async Task ReturnRequestsAsync(LiveRun run, string stepId, LlmChatResult result,
        ChannelWriter<LlmAgentEvent> writer, CancellationToken token)
    {
        ValidateCalls(result.ToolCalls);
        lock (_sync)
        {
            _context = Append(_context, new LlmAgentContextEntry(stepId, run.Id,
                ManagedAgentContext.CopyMessage(new("assistant", result.Text))));
        }
        await writer.WriteAsync(new LlmAgentMessageCompletedEvent(ThreadId, run.Id, stepId, result.Text), token);
        foreach (LlmToolCall call in result.ToolCalls.OfType<LlmToolCall>())
        {
            string? rejection = _tools.TryGetValue(call.Name, out var definition)
                ? ArgumentRejection(definition, call.ArgumentsJson, out _) : UnknownToolResult;
            await writer.WriteAsync(rejection is null
                ? new LlmAgentToolEvent(ThreadId, run.Id, call.Id, null, call.Name, RecordedArguments(call.ArgumentsJson),
                    null, "returned")
                : new LlmAgentToolEvent(ThreadId, run.Id, call.Id, null, call.Name, RecordedArguments(call.ArgumentsJson),
                    false, "failed", LlmToolOutput.FromText(rejection, isError: true)), token);
        }
        await CompleteAsync(run, writer, LlmAgentCompletionReason.ToolRequests, token);
    }

    private const string UnknownToolResult = "Unknown tool.";

    /// <summary>
    /// The 6.3 rejection text when the arguments fail the tool's schema, otherwise null. <paramref name="instance"/>
    /// receives the arguments as the model sent them, with a wrapped schema's <c>value</c> unwrapped; a schema that cannot
    /// be checked lets the call run.
    /// </summary>
    private static string? ArgumentRejection(LlmAgentToolDefinition definition, string arguments, out JsonElement instance)
    {
        StructuredOutput.Evaluation evaluation = ToolSchemas.EvaluateArguments(definition.InputSchema, arguments);
        instance = evaluation.Instance;
        return evaluation is { Checked: true, Errors.Count: > 0 }
            ? $"Tool '{definition.Name}' arguments were rejected:\n{string.Join('\n', evaluation.Errors)}"
            : null;
    }

    /// <summary>The model's arguments as JSON when they parse, otherwise a JSON string holding the raw text.</summary>
    private static JsonElement RecordedArguments(string arguments)
    {
        try { return JsonElement.Parse(arguments); }
        catch (JsonException) { return JsonSerializer.SerializeToElement(arguments, ConnectLlmJsonContext.Default.String); }
    }

    /// <summary>
    /// The request one model step sends, normalized as the provider receives it. <paramref name="firstStep"/> applies
    /// the turn's tool choice; later steps use Auto, so a Required or Specific choice does not force every step.
    /// </summary>
    private LlmChatRequest Request(LlmAgentContext context, LlmAgentTurnRequest turn, bool firstStep = true) =>
        LlmRequestNormalizer.Normalize(_connection, new()
        {
            ModelId = turn.ModelId!,
            UnboundContinuationState = LlmUnboundContinuationState.Omit,
            Instructions = turn.Instructions ?? _options.Instructions,
            Messages = ManagedAgentContext.Outgoing(context.Entries),
            ConversationKey = ThreadId,
            PromptCache = new LlmPromptCacheHint { Scope = LlmPromptCacheScope.Conversation },
            ReasoningLevel = turn.ReasoningLevel,
            Temperature = turn.Temperature,
            MaxOutputTokens = turn.MaxOutputTokens,
            ToolChoice = turn.ToolChoice is null || firstStep ? turn.ToolChoice : new LlmToolChoice { Mode = LlmToolChoiceMode.Auto },
            StructuredOutput = turn.OutputSchema is { } schema ? new() { Name = "agent_output", Schema = schema } : null,
            Tools = _options.Tools.Select(tool => tool switch
            {
                LlmAgentToolDefinition function => (LlmClientToolDefinition)new LlmToolDefinition
                {
                    Name = function.Name,
                    Description = function.Description,
                    InputSchema = ToolSchemas.Wire(function.InputSchema)
                },
                LlmAgentShellTool shell => shell.Tool,
                _ => throw new NotSupportedException("Unknown agent tool definition.")
            }).ToArray(),
            HostedTools = _hostedTools,
            MaxHostedTurns = _options.MaxHostedTurns,
            Resources = _callerServices?.Resources
        }, _capabilities(turn.ModelId!));

    private LlmAgentContextStatus Measure(LlmAgentContext context, LlmAgentTurnRequest turn)
    {
        LlmAgentContextPolicy policy = ManagedAgentPolicy.Normalize(_execution, Model(turn.ModelId!)).ContextPolicy;
        ContextEstimate? used = EstimateInputTokens(context, turn);
        return ManagedAgentPolicy.Status(turn.ModelId!, context.Revision, used, policy, Model(turn.ModelId!), learnedLimit: null);
    }

    private bool ExceedsHardWindow(LlmAgentContextStatus status, LlmAgentTurnRequest turn) =>
        status.UsedContextTokens is long used && Model(turn.ModelId!)?.MaxContextTokens is long hard && used > hard;

    private async Task<(ProviderCompactionResult Result, long Revision)> CompactAutomaticallyAsync(LiveRun run, CancellationToken token)
    {
        using var operation = _resourceCallbacks.Enter(token);
        token = operation.Token;
        LlmAgentContext source;
        IInferenceResourceAccess? access;
        Func<LlmChatRequest, CancellationToken, Task<ProviderCompactionResult>> compactor;
        lock (_sync)
        {
            FenceResourceCallback(token);
            if (!ReferenceEquals(_run, run) || !_segmentActive || run.Terminal)
                throw new InvalidOperationException("The model step changed before provider compaction.");
            source = ManagedAgentContext.Copy(_context, includeAttachmentData: true);
            access = _callerServices?.Resources;
            compactor = _compactor ?? throw new NotSupportedException("This session has no provider compactor.");
        }
        await ValidateCompactionResourcesAsync(source, access, token).ConfigureAwait(false);
        lock (_sync) { RequireAutomaticCompactionPosition(run, source.Revision, token); }
        ProviderCompactionResult result = await compactor(ClientTools.Snapshot(Request(source, run.Turn)), token).ConfigureAwait(false);
        FenceResourceCallback(token);
        LlmAgentContext replacement = ProviderContext(source, run.Turn, result);
        await ValidateCompactionResourcesAsync(replacement, access, token).ConfigureAwait(false);
        lock (_sync)
        {
            RequireAutomaticCompactionPosition(run, source.Revision, token);
            _context = WithQueuedSteering(replacement, run) with { Revision = checked(source.Revision + 1) };
            run.Steering.Clear();
            run.Protected.Clear();
            run.UserEntryId = "";
            AddUsage(run, result.Usage);
            return (result, _context.Revision);
        }
    }

    private void RequireAutomaticCompactionPosition(LiveRun run, long revision, CancellationToken token)
    {
        FenceResourceCallback(token);
        RequireRevision(revision);
        if (!ReferenceEquals(_run, run) || !_segmentActive || run.Terminal)
            throw new InvalidOperationException("The model step changed during provider compaction.");
    }

    private static void AddUsage(LiveRun run, LlmUsage? usage)
    {
        run.Usage = run.UsageUnknown ? null : UsageTotals.Add(run.Usage, usage, run.UsageCalls == 0);
        run.UsageCalls++;
    }

    private ContextEstimate? EstimateInputTokens(LlmAgentContext context, LlmAgentTurnRequest turn)
    {
        return ManagedAgentPolicy.Estimate(_estimator, _connection, Request(context, turn),
            context.Entries.Any(entry => entry.Message.OmittedAttachments.Count > 0));
    }

    private LlmAgentTurnRequest FreezeTurn(LlmAgentTurnRequest turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(turn.Input);
        ArgumentNullException.ThrowIfNull(turn.Attachments);
        var capabilities = _capabilities(turn.ModelId ?? _options.ModelId);
        HostedTools.Validate(_hostedTools, _options.MaxHostedTurns, capabilities.HostedTools);
        ClientTools.ValidateManagedOpening(_options with { ToolHandler = _toolHandler, CallerServices = _callerServices }, capabilities, _supportsResources);
        if (capabilities.SupportsAgentSessions == InferenceSupport.Unsupported)
            throw new NotSupportedException(NativeAgentContract.AgentSessionsUnsupported);
        if (_options.Tools.Count > 0 && (capabilities.SupportsAgentToolUse == InferenceSupport.Unsupported))
            throw new NotSupportedException(NativeAgentContract.AgentToolUseUnsupported);
        return ClientTools.Snapshot(turn) with
        {
            ModelId = turn.ModelId ?? _options.ModelId,
            OutputSchema = turn.OutputSchema?.Clone()
        };
    }

    private LlmAgentTurnRequest BaseTurn() => new() { Input = [], ModelId = _options.ModelId };
    private LlmAgentCapabilities Capabilities
    {
        get
        {
            var capabilities = _capabilities(_run?.Turn.ModelId ?? _options.ModelId);
            return ClientTools.ManagedCapabilities(capabilities) with { HostedTools = capabilities.HostedTools };
        }
    }
    private static void RequireCapability(InferenceSupport supported)
    {
        if (supported == InferenceSupport.Unsupported) { throw new NotSupportedException("The selected connection disables this agent operation."); }
    }
    private LlmModel? Model(string id) => _models.FirstOrDefault(model => model.Id == id);
    private static LlmChatMessage UserMessage(LlmAgentTurnRequest turn) => new("user", TextContent(turn.Input)) { Attachments = turn.Attachments };
    /// <summary>The input as message text; an image URL becomes the text <c>[Image: {url}]</c>.</summary>
    private static string TextContent(IReadOnlyList<LlmAgentContent> input) => string.Join("\n", input.Select(item => item switch
    {
        LlmAgentTextContent text => text.Text,
        LlmAgentImageContent image => $"[Image: {image.ImageUrl}]",
        null => throw new ArgumentNullException(nameof(input)),
        _ => throw new NotSupportedException("Unknown agent content.")
    }));
    private void AdmitSteering(LiveRun run)
    {
        while (run.Steering.TryDequeue(out var text))
        {
            var entry = new LlmAgentContextEntry(NewId(), run.Id, new("user", text));
            _context = Append(_context, entry);
            if (!_automaticProviderCompaction) run.Protected.Add(entry.Id);
        }
    }
    private static LlmAgentContext WithQueuedSteering(LlmAgentContext context, LiveRun run) => context with
    {
        Entries = [.. context.Entries, .. run.Steering.Select(text => new LlmAgentContextEntry(NewId(), run.Id, new("user", text)))]
    };
    private static LlmAgentContext Append(LlmAgentContext context, LlmAgentContextEntry entry, bool increment = true) =>
        context with { Entries = [.. context.Entries, entry], Revision = increment ? checked(context.Revision + 1) : context.Revision };
    private static string NewId() => Guid.NewGuid().ToString("N");
    private void FenceResourceCallback(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync) { if (_disposed) throw new OperationCanceledException(token); }
        token.ThrowIfCancellationRequested();
    }
    private async Task<InferenceResourceSelection> ReadAdmittedSelectionAsync(IInferenceResourceAccess access, CancellationToken token)
    {
        if (_resourceScope is { } expected)
            LlmResourcePreparation.ValidateScope(access, expected, () => FenceResourceCallback(token), token);
        InferenceResourceSelection selection = await LlmResourcePreparation.ReadSelectionAsync(access,
            () => FenceResourceCallback(token), token).ConfigureAwait(false);
        if (_resourceScope is not { } scope || selection.WorkspaceId != scope.WorkspaceId || selection.SessionId != scope.SessionId)
            throw new ArgumentException("The caller-owned resource scope changed after session admission.");
        return selection;
    }
    private static LlmAgentContext WithSelection(LlmAgentContext context, InferenceResourceSelection selection) => context with
    {
        SelectedResources = selection.SelectedResources,
        ResourceSelectionVersion = new(selection.WorkspaceId, selection.SessionId, selection.Revision)
    };
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void RequireIdle()
    {
        if (_run is not null || _segmentActive) { throw new InvalidOperationException("The session is running or paused."); }
    }
    private LiveRun RequireRun(string id) => _run is { Terminal: false } run && run.Id == id
        ? run : throw new InvalidOperationException("The turn ID is stale or terminal.");
    private void RequireRevision(long revision)
    {
        if (_context.Revision != revision) { throw new InvalidOperationException("The context revision is stale."); }
    }
    private void ValidateCalls(IReadOnlyList<LlmClientToolCall> returnedCalls)
    {
        var ids = _context.Entries.SelectMany(entry => entry.Message.ToolCalls)
            .Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var call in returnedCalls)
        {
            if (string.IsNullOrWhiteSpace(call.Id) || !ids.Add(call.Id))
            {
                throw new InvalidOperationException("Model returned invalid or duplicate tool-call identities.");
            }
            if (call is LlmShellCall)
            {
                if (!_options.Tools.Any(tool => tool is LlmAgentShellTool) || (Capabilities.SupportsShellTools == InferenceSupport.Unsupported))
                    throw new NotSupportedException("The session does not declare shell tools.");
            }
            else if (call is LlmToolCall function)
            {
                // Arguments may be any string; the tool's schema check returns a bad one to the model.
                if (string.IsNullOrWhiteSpace(function.Name)) throw new InvalidOperationException("Model returned an invalid tool name.");
            }
            else throw new NotSupportedException("Unknown client tool call.");
        }
    }

    private async IAsyncEnumerable<LlmStreamEvent> StreamModelAsync(LiveRun run, LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken token)
    {
        // Only pre-output transient failures are repeatable. Never splice a new response into an exposed stream.
        for (int attempt = 0; ; attempt++)
        {
            bool exposed = false;
            TimeSpan? retryDelay = null;
            await using var stream = _stream(request, token).GetAsyncEnumerator(token);
            while (true)
            {
                bool next;
                try { next = await stream.MoveNextAsync(); }
                catch (Exception error) when (request.Resources is null && !token.IsCancellationRequested && !exposed && attempt < 2
                    && RetryDelay(error, attempt) is not null)
                {
                    retryDelay = RetryDelay(error, attempt);
                    run.UsageUnknown = true;
                    break;
                }
                if (!next) { yield break; }
                exposed = true;
                yield return stream.Current;
            }
            await Task.Delay(retryDelay!.Value, token);
        }
    }

    private static TimeSpan? RetryDelay(Exception error, int attempt)
    {
        if (error is not HttpRequestException && error is not LlmProviderException
            {
                StatusCode: System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.BadGateway
                or System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout
            }) { return null; }
        var delay = (error as LlmProviderException)?.RetryAfter ?? TimeSpan.FromMilliseconds(250 * (attempt + 1));
        return delay >= TimeSpan.Zero && delay <= TimeSpan.FromSeconds(30) ? delay : null;
    }

    private sealed record Segment(ChannelReader<LlmAgentEvent> Reader, CancellationTokenSource Cancellation, Task Producer);

    private sealed class LiveRun(LlmAgentTurnRequest turn, string id) : IDisposable
    {
        public string Id { get; } = id;
        public LlmAgentTurnRequest Turn { get; } = turn;
        public CancellationTokenSource Cancellation { get; } = new();
        public string UserEntryId { get; set; } = "";
        public HashSet<string> Protected { get; } = new(StringComparer.Ordinal);
        public Queue<string> Steering { get; } = new();
        public string? PauseId { get; set; }
        public int Iterations { get; set; }
        public int UsageCalls { get; set; }
        public int ProviderOverflowCount { get; set; }
        public LlmUsage? Usage { get; set; }
        public bool UsageUnknown { get; set; }
        public bool Terminal { get; set; }
        public void Dispose() => Cancellation.Dispose();
    }
}
