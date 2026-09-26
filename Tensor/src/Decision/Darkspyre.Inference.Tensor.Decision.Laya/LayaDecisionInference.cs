using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Decision;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Decision;
using Microsoft.ML.OnnxRuntime;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

public sealed class LayaDecisionInference : ITensorDecisionInference, IDecisionInputMeasurement
{
    private readonly ITensorModelPackManager _packs;
    private readonly ILayaModelLoader _loader;
    private readonly TensorBackendKind _requestedBackend;
    private readonly TensorExecutionCoordinator _coordinator;
    private readonly bool _ownsCoordinator;
    private readonly TensorModelLifetime _lifetime = new();
    private TensorModelPackLease? _modelLease;
    private LayaModelResources? _resources;
    private LayaAgentConfiguration? _configuration;
    private LayaInputMeter? _meter;
    private TensorBackendKind? _loadedBackend;
    private bool _disposed;

    public LayaDecisionInference(
        ITensorModelPackManager packs,
        TensorBackendKind backend = TensorBackendKind.Auto,
        TensorExecutionCoordinator? coordinator = null)
        : this(packs, new LayaModelLoader(), backend, coordinator)
    {
    }

    internal LayaDecisionInference(ITensorModelPackManager packs, ILayaModelLoader loader,
        TensorBackendKind backend = TensorBackendKind.Auto, TensorExecutionCoordinator? coordinator = null)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(loader);
        _packs = packs;
        _loader = loader;

