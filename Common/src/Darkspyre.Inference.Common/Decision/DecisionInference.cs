using Darkspyre.Inference.Common;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Common.Decision;

/// <summary>The answer shape a decision question asks for.</summary>
public enum DecisionQuestionKind
{
    /// <summary>Pick one option from a list of choices.</summary>
    Choice,
    /// <summary>Rate on an ordered scale of levels.</summary>
    Score,
    /// <summary>Answer true or false.</summary>
    Boolean
}

/// <summary>A question the decision model answers about the request state. JSON carries the concrete kind in the <c>type</c> property.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ChoiceDecisionQuestion), "choice")]
[JsonDerivedType(typeof(ScoreDecisionQuestion), "score")]
[JsonDerivedType(typeof(BooleanDecisionQuestion), "boolean")]
public abstract record DecisionQuestion
{
    /// <summary>Identifier that matches the question to its answer in <see cref="DecisionInferenceResult.Answers"/>.</summary>
    public required string Id { get; init; }

    /// <summary>Text that tells the model what to decide.</summary>
    public required string Instructions { get; init; }

    /// <summary>The answer shape this question asks for.</summary>
    [JsonIgnore]
    public abstract DecisionQuestionKind Kind { get; }
}

/// <summary>One option of a <see cref="ChoiceDecisionQuestion"/>.</summary>
public sealed record DecisionChoice
{
    /// <summary>Identifier the answer returns in <see cref="ChoiceDecisionAnswer.ChoiceId"/> and as a key of its probabilities.</summary>
    public required string Id { get; init; }

    /// <summary>Optional text that describes the option.</summary>
    public string? Description { get; init; }
}

/// <summary>A question whose answer is one option from <see cref="Choices"/>.</summary>
public sealed record ChoiceDecisionQuestion : DecisionQuestion
{
    /// <inheritdoc />
    [JsonIgnore]
    public override DecisionQuestionKind Kind => DecisionQuestionKind.Choice;

    /// <summary>The options the model chooses among.</summary>
    public IReadOnlyList<DecisionChoice> Choices { get; init; } = [];
}

/// <summary>One level of a <see cref="ScoreDecisionQuestion"/> scale.</summary>
public sealed record DecisionScoreLevel
{
    /// <summary>Identifier that keys this level in the answer's legend and probabilities.</summary>
    public required string Id { get; init; }

    /// <summary>Text that describes what this level means.</summary>
    public required string Description { get; init; }
}

/// <summary>A question whose answer is a position on an ordered scale of <see cref="Levels"/>.</summary>
public sealed record ScoreDecisionQuestion : DecisionQuestion
{
    /// <inheritdoc />
    [JsonIgnore]
    public override DecisionQuestionKind Kind => DecisionQuestionKind.Score;

    /// <summary>The scale levels in order. The first level is score 0, the next is score 1, and so on.</summary>
    public IReadOnlyList<DecisionScoreLevel> Levels { get; init; } = [];
}

/// <summary>A question whose answer is true or false.</summary>
public sealed record BooleanDecisionQuestion : DecisionQuestion
{
    /// <inheritdoc />
    [JsonIgnore]
    public override DecisionQuestionKind Kind => DecisionQuestionKind.Boolean;

    /// <summary>Optional text that describes what a false answer means.</summary>
    public string? FalseDescription { get; init; }

    /// <summary>Optional text that describes what a true answer means.</summary>
    public string? TrueDescription { get; init; }
}

/// <summary>The input to one decision call: a model, a JSON state and the questions to answer about it.</summary>
public sealed record DecisionInferenceRequest
{
    /// <summary>Identifier of the model that answers the questions.</summary>
    public required string ModelId { get; init; }

    /// <summary>The JSON state the questions are asked about.</summary>
    public required JsonElement State { get; init; }

    /// <summary>The questions to answer in this call.</summary>
    public IReadOnlyList<DecisionQuestion> Questions { get; init; } = [];
}

/// <summary>The model's answer to one question. JSON carries the concrete kind in the <c>type</c> property.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ChoiceDecisionAnswer), "choice")]
[JsonDerivedType(typeof(ScoreDecisionAnswer), "score")]
[JsonDerivedType(typeof(BooleanDecisionAnswer), "boolean")]
public abstract record DecisionAnswer
{
    /// <summary>The <see cref="DecisionQuestion.Id"/> of the question this answers.</summary>
    public required string QuestionId { get; init; }

    /// <summary>The answer shape, matching the question's kind.</summary>
    [JsonIgnore]
    public abstract DecisionQuestionKind Kind { get; }

    /// <summary>Confidence in the answer, from 0 to 1.</summary>
    public required double Confidence { get; init; }

    /// <summary>Probability from the model's separate action output, or null when the implementation does not report one.</summary>
    public double? ActionProbability { get; init; }
}

