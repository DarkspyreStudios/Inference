using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Decision;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Decision;
using TensorSharp.Cuda;
using TensorSharp.Models.Direct;
using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

public sealed class LayaDecisionInference : ITensorDecisionInference
{
    private static readonly double[] Temperatures =
    [
        1.0148024559020996,
        1.0374259948730469,
        1.0575125217437744
    ];

    private readonly ITensorModelPackManager _packs;
    private readonly TensorModelPackDefinition _definition;
    private readonly TensorBackendKind _requestedBackend;
    private readonly TensorExecutionCoordinator _coordinator;
    private readonly bool _ownsCoordinator;
    private readonly TensorModelLifetime _lifetime = new();
    private TensorModelPackLease? _modelLease;
    private LayaTokenizer? _tokenizer;
    private LayaDecisionModel? _model;
    private TensorBackendKind? _loadedBackend;
    private bool _disposed;

    public LayaDecisionInference(
        ITensorModelPackManager packs,
        TensorBackendKind backend = TensorBackendKind.Auto,
        TensorExecutionCoordinator? coordinator = null)
    {
        ArgumentNullException.ThrowIfNull(packs);
        _packs = packs;
        _definition = TensorDecisionModels.LayaTypedDecisions;

        _requestedBackend = backend;
        _coordinator = coordinator ?? new TensorExecutionCoordinator();
        _ownsCoordinator = coordinator is null;
    }

    public InferenceImplementationDescriptor Descriptor { get; } = new(
        "darkspyre.tensor.laya",
        "Darkspyre Tensor Laya",
        InferenceImplementationKind.Tensor);

    public Task<DecisionCapabilities> GetCapabilitiesAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateModelId(modelId);
        return Task.FromResult(new DecisionCapabilities
        {
            SupportedQuestionKinds = new HashSet<DecisionQuestionKind>
            {
                DecisionQuestionKind.Choice,
                DecisionQuestionKind.Score,
                DecisionQuestionKind.Boolean
            },
            MaximumInputTokens = LayaSequenceBuilder.MaximumLength,
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
        LayaTokenizer tokenizer = _tokenizer
            ?? throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The Laya tokenizer was not loaded.");
        LayaDecisionModel model = _model
            ?? throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The Laya model was not loaded.");
        TensorBackendKind backend = _loadedBackend
            ?? throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The Laya backend was not loaded.");
        await using IAsyncDisposable execution = await _coordinator.AcquireAsync(
            _definition.Key,
            backend,
            executionToken).ConfigureAwait(false);

        long started = Stopwatch.GetTimestamp();
        int inputTokens = 0;
        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (DecisionQuestion question in request.Questions)
        {
            executionToken.ThrowIfCancellationRequested();
            LayaSequence sequence = LayaSequenceBuilder.Build(tokenizer, request.State, question);
            if (sequence.MarkerPositions.Length != sequence.OptionIds.Length)
            {
                throw new InferenceException(
                    InferenceFailureCode.InvalidRequest,
                    $"Question '{question.Id}' has too many options for the Laya input window.");
            }

            inputTokens = checked(inputTokens + sequence.TokenIds.Length);
            LayaModelOutput output;
            try
            {
                output = model.Forward(sequence);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not InferenceException)
            {
                throw new InferenceException(
                    InferenceFailureCode.ExecutionFailed,
                    $"Laya failed while evaluating question '{question.Id}'.",
                    exception);
            }

            double temperature = Temperature(sequence.QuestionType, sequence.OptionIds.Length);
            float[] scaled = output.Logits.Select(logit => checked((float)(logit / temperature))).ToArray();
            float[] probabilities = LayaDecisionModel.Softmax(scaled);
            answers.Add(question.Id, CreateAnswer(question, sequence.OptionIds, probabilities, output.ActionProbability));
        }

        return new DecisionInferenceResult
        {
            Answers = answers,
            Usage = new DecisionUsage(inputTokens, 0, inputTokens),
            Provenance = new InferenceProvenance
            {
                ImplementationId = Descriptor.Id,
                ModelId = _definition.Key.ModelId,
                ModelRevision = _definition.Key.Revision,
                Runtime = "TensorSharp",
                RuntimeVersion = RuntimeVersion(),
                Backend = backend.ToString(),
                Elapsed = Stopwatch.GetElapsedTime(started)
            }
        };
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        using IDisposable unloading = await _lifetime.EnterUnloadAsync(cancellationToken).ConfigureAwait(false);
        _model?.Dispose();
        _model = null;
        _tokenizer = null;
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
        if (model != _definition.Key)
        {
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, $"Laya does not support model '{model}'.");
        }

        TensorModelPackDefinition definition;
        try
        {
            definition = _packs.GetRequiredDefinition(model);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, exception.Message, exception);
        }

