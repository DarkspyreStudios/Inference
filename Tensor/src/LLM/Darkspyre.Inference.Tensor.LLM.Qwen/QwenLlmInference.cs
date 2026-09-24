using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.LLM;
using TensorSharp.Runtime;
using TensorSharp.Runtime.Grammar;
using TensorSharp.Server;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

public sealed class QwenLlmInference : ITensorLlmInference
{
    /// <summary>Qwen runs with thinking disabled, so it accepts only <see cref="LlmReasoningLevel.None"/>.</summary>
    private static readonly System.Collections.Frozen.FrozenSet<LlmReasoningLevel> ReasoningLevels =
        System.Collections.Frozen.FrozenSet.ToFrozenSet([LlmReasoningLevel.None]);

    private readonly ITensorModelPackManager _packs;
    private TensorModelPackDefinition? _definition;
    private readonly TensorBackendKind _requestedBackend;
    private readonly TensorExecutionCoordinator _coordinator;
    private readonly bool _ownsCoordinator;
    private readonly TensorModelLifetime _lifetime = new();
    private readonly IQwenModelRuntime _runtime;
    private TensorModelPackLease? _modelLease;
    private TensorBackendKind? _loadedBackend;
    private bool _disposed;

    public QwenLlmInference(
        ITensorModelPackManager packs,
        TensorBackendKind backend = TensorBackendKind.Auto,
        TensorExecutionCoordinator? coordinator = null)
        : this(packs, new QwenModelRuntime(), backend, coordinator) { }

    internal QwenLlmInference(ITensorModelPackManager packs, IQwenModelRuntime runtime,
        TensorBackendKind backend = TensorBackendKind.Auto, TensorExecutionCoordinator? coordinator = null)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(runtime);
        _packs = packs;
        _runtime = runtime;