/// <summary>The answer to a <see cref="ChoiceDecisionQuestion"/>.</summary>
public sealed record ChoiceDecisionAnswer : DecisionAnswer
{
    /// <inheritdoc />
    [JsonIgnore]
    public override DecisionQuestionKind Kind => DecisionQuestionKind.Choice;

    /// <summary>Identifier of the most probable choice.</summary>
    public required string ChoiceId { get; init; }

    /// <summary>Probability of each choice, keyed by choice identifier.</summary>
    public IReadOnlyDictionary<string, double> Probabilities { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);
}

/// <summary>The answer to a <see cref="ScoreDecisionQuestion"/>.</summary>
public sealed record ScoreDecisionAnswer : DecisionAnswer
{
    /// <inheritdoc />
    [JsonIgnore]
    public override DecisionQuestionKind Kind => DecisionQuestionKind.Score;

    /// <summary>Expected level position: each level's probability times its zero-based position, summed.</summary>
    public required double Score { get; init; }

    /// <summary>Maps each level identifier to its description.</summary>
    public IReadOnlyDictionary<string, string> Legend { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Probability of each level, keyed by level identifier.</summary>
    public IReadOnlyDictionary<string, double> Probabilities { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);
}

/// <summary>The answer to a <see cref="BooleanDecisionQuestion"/>.</summary>
public sealed record BooleanDecisionAnswer : DecisionAnswer
{
    /// <inheritdoc />
    [JsonIgnore]
    public override DecisionQuestionKind Kind => DecisionQuestionKind.Boolean;

    /// <summary>True when <see cref="TrueProbability"/> is at least the probability of false.</summary>
    public required bool Value { get; init; }

    /// <summary>Probability that the answer is true.</summary>
    public required double TrueProbability { get; init; }
}

/// <summary>Token counts for one decision call.</summary>
/// <param name="InputTokens">Tokens the model read.</param>
/// <param name="OutputTokens">Tokens the model produced.</param>
/// <param name="TotalTokens">Input and output tokens together.</param>
public sealed record DecisionUsage(int InputTokens, int OutputTokens, int TotalTokens);

/// <summary>The output of one decision call.</summary>
public sealed record DecisionInferenceResult
{
    /// <summary>Answers keyed by question identifier.</summary>
    public IReadOnlyDictionary<string, DecisionAnswer> Answers { get; init; } =
        new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);

    /// <summary>Token counts for the call.</summary>
    public required DecisionUsage Usage { get; init; }

    /// <summary>Records which implementation, model and runtime produced the answers.</summary>
    public required InferenceProvenance Provenance { get; init; }
}

/// <summary>What a decision implementation supports for a model.</summary>
public sealed record DecisionCapabilities
{
    /// <summary>The question kinds the implementation answers.</summary>
    public IReadOnlySet<DecisionQuestionKind> SupportedQuestionKinds { get; init; } =
        new HashSet<DecisionQuestionKind>();

    /// <summary>Most questions one request may carry, or null when the implementation states no limit.</summary>
    public int? MaximumQuestions { get; init; }

    /// <summary>Most input tokens the model accepts, or null when the implementation states no limit.</summary>
    public int? MaximumInputTokens { get; init; }

    /// <summary>Support for calibrated <see cref="DecisionAnswer.Confidence"/> values.</summary>
    public InferenceSupport SupportsCalibratedConfidence { get; init; }

    /// <summary>Support for <see cref="DecisionAnswer.ActionProbability"/> on answers.</summary>
    public InferenceSupport SupportsActionProbability { get; init; }
}

/// <summary>An inference implementation that answers decision questions about a JSON state.</summary>
public interface IDecisionInference
{
    /// <summary>Identity, family and tasks of this implementation.</summary>
    InferenceImplementationDescriptor Descriptor { get; }

    /// <summary>Returns what the implementation supports, for <paramref name="modelId"/> when given.</summary>
    Task<DecisionCapabilities> GetCapabilitiesAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Answers every question in <paramref name="request"/> in one call.</summary>
    Task<DecisionInferenceResult> DecideAsync(
        DecisionInferenceRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Token counts of a decision request's state against the space each question leaves for it.</summary>
public sealed record DecisionInputMeasurement
{
    /// <summary>Tokens the encoded state takes.</summary>
    public required int StateTokens { get; init; }

    /// <summary>Tokens available for the state with each question, keyed by question identifier.</summary>
    public IReadOnlyDictionary<string, int> StateTokenBudgets { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

/// <summary>Measures a decision request's state against the model's input limits without running the model.</summary>
public interface IDecisionInputMeasurement
{
    /// <summary>Measures the state tokens of <paramref name="request"/> and the state budget for each of its questions.</summary>
    Task<DecisionInputMeasurement> MeasureAsync(
        DecisionInferenceRequest request,
        CancellationToken cancellationToken = default);
}
