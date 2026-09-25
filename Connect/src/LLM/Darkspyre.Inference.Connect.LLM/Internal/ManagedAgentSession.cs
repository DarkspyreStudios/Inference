using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>One live context owner. No database, host filesystem tools, or durable run recovery.</summary>
internal sealed class ManagedAgentSession : ILlmAgentSession
{
    private readonly object _sync = new();
    private readonly LlmConnection _connection;
    private readonly LlmAgentSessionOptions _options;
    private readonly LlmAgentExecutionOptions _execution;
    private readonly IReadOnlyList<LlmModel> _models;
    private readonly ILlmContextEstimator _estimator;
    private readonly Func<string, LlmProviderCapabilities> _capabilities;
    private readonly Func<LlmChatRequest, CancellationToken, IAsyncEnumerable<LlmStreamEvent>> _stream;
    private readonly Dictionary<string, LlmAgentToolDefinition> _tools;
    private LlmAgentContext _context;
    // Messages whose response state was left out at start; reported once, on the first turn.
    private int _startOmittedResponseStates;
    private LiveRun? _run;
    private bool _segmentActive;
    private bool _disposed;
    private Task _producer = Task.CompletedTask;
    private LlmAgentContextStatus? _latestStatus;

    // Managed runs host-executed tools; their processes belong to the host, not this runtime.
    private readonly NoAgentResources _resources = new(LlmAgentResourceStopStatus.Unsupported);

