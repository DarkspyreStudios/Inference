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

public sealed class QwenLlmInference : ITensorLlmInference
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
    private GgmlRuntimeOwner.ModelLease? _runtimeModelLease;
    private TensorModelPackLease? _modelLease;
    private TensorBackendKind? _loadedBackend;
    private readonly object _stopGate = new();
    private volatile bool _disposed;
    private volatile bool _stopRequested;
    private volatile bool _releaseRequired;
    private Task? _stopAttempt;
    private Task? _disposal;

    public QwenLlmInference(
        ITensorModelPackManager packs,
        TensorBackendKind backend = TensorBackendKind.Auto,
        TensorExecutionCoordinator? coordinator = null, GgmlRuntimeOwner? runtimeOwner = null)
        : this(packs, new QwenModelRuntime(), backend, coordinator, runtimeOwner) { }

    internal QwenLlmInference(ITensorModelPackManager packs, IQwenModelRuntime runtime,
        TensorBackendKind backend = TensorBackendKind.Auto, TensorExecutionCoordinator? coordinator = null,
        GgmlRuntimeOwner? runtimeOwner = null)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(runtime);
        _packs = packs;
        _runtime = runtime;
        _runtimeOwner = runtimeOwner ?? GgmlRuntimeOwner.GetOrCreate();
        _lifetime = new(this);

        _requestedBackend = backend;
        _coordinator = coordinator ?? new TensorExecutionCoordinator();
        _ownsCoordinator = coordinator is null;
    }

    public InferenceImplementationDescriptor Descriptor => QwenSetup.Definition.Implementation;

    public async Task<LlmCapabilities> GetCapabilitiesAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        await ValidateModelIdAsync(modelId, cancellationToken).ConfigureAwait(false);
        return new LlmCapabilities
        {
            OutputLimitEnforcement = LlmOutputLimitEnforcement.EngineEnforced,
            SupportsStreaming = true,
            SupportsModelListing = true,
            SupportsJsonSchema = true,
            SupportsJsonObject = true,
            ReasoningLevels = ReasoningLevels,
            SupportsReasoningText = true,
            SupportsStreamingUsage = true,
            SupportsTemperature = true,
            SupportsTopP = true,
            SupportsTopK = true,
            SupportsSeed = true,
            SupportsPresencePenalty = true,
            SupportsFrequencyPenalty = true,
            MaxStopSequences = 16,
            SupportsToolUse = true,
            Tools = new() { SupportsParallelCalls = true, StrictInputSchema = LlmStrictInputSchemaSupport.Unsupported },
            SupportsPromptImages = false,
            SupportsPromptDocuments = false
        };
    }

    /// <summary>Qwen reads its local model packs on every call and keeps no list.</summary>
    public Task<IReadOnlyList<LlmModelInfo>> RefreshModelsAsync(CancellationToken cancellationToken = default) =>
        ListModelsAsync(cancellationToken);

    public async Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        LlmCapabilities capabilities = await GetCapabilitiesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return (await _packs.GetDefinitionsAsync(cancellationToken).ConfigureAwait(false)).Where(definition => definition.Capability == TensorCapability.Llm
            && definition.RuntimeArchitecture == TensorLlmModels.QwenRuntimeArchitecture).Select(definition =>
            new LlmModelInfo(definition.Key.ModelId, definition.Key.ModelId, "Qwen", capabilities)
            {
                // Qwen declares no separate output limit: a reply can use whatever context is left.
                MaxContextTokens = definition.Key == _definition?.Key ? _runtime.ContextLength : null
            }).ToArray();
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmInferenceRequest request,
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
        await using TensorModelLifetime.ExecutionLease modelUse = await _lifetime.EnterExecutionAsync(cancellationToken)
            .ConfigureAwait(false);
        using IDisposable? runtimeUse = _runtimeModelLease is not null ? _runtimeOwner.EnterExecution() : null;
        await ValidateModelIdAsync(request.ModelId, cancellationToken).ConfigureAwait(false);
        if (runtimeUse is not null) _runtimeOwner.ThrowIfOperationCanceled(cancellationToken);
        TensorModelPackDefinition definition = _definition!;
        CancellationToken executionToken = modelUse.CancellationToken;
        TensorBackendKind backend = _loadedBackend
            ?? throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The Qwen backend was not loaded.");
        await using IAsyncDisposable execution = await _coordinator.AcquireAsync(
            definition.Key,
            backend,
            executionToken).ConfigureAwait(false);
        if (runtimeUse is not null) _runtimeOwner.ThrowIfOperationCanceled(executionToken);
        using var session = new ChatSession();
        List<ChatMessage> messages = BuildMessages(request);
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
        if (runtimeUse is not null) _runtimeOwner.ThrowIfOperationCanceled(executionToken);
        SamplingConfig sampling = await CreateSamplingAsync(request, format, thinking, executionToken).ConfigureAwait(false);
        if (runtimeUse is not null) _runtimeOwner.ThrowIfOperationCanceled(executionToken);
        // The architecture's protocol parser separates the answer from thinking and tool calls.
        IOutputParser parser = OutputParserFactory.Create(_runtime.Architecture);
        parser.Init(thinking, tools);
        var output = new StringBuilder();
        var toolCalls = new List<LlmToolCall>();
        int promptTokens = 0;
        int outputTokens = 0;
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
        try
        {
            // TensorSharp renders the chat template and tokenizes synchronously in its first move.
            bool more = await Task.Run(() => updates.MoveNextAsync().AsTask(), abandonment.Token).ConfigureAwait(false);
            while (more)
            {
                if (runtimeUse is not null) _runtimeOwner.ThrowIfOperationCanceled(executionToken);
                ChatStreamUpdate update = updates.Current;
                if (!update.Done)
                {
                    if (update.RawGenerationSuffix is not null)
                    {
                        parser.SetGenerationPromptSuffix(update.RawGenerationSuffix);
                    }

                    ParsedOutput? part = update.IsParsed
                        ? new ParsedOutput { Content = update.Piece ?? "", Thinking = update.ThinkingPiece ?? "", ToolCalls = update.ParsedToolCalls?.ToList() }
                        : string.IsNullOrEmpty(update.Piece) ? null : parser.Add(update.Piece, false);
                    foreach (LlmStreamEvent streamEvent in Events(part, output, toolCalls))
                    {
                        if (runtimeUse is not null) _runtimeOwner.ThrowIfOperationCanceled(executionToken);
                        yield return streamEvent;
                    }
                }
                else
                {
                    promptTokens = update.PromptTokens;
                    outputTokens = update.EvalTokens;
                    finishReason = update.FinishReason;
                }

                if (runtimeUse is not null) _runtimeOwner.ThrowIfOperationCanceled(executionToken);
                more = await updates.MoveNextAsync().ConfigureAwait(false);
            }

            if (runtimeUse is not null) _runtimeOwner.ThrowIfOperationCanceled(executionToken);
            foreach (LlmStreamEvent streamEvent in Events(parser.Add(string.Empty, true), output, toolCalls))
            {
                if (runtimeUse is not null) _runtimeOwner.ThrowIfOperationCanceled(executionToken);
                yield return streamEvent;
            }

            completed = true;
        }
        finally
        {
            if (!completed)
            {
                await abandonment.CancelAsync().ConfigureAwait(false);
            }

            await updates.DisposeAsync().ConfigureAwait(false);
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

        if (runtimeUse is not null) _runtimeOwner.ThrowIfOperationCanceled(executionToken);
        yield return new LlmCompletionEvent(new LlmInferenceResult
        {
            Text = text,
            OmittedContinuationStateCount = omitted,
            Status = string.Equals(finishReason, "max_tokens", StringComparison.Ordinal)
                ? LlmCompletionStatus.Incomplete
                : LlmCompletionStatus.Completed,
            FinishReason = finishReason,
            IncompleteReason = string.Equals(finishReason, "max_tokens", StringComparison.Ordinal)
                ? "max_output_tokens"
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

    public event EventHandler<TensorEngineStatus>? EngineStatusChanged
    {
        add => _lifetime.EngineStatusChanged += value;
        remove => _lifetime.EngineStatusChanged -= value;
    }

    public Task<TensorModelObservation> GetObservationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_lifetime.Observation);
    }

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

        IDisposable? admission = GgmlRuntimeOwner.IsGgml(_requestedBackend) ? _runtimeOwner.EnterExecution() : null;
        try
        {
            lock (_stopGate)
            {
                if (_stopRequested) { admission?.Dispose(); return Task.CompletedTask; }
                return _lifetime.AcceptAsync(TensorEngineState.Starting, null, null,
                    token =>
                    {
                        using (admission)
                        {
                            if (admission is not null) _runtimeOwner.ThrowIfOperationCanceled(token);
                            ThrowIfReleaseRequired();
                            return Task.FromResult(new TensorEngineStatus(null, null, TensorEngineState.Started, DateTimeOffset.UtcNow));
                        }
                    }, cancellationToken);
            }
        }
        catch { admission?.Dispose(); throw; }
    }

    public async Task LoadAsync(TensorModelKey model, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfReleaseRequired();
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        IDisposable? admission = GgmlRuntimeOwner.IsGgml(_requestedBackend) ? _runtimeOwner.EnterExecution() : null;
        try
        {
            await AdmitLoadAsync(model, admission, cancellationToken).ConfigureAwait(false);
            admission = null;
        }
        finally { admission?.Dispose(); }
    }

    private async Task AdmitLoadAsync(TensorModelKey model, IDisposable? admission, CancellationToken cancellationToken)
    {
        TensorModelPackDefinition definition;
        try
        {
            definition = await _packs.GetRequiredDefinitionAsync(model, cancellationToken).ConfigureAwait(false);
            if (admission is not null) _runtimeOwner.ThrowIfOperationCanceled(cancellationToken);
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
        if (admission is not null) _runtimeOwner.ThrowIfOperationCanceled(cancellationToken);
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
                async token =>
                {
                    using (admission)
                        return await LoadCoreAsync(definition, _requestedBackend, token).ConfigureAwait(false);
                }, cancellationToken);
        }
        await accepted.ConfigureAwait(false);
    }

    private async Task<TensorEngineStatus> LoadCoreAsync(
        TensorModelPackDefinition definition, TensorBackendKind backend, CancellationToken cancellationToken)
    {
        using IDisposable loading = await _lifetime.EnterLoadAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfReleaseRequired();
        if (_modelLease is not null || _runtimeModelLease is not null)
            throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The previous model ownership must be released before loading another model.");
        if (GgmlRuntimeOwner.IsGgml(backend)) _runtimeOwner.ThrowIfOperationCanceled(cancellationToken);
        TensorModelPackLease lease;
        try
        {
            lease = await _packs.AcquireAsync(definition.Key, cancellationToken).ConfigureAwait(false);
        }
        catch (TensorModelPackException exception)
        {
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, exception.Message, exception);
        }

        GgmlRuntimeOwner.ModelLease? runtimeLease = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GgmlRuntimeOwner.IsGgml(backend))
            {
                runtimeLease = await _runtimeOwner.AcquireModelAsync(backend, cancellationToken).ConfigureAwait(false);
                backend = runtimeLease.Backend;
                if (!definition.SupportedBackends.Contains(backend))
                    throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The initialized backend is incompatible with this model pack.");
            }
            _modelLease = lease;
            _runtimeModelLease = runtimeLease;
            await _runtime.LoadAsync(definition, lease, BackendName(backend), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (runtimeLease is not null) _runtimeOwner.ThrowIfOperationCanceled(cancellationToken);
            _lifetime.MarkLoaded(definition.Key, backend);
            _modelLease = lease;
            _runtimeModelLease = runtimeLease;
            _loadedBackend = backend;
            _definition = definition;
            return new TensorEngineStatus(definition.Key, backend, TensorEngineState.Loaded, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException error)
        {
            await CleanupFailedLoadAsync(lease, runtimeLease, error).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await CleanupFailedLoadAsync(lease, runtimeLease, exception).ConfigureAwait(false);
            throw new InferenceException(
                exception switch
                {
                    OutOfMemoryException => InferenceFailureCode.CapacityExhausted,
                    InvalidDataException or IOException or OverflowException or FormatException => InferenceFailureCode.ModelCorrupt,
                    _ => InferenceFailureCode.BackendUnavailable
                },
                $"TensorSharp could not load Qwen on backend '{backend}'.",
                exception);
        }
    }

    private async Task CleanupFailedLoadAsync(TensorModelPackLease lease, GgmlRuntimeOwner.ModelLease? runtimeLease, Exception original)
    {
        // Retain actual ownership when cleanup fails; Stop/Unload can retry release-only work.
        _modelLease = lease;
        _runtimeModelLease = runtimeLease;
        try
        {
            _runtime.Unload();
            await lease.DisposeAsync().ConfigureAwait(false);
            runtimeLease?.Dispose();
            _modelLease = null;
            _runtimeModelLease = null;
            _releaseRequired = false;
        }
        catch (Exception cleanup)
        {
            _releaseRequired = true;
            throw new AggregateException(original, cleanup);
        }
    }

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
        using IDisposable unloading = await _lifetime.EnterUnloadAsync(urgency).ConfigureAwait(false);
        if (_modelLease is null)
        {
            return;
        }

        try
        {
            _runtime.Unload();
            await _modelLease.DisposeAsync().ConfigureAwait(false);
            _runtimeModelLease?.Dispose();
            _runtimeModelLease = null;
            _modelLease = null;
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
        sampling.StopSequences = request.StopSequences.ToList();
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
                request.Strict,
                request.Description);

    private static List<ChatMessage> BuildMessages(LlmInferenceRequest request)
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
        if (request.StopSequences is null || request.StopSequences.Any(string.IsNullOrEmpty))
        {
            throw new InferenceException(InferenceFailureCode.InvalidRequest, "Stop sequences must be non-empty strings.");
        }
        if (request.StopSequences.Count > 16)
        {
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Qwen accepts at most 16 stop sequences.");
        }

        if (request.Tools.Any(tool => tool is LlmToolDefinition { Strict: true }))
        { throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Qwen cannot enforce strict tool argument schemas."); }

        // The chat template has no control for a forced or serial tool choice.
        if (request.ToolChoice is not null || request.AllowParallelToolCalls is not null)
        {
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Qwen does not support a tool choice or parallel-call control.");
        }

        if (request.Messages.Any(static message => message.Attachments.Count > 0
            || message.ToolResults.Any(static result => result is LlmToolResult function
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
