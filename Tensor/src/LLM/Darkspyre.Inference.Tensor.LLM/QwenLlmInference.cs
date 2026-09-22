using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Tensor.Common;
using Microsoft.Extensions.Logging.Abstractions;
using TensorSharp.Runtime;
using TensorSharp.Runtime.Grammar;
using TensorSharp.Server;

namespace Darkspyre.Inference.Tensor.LLM;

public sealed class QwenLlmInference : ILlmInference, IAsyncDisposable
{
    private readonly ITensorModelPackManager _packs;
    private readonly TensorModelPackDefinition _definition;
    private readonly TensorBackendKind _requestedBackend;
    private readonly TensorExecutionCoordinator _coordinator;
    private readonly bool _ownsCoordinator;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly ModelService _modelService = new(NullLogger<ModelService>.Instance);
    private TensorModelPackLease? _modelLease;
    private TensorBackendKind? _loadedBackend;
    private bool _disposed;

    public QwenLlmInference(
        ITensorModelPackManager packs,
        TensorModelPackDefinition? definition = null,
        TensorBackendKind backend = TensorBackendKind.Auto,
        TensorExecutionCoordinator? coordinator = null)
    {
        ArgumentNullException.ThrowIfNull(packs);
        _packs = packs;
        _definition = definition ?? TensorLlmModels.Qwen3FourBillionQ4Km;
        if (_definition.Capability != TensorCapability.Llm)
        {
            throw new ArgumentException("The model pack is not an LLM pack.", nameof(definition));
        }

        _requestedBackend = backend;
        _coordinator = coordinator ?? new TensorExecutionCoordinator();
        _ownsCoordinator = coordinator is null;
    }

    public InferenceImplementationDescriptor Descriptor { get; } = new(
        "darkspyre.tensor.qwen",
        "Darkspyre Tensor Qwen",
        InferenceImplementationKind.Tensor);

    public Task<LlmCapabilities> GetCapabilitiesAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateModelId(modelId);
        return Task.FromResult(new LlmCapabilities
        {
            SupportsStreaming = true,
            SupportsModelListing = true,
            SupportsJsonSchema = true,
            SupportsJsonObject = true,
            SupportsReasoningEffort = false,
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
        TensorModelPackStatus status = await _packs.GetStatusAsync(_definition.Key, cancellationToken)
            .ConfigureAwait(false);
        LlmCapabilities capabilities = await GetCapabilitiesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return
        [
            new LlmModelInfo(_definition.Key.ModelId, "Qwen3 4B Q4_K_M", "Qwen", capabilities)
            {
                MaxContextTokens = 32_768,
                MaxOutputTokens = status.State == TensorModelPackState.Installed ? 32_768 : null
            }
        ];
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmInferenceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        TensorBackendKind backend = _loadedBackend
            ?? throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The Qwen backend was not loaded.");
        await using IAsyncDisposable execution = await _coordinator.AcquireAsync(
            _definition.Key,
            backend,
            cancellationToken).ConfigureAwait(false);
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
        await foreach (ChatStreamUpdate update in _modelService.ChatStreamWithMetricsAsync(
                           session,
                           messages,
                           request.MaxOutputTokens ?? 1_024,
                           cancellationToken,
                           sampling,
                           tools: null,
                           enableThinking: false).ConfigureAwait(false))
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
                ModelId = _definition.Key.ModelId,
                ModelRevision = _definition.Key.Revision,
                Runtime = "TensorSharp",
                RuntimeVersion = RuntimeVersion(),
                Backend = _modelService.LoadedBackend,
                Elapsed = Stopwatch.GetElapsedTime(started)
            }
        });
    }

    public async ValueTask UnloadAsync()
    {
        await _loadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_modelLease is null)
            {
                return;
            }

            _modelService.UnloadModel();
            await _modelLease.DisposeAsync().ConfigureAwait(false);
            _modelLease = null;
            _loadedBackend = null;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await UnloadAsync().ConfigureAwait(false);
        _disposed = true;
        _modelService.Dispose();
        _loadGate.Dispose();
        if (_ownsCoordinator)
        {
            _coordinator.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_modelLease is not null)
        {
            return;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_modelLease is not null)
            {
                return;
            }

            TensorBackendKind backend = ResolveBackend(_requestedBackend);
            if (!_definition.SupportedBackends.Contains(backend))
            {
                throw new InferenceException(
                    InferenceFailureCode.BackendUnavailable,
                    $"Model pack '{_definition.Key}' does not support backend '{backend}'.");
            }

            TensorModelPackLease lease;
            try
            {
                lease = await _packs.AcquireAsync(_definition.Key, cancellationToken).ConfigureAwait(false);
            }
            catch (TensorModelPackException exception)
            {
                throw new InferenceException(InferenceFailureCode.ModelUnavailable, exception.Message, exception);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _modelService.LoadModelAsync(
                    lease.GetRequiredArtifact("model"),
                    mmProj: null,
                    BackendName(backend),
                    cancellationToken).ConfigureAwait(false);
                _modelLease = lease;
                _loadedBackend = backend;
            }
            catch (OperationCanceledException)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                throw new InferenceException(
                    InferenceFailureCode.BackendUnavailable,
                    $"TensorSharp could not load Qwen on backend '{backend}'.",
                    exception);
            }
        }
        finally
        {
            _loadGate.Release();
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
            var grammarCache = GrammarLibrary.ForJsonSchema(format.SchemaJson, _modelService.Model.Tokenizer);
            sampling.Grammar = GrammarLibrary.NewConstraint(grammarCache, _modelService.Model.Tokenizer);
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

    private void ValidateRequest(LlmInferenceRequest request)
    {
        ValidateModelId(request.ModelId);
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

        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
        {
            throw new InferenceException(
                InferenceFailureCode.UnsupportedFeature,
                "The initial Qwen runtime does not expose reasoning-effort controls.");
        }
    }

    private void ValidateModelId(string? modelId)
    {
        if (modelId is not null && !string.Equals(modelId, _definition.Key.ModelId, StringComparison.Ordinal))
        {
            throw new InferenceException(
                InferenceFailureCode.ModelUnavailable,
                $"Model '{modelId}' is not bound to this Qwen runtime.");
        }
    }

    private static TensorBackendKind ResolveBackend(TensorBackendKind backend) => backend switch
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