    public ManagedAgentSession(LlmConnection connection, LlmAgentSessionOptions options,
        IReadOnlyList<LlmModel> models, ILlmContextEstimator estimator,
        Func<LlmChatRequest, CancellationToken, IAsyncEnumerable<LlmStreamEvent>> stream,
        Func<string, LlmProviderCapabilities> capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelId);
        AgentResourceOptions.Validate(ManagedAgentPolicy.Capabilities, options.AllowRuntimeBackgroundWork, options.ResourceOutputRetentionBytes);
        _connection = connection;
        _execution = options.ExecutionOptions ?? new();
        _models = Array.AsReadOnly(models.ToArray());
        _estimator = estimator;
        _stream = stream;
        _capabilities = capabilities;
        ManagedAgentPolicy.Validate(_execution, Model(options.ModelId));
        if (options.Tools.Any(tool => tool.DeferLoading))
        {
            throw new NotSupportedException("Managed agents do not implement deferred tool loading.");
        }
        if (options.Tools.Count > 0 && options.ToolHandler is null)
        {
            throw new ArgumentException("Managed tools require a host tool handler.");
        }
        _options = options with
        {
            Tools = Array.AsReadOnly(options.Tools.Select(tool => tool with { InputSchema = tool.InputSchema.Clone() }).ToArray()),
            Context = null
        };
        _tools = _options.Tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        (_context, _startOmittedResponseStates) = ManagedAgentContext.Import(
            options.Context ?? new(), connection, options.ModelId, options.UnboundResponseState);
        ThreadId = options.ThreadId ?? Guid.NewGuid().ToString();
        SessionId = options.SessionId ?? ThreadId;
        ArgumentException.ThrowIfNullOrWhiteSpace(ThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SessionId);
        _ = Measure(_context, BaseTurn());
    }

    public string ThreadId { get; }
    public string SessionId { get; }

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
            var proposal = ManagedAgentContext.Import(proposedContext, _connection, turn.ModelId!);
            if (_run is not null) { proposal = WithQueuedSteering(proposal, _run); }
            ContextEstimate? used = EstimateInputTokens(proposal, turn);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ManagedAgentPolicy.Account(turn.ModelId!, used,
                _execution.ContextPolicy, Model(turn.ModelId!)) with
            {
                ContextRevision = _context.Revision,
                OutputLimitEnforcement = _capabilities(turn.ModelId!).OutputLimitEnforcement
            });
        }
    }

    public Task<LlmAgentContext> CaptureContextAsync(LlmAgentContextCaptureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireCapability(Capabilities.SupportsContextCapture);
            if (_segmentActive) { throw new InvalidOperationException("Capture requires settled execution."); }
            return Task.FromResult(ManagedAgentContext.Copy(_context, options?.IncludeAttachmentData == true));
        }
    }

    public Task ReplaceContextAsync(LlmAgentContext context, long expectedRevision, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireCapability(Capabilities.SupportsContextReplacement);
            RequireIdle();
            RequireRevision(expectedRevision);
            var replacement = ManagedAgentContext.Import(context, _connection, _options.ModelId);
            _ = Measure(replacement, BaseTurn());
            _context = replacement with { Revision = checked(_context.Revision + 1) };
            _latestStatus = null;
            return Task.CompletedTask;
        }
    }

    public async IAsyncEnumerable<LlmAgentEvent> RunTurnAsync(LlmAgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LiveRun run;
        Segment segment;
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            RequireIdle();
            var turn = FreezeTurn(request);
            // Validate the entire projected request before mutating context or emitting start.
            _ = ManagedAgentContext.Import(_context, _connection, turn.ModelId!);
            string turnId = NewId();
            var user = new LlmAgentContextEntry(NewId(), turnId, UserMessage(turn));
            var projected = Append(_context, user);
            _latestStatus = Measure(projected, turn);
            run = new(turn, turnId);
            _context = projected;
            run.Protected.Add(user.Id);
            run.UserEntryId = user.Id;
            _run = run;
            _segmentActive = true;
            segment = StartSegment(run, null, cancellationToken);
        }
        await foreach (var item in ReadSegmentAsync(run, segment, cancellationToken)) { yield return item; }
    }

    public async IAsyncEnumerable<LlmAgentEvent> ContinueTurnAsync(LlmAgentTurnContinuation continuation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LiveRun run;
        Segment segment;
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
            var replacement = ManagedAgentContext.Import(continuation.ReplacementContext, _connection, run.Turn.ModelId!);
            var oldProtected = _context.Entries.Where(entry => run.Protected.Contains(entry.Id)).ToArray();
            var newProtected = replacement.Entries.Where(entry => run.Protected.Contains(entry.Id)).ToArray();
            if (oldProtected.Length != newProtected.Length
                || oldProtected.Where((entry, index) => !ManagedAgentContext.Equivalent(entry, newProtected[index])).Any())
            {
                throw new ArgumentException("Compaction must retain protected records intact and in order.");
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
            segment = StartSegment(run, continuation.PauseId, cancellationToken);
        }
        await foreach (var item in ReadSegmentAsync(run, segment, cancellationToken)) { yield return item; }
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
            RequireCapability(Capabilities.Steering == LlmAgentSteering.SafeBoundary);
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
        await cancellation;
        await producer.WaitAsync(cancellationToken);
    }

    public IAsyncEnumerable<LlmAgentEvent> ObserveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _resources.ObserveAsync(cancellationToken);
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

    public async ValueTask DisposeAsync()
    {
        LiveRun? run;
        Task producer;
        Task cancellation;
        lock (_sync)
        {
            if (_disposed) { return; }
            _disposed = true;
            _resources.Dispose();
            run = _run;
            producer = _producer;
            cancellation = run?.Cancellation.CancelAsync() ?? Task.CompletedTask;
        }
        await cancellation;
        await producer;
        lock (_sync)
        {
            _run = null;
            if (!_segmentActive) { run?.Dispose(); }
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
        _producer = Task.Run(() => ProduceAsync(run, continuedPause, channel.Writer, cancellation.Token), CancellationToken.None);
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
        finally
        {
            await segment.Cancellation.CancelAsync();
            await segment.Producer;
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
        try
        {
            await writer.WriteAsync(continuedPause is null
                ? new LlmAgentTurnStartedEvent(ThreadId, run.Id)
                : new LlmAgentTurnContinuedEvent(ThreadId, run.Id, continuedPause), token);
            if (_context.Entries.Any(entry => entry.Message.OmittedAttachments.Count > 0))
            {
                await writer.WriteAsync(new LlmAgentWarningEvent(ThreadId, run.Id,
                    "Some restored attachments are unavailable; omission descriptors are not sent to the model."), token);
            }
            if (continuedPause is null && Interlocked.Exchange(ref _startOmittedResponseStates, 0) is int omitted and > 0)
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
                    request = Request(_context, run.Turn);
                    status = _latestStatus = Measure(_context, run.Turn);
                }
                await writer.WriteAsync(new LlmAgentContextStatusEvent(ThreadId, run.Id, status), token);
                if (status.NeedsCompaction is true)
                {
                    await PauseAsync(run, status, LlmAgentCompactionReason.Threshold, writer, token);
                    return;
                }
                string stepId = NewId();
                LlmChatResult? result = null;
                try
                {
                    await foreach (var item in StreamModelAsync(run, request, token))
                    {
                        switch (item)
                        {
                            case LlmTextDeltaEvent text:
                                await writer.WriteAsync(new LlmAgentTextDeltaEvent(ThreadId, run.Id, stepId, text.Delta), token);
                                break;
                            case LlmReasoningDeltaEvent reasoning:
                                await writer.WriteAsync(new LlmAgentReasoningDeltaEvent(ThreadId, run.Id, stepId,
                                    reasoning.Delta, reasoning.IsSummary), token);
                                break;
                            case LlmCompletionEvent completion:
                                if (result is not null) { throw new InvalidOperationException("Duplicate model completion."); }
                                result = completion.Result;
                                break;
                        }
                    }
                }
                catch (LlmProviderException error) when (error.ProviderCode == "context_length_exceeded")
                {
                    await PauseAsync(run, status with { NeedsCompaction = true }, LlmAgentCompactionReason.ProviderOverflow, writer, token);
                    return;
                }
                if (result is null) { throw new InvalidOperationException("Model stream ended without completion."); }
                run.Iterations++;
                run.Usage = run.UsageUnknown ? null : AddUsage(run.Usage, result.Usage, run.Iterations == 1);
                await writer.WriteAsync(new LlmAgentUsageEvent(ThreadId, run.Id, stepId, result.Usage, run.Usage), token);
                if (result.Status is not (LlmCompletionStatus.Completed or LlmCompletionStatus.RequiresAction))
                {
                    await CompleteAsync(run, writer, result.Status switch
                    {
                        LlmCompletionStatus.Refused => LlmAgentCompletionReason.Refusal,
                        LlmCompletionStatus.Incomplete => LlmAgentCompletionReason.Truncation,
                        _ => LlmAgentCompletionReason.ProviderFailure
                    }, token);
                    return;
                }
                if (result.StructuredOutput is { WasEvaluated: true, IsValid: false })
                {
                    await CompleteAsync(run, writer, LlmAgentCompletionReason.ProviderFailure, token);
                    return;
                }
                var assistant = new LlmAgentContextEntry(stepId, run.Id, ManagedAgentContext.CopyMessage(new("assistant", result.Text)
                {
                    ToolCalls = result.ToolCalls,
                    ResponseState = result.ResponseState
                }));
                ValidateCalls(result.ToolCalls);
                lock (_sync) { _context = Append(_context, assistant); }
                // Once calls enter context, every call must settle, even if an event write is cancelled.
                if (result.ToolCalls.Count > 0)
                {
                    try { await writer.WriteAsync(new LlmAgentMessageCompletedEvent(ThreadId, run.Id, stepId, result.Text), token); }
                    finally { await ExecuteToolsAsync(run, result.ToolCalls, assistant.Id, writer, token); }
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
                    await CompleteAsync(run, writer, LlmAgentCompletionReason.Completed, token);
                    return;
                }
            }
            await CompleteAsync(run, writer, LlmAgentCompletionReason.IterationLimit, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Best effort only: a cancelled/abandoned reader must never block tool settlement.
            writer.TryWrite(new LlmAgentTurnCompletedEvent(ThreadId, run.Id, LlmAgentTurnStatus.Interrupted, null)
            { Reason = LlmAgentCompletionReason.Interruption });
        }
        catch (Exception error)
        {
            // Host exceptions/opaque provider payloads are not exposed as diagnostics or context.
            try
            {
                await writer.WriteAsync(new LlmAgentTurnCompletedEvent(ThreadId, run.Id, LlmAgentTurnStatus.Failed,
                    error is LlmProviderException provider ? provider.Message : "Managed execution failed; inspect the host operation boundary.")
                { Reason = LlmAgentCompletionReason.ProviderFailure }, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
        finally
        {
            lock (_sync) { if (run.PauseId is null) { run.Terminal = true; } }
            writer.TryComplete();
        }
    }

    private async Task ExecuteToolsAsync(LiveRun run, IReadOnlyList<LlmToolCall> calls, string assistantId,
        ChannelWriter<LlmAgentEvent> writer, CancellationToken token)
    {
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
                        call.Name, JsonSerializer.SerializeToElement(new { }), false, "failed"), token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                }
                return new(call.Id, LlmToolOutput.FromText(message, isError: true));
            }
            try
            {
                await concurrency.WaitAsync(token);
                entered = true;
                if (!_tools.TryGetValue(call.Name, out var definition)) { return await FailedAsync("Unknown tool."); }
                var validation = StructuredOutput.Validate(call.ArgumentsJson,
                    new() { Name = definition.Name, Schema = definition.InputSchema }, LlmStructuredOutputEnforcement.LocalValidationOnly);
                if (!validation.IsValid) { return await FailedAsync("Tool arguments do not satisfy the declared schema."); }
                using var arguments = JsonDocument.Parse(call.ArgumentsJson);
                await writer.WriteAsync(new LlmAgentToolEvent(ThreadId, run.Id, call.Id, null, call.Name,
                    arguments.RootElement.Clone(), null, "started"), token);
                token.ThrowIfCancellationRequested();
                started = true;
                var output = await _options.ToolHandler!(new(ThreadId, run.Id, call.Id, null, call.Name,
                    arguments.RootElement.Clone()), token);
                // The output is kept whole; the route's projection decides what reaches the model when it is sent.
                var mapped = new LlmToolResult(call.Id, ManagedAgentContext.CopyOutput(output));
                // A completed external effect is retained even if cancellation prevents its event.
                if (!token.IsCancellationRequested)
                {
                    try
                    {
                        await writer.WriteAsync(new LlmAgentToolEvent(ThreadId, run.Id, call.Id, null, call.Name,
                            arguments.RootElement.Clone(), !output.IsError, "completed"), token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                }
                return mapped;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new(call.Id, LlmToolOutput.FromText(started ? "Tool interrupted; external outcome is unknown. Do not automatically replay."
                    : "Tool was not executed because the run was cancelled.", isError: true));
            }
            catch (Exception)
            {
                return await FailedAsync("Tool handler failed; do not automatically replay.");
            }
            finally { if (entered) { concurrency.Release(); } }
        }
        LlmToolResult[] results;
        if (parallel) { results = await Task.WhenAll(calls.Select(InvokeAsync)); }
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
            run.Protected.Clear();
            run.Protected.UnionWith([run.UserEntryId, assistantId, entry.Id]);
        }
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
            bool compactable = protectedOnly.Entries.Count < _context.Entries.Count
                && Measure(protectedOnly, run.Turn).NeedsCompaction is not true;
            pause = new(ThreadId, run.Id, run.PauseId, status, reason, compactable, run.Protected.ToArray());
        }
        await writer.WriteAsync(pause, token);
    }

    private async Task CompleteAsync(LiveRun run, ChannelWriter<LlmAgentEvent> writer, LlmAgentCompletionReason reason, CancellationToken token)
    {
        lock (_sync) { run.Terminal = true; }
        await writer.WriteAsync(new LlmAgentTurnCompletedEvent(ThreadId, run.Id,
            reason == LlmAgentCompletionReason.Completed ? LlmAgentTurnStatus.Completed : LlmAgentTurnStatus.Failed,
            reason == LlmAgentCompletionReason.Completed ? null : reason.ToString())
        { Reason = reason }, token);
    }

    private LlmChatRequest Request(LlmAgentContext context, LlmAgentTurnRequest turn) => new()
    {
        ModelId = turn.ModelId!,
        Instructions = turn.Instructions ?? _options.Instructions,
        Messages = context.Entries.Select(entry => entry.Message with { OmittedAttachments = [] }).ToArray(),
        ConversationKey = ThreadId,
        ReasoningLevel = turn.ReasoningLevel,
        StructuredOutput = turn.OutputSchema is { } schema ? new() { Name = "agent_output", Schema = schema } : null,
        Tools = _options.Tools.Select(tool => new LlmToolDefinition
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = tool.InputSchema,
            Strict = tool.Strict
        }).ToArray()
    };

    private LlmAgentContextStatus Measure(LlmAgentContext context, LlmAgentTurnRequest turn)
    {
        ManagedAgentPolicy.Validate(_execution, Model(turn.ModelId!));
        ContextEstimate? used = EstimateInputTokens(context, turn);
        return ManagedAgentPolicy.Status(turn.ModelId!, context.Revision, used, _execution.ContextPolicy, Model(turn.ModelId!));
    }

    private ContextEstimate? EstimateInputTokens(LlmAgentContext context, LlmAgentTurnRequest turn)
    {
        ContextEstimate? used = _estimator.EstimateInputTokens(_connection, Request(context, turn));
        // Validate the real projection first, but never count unavailable attachment bytes as free input.
        return context.Entries.Any(entry => entry.Message.OmittedAttachments.Count > 0) ? null : used;
    }

    private LlmAgentTurnRequest FreezeTurn(LlmAgentTurnRequest turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (turn.Input.Count == 0 && turn.Attachments.Count == 0) { throw new ArgumentException("A turn requires input."); }
        _ = TextContent(turn.Input);
        LlmAttachments.Validate(turn.Attachments, true, true, nameof(turn));
        var capabilities = _capabilities(turn.ModelId ?? _options.ModelId);
        if (!capabilities.SupportsAgentSessions || (_tools.Count > 0 && !capabilities.SupportsAgentToolUse)
            || (turn.Instructions is not null && !capabilities.SupportsAgentTurnInstructions))
        {
            throw new NotSupportedException("The selected model disables the requested agent operation.");
        }
        return turn with
        {
            ModelId = turn.ModelId ?? _options.ModelId,
            Input = Array.AsReadOnly(turn.Input.ToArray()),
            Attachments = Array.AsReadOnly(turn.Attachments.Select(attachment => attachment with { Data = attachment.Data.ToArray() }).ToArray()),
            OutputSchema = turn.OutputSchema?.Clone()
        };
    }

    private LlmAgentTurnRequest BaseTurn() => new() { Input = [], ModelId = _options.ModelId };
    private LlmAgentCapabilities Capabilities => _capabilities(_run?.Turn.ModelId ?? _options.ModelId).Agent;
    private static void RequireCapability(bool supported)
    {
        if (!supported) { throw new NotSupportedException("The selected connection disables this agent operation."); }
    }
    private LlmModel? Model(string id) => _models.FirstOrDefault(model => model.Id == id);
    private static LlmChatMessage UserMessage(LlmAgentTurnRequest turn) => new("user", TextContent(turn.Input)) { Attachments = turn.Attachments };
    private static string TextContent(IReadOnlyList<LlmAgentContent> input)
    {
        if (input.Any(item => item is not LlmAgentTextContent))
        {
            throw new NotSupportedException("Managed agents accept text content and binary Attachments; use attachments instead of provider-specific image/audio URLs.");
        }
        return string.Join("\n", input.Cast<LlmAgentTextContent>().Select(item => item.Text));
    }
    private void AdmitSteering(LiveRun run)
    {
        while (run.Steering.TryDequeue(out var text))
        {
            var entry = new LlmAgentContextEntry(NewId(), run.Id, new("user", text));
            _context = Append(_context, entry);
            run.Protected.Add(entry.Id);
        }
    }
    private static LlmAgentContext WithQueuedSteering(LlmAgentContext context, LiveRun run) => context with
    {
        Entries = [.. context.Entries, .. run.Steering.Select(text => new LlmAgentContextEntry(NewId(), run.Id, new("user", text)))]
    };
    private static LlmAgentContext Append(LlmAgentContext context, LlmAgentContextEntry entry, bool increment = true) =>
        context with { Entries = [.. context.Entries, entry], Revision = increment ? checked(context.Revision + 1) : context.Revision };
    private static string NewId() => Guid.NewGuid().ToString("N");
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
    private static LlmChatUsage? AddUsage(LlmChatUsage? total, LlmChatUsage? usage, bool first) => first ? usage
        : total is null || usage is null ? null : new(
            SumTokens(total.InputTokens, usage.InputTokens), SumTokens(total.OutputTokens, usage.OutputTokens),
            SumTokens(total.TotalTokens, usage.TotalTokens));
    private static int? SumTokens(int? left, int? right) => left is >= 0 && right is >= 0
        && (long)left.Value + right.Value <= int.MaxValue ? left.Value + right.Value : null;

    private void ValidateCalls(IReadOnlyList<LlmToolCall> calls)
    {
        var ids = _context.Entries.SelectMany(entry => entry.Message.ToolCalls)
            .Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var call in calls)
        {
            if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name) || !ids.Add(call.Id))
            {
                throw new InvalidOperationException("Model returned invalid or duplicate tool-call identities.");
            }
            using var arguments = JsonDocument.Parse(call.ArgumentsJson);
            if (arguments.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Model returned incomplete tool arguments.");
            }
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
                catch (Exception error) when (!token.IsCancellationRequested && !exposed && attempt < 2
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
        public LlmChatUsage? Usage { get; set; }
        public bool UsageUnknown { get; set; }
        public bool Terminal { get; set; }
        public void Dispose() => Cancellation.Dispose();
    }
}