        if (definition.Capability != TensorCapability.Decision
            || definition.RuntimeArchitecture != _definition.RuntimeArchitecture)
        {
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The model pack is not supported by Laya.");
        }

        using IDisposable loading = await _lifetime.EnterLoadAsync(cancellationToken).ConfigureAwait(false);

        TensorBackendKind backend = ResolveBackend(_requestedBackend);
        if (!definition.SupportedBackends.Contains(backend))
        {
            throw new InferenceException(
                InferenceFailureCode.BackendUnavailable,
                $"Model pack '{_definition.Key}' does not support backend '{backend}'.");
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

        LayaDecisionModel? loadedModel = null;
        try
        {
            LayaTokenizer tokenizer = await LayaTokenizer.CreateAsync(
                lease.GetRequiredArtifact("tokenizer"),
                cancellationToken).ConfigureAwait(false);
            using SafetensorsFile weights = await SafetensorsFile.OpenAsync(
                lease.GetRequiredArtifact("model"),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            loadedModel = LayaDecisionModel.Load(weights, backend == TensorBackendKind.Cuda);
            cancellationToken.ThrowIfCancellationRequested();
            _lifetime.MarkLoaded();
            _tokenizer = tokenizer;
            _model = loadedModel;
            _modelLease = lease;
            _loadedBackend = backend;
            loadedModel = null;
        }
        catch (OperationCanceledException)
        {
            loadedModel?.Dispose();
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not InferenceException)
        {
            loadedModel?.Dispose();
            await lease.DisposeAsync().ConfigureAwait(false);
            throw new InferenceException(
                exception switch
                {
                    OutOfMemoryException => InferenceFailureCode.CapacityExhausted,
                    InvalidDataException or JsonException => InferenceFailureCode.ModelCorrupt,
                    _ => InferenceFailureCode.BackendUnavailable
                },
                $"TensorSharp could not load Laya on backend '{backend}'.",
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

    private void ValidateRequest(DecisionInferenceRequest request)
    {
        ValidateModelId(request.ModelId);
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

    private void ValidateModelId(string? modelId)
    {
        if (modelId is not null && !string.Equals(modelId, _definition.Key.ModelId, StringComparison.Ordinal))
        {
            throw new InferenceException(
                InferenceFailureCode.ModelUnavailable,
                $"Model '{modelId}' is not bound to this Laya runtime.");
        }
    }

    private static TensorBackendKind ResolveBackend(TensorBackendKind backend) => backend switch
    {
        TensorBackendKind.Auto when CudaBackend.IsAvailable() => TensorBackendKind.Cuda,
        TensorBackendKind.Auto => TensorBackendKind.Cpu,
        _ => backend
    };

    private static double Temperature(int questionType, int optionCount)
    {
        double temperature = questionType switch
        {
            0 when optionCount <= 2 => 1.9063563346862793,
            0 when optionCount <= 5 => 1.7601518630981445,
            0 when optionCount <= 10 => 1.0000158548355103,
            0 => 0.10058280825614929,
            1 when optionCount is >= 3 and <= 5 => 1.2514300346374512,
            2 when optionCount <= 2 => 1.983399510383606,
            >= 0 and < 3 => Temperatures[questionType],
            _ => 1d
        };
        return Math.Clamp(temperature, 0.5d, 5d);
    }

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
        typeof(DirectContext).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? typeof(DirectContext).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