        _requestedBackend = backend;
        _coordinator = coordinator ?? new TensorExecutionCoordinator();
        _ownsCoordinator = coordinator is null;
    }

    public InferenceImplementationDescriptor Descriptor => LayaSetup.Definition.Implementation;

    public Task<DecisionCapabilities> GetCapabilitiesAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        // Before Load these are family capabilities, not evidence that a particular pack is installed.
        if (_lifetime.Observation.Model is { } loaded) { ValidateModelId(modelId, loaded); }
        return Task.FromResult(new DecisionCapabilities
        {
            SupportedQuestionKinds = new HashSet<DecisionQuestionKind>
            {
                DecisionQuestionKind.Choice,
                DecisionQuestionKind.Score,
                DecisionQuestionKind.Boolean
            },
            MaximumInputTokens = _configuration?.MaximumLength,
            SupportsCalibratedConfidence = true,
            SupportsActionProbability = true
        });
    }

    public async Task<DecisionInferenceResult> DecideAsync(
        DecisionInferenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        await using TensorModelLifetime.ExecutionLease modelUse = await _lifetime.EnterExecutionAsync(cancellationToken)
            .ConfigureAwait(false);
        CancellationToken executionToken = modelUse.CancellationToken;
        LayaModelResources resources = _resources
            ?? throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The Laya model was not loaded.");
        LayaAgentConfiguration configuration = _configuration
            ?? throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The Laya agent configuration was not loaded.");
        TensorModelKey selected = _modelLease?.Key
            ?? throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The Laya model pack was not loaded.");
        ValidateModelId(request.ModelId, selected);
        TensorBackendKind backend = _loadedBackend
            ?? throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The Laya backend was not loaded.");
        await using IAsyncDisposable execution = await _coordinator.AcquireAsync(
            selected,
            backend,
            executionToken).ConfigureAwait(false);

        long started = Stopwatch.GetTimestamp();
        int inputTokens = 0;
        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (DecisionQuestion question in request.Questions)
        {
            executionToken.ThrowIfCancellationRequested();
            LayaSequence sequence = LayaSequenceBuilder.Build(resources.Tokenizer, request.State, question,
                configuration.MaximumLength, configuration.MaximumHeadLength);
            if (sequence.MarkerPositions.Length != sequence.OptionIds.Length
                || sequence.OptionIds.Length > resources.Model.MaximumOptions)
            {
                throw new InferenceException(
                    InferenceFailureCode.InvalidRequest,
                    $"Question '{question.Id}' has too many options for the Laya input window.");
            }

            inputTokens = checked(inputTokens + sequence.TokenIds.Length);
            LayaModelOutput output;
            try
            {
                output = resources.Model.Forward(sequence, executionToken);
                executionToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not InferenceException)
            {
                throw new InferenceException(
                    InferenceFailureCode.ExecutionFailed,
                    $"Laya failed while evaluating question '{question.Id}'.",
                    exception);
            }

            double temperature = configuration.Temperature(sequence.QuestionType, sequence.OptionIds.Length);
            float[] scaled = output.Logits.Select(logit => checked((float)(logit / temperature))).ToArray();
            float[] probabilities = LayaOnnxModel.Softmax(scaled);
            answers.Add(question.Id, CreateAnswer(question, sequence.OptionIds, probabilities, output.ActionProbability));
        }

        return new DecisionInferenceResult
        {
            Answers = answers,
            Usage = new DecisionUsage(inputTokens, 0, inputTokens),
            Provenance = new InferenceProvenance
            {
                ImplementationId = Descriptor.Id,
                ModelId = selected.ModelId,
                ModelRevision = selected.Revision,
                Runtime = "OnnxRuntime",
                RuntimeVersion = RuntimeVersion(),
                Backend = backend.ToString(),
                Elapsed = Stopwatch.GetElapsedTime(started)
            }
        };
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<DecisionInputMeasurement> MeasureAsync(
        DecisionInferenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        // The lease keeps an unload from releasing the tokenizer; no backend is needed, so the
        // execution coordinator is not acquired.
        await using TensorModelLifetime.ExecutionLease modelUse = await _lifetime.EnterExecutionAsync(cancellationToken)
            .ConfigureAwait(false);
        LayaInputMeter meter = _meter
            ?? throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The Laya model was not loaded.");
        TensorModelKey selected = _modelLease?.Key
            ?? throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The Laya model pack was not loaded.");
        ValidateModelId(request.ModelId, selected);
        return meter.Measure(request, modelUse.CancellationToken);
    }

    public TensorModelObservation Observation => _lifetime.Observation;
    public TensorBackendKind RequestedBackend => _requestedBackend;

    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        using IDisposable unloading = await _lifetime.EnterUnloadAsync(cancellationToken).ConfigureAwait(false);
        _resources?.Dispose();
        _resources = null;
        _configuration = null;
        _meter = null;
        if (_modelLease is not null)
        {
            await _modelLease.DisposeAsync().ConfigureAwait(false);
            _modelLease = null;
        }

        _loadedBackend = null;
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
            definition = await _packs.GetRequiredDefinitionAsync(model, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, exception.Message, exception);
        }

        if (definition.Key != model)
        {
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The pack manager returned a different model key.");
        }
        try { LayaModelCompatibility.Validate(definition); }
        catch (ArgumentException exception)
        { throw new InferenceException(InferenceFailureCode.UnsupportedFeature, exception.Message, exception); }

        using IDisposable loading = await _lifetime.EnterLoadAsync(cancellationToken).ConfigureAwait(false);

        TensorBackendKind backend = ResolveBackend(_requestedBackend);
        if (backend is not (TensorBackendKind.CoreMl or TensorBackendKind.Cpu) || !definition.SupportedBackends.Contains(backend))
        {
            throw new InferenceException(
                InferenceFailureCode.BackendUnavailable,
                $"Model pack '{model}' does not support backend '{backend}'.");
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

        LayaModelResources? loadedResources = null;
        try
        {
            if (lease.Key != model)
            { throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The pack lease identifies a different model."); }
            LayaAgentConfiguration configuration = await LayaAgentConfiguration.LoadAsync(
                lease.GetRequiredArtifact("agent-config"), cancellationToken).ConfigureAwait(false);
            loadedResources = await _loader.LoadAsync(lease, backend, configuration, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _lifetime.MarkLoaded(model, backend);
            _resources = loadedResources;
            _configuration = configuration;
            _meter = new LayaInputMeter(loadedResources.Tokenizer, configuration);
            _modelLease = lease;
            _loadedBackend = backend;
            loadedResources = null;
        }
        catch (Exception exception)
        {
            loadedResources?.Dispose();
            await lease.DisposeAsync().ConfigureAwait(false);
            if (exception is OperationCanceledException or InferenceException) { throw; }
            throw new InferenceException(
                exception switch
                {
                    OutOfMemoryException => InferenceFailureCode.CapacityExhausted,
                    InvalidDataException or JsonException => InferenceFailureCode.ModelCorrupt,
                    _ => InferenceFailureCode.BackendUnavailable
                },
                $"ONNX Runtime could not load Laya on backend '{backend}'.",
                exception);
        }
    }

    private static DecisionAnswer CreateAnswer(
        DecisionQuestion question,
        string[] optionIds,
        float[] probabilities,
        float actionProbability)
    {
        double confidence = Confidence(probabilities);
        return question switch
        {
            ChoiceDecisionQuestion choice => new ChoiceDecisionAnswer
            {
                QuestionId = question.Id,
                ChoiceId = optionIds[MaximumIndex(probabilities)],
                Probabilities = optionIds.Zip(probabilities)
                    .ToDictionary(
                        static item => item.First,
                        static item => Round(item.Second),
                        StringComparer.Ordinal),
                Confidence = Round(confidence),
                ActionProbability = Round(actionProbability)
            },
            ScoreDecisionQuestion score => new ScoreDecisionAnswer
            {
                QuestionId = question.Id,
                Score = Round(probabilities.Select((probability, index) => probability * index).Sum()),
                Legend = score.Levels.ToDictionary(
                    static level => level.Id,
                    static level => level.Description,
                    StringComparer.Ordinal),
                Probabilities = score.Levels.Zip(probabilities)
                    .ToDictionary(
                        static item => item.First.Id,
                        static item => Round(item.Second),
                        StringComparer.Ordinal),
                Confidence = Round(confidence),
                ActionProbability = Round(actionProbability)
            },
            BooleanDecisionQuestion => new BooleanDecisionAnswer
            {
                QuestionId = question.Id,
                Value = probabilities[1] >= probabilities[0],
                TrueProbability = Round(probabilities[1]),
                Confidence = Round(Math.Max(probabilities[1], 1f - probabilities[1])),
                ActionProbability = Round(actionProbability)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(question))
        };
    }

    internal static void ValidateRequest(DecisionInferenceRequest request)
    {
        if (request.State.ValueKind == JsonValueKind.Undefined)
        {
            throw new InferenceException(InferenceFailureCode.InvalidRequest, "Decision state is required.");
        }

        if (request.Questions.Count == 0)
        {
            throw new InferenceException(InferenceFailureCode.InvalidRequest, "At least one decision question is required.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (DecisionQuestion question in request.Questions)
        {
            if (string.IsNullOrWhiteSpace(question.Id) || !ids.Add(question.Id))
            {
                throw new InferenceException(
                    InferenceFailureCode.InvalidRequest,
                    "Decision question IDs must be non-empty and unique.");
            }

            if (string.IsNullOrWhiteSpace(question.Instructions))
            {
                throw new InferenceException(
                    InferenceFailureCode.InvalidRequest,
                    $"Question '{question.Id}' must include instructions.");
            }

            switch (question)
            {
                case ChoiceDecisionQuestion choice:
                    ValidateChoices(choice);
                    break;
                case ScoreDecisionQuestion score:
                    ValidateLevels(score);
                    break;
                case BooleanDecisionQuestion:
                    break;
                default:
                    throw new InferenceException(
                        InferenceFailureCode.UnsupportedFeature,
                        $"Question '{question.Id}' has an unsupported typed question implementation.");
            }
        }
    }

    private static void ValidateChoices(ChoiceDecisionQuestion question)
    {
        if (question.Choices.Count == 0
            || question.Choices.Any(static choice => string.IsNullOrWhiteSpace(choice.Id))
            || question.Choices.Select(static choice => choice.Id).Distinct(StringComparer.Ordinal).Count()
            != question.Choices.Count)
        {
            throw new InferenceException(
                InferenceFailureCode.InvalidRequest,
                $"Choice question '{question.Id}' requires at least one choice with a unique, non-empty ID.");
        }
    }

    private static void ValidateLevels(ScoreDecisionQuestion question)
    {
        if (question.Levels.Count < 2
            || question.Levels.Any(static level =>
                string.IsNullOrWhiteSpace(level.Id) || string.IsNullOrWhiteSpace(level.Description))
            || question.Levels.Select(static level => level.Id).Distinct(StringComparer.Ordinal).Count()
            != question.Levels.Count)
        {
            throw new InferenceException(
                InferenceFailureCode.InvalidRequest,
                $"Score question '{question.Id}' requires at least two levels with unique IDs and descriptions.");
        }
    }

    private static void ValidateModelId(string? modelId, TensorModelKey selected)
    {
        if (modelId is not null && !string.Equals(modelId, selected.ModelId, StringComparison.Ordinal))
        {
            throw new InferenceException(
                InferenceFailureCode.ModelUnavailable,
                $"Model '{modelId}' is not bound to this Laya runtime.");
        }
    }

    internal static TensorBackendKind ResolveBackend(TensorBackendKind backend) => backend switch
    {
        TensorBackendKind.Auto when OperatingSystem.IsMacOS() => TensorBackendKind.CoreMl,
        TensorBackendKind.Auto => TensorBackendKind.Cpu,
        _ => backend
    };

    private static double Confidence(ReadOnlySpan<float> probabilities)
    {
        if (probabilities.Length < 2)
        {
            return 1d;
        }

        double entropy = 0d;
        foreach (float probability in probabilities)
        {
            entropy -= probability * Math.Log(Math.Max(probability, 1e-12f));
        }

        return Math.Clamp(1d - entropy / Math.Log(probabilities.Length), 0d, 1d);
    }

    private static int MaximumIndex(ReadOnlySpan<float> values)
    {
        int result = 0;
        for (int index = 1; index < values.Length; index++)
        {
            if (values[index] > values[result])
            {
                result = index;
            }
        }

        return result;
    }

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.ToEven);

    private static string RuntimeVersion() =>
        typeof(InferenceSession).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? typeof(InferenceSession).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
