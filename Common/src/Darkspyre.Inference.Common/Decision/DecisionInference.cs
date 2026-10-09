using Darkspyre.Inference.Common;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Common.Decision;

public enum DecisionQuestionKind
{
    Choice,
    Score,
    Boolean
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ChoiceDecisionQuestion), "choice")]
[JsonDerivedType(typeof(ScoreDecisionQuestion), "score")]
[JsonDerivedType(typeof(BooleanDecisionQuestion), "boolean")]
public abstract record DecisionQuestion
{
    public required string Id { get; init; }

    public required string Instructions { get; init; }

    [JsonIgnore]
    public abstract DecisionQuestionKind Kind { get; }
}

public sealed record DecisionChoice
{
    public required string Id { get; init; }

    public string? Description { get; init; }
}

public sealed record ChoiceDecisionQuestion : DecisionQuestion
{
    [JsonIgnore]
    public override DecisionQuestionKind Kind => DecisionQuestionKind.Choice;

    public IReadOnlyList<DecisionChoice> Choices { get; init; } = [];
}

public sealed record DecisionScoreLevel
{
    public required string Id { get; init; }

    public required string Description { get; init; }
}

public sealed record ScoreDecisionQuestion : DecisionQuestion
{
    [JsonIgnore]
    public override DecisionQuestionKind Kind => DecisionQuestionKind.Score;

    public IReadOnlyList<DecisionScoreLevel> Levels { get; init; } = [];
}

public sealed record BooleanDecisionQuestion : DecisionQuestion
{
    [JsonIgnore]
    public override DecisionQuestionKind Kind => DecisionQuestionKind.Boolean;

    public string? FalseDescription { get; init; }

    public string? TrueDescription { get; init; }
}

public sealed record DecisionInferenceRequest
{
    public required string ModelId { get; init; }

    public required JsonElement State { get; init; }

    public IReadOnlyList<DecisionQuestion> Questions { get; init; } = [];
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ChoiceDecisionAnswer), "choice")]
[JsonDerivedType(typeof(ScoreDecisionAnswer), "score")]
[JsonDerivedType(typeof(BooleanDecisionAnswer), "boolean")]
public abstract record DecisionAnswer
{
    public required string QuestionId { get; init; }

    [JsonIgnore]
    public abstract DecisionQuestionKind Kind { get; }

    public required double Confidence { get; init; }

    public double? ActionProbability { get; init; }
}

public sealed record ChoiceDecisionAnswer : DecisionAnswer
{
    [JsonIgnore]
    public override DecisionQuestionKind Kind => DecisionQuestionKind.Choice;

    public required string ChoiceId { get; init; }

    public IReadOnlyDictionary<string, double> Probabilities { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);
}

public sealed record ScoreDecisionAnswer : DecisionAnswer
{
    [JsonIgnore]
    public override DecisionQuestionKind Kind => DecisionQuestionKind.Score;

    public required double Score { get; init; }

    public IReadOnlyDictionary<string, string> Legend { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, double> Probabilities { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);
}

public sealed record BooleanDecisionAnswer : DecisionAnswer
{
    [JsonIgnore]
    public override DecisionQuestionKind Kind => DecisionQuestionKind.Boolean;

    public required bool Value { get; init; }

    public required double TrueProbability { get; init; }
}

public sealed record DecisionUsage(int InputTokens, int OutputTokens, int TotalTokens);

public sealed record DecisionInferenceResult
{
    public IReadOnlyDictionary<string, DecisionAnswer> Answers { get; init; } =
        new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);

    public required DecisionUsage Usage { get; init; }

    public required InferenceProvenance Provenance { get; init; }
}

public sealed record DecisionCapabilities
{
    public IReadOnlySet<DecisionQuestionKind> SupportedQuestionKinds { get; init; } =
        new HashSet<DecisionQuestionKind>();

    public int? MaximumQuestions { get; init; }

    public int? MaximumInputTokens { get; init; }

    public InferenceSupport SupportsCalibratedConfidence { get; init; }

    public InferenceSupport SupportsActionProbability { get; init; }
}

public interface IDecisionInference
{
    InferenceImplementationDescriptor Descriptor { get; }

    Task<DecisionCapabilities> GetCapabilitiesAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default);

    Task<DecisionInferenceResult> DecideAsync(
        DecisionInferenceRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record DecisionInputMeasurement
{
    public required int StateTokens { get; init; }

    public IReadOnlyDictionary<string, int> StateTokenBudgets { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

public interface IDecisionInputMeasurement
{
    Task<DecisionInputMeasurement> MeasureAsync(
        DecisionInferenceRequest request,
        CancellationToken cancellationToken = default);
}
