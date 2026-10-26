using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.LLM;
using TensorSharp.Runtime;
using TensorSharp.Runtime.Grammar;
using TensorSharp.Server;
using Darkspyre.Inference.Tensor.Ggml;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

/// <summary>Runs Qwen GGUF language models locally through the GGML runtime.</summary>
public sealed partial class QwenLlmInference : ITensorLlmInference
{
    /// <summary>Qwen's thinking is on or off: <see cref="LlmReasoningLevel.None"/> turns it off, <see cref="LlmReasoningLevel.High"/> on.</summary>
    private static readonly System.Collections.Frozen.FrozenSet<LlmReasoningLevel> ReasoningLevels =
        System.Collections.Frozen.FrozenSet.ToFrozenSet([LlmReasoningLevel.None, LlmReasoningLevel.High]);

    private readonly ITensorModelPackManager _packs;
    private TensorModelPackDefinition? _definition;
    private readonly TensorBackendKind _requestedBackend;
    private readonly TensorExecutionCoordinator _coordinator;
    private readonly bool _ownsCoordinator;
    private readonly TensorModelLifetime _lifetime;
    private readonly IQwenModelRuntime _runtime;
    private readonly GgmlRuntimeOwner _runtimeOwner;
    private readonly string _scratchDirectory;
    private QwenImageFiles? _images;
    private TensorModelPackLease? _modelLease;
    private TensorBackendKind? _loadedBackend;
    private readonly object _stopGate = new();
    private volatile bool _disposed;
    private volatile bool _stopRequested;
    private volatile bool _releaseRequired;
    private Task? _stopAttempt;
    private Task? _disposal;

    /// <summary>Creates an instance that loads packs on the requested backend and writes request images under
    /// <paramref name="scratchDirectory"/>. Without a coordinator it creates and owns its own; without a runtime owner it uses the shared one.</summary>
    public QwenLlmInference(
        ITensorModelPackManager packs,
        TensorBackendKind backend, string scratchDirectory,
        TensorExecutionCoordinator? coordinator = null, GgmlRuntimeOwner? runtimeOwner = null)
        : this(packs, new QwenModelRuntime(), backend, scratchDirectory, coordinator, runtimeOwner) { }

    internal QwenLlmInference(ITensorModelPackManager packs, IQwenModelRuntime runtime,
        TensorBackendKind backend, string scratchDirectory, TensorExecutionCoordinator? coordinator = null,
        GgmlRuntimeOwner? runtimeOwner = null)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(runtime);
        _scratchDirectory = scratchDirectory;
        _packs = packs;
        _runtime = runtime;
        _runtimeOwner = runtimeOwner ?? GgmlRuntimeOwner.GetOrCreate();
        _lifetime = new(this);

