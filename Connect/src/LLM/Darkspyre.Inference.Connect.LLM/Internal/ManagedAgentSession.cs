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
    private readonly LearnedModelContext _learned;
    private readonly ConversationCompactor _compactor;
    // Entries an opening left empty by omitting their native compaction state; summarized at the first decision point.
    private HashSet<string> _emptiedByOmission;
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
        Func<string, LlmProviderCapabilities> capabilities, LearnedModelContext learned, Action? openingFence = null,
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
            supportsResources, learned);
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
        Func<string, LlmProviderCapabilities> capabilities, bool supportsResources, LearnedModelContext learned)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelId);
        _supportsResources = supportsResources;
        _learned = learned;
        _compactor = new(MeasureForCompactionAsync, SideRequestAsync, EstimateText, options.CallerServices?.Transcript);
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
        _emptiedByOmission = EmptiedByOmission(options.Context, _context);
        ThreadId = options.ThreadId ?? Guid.NewGuid().ToString();
        SessionId = options.SessionId ?? ThreadId;
        ArgumentException.ThrowIfNullOrWhiteSpace(ThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SessionId);
        _ = Measure(_context, BaseTurn(), ResourceSizes.None);
    }

    public string ThreadId { get; }
    public string SessionId { get; }

    public LlmCompactionResponsibility CompactionResponsibility => LlmCompactionResponsibility.Provider;

    public async Task<LlmAgentCompactedEvent?> CompactNowAsync(CancellationToken cancellationToken = default)
    {
        using var operation = _resourceCallbacks.Enter(cancellationToken);
        cancellationToken = operation.Token;
        LlmAgentContext source;
        CompactionLimits? limits;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireCapability(Capabilities.SupportsCompactNow);
            RequireIdle();
            source = _context;
            limits = Limits(_options.ModelId);
            if (source.Entries.Count == 0 || limits is null) return null;
            _segmentActive = true;
        }
        try
        {
            string lastTurnId = source.Entries[^1].TurnId;
            CompactionResult result = await _compactor.CompactAsync(source, _options.ModelId, lastTurnId,
                new HashSet<string>(StringComparer.Ordinal), limits, static (_, _) => ValueTask.CompletedTask,
                cancellationToken).ConfigureAwait(false);
            if (Unchanged(result)) return null;
            long revision;
            lock (_sync)
            {
                FenceResourceCallback(cancellationToken);
                _context = result.Context with { Revision = revision = checked(_context.Revision + 1) };
                _latestStatus = null;
            }
            return Compacted(lastTurnId, NewId(), revision, result);
        }
        finally
        {
            lock (_sync) { _segmentActive = false; }
        }
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

    public async Task<LlmAgentContextStatus> GetContextStatusAsync(LlmAgentTurnRequest? prospectiveTurn = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IInferenceResourceAccess? access;
        InferenceResourceReference[] references;
        LlmAgentTurnRequest? prospective = null;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireCapability(Capabilities.SupportsContextStatus);
            if (prospectiveTurn is not null)
            {
                RequireIdle();
                prospective = FreezeTurn(prospectiveTurn);
            }
            access = _callerServices?.Resources;
            references = [.. ResourceReferences(_context, prospective ?? _run?.Turn ?? BaseTurn())];
        }
        var resources = await ResolveResourcesAsync(access, references, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (prospective is { } turn)
            {
                RequireIdle();
                var preview = Append(_context, new("preview", "preview", UserMessage(turn)), increment: false);
                return Measure(preview, turn, resources);
            }
            return _segmentActive && _latestStatus is not null
                ? _latestStatus : Measure(_run is null ? _context : WithQueuedSteering(_context, _run), _run?.Turn ?? BaseTurn(), resources);
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
            ContextEstimate? used = Corrected(EstimateInputTokens(proposal, turn, ResourceSizes.None), turn.ModelId!);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ManagedAgentPolicy.Account(turn.ModelId!, used,
                _execution.ContextPolicy, Model(turn.ModelId!), _learned.Limit(_connection.Transport, turn.ModelId!)) with
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
            _ = Measure(replacement, BaseTurn(), ResourceSizes.None);
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
            _latestStatus = Measure(projected, turn, ResourceSizes.None);
            run = new(turn, turnId);
            _context = projected;
            _run = run;
            _segmentActive = true;
            segment = StartSegment(run, cancellationToken);
        }
        admission.Dispose();
        await foreach (var item in ReadSegmentAsync(run, segment, cancellationToken)) { yield return item; }
    }

    public async IAsyncEnumerable<LlmAgentEvent> ContinueTurnAsync(LlmAgentTurnContinuation continuation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireCapability(Capabilities.SupportsCompactionHandoff);
        yield break;
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
                _run = null;
                if (!_segmentActive) { run?.Dispose(); }
            }
        }
    }

    // Called while admission owns _sync, before interrupt/disposal can observe the run.
    private Segment StartSegment(LiveRun run, CancellationToken cancellationToken)
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
                await ProduceAsync(run, channel.Writer, cancellation.Token).ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
        }, CancellationToken.None);
        return new(channel.Reader, cancellation, _producer);
    }

    private async IAsyncEnumerable<LlmAgentEvent> ReadSegmentAsync(LiveRun run, Segment segment,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in segment.Reader.ReadAllAsync(cancellationToken)) { yield return item; }
        }
        finally { await DrainSegmentAsync(run, segment).ConfigureAwait(false); }
    }

    private async Task DrainSegmentAsync(LiveRun run, Segment segment)
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
                _run = null;
                run.Dispose();
            }
            segment.Cancellation.Dispose();
        }
    }

    private async Task ProduceAsync(LiveRun run, ChannelWriter<LlmAgentEvent> writer, CancellationToken token)
    {
        bool executingMixedTools = false;
        try
        {
            await writer.WriteAsync(new LlmAgentTurnStartedEvent(ThreadId, run.Id), token);
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
            if (Interlocked.Exchange(ref _startOmittedContinuationStates, 0) is int omitted and > 0)
            {
                await writer.WriteAsync(new LlmAgentWarningEvent(ThreadId, run.Id,
                    $"Response state from {omitted} earlier messages was not carried to this model."), token);
            }
            bool preserve = _execution.ContextPolicy.PreserveSuppliedContext;
            HashSet<string> emptied;
            lock (_sync) { (emptied, _emptiedByOmission) = (_emptiedByOmission, []); }
            if (emptied.Count > 0 && !preserve && !await SummarizeOmittedAsync(run, emptied, writer, token).ConfigureAwait(false)) return;
            while (run.Iterations < _execution.MaxModelIterations)
            {
                LlmChatRequest request;
                LlmAgentContextStatus status;
                ContextEstimate? estimate;
                IInferenceResourceAccess? access;
                InferenceResourceReference[] references;
                lock (_sync)
                {
                    access = _callerServices?.Resources;
                    references = [.. ResourceReferences(_context, run.Turn)];
                }
                var resources = await ResolveResourcesAsync(access, references, token).ConfigureAwait(false);
                lock (_sync)
                {
                    AdmitSteering(run);
                    request = Request(_context, run.Turn, firstStep: run.Iterations == 0);
                    estimate = EstimateInputTokens(_context, run.Turn, resources);
                    status = _latestStatus = Status(_context, run.Turn, estimate);
                }
                await writer.WriteAsync(new LlmAgentContextStatusEvent(ThreadId, run.Id, status), token);
                if (status.NeedsCompaction is true && !preserve)
                {
                    if (!await CompactRunAsync(run, writer, token).ConfigureAwait(false)) return;
                    lock (_sync)
                    {
                        request = Request(_context, run.Turn, firstStep: run.Iterations == 0);
                        estimate = EstimateInputTokens(_context, run.Turn, resources);
                        status = _latestStatus = Status(_context, run.Turn, estimate);
                    }
                    await writer.WriteAsync(new LlmAgentContextStatusEvent(ThreadId, run.Id, status), token);
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
                    if (preserve || run.OverflowResent)
                    {
                        await CompleteAsync(run, writer, LlmAgentCompletionReason.ProviderFailure, token, error.Message);
                        return;
                    }
                    // The first too-long refusal teaches the model's size, compacts whatever the measure says and
                    // sends the same step again; a second refusal in the turn ends it.
                    run.OverflowResent = true;
                    if (estimate is { } refused)
                        _learned.ObserveRefusal(_connection.Transport, request.ModelId, refused.Tokens, null, null,
                            Model(request.ModelId)?.MaxContextTokens);
                    if (Limits(request.ModelId) is null)
                    {
                        await CompleteAsync(run, writer, LlmAgentCompletionReason.ProviderFailure, token, error.Message);
                        return;
                    }
                    if (!await CompactRunAsync(run, writer, token).ConfigureAwait(false)) return;
                    continue;
                }
                if (request.Resources is not null)
                {
                    try { FenceResourceCallback(token); }
                    catch (Exception error) { ExceptionDispatchInfo.Capture(LlmResourcePreparation.AsResourceFailure(error, token)).Throw(); throw; }
                }
                if (result is null) { throw new InvalidOperationException("Model stream ended without completion."); }
                if (estimate is { IncludesProviderReport: false } observed && result.Usage?.InputTokens is int reported
                    && Limits(request.ModelId) is { } window)
                    _learned.ObserveReport(_connection.Transport, request.ModelId, observed.Tokens, reported, window.UsableWindow);
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
                AppendAssistant(stepId, run, result);
                // Mixed calls retain actual completed results; interruption leaves the remaining tail unresolved.
                if (result.ToolCalls.Count > 0)
                {
                    executingMixedTools = result.ToolCalls.Any(call => call is LlmShellCall);
                    try { await writer.WriteAsync(new LlmAgentMessageCompletedEvent(ThreadId, run.Id, stepId, result.Text), token); }
                    finally { await ExecuteToolsAsync(run, result.ToolCalls, writer, token); }
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
            lock (_sync) { run.Terminal = true; }
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

    private async Task ExecuteToolsAsync(LiveRun run, IReadOnlyList<LlmClientToolCall> returnedCalls,
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

    private LlmAgentContextStatus Measure(LlmAgentContext context, LlmAgentTurnRequest turn,
        IReadOnlyDictionary<InferenceResourceReference, InferenceResourceDescriptor?> resources) =>
        Status(context, turn, EstimateInputTokens(context, turn, resources));

    /// <summary>The status of a request whose local estimate is <paramref name="estimate"/>, corrected with what the model taught.</summary>
    private LlmAgentContextStatus Status(LlmAgentContext context, LlmAgentTurnRequest turn, ContextEstimate? estimate)
    {
        LlmAgentContextPolicy policy = ManagedAgentPolicy.Normalize(_execution, Model(turn.ModelId!)).ContextPolicy;
        return ManagedAgentPolicy.Status(turn.ModelId!, context.Revision, Corrected(estimate, turn.ModelId!), policy,
            Model(turn.ModelId!), _learned.Limit(_connection.Transport, turn.ModelId!));
    }

    /// <summary>An estimate anchored on a provider report is already the provider's count; any other is corrected.</summary>
    private ContextEstimate? Corrected(ContextEstimate? estimate, string modelId) =>
        estimate is { IncludesProviderReport: false } local
            ? local with { Tokens = _learned.Correct(_connection.Transport, modelId, local.Tokens) }
            : estimate;

    /// <summary>The model's limits under the one limit rule; null when no window is known.</summary>
    private CompactionLimits? Limits(string modelId)
    {
        LlmModel? model = Model(modelId);
        return CompactionLimits.For(model?.MaxContextTokens, _learned.Limit(_connection.Transport, modelId),
            model?.MaxOutputTokens, ManagedAgentPolicy.Normalize(_execution, model).ContextPolicy);
    }

    /// <summary>
    /// Compacts the running turn's context, installs it as the next revision and reports it. Returns false when the turn
    /// ended because compaction failed; the context is then unchanged.
    /// </summary>
    private async Task<bool> CompactRunAsync(LiveRun run, ChannelWriter<LlmAgentEvent> writer, CancellationToken token)
    {
        LlmAgentContext source;
        CompactionLimits? limits;
        HashSet<string> protectedIds;
        lock (_sync)
        {
            source = _context;
            limits = Limits(run.Turn.ModelId!);
            protectedIds = ProtectedEntries(source, run.Id);
        }
        if (limits is null) return true;
        string itemId = NewId();
        return await ReportCompactionAsync(run, itemId, writer, () => _compactor.CompactAsync(source,
            run.Turn.ModelId!, run.Id, protectedIds, limits, Stage(run, itemId, writer), token), installUnchanged: false, token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the entries an opening left empty by omitting native compaction state with a summary of the transcript
    /// turns the context no longer holds. Returns false when the turn ended because the summary failed.
    /// </summary>
    private async Task<bool> SummarizeOmittedAsync(LiveRun run, HashSet<string> emptied, ChannelWriter<LlmAgentEvent> writer,
        CancellationToken token)
    {
        LlmAgentContext source;
        CompactionLimits? limits;
        lock (_sync)
        {
            source = _context;
            limits = Limits(run.Turn.ModelId!);
        }
        if (limits is null)
        {
            lock (_sync)
            {
                _context = source with
                {
                    Entries = [.. source.Entries.Where(entry => !emptied.Contains(entry.Id))],
                    Revision = checked(source.Revision + 1)
                };
            }
            return true;
        }
        string itemId = NewId();
        return await ReportCompactionAsync(run, itemId, writer, () => _compactor.SummarizeOmittedAsync(source,
            run.Turn.ModelId!, run.Id, emptied, limits, Stage(run, itemId, writer), token), installUnchanged: true, token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one compaction for the running turn, installs a changed result as the next revision, and reports it with
    /// its side requests' usage. A failure ends the turn with its message and the usage it reported.
    /// </summary>
    private async Task<bool> ReportCompactionAsync(LiveRun run, string itemId, ChannelWriter<LlmAgentEvent> writer,
        Func<Task<CompactionResult>> compact, bool installUnchanged, CancellationToken token)
    {
        CompactionResult result;
        try { result = await compact().ConfigureAwait(false); }
        catch (Exception error) when (!token.IsCancellationRequested
            && error is CompactionFailedException or LlmProviderException)
        {
            LlmUsage? failedUsage = error is CompactionFailedException failed ? failed.Usage : ((LlmProviderException)error).Usage;
            if (failedUsage is not null)
            {
                AddUsage(run, failedUsage);
                await writer.WriteAsync(new LlmAgentUsageEvent(ThreadId, run.Id, NewId(), failedUsage, run.Usage), token);
            }
            await CompleteAsync(run, writer, LlmAgentCompletionReason.ProviderFailure, token, error.Message);
            return false;
        }
        bool unchanged = Unchanged(result);
        long revision = 0;
        if (!unchanged || installUnchanged)
        {
            await writer.WriteAsync(new LlmAgentActivityEvent(ThreadId, run.Id, itemId, "Applying the compacted context"), token);
            lock (_sync)
            {
                _context = result.Context with { Revision = revision = checked(_context.Revision + 1) };
                if (result.Usage is not null) AddUsage(run, result.Usage);
            }
        }
        await writer.WriteAsync(new LlmAgentActivityEvent(ThreadId, run.Id, itemId, string.Empty), token);
        if (unchanged) return true;
        string requestId = NewId();
        await writer.WriteAsync(Compacted(run.Id, requestId, revision, result), token);
        if (result.Usage is not null)
            await writer.WriteAsync(new LlmAgentUsageEvent(ThreadId, run.Id, requestId, result.Usage, run.Usage), token);
        return true;
    }

    /// <summary>The working-indicator stage of one automatic compaction, as an activity event.</summary>
    private Func<string, CancellationToken, ValueTask> Stage(LiveRun run, string itemId, ChannelWriter<LlmAgentEvent> writer) =>
        (text, token) => writer.WriteAsync(new LlmAgentActivityEvent(ThreadId, run.Id, itemId, text), token);

    /// <summary>A compaction that shortened nothing and summarized nothing.</summary>
    private static bool Unchanged(CompactionResult result) =>
        result is { DroppedMessageCount: null, ShortenedToolOutputCount: 0, RemovedImageCount: 0 };

    private LlmAgentCompactedEvent Compacted(string turnId, string requestId, long revision, CompactionResult result) =>
        new(ThreadId, turnId, requestId, revision, result.DroppedMessageCount, result.Usage)
        {
            ShortenedToolOutputCount = result.ShortenedToolOutputCount,
            RemovedImageCount = result.RemovedImageCount
        };

    /// <summary>
    /// The current turn's entries that hold a result of a tool marked <see cref="LlmAgentToolDefinition.ProtectedResults"/>,
    /// or hold its call.
    /// </summary>
    private HashSet<string> ProtectedEntries(LlmAgentContext context, string turnId)
    {
        var protectedCalls = context.Entries.Where(entry => entry.TurnId == turnId)
            .SelectMany(entry => entry.Message.ToolCalls).OfType<LlmToolCall>()
            .Where(call => _tools.TryGetValue(call.Name, out var tool) && tool.ProtectedResults)
            .Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
        return context.Entries.Where(entry => entry.TurnId == turnId
                && (entry.Message.ToolCalls.Any(call => protectedCalls.Contains(call.Id))
                    || entry.Message.ToolResults.Any(result => protectedCalls.Contains(result.ToolCallId))))
            .Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The entries whose only content was continuation state the import omitted, such as a native compaction from
    /// another connection or model.
    /// </summary>
    private static HashSet<string> EmptiedByOmission(LlmAgentContext? original, LlmAgentContext imported)
    {
        var emptied = new HashSet<string>(StringComparer.Ordinal);
        if (original is null) return emptied;
        for (int index = 0; index < imported.Entries.Count && index < original.Entries.Count; index++)
        {
            LlmChatMessage before = original.Entries[index].Message, after = imported.Entries[index].Message;
            if (before.ContinuationState is not null && after is
                {
                    ContinuationState: null, Content.Length: 0, ToolCalls.Count: 0, ToolResults.Count: 0,
                    Attachments.Count: 0, OmittedAttachments.Count: 0
                })
                emptied.Add(imported.Entries[index].Id);
        }
        return emptied;
    }

    /// <summary>The corrected size of the request the current turn would send with <paramref name="context"/>.</summary>
    private async ValueTask<long> MeasureForCompactionAsync(LlmAgentContext context, CancellationToken token)
    {
        IInferenceResourceAccess? access;
        LlmAgentTurnRequest turn;
        InferenceResourceReference[] references;
        lock (_sync)
        {
            access = _callerServices?.Resources;
            turn = _run?.Turn ?? BaseTurn();
            references = [.. ResourceReferences(context, turn)];
        }
        var resources = await ResolveResourcesAsync(access, references, token).ConfigureAwait(false);
        lock (_sync)
        {
            return Corrected(EstimateInputTokens(context, turn, resources), turn.ModelId!)?.Tokens
                ?? throw new InvalidOperationException("The request size is unknown.");
        }
    }

    /// <summary>Sends one summary request on this conversation's connection and returns its result.</summary>
    private async Task<LlmChatResult> SideRequestAsync(LlmChatRequest request, CancellationToken token)
    {
        LlmChatResult? result = null;
        await foreach (LlmStreamEvent item in _stream(request, token).WithCancellation(token).ConfigureAwait(false))
            if (item is LlmChatCompletionEvent completion) result = completion.Result;
        return result ?? throw new InvalidOperationException("The summary request ended without completion.");
    }

    /// <summary>The corrected size of a text sent as one user message.</summary>
    private long? EstimateText(string text)
    {
        string modelId;
        lock (_sync) { modelId = _run?.Turn.ModelId ?? _options.ModelId; }
        return Corrected(_estimator.EstimateInputTokens(_connection,
            new LlmChatRequest { ModelId = modelId, Messages = [new("user", text)] }, ResourceSizes.None), modelId)?.Tokens;
    }

    private static void AddUsage(LiveRun run, LlmUsage? usage)
    {
        run.Usage = run.UsageUnknown ? null : UsageTotals.Add(run.Usage, usage, run.UsageCalls == 0);
        run.UsageCalls++;
    }

    private ContextEstimate? EstimateInputTokens(LlmAgentContext context, LlmAgentTurnRequest turn,
        IReadOnlyDictionary<InferenceResourceReference, InferenceResourceDescriptor?> resources) =>
        _estimator.EstimateInputTokens(_connection, Request(context, turn), resources);

    /// <summary>The caller-owned resources a request built from the context and turn sends.</summary>
    private static IEnumerable<InferenceResourceReference> ResourceReferences(LlmAgentContext context, LlmAgentTurnRequest turn) =>
        context.Entries.SelectMany(entry => ResourceSizes.References(entry.Message)).Concat(context.SelectedResources)
            .Concat(turn.Attachments.OfType<LlmResourceAttachment>().Select(attachment => attachment.Resource));

    /// <summary>Resolves descriptors outside the session lock, fenced like every resource callback.</summary>
    private async Task<IReadOnlyDictionary<InferenceResourceReference, InferenceResourceDescriptor?>> ResolveResourcesAsync(
        IInferenceResourceAccess? access, InferenceResourceReference[] references, CancellationToken token)
    {
        if (access is null || references.Length == 0) return ResourceSizes.None;
        using var callback = _resourceCallbacks.Enter(token);
        var resolved = await ResourceSizes.ResolveAsync(access, references, callback.Token).ConfigureAwait(false);
        FenceResourceCallback(callback.Token);
        return resolved;
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
            _context = Append(_context, new LlmAgentContextEntry(NewId(), run.Id, new("user", text)));
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
        public Queue<string> Steering { get; } = new();
        public int Iterations { get; set; }
        public int UsageCalls { get; set; }
        /// <summary>The turn's first too-long refusal was compacted and the step sent again.</summary>
        public bool OverflowResent { get; set; }
        public LlmUsage? Usage { get; set; }
        public bool UsageUnknown { get; set; }
        public bool Terminal { get; set; }
        public void Dispose() => Cancellation.Dispose();
    }
}