        _requestedBackend = backend;
        _coordinator = coordinator ?? new TensorExecutionCoordinator();
        _ownsCoordinator = coordinator is null;
    }

    public InferenceImplementationDescriptor Descriptor => QwenSetup.Definition.Implementation;

    public Task<LlmCapabilities> GetCapabilitiesAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateModelId(modelId);
        return Task.FromResult(new LlmCapabilities
        {
            OutputLimitEnforcement = LlmOutputLimitEnforcement.EngineEnforced,
            SupportsStreaming = true,
            SupportsModelListing = true,
            SupportsJsonSchema = true,
            SupportsJsonObject = true,
            ReasoningLevels = ReasoningLevels,
            SupportsTemperature = true,
            SupportsToolUse = false,
            SupportsPromptImages = false,
            SupportsPromptDocuments = false
        });
    }

    public async Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        LlmCapabilities capabilities = await GetCapabilitiesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return _packs.Definitions.Where(definition => definition.Capability == TensorCapability.Llm
            && definition.RuntimeArchitecture == TensorLlmModels.QwenRuntimeArchitecture).Select(definition =>
            new LlmModelInfo(definition.Key.ModelId, definition.Key.ModelId, "Qwen", capabilities)
            {
                MaxContextTokens = definition.Key == _definition?.Key ? _runtime.ContextLength : null,
                MaxOutputTokens = definition.Key == _definition?.Key ? _runtime.ContextLength : null
            }).ToArray();
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmInferenceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        await using TensorModelLifetime.ExecutionLease modelUse = await _lifetime.EnterExecutionAsync(cancellationToken)
            .ConfigureAwait(false);
        ValidateModelId(request.ModelId);
        TensorModelPackDefinition definition = _definition!;
        CancellationToken executionToken = modelUse.CancellationToken;
        TensorBackendKind backend = _loadedBackend
            ?? throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The Qwen backend was not loaded.");
        await using IAsyncDisposable execution = await _coordinator.AcquireAsync(
            definition.Key,
            backend,
            executionToken).ConfigureAwait(false);
        using var session = new ChatSession();
        List<ChatMessage> messages = BuildMessages(request);
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

        SamplingConfig sampling = CreateSampling(request, format);
        var output = new StringBuilder();
        int promptTokens = 0;
        int outputTokens = 0;
        string? finishReason = null;
        long started = Stopwatch.GetTimestamp();
        await foreach (ChatStreamUpdate update in _runtime.StreamAsync(
                           session,
                           messages,
                           request.MaxOutputTokens ?? 1_024,
                           sampling,
                           executionToken).ConfigureAwait(false))
        {
            if (!update.Done)
            {
                if (!string.IsNullOrEmpty(update.Piece))
                {
                    output.Append(update.Piece);
                    yield return new LlmTextDeltaEvent(update.Piece);
                }

                continue;
            }

            promptTokens = update.PromptTokens;
            outputTokens = update.EvalTokens;
            finishReason = update.FinishReason;
        }

        string text = output.ToString();
        LlmStructuredOutputValidation? structuredValidation = null;
        if (format is not null)
        {
            StructuredOutputNormalizationResult normalized = StructuredOutputValidator.NormalizeOutput(text, format);
            structuredValidation = new LlmStructuredOutputValidation(
                LlmStructuredOutputEnforcement.NativeJsonSchema,
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

        yield return new LlmCompletionEvent(new LlmInferenceResult
        {
            Text = text,
            Status = string.Equals(finishReason, "max_tokens", StringComparison.Ordinal)
                ? LlmCompletionStatus.Incomplete
                : LlmCompletionStatus.Completed,
            FinishReason = finishReason,
            IncompleteReason = string.Equals(finishReason, "max_tokens", StringComparison.Ordinal)
                ? "max_output_tokens"
                : null,
            Usage = new LlmUsage(promptTokens, outputTokens, promptTokens + outputTokens),
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

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public TensorModelObservation Observation => _lifetime.Observation;
    public TensorBackendKind RequestedBackend => _requestedBackend;

    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        using IDisposable unloading = await _lifetime.EnterUnloadAsync(cancellationToken).ConfigureAwait(false);
        if (_modelLease is null)
        {
            return;
        }

        _runtime.Unload();
        await _modelLease.DisposeAsync().ConfigureAwait(false);
        _modelLease = null;
        _loadedBackend = null;
        _definition = null;
        _lifetime.MarkUnloaded();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await UnloadAsync(cancellationToken).ConfigureAwait(false);
        _disposed = true;
        _runtime.Dispose();
        _lifetime.Dispose();
        if (_ownsCoordinator)
        {
            _coordinator.Dispose();
        }

    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    public async Task LoadAsync(TensorModelKey model, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(model);
        TensorModelPackDefinition definition;
        try
        {
            definition = _packs.GetRequiredDefinition(model);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, exception.Message, exception);
        }

        try { QwenModelCompatibility.Validate(definition); }
        catch (ArgumentException exception)
        { throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The model pack is not supported by Qwen.", exception); }

        using IDisposable loading = await _lifetime.EnterLoadAsync(cancellationToken).ConfigureAwait(false);

        TensorBackendKind backend = ResolveBackend(_requestedBackend);
        if (!definition.SupportedBackends.Contains(backend))
        {
            throw new InferenceException(
                InferenceFailureCode.BackendUnavailable,
                $"Model pack '{definition.Key}' does not support backend '{backend}'.");
        }

        TensorModelPackLease lease;
        try
        {
            lease = await _packs.AcquireAsync(model, cancellationToken).ConfigureAwait(false);
        }
        catch (TensorModelPackException exception)
        {
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, exception.Message, exception);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _runtime.LoadAsync(definition, lease, BackendName(backend), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _lifetime.MarkLoaded(model, backend);
            _modelLease = lease;
            _loadedBackend = backend;
            _definition = definition;
        }
        catch (OperationCanceledException)
        {
            _runtime.Unload();
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _runtime.Unload();
            await lease.DisposeAsync().ConfigureAwait(false);
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

    private SamplingConfig CreateSampling(
        LlmInferenceRequest request,
        StructuredOutputFormat? format)
    {
        SamplingConfig sampling = request.Temperature is null
            ? SamplingConfig.Greedy
            : new SamplingConfig { Temperature = checked((float)request.Temperature.Value) };
        if (format is null)
        {
            return sampling;
        }

        try
        {
            _runtime.ApplyGrammar(sampling, format);
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

        messages.AddRange(request.Messages.Select(static message => new ChatMessage
        {
            Role = message.Role,
            Content = message.Content
        }));
        return messages;
    }

    private static void ValidateRequest(LlmInferenceRequest request)
    {
        if (request.Messages.Count == 0 && string.IsNullOrWhiteSpace(request.Instructions))
        {
            throw new InferenceException(InferenceFailureCode.InvalidRequest, "At least one instruction or message is required.");
        }

        if (request.MaxOutputTokens is <= 0)
        {
            throw new InferenceException(InferenceFailureCode.InvalidRequest, "MaxOutputTokens must be positive.");
        }

        if (request.Temperature is < 0 or > 2)
        {
            throw new InferenceException(InferenceFailureCode.InvalidRequest, "Temperature must be between 0 and 2.");
        }

        if (request.Tools.Count > 0 || request.ToolChoice is not null || request.AllowParallelToolCalls is not null)
        {
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The initial Qwen runtime does not support tools.");
        }

        if (request.Messages.Any(static message => message.Attachments.Count > 0
                                                  || message.ToolCalls.Count > 0
                                                  || message.ToolResults.Count > 0))
        {
            throw new InferenceException(
                InferenceFailureCode.UnsupportedFeature,
                "The initial Qwen runtime supports text messages only.");
        }

        if (request.ReasoningLevel is { } level && !ReasoningLevels.Contains(level))
        {
            // The engine always runs Qwen with thinking disabled, so only None describes a request.
            throw new InferenceException(
                InferenceFailureCode.InvalidRequest,
                $"Qwen accepts only reasoning level '{LlmReasoningLevel.None}'; '{level}' was requested.");
        }
    }

    private void ValidateModelId(string? modelId)
    {
        bool known = _definition is not null ? modelId == _definition.Key.ModelId
            : _packs.Definitions.Any(definition => definition.Key.ModelId == modelId
                && definition.Capability == TensorCapability.Llm && definition.RuntimeArchitecture == TensorLlmModels.QwenRuntimeArchitecture);
        if (modelId is not null && !known)
        {
            throw new InferenceException(
                InferenceFailureCode.ModelUnavailable,
                $"Model '{modelId}' is not bound to this Qwen runtime.");
        }
    }

    internal static TensorBackendKind ResolveBackend(TensorBackendKind backend) => backend switch
    {
        TensorBackendKind.Auto when OperatingSystem.IsMacOS() => TensorBackendKind.GgmlMetal,
        TensorBackendKind.Auto => TensorBackendKind.GgmlCuda,
        _ => backend
    };

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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