        _requestedBackend = backend;
        _coordinator = coordinator ?? new TensorExecutionCoordinator();
        _ownsCoordinator = coordinator is null;
    }

    /// <inheritdoc />
    public InferenceImplementationDescriptor Descriptor => QwenSetup.Definition.Implementation;

    /// <inheritdoc />
    public async Task<LlmCapabilities> GetCapabilitiesAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        await ValidateModelIdAsync(modelId, cancellationToken).ConfigureAwait(false);
        return CapabilitiesFor(modelId);
    }

    private LlmCapabilities CapabilitiesFor(string? modelId) => new()
        {
            OutputLimitEnforcement = LlmOutputLimitEnforcement.EngineEnforced,
            SupportsStreaming = InferenceSupport.Supported,
            SupportsModelListing = InferenceSupport.Supported,
            SupportsJsonSchema = InferenceSupport.Supported,
            SupportsJsonObject = InferenceSupport.Supported,
            ReasoningLevels = ReasoningLevels,
            SupportsReasoningText = InferenceSupport.Supported,
            SupportsStreamingUsage = InferenceSupport.Supported,
            SupportsTemperature = InferenceSupport.Supported,
            SupportsTopP = InferenceSupport.Supported,
            SupportsTopK = InferenceSupport.Supported,
            SupportsSeed = InferenceSupport.Supported,
            SupportsPresencePenalty = InferenceSupport.Supported,
            SupportsFrequencyPenalty = InferenceSupport.Supported,
            SupportsToolUse = InferenceSupport.Supported,
            Tools = new() { SupportsParallelCalls = InferenceSupport.Supported },
            SupportsPromptImages = _definition is not null && (modelId is null || modelId == _definition.Key.ModelId)
                && _runtime.VisionEncoderLoaded ? InferenceSupport.Supported : InferenceSupport.Unsupported,
            SupportsPromptDocuments = InferenceSupport.Unsupported
        };

    /// <summary>Qwen reads its local model packs on every call and keeps no list.</summary>
    public Task<IReadOnlyList<LlmModelInfo>> RefreshModelsAsync(CancellationToken cancellationToken = default) =>
        ListModelsAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var models = new List<LlmModelInfo>();
        foreach (TensorModelPackDefinition definition in await _packs.GetDefinitionsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (definition.Capability != TensorCapability.Llm || definition.RuntimeArchitecture != TensorLlmModels.QwenRuntimeArchitecture) continue;
            models.Add(new LlmModelInfo(definition.Key.ModelId, definition.Key.ModelId, "Qwen",
                CapabilitiesFor(definition.Key.ModelId))
            {
                // Qwen declares no separate output limit: a reply can use whatever context is left.
                MaxContextTokens = definition.Key == _definition?.Key ? _runtime.ContextLength : null
            });
        }
        return models;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmInferenceRequest request, CancellationToken cancellationToken = default) =>
        ReadStreamAsync(this, request, cancellationToken);

    private async IAsyncEnumerable<LlmStreamEvent> StreamCoreAsync(
        LlmInferenceRequest request,
        StreamOperation operation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfReleaseRequired();
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.UnboundContinuationState))
        {
            throw new InferenceException(InferenceFailureCode.InvalidRequest, "Unknown continuation policy.");
        }
        int omitted = request.Messages.Count(message => message.ContinuationState is not null);
        if (omitted > 0 && request.UnboundContinuationState != LlmUnboundContinuationState.Omit)
        {
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Qwen cannot replay supplied continuation state.");
        }
        if (omitted > 0)
        {
            request = request with { Messages = request.Messages.Select(message => message with { ContinuationState = null }).ToArray() };
        }
        ValidateRequest(request);
        await using TensorModelLifetime.ExecutionLease modelUse = await _lifetime.EnterExecutionAsync(urgent: false, cancellationToken)
            .ConfigureAwait(false);
        await ValidateModelIdAsync(request.ModelId, cancellationToken).ConfigureAwait(false);
        if (request.Messages.Any(message => message.Attachments.Count > 0) && !_runtime.VisionEncoderLoaded)
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "This Qwen model has no vision projector, so it accepts text and no images.");
        TensorModelPackDefinition definition = _definition!;
        CancellationToken executionToken = modelUse.CancellationToken;
        TensorBackendKind backend = _loadedBackend
            ?? throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The Qwen backend was not loaded.");
        await using IAsyncDisposable execution = await _coordinator.AcquireAsync(
            definition.Key,
            backend,
            executionToken).ConfigureAwait(false);
        using var session = new ChatSession();
        List<ChatMessage> messages = BuildMessages(request, _images!);
        List<ToolFunction>? tools = BuildTools(request.Tools);
        bool thinking = request.ReasoningLevel == LlmReasoningLevel.High;
        StructuredOutputFormat? format = CreateStructuredFormat(request.StructuredOutput);
        if (format is not null)
        {
            StructuredOutputSchemaValidationResult validation = StructuredOutputValidator.ValidateSchema(format);
            if (!validation.IsValid)
            {
                throw new InferenceException(
                    InferenceFailureCode.InvalidRequest,
                    $"The response schema is not supported: {string.Join("; ", validation.Errors)}");
            }

            messages = StructuredOutputPrompt.Apply(messages, format);
        }

        int maxOutputTokens = request.MaxOutputTokens ?? 1_024;
        await EnsureFitsAsync(messages, tools, thinking, maxOutputTokens, executionToken).ConfigureAwait(false);
        SamplingConfig sampling = await CreateSamplingAsync(request, format, thinking, executionToken).ConfigureAwait(false);
        // The architecture's protocol parser separates the answer from thinking and tool calls.
        IOutputParser parser = OutputParserFactory.Create(_runtime.Architecture);
        parser.Init(thinking, tools);
        var output = new StringBuilder();
        var toolCalls = new List<LlmToolCall>();
        int? promptTokens = null;
        int? outputTokens = null;
        string? finishReason = null;
        long started = Stopwatch.GetTimestamp();
        // Cancelling this token aborts the engine request; a stream disposed before it completes cancels it.
        using var abandonment = CancellationTokenSource.CreateLinkedTokenSource(executionToken);
        IAsyncEnumerator<ChatStreamUpdate> updates = _runtime.StreamAsync(
            session,
            messages,
            maxOutputTokens,
            sampling,
            tools,
            thinking,
            abandonment.Token).GetAsyncEnumerator(abandonment.Token);
        bool completed = false;
        Exception? operationFailure = null;
        try
        {
            while (true)
            {
                bool more;
                ParsedOutput? part = null;
                try
                {
                    executionToken.ThrowIfCancellationRequested();
                    more = await updates.MoveNextAsync().ConfigureAwait(false);
                    if (more)
                    {
                        ChatStreamUpdate update = updates.Current;
                        if (update.Done)
                        {
                            promptTokens = update.PromptTokens;
                            outputTokens = update.EvalTokens;
                            finishReason = update.FinishReason;
                        }
                        else
                        {
                            if (update.RawGenerationSuffix is not null)
                                parser.SetGenerationPromptSuffix(update.RawGenerationSuffix);
                            string piece = update.Piece ?? "";
                            part = update.IsParsed
                                ? new ParsedOutput { Content = piece, Thinking = update.ThinkingPiece ?? "", ToolCalls = update.ParsedToolCalls?.ToList() }
                                : string.IsNullOrEmpty(piece) ? null : parser.Add(piece, false);
                        }
                    }
                    else
                    {
                        executionToken.ThrowIfCancellationRequested();
                        if (promptTokens is null)
                            throw new InferenceException(InferenceFailureCode.ExecutionFailed, "The Qwen runtime ended without a terminal update.");
                        part = parser.Add(string.Empty, true);
                    }
                }
                catch (Exception error) { operationFailure = error; throw; }

                using IEnumerator<LlmStreamEvent> parsed = Events(part, output, toolCalls).GetEnumerator();
                while (true)
                {
                    bool hasEvent;
                    try
                    {
                        hasEvent = parsed.MoveNext();
                    }
                    catch (Exception error) { operationFailure = error; throw; }
                    if (!hasEvent) break;
                    yield return parsed.Current;
                }

                if (!more)
                {
                    completed = true;
                    break;
                }
            }
        }
        finally
        {
            List<Exception> cleanupFailures = [];
            if (!completed)
            {
                try { await abandonment.CancelAsync().ConfigureAwait(false); }
                catch (Exception error) { cleanupFailures.Add(error); operation.RecordCleanup(error, abandonment); }
            }

            try { await operation.WaitForAbortAsync().ConfigureAwait(false); }
            catch (Exception error) { cleanupFailures.Add(error); operation.RecordCleanup(error, operation); }
            try { await updates.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { cleanupFailures.Add(error); operation.RecordCleanup(error, updates); }
            if (cleanupFailures.Count != 0)
            {
                if (operationFailure is not null) cleanupFailures.Insert(0, operationFailure);
                if (cleanupFailures.Count == 1)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
#pragma warning disable CA2219 // Iterator disposal must report cleanup failure; the aggregate retains the original operation.
                throw new AggregateException(cleanupFailures);
#pragma warning restore CA2219
            }
        }

        string text = output.ToString();
        LlmStructuredOutputValidation? structuredValidation = null;
        if (format is not null)
        {
            StructuredOutputNormalizationResult normalized = StructuredOutputValidator.NormalizeOutput(text, format);
            structuredValidation = new LlmStructuredOutputValidation(
                LlmStructuredOutputEnforcement.ProviderJsonSchema,
                WasEvaluated: true,
                normalized.IsValid,
                normalized.Errors);
            if (!normalized.IsValid)
            {
                throw new InferenceException(
                    InferenceFailureCode.ExecutionFailed,
                    $"Qwen output did not satisfy the requested schema: {string.Join("; ", normalized.Errors)}");
            }

            text = normalized.NormalizedContent;
        }

        executionToken.ThrowIfCancellationRequested();
        LlmCompletionStatus status = finishReason switch
        {
            "eos" or "stop" or "stop_sequence" => LlmCompletionStatus.Completed,
            "max_tokens" or "thinking_budget" or "repetition" or "aborted" or "cancelled" => LlmCompletionStatus.Incomplete,
            "error" => LlmCompletionStatus.Failed,
            _ => LlmCompletionStatus.Unknown
        };
        yield return new LlmCompletionEvent(new LlmInferenceResult
        {
            Text = text,
            OmittedContinuationStateCount = omitted,
            Status = status,
            FinishReason = finishReason,
            IncompleteReason = status == LlmCompletionStatus.Incomplete
                ? finishReason == "max_tokens" ? "max_output_tokens" : finishReason
                : null,
            Usage = new LlmUsage(promptTokens, outputTokens, promptTokens + outputTokens),
            ToolCalls = toolCalls,
            StructuredOutput = structuredValidation,
            Provenance = new InferenceProvenance
            {
                ImplementationId = Descriptor.Id,
                ModelId = definition.Key.ModelId,
                ModelRevision = definition.Key.Revision,
                Runtime = "TensorSharp",
                RuntimeVersion = RuntimeVersion(),
                Backend = _runtime.LoadedBackend,
                Elapsed = Stopwatch.GetElapsedTime(started)
            }
        });
    }

    /// <inheritdoc />
    public event EventHandler<TensorEngineStatus>? EngineStatusChanged
    {
        add => _lifetime.EngineStatusChanged += value;
        remove => _lifetime.EngineStatusChanged -= value;
    }

    /// <inheritdoc />
    public Task<TensorModelObservation> GetObservationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_lifetime.Observation);
    }

    /// <inheritdoc />
    public TensorBackendKind RequestedBackend => _requestedBackend;

    /// <summary>Start after Stop is an allowed no-op; it does not reset a stopped instance.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_stopRequested)
        {
            return Task.CompletedTask;
        }
        ThrowIfReleaseRequired();

        lock (_stopGate)
        {
            if (_stopRequested) return Task.CompletedTask;
            return _lifetime.AcceptAsync(TensorEngineState.Starting, null, null,
                token =>
                {
                    ThrowIfReleaseRequired();
                    return Task.FromResult(new TensorEngineStatus(null, null, TensorEngineState.Started, DateTimeOffset.UtcNow));
                }, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task LoadAsync(TensorModelKey model, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfReleaseRequired();
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        TensorModelPackDefinition definition;
        try
        {
            definition = await _packs.GetRequiredDefinitionAsync(model, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, exception.Message, exception);
        }

        try { QwenModelCompatibility.Validate(definition); }
        catch (ArgumentException exception)
        { throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The model pack is not supported by Qwen.", exception); }

        if (!GgmlRuntimeOwner.CompatibleBackends(_requestedBackend, _runtimeOwner.Plan.TargetRid).Any(definition.SupportedBackends.Contains))
        {
            throw new InferenceException(
                InferenceFailureCode.BackendUnavailable,
                $"Model pack '{definition.Key}' does not support the requested backend.");
        }

        TensorModelPackStatus installed = await _packs.GetStatusAsync(model, cancellationToken).ConfigureAwait(false);
        if (installed.State != TensorModelPackState.Installed)
        {
            throw new InferenceException(InferenceFailureCode.ModelUnavailable,
                installed.Detail ?? $"Model pack '{definition.Key}' is not installed.");
        }

        Task accepted;
        lock (_stopGate)
        {
            ThrowIfDisposed();
            ThrowIfReleaseRequired();
            accepted = _lifetime.AcceptAsync(TensorEngineState.Loading, model, _requestedBackend == TensorBackendKind.Auto ? null : _requestedBackend,
                token => LoadCoreAsync(definition, _requestedBackend, token), cancellationToken);
        }
        await accepted.ConfigureAwait(false);
    }

    private async Task<TensorEngineStatus> LoadCoreAsync(
        TensorModelPackDefinition definition, TensorBackendKind backend, CancellationToken cancellationToken)
    {
        using IDisposable loading = await _lifetime.EnterLoadAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfReleaseRequired();
        if (_modelLease is not null)
            throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The previous model ownership must be released before loading another model.");
        TensorModelPackLease lease;
        try
        {
            lease = await _packs.AcquireAsync(definition.Key, cancellationToken).ConfigureAwait(false);
        }
        catch (TensorModelPackException exception)
        {
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, exception.Message, exception);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GgmlRuntimeOwner.IsNative(backend))
            {
                backend = await _runtimeOwner.AcquireBackendAsync(backend, cancellationToken).ConfigureAwait(false);
                if (!definition.SupportedBackends.Contains(backend))
                    throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The initialized backend is incompatible with this model pack.");
            }
            _modelLease = lease;
            await _runtime.LoadAsync(definition, lease, BackendName(backend), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _images = QwenImageFiles.Create(_scratchDirectory);
            _lifetime.MarkLoaded(definition.Key, backend, _runtime.ExpertPlacement);
            _modelLease = lease;
            _loadedBackend = backend;
            _definition = definition;
            return new TensorEngineStatus(definition.Key, backend, TensorEngineState.Loaded, DateTimeOffset.UtcNow, ExpertPlacement: _runtime.ExpertPlacement);
        }
        catch (OperationCanceledException error)
        {
            await CleanupFailedLoadAsync(lease, error).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await CleanupFailedLoadAsync(lease, exception).ConfigureAwait(false);
            throw new InferenceException(
                exception switch
                {
                    OutOfMemoryException => InferenceFailureCode.CapacityExhausted,
                    InvalidDataException or IOException or OverflowException or FormatException => InferenceFailureCode.ModelCorrupt,
                    _ => InferenceFailureCode.BackendUnavailable
                },
                $"TensorSharp could not load Qwen on backend '{backend}': {(exception is AggregateException aggregate ? aggregate.InnerExceptions[0].Message : exception.Message)}",
                exception);
        }
    }

    private async Task CleanupFailedLoadAsync(TensorModelPackLease lease, Exception original)
    {
        // Retain actual ownership when cleanup fails; Stop/Unload can retry release-only work.
        _modelLease = lease;
        try
        {
            _runtime.Unload();
            await lease.DisposeAsync().ConfigureAwait(false);
            _modelLease = null;
            _images?.Dispose();
            _images = null;
            _releaseRequired = false;
        }
        catch (Exception cleanup)
        {
            _releaseRequired = true;
            throw new AggregateException(original, cleanup);
        }
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        lock (_stopGate)
        {
            ThrowIfDisposed();
            TensorModelObservation current = _lifetime.Observation;
            return _lifetime.AcceptAsync(TensorEngineState.Unloading, current.Model, current.Backend, async token =>
            {
                await ReleaseModelAsync(token).ConfigureAwait(false);
                return new TensorEngineStatus(null, null, TensorEngineState.Unloaded, DateTimeOffset.UtcNow);
            }, cancellationToken, teardown: true);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stopGate)
        {
            if (_disposed || _stopAttempt is { IsCompleted: false }) return Task.CompletedTask;
            _stopRequested = true;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopAttempt = completion.Task;
            TensorModelObservation current = _lifetime.Observation;
            try
            {
                // Qwen owns the terminal admission fence; the existing lifecycle queues release-only retries.
                return _lifetime.AcceptAsync(TensorEngineState.Stopping, current.Model, current.Backend,
                    token => StopCoreAsync(completion, token), cancellationToken, teardown: true);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
                throw;
            }
        }
    }

    private async Task<TensorEngineStatus> StopCoreAsync(TaskCompletionSource completion, CancellationToken token)
    {
        try
        {
            await ReleaseModelAsync(token).ConfigureAwait(false);
            _runtime.Dispose();
            if (_ownsCoordinator)
            {
                _coordinator.Dispose();
            }

            _lifetime.Dispose();
            _disposed = true;
            completion.TrySetResult();
            return new TensorEngineStatus(null, null, TensorEngineState.Stopped, DateTimeOffset.UtcNow);
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
            throw;
        }
    }

    /// <summary>Performs Stop and completes after native work has released model memory.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_stopGate) return new(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Task admission;
        Task actualCleanup;
        lock (_stopGate)
        {
            admission = _stopAttempt is null ? StopAsync() : Task.CompletedTask;
            actualCleanup = _stopAttempt!;
        }
        await admission.ConfigureAwait(false);
        await _lifetime.WhenIdleAsync().ConfigureAwait(false);
        await actualCleanup.ConfigureAwait(false);
    }

    /// <summary>Waits for active requests, or cancels them when the token is cancelled, then releases the model.</summary>
    private async Task ReleaseModelAsync(CancellationToken urgency)
    {
        await DrainStreamsAsync().ConfigureAwait(false);
        using IDisposable unloading = await _lifetime.EnterUnloadAsync(urgency).ConfigureAwait(false);
        if (_modelLease is null)
        {
            return;
        }

        try
        {
            _runtime.Unload();
            await _modelLease.DisposeAsync().ConfigureAwait(false);
            _modelLease = null;
            _images?.Dispose();
            _images = null;
            _loadedBackend = null;
            _definition = null;
            _releaseRequired = false;
            _lifetime.MarkUnloaded();
        }
        catch
        {
            _releaseRequired = true;
            throw;
        }
    }

    /// <summary>Refuses a request whose estimated prompt and output budget exceed the usable context.</summary>
    private async Task EnsureFitsAsync(List<ChatMessage> messages, List<ToolFunction>? tools, bool thinking, int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        if (_runtime.ContextLength is not { } usable) { return; }
        // Rendering the template and tokenizing the prompt are CPU work in TensorSharp.
        int? prompt = await Task.Run(() => _runtime.EstimatePromptTokens(messages, tools, thinking), cancellationToken).ConfigureAwait(false);
        if (prompt is { } tokens && (long)tokens + maxOutputTokens > usable)
        {
            throw new InferenceException(
                InferenceFailureCode.CapacityExhausted,
                $"The request needs about {tokens + (long)maxOutputTokens} tokens; the loaded model fits {usable} in the memory available.");
        }
    }

    private async Task<SamplingConfig> CreateSamplingAsync(
        LlmInferenceRequest request,
        StructuredOutputFormat? format,
        bool thinking,
        CancellationToken cancellationToken)
    {
        SamplingConfig sampling = request.Temperature is null
            ? SamplingConfig.Greedy
            : new SamplingConfig { Temperature = checked((float)request.Temperature.Value) };
        if (request.TopP is double topP) sampling.TopP = (float)topP;
        if (request.TopK is int topK) sampling.TopK = topK;
        if (request.Seed is int seed) sampling.Seed = seed;
        if (request.PresencePenalty is double presence) sampling.PresencePenalty = (float)presence;
        if (request.FrequencyPenalty is double frequency) sampling.FrequencyPenalty = (float)frequency;
        if (format is null)
        {
            return sampling;
        }

        try
        {
            // Compiling the schema into a grammar is CPU work in TensorSharp.
            await Task.Run(() => _runtime.ApplyGrammar(sampling, format, thinking), cancellationToken).ConfigureAwait(false);
            return sampling;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or GrammarParseException)
        {
            throw new InferenceException(
                InferenceFailureCode.InvalidRequest,
                "The response schema could not be compiled into a decoding constraint.",
                exception);
        }
    }

    private static StructuredOutputFormat? CreateStructuredFormat(LlmStructuredOutputRequest? request) =>
        request is null
            ? null
            : StructuredOutputFormat.JsonSchema(
                request.Name,
                request.Schema.GetRawText(),
                description: request.Description);

    private static List<ChatMessage> BuildMessages(LlmInferenceRequest request, QwenImageFiles images)
    {
        var messages = new List<ChatMessage>(request.Messages.Count + (request.Instructions is null ? 0 : 1));
        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            messages.Add(new ChatMessage { Role = "system", Content = request.Instructions });
        }

        foreach (LlmMessage message in request.Messages)
        {
            if (message.ToolResults.Count == 0 || !string.IsNullOrEmpty(message.Content))
            {
                messages.Add(new ChatMessage
                {
                    Role = message.Role,
                    Content = message.Content,
                    ImagePaths = message.Attachments.Count == 0 ? null : message.Attachments.Cast<LlmAttachment>().Select(images.Write).ToList(),
                    ToolCalls = message.ToolCalls.Count == 0 ? null : message.ToolCalls.Cast<LlmToolCall>().Select((call, index) => new ToolCall
                    {
                        Id = call.Id,
                        Name = call.Name,
                        Arguments = QwenJson.ReadArguments(call.ArgumentsJson),
                        Index = index
                    }).ToList()
                });
            }

            // The chat template renders each tool result as its own tool-role message.
            messages.AddRange(message.ToolResults.Cast<LlmToolResult>().Select(static result => new ChatMessage
            {
                Role = "tool",
                ToolCallId = result.ToolCallId,
                Content = ToolResultText(result.Output)
            }));
        }

        return messages;
    }

    private static string ToolResultText(LlmToolOutput output)
    {
        string text = string.Join("\n", output.Parts.Select(static part => part switch
        {
            LlmToolOutputText value => value.Text,
            LlmToolOutputResource { Data: null } resource => resource.Text ?? resource.Uri,
            _ => throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Qwen tool results accept text parts only.")
        }));
        return output.IsError ? "Error: " + text : text;
    }

    private static List<ToolFunction>? BuildTools(IReadOnlyList<LlmClientToolDefinition> tools)
    {
        if (tools.Count == 0)
        {
            return null;
        }

        // TensorSharp builds its tool functions from the same JSON-schema shape an OpenAI client sends.
        string json = QwenJson.WriteTools(tools.Cast<LlmToolDefinition>().ToArray());
        return ToolFunction.ParseList(json);
    }

    /// <summary>Maps one parsed piece to Common stream events, recording the answer text and completed tool calls.</summary>
    private static IEnumerable<LlmStreamEvent> Events(ParsedOutput? part, StringBuilder output, List<LlmToolCall> toolCalls)
    {
        if (part is null)
        {
            yield break;
        }

        if (!string.IsNullOrEmpty(part.Thinking))
        {
            yield return new LlmReasoningDeltaEvent(part.Thinking, IsSummary: false);
        }

        if (!string.IsNullOrEmpty(part.Content))
        {
            output.Append(part.Content);
            yield return new LlmTextDeltaEvent(part.Content);
        }

        foreach (ToolCall call in part.ToolCalls ?? [])
        {
            var completed = new LlmToolCall(call.Id ?? $"call_{Guid.NewGuid():N}", call.Name, QwenJson.WriteArguments(call.Arguments));
            yield return new LlmToolCallStartedEvent(completed.Id, completed.Name, toolCalls.Count);
            yield return new LlmToolCallArgumentsDeltaEvent(completed.Id, completed.ArgumentsJson);
            yield return new LlmToolCallCompletedEvent(completed);
            toolCalls.Add(completed);
        }
    }

    private static void ValidateRequest(LlmInferenceRequest request)
    {
        if (request.ProviderControls is not null)
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Qwen does not implement connector-owned provider controls.");
        if (request.ExplicitCache is not null)
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Qwen does not implement native provider cache selection.");
        if (request.Tools.Any(tool => tool is not LlmToolDefinition)
            || request.Messages.Any(message => message.ToolCalls.Any(call => call is not LlmToolCall)
                || message.ToolResults.Any(result => result is not LlmToolResult)))
        {
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Qwen implements function tools only.");
        }
        if (request.HostedTools is null || request.HostedTools.Count > 0 || request.MaxHostedTurns is not null)
        {
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Qwen does not implement hosted tools or hosted turn limits.");
        }
        if (request.Resources is not null)
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Qwen does not materialize caller-owned resources.");
        if (request.Messages.Count == 0 && string.IsNullOrWhiteSpace(request.Instructions))
        {
            throw new InferenceException(InferenceFailureCode.InvalidRequest, "At least one instruction or message is required.");
        }

        if (request.MaxOutputTokens is <= 0)
        {
            throw new InferenceException(InferenceFailureCode.InvalidRequest, "MaxOutputTokens must be positive.");
        }

        if (request.Temperature is double temperature && (!double.IsFinite(temperature) || temperature is < 0 or > 2))
        {
            throw new InferenceException(InferenceFailureCode.InvalidRequest, "Temperature must be between 0 and 2.");
        }
        if (request.TopP is double topP && (!double.IsFinite(topP) || topP is <= 0 or > 1 || (float)topP <= 0)
            || request.TopK is <= 0 || request.Seed is < 0
            || request.PresencePenalty is double presence && !float.IsFinite((float)presence)
            || request.FrequencyPenalty is double frequency && !float.IsFinite((float)frequency))
        {
            throw new InferenceException(InferenceFailureCode.InvalidRequest, "Invalid Qwen sampling control value.");
        }
        if ((request.TopP is not null || request.TopK is not null || request.Seed is not null)
            && (request.Temperature is null || (float)request.Temperature.Value <= 0))
        {
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature,
                "Qwen decodes greedily unless Temperature is positive; TopP, TopK and Seed require a sampling temperature.");
        }
        if (request.StopSequences is null || request.StopSequences.Count > 0)
        {
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Qwen does not support stop sequences.");
        }

        // The chat template has no control for a forced or serial tool choice.
        if (request.ToolChoice is not null || request.AllowParallelToolCalls is not null)
        {
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Qwen does not support a tool choice or parallel-call control.");
        }

        if (request.Messages.Any(static message => message.Attachments.Any(attachment => attachment is not LlmAttachment { Kind: LlmBinaryKind.Image })))
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Qwen accepts image attachments only.");
        if (request.Messages.Any(static message => message.ToolResults.Any(static result => result is LlmToolResult function
                && function.Output.Parts.Any(static part => part is LlmToolOutputSessionResource))))
        {
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Qwen accepts text messages only.");
        }

        if (request.ReasoningLevel is { } level && !ReasoningLevels.Contains(level))
        {
            throw new InferenceException(
                InferenceFailureCode.InvalidRequest,
                $"Qwen accepts reasoning level '{LlmReasoningLevel.None}' or '{LlmReasoningLevel.High}'; '{level}' was requested.");
        }
    }

    private async Task ValidateModelIdAsync(string? modelId, CancellationToken cancellationToken)
    {
        bool known = _definition is not null ? modelId == _definition.Key.ModelId
            : (await _packs.GetDefinitionsAsync(cancellationToken).ConfigureAwait(false)).Any(definition => definition.Key.ModelId == modelId
                && definition.Capability == TensorCapability.Llm && definition.RuntimeArchitecture == TensorLlmModels.QwenRuntimeArchitecture);
        if (modelId is not null && !known)
        {
            throw new InferenceException(
                InferenceFailureCode.ModelUnavailable,
                $"Model '{modelId}' is not bound to this Qwen runtime.");
        }
    }

    private static string BackendName(TensorBackendKind backend) => backend switch
    {
        TensorBackendKind.Cpu => "cpu",
        TensorBackendKind.GgmlCpu => "ggml_cpu",
        TensorBackendKind.GgmlMetal => "ggml_metal",
        TensorBackendKind.GgmlCuda => "ggml_cuda",
        TensorBackendKind.GgmlVulkan => "ggml_vulkan",
        TensorBackendKind.Cuda => "cuda",
        TensorBackendKind.Mlx => "mlx",
        _ => throw new ArgumentOutOfRangeException(nameof(backend))
    };

    private static string RuntimeVersion() =>
        typeof(ModelService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? typeof(ModelService).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed || _stopRequested, this);

    private void ThrowIfReleaseRequired()
    {
        if (_releaseRequired)
            throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The previous model cleanup failed. Release its ownership before admitting more model work.");
    }
}
