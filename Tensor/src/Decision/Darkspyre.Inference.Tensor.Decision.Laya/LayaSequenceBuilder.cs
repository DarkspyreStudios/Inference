using System.Text.Json;
using Darkspyre.Inference.Common.Decision;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

internal readonly record struct LayaSequence(
    int[] TokenIds,
    int[] MarkerPositions,
    string[] OptionIds,
    int QuestionType);

/// <summary>
/// A question's sequence up to and including the separator after its options, and the state tokens
/// that still fit before the final separator.
/// </summary>
internal readonly record struct LayaFrame(
    int[] Prefix,
    int[] MarkerPositions,
    string[] OptionIds,
    int QuestionType,
    int StateBudget);

internal static class LayaSequenceBuilder
{
    private const int MaximumOptionLength = 48;

    public static LayaSequence Build(
        ILayaTokenizer tokenizer,
        JsonElement state,
        DecisionQuestion question,
        int maximumLength,
        int maximumHeadLength)
    {
        LayaFrame frame = Frame(tokenizer, question, maximumLength, maximumHeadLength);
        var tokenIds = new List<int>(maximumLength);
        tokenIds.AddRange(frame.Prefix);
        tokenIds.AddRange(EncodeState(tokenizer, state).Take(frame.StateBudget));
        tokenIds.Add(LayaTokenizer.SeparatorTokenId);

        return new LayaSequence(
            [.. tokenIds.Take(maximumLength)],
            [.. frame.MarkerPositions.Where(marker => marker < maximumLength)],
            frame.OptionIds,
            frame.QuestionType);
    }

    /// <summary>The state text as <see cref="Build"/> reads it, encoded without any framing token.</summary>
    public static IReadOnlyList<int> EncodeState(ILayaTokenizer tokenizer, JsonElement state)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        string serializedState = state.ValueKind == JsonValueKind.String
            ? state.GetString() ?? string.Empty
            : state.GetRawText();
        return tokenizer.Encode(serializedState.Replace(
            LayaTokenizer.MaskToken,
            " ",
            StringComparison.Ordinal));
    }

    public static LayaFrame Frame(
        ILayaTokenizer tokenizer,
        DecisionQuestion question,
        int maximumLength,
        int maximumHeadLength)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(question);
        (string type, string[] optionIds, string[] optionText, int questionType) = Render(question);
        string instructions = question.Instructions.Replace(LayaTokenizer.MaskToken, " ", StringComparison.Ordinal);
        List<int> head = [.. tokenizer.Encode($"{type} question: {instructions}")];
        List<int>[] options = optionText
            .Select(text =>
            {
                List<int> tokens = [LayaTokenizer.MaskTokenId];
                tokens.AddRange(tokenizer.Encode(" " + text.Replace(
                    LayaTokenizer.MaskToken,
                    " ",
                    StringComparison.Ordinal)).Take(MaximumOptionLength));
                return tokens;
            })
            .ToArray();

        int optionBudget = maximumHeadLength - options.Sum(static item => item.Count);
        if (optionBudget < 16)
        {
            int perOption = Math.Max(4, (maximumHeadLength - 16) / Math.Max(1, options.Length));
            for (int index = 0; index < options.Length; index++)
            {
                options[index] = [.. options[index].Take(perOption)];
            }

            optionBudget = maximumHeadLength - options.Sum(static item => item.Count);
        }

        head = [.. head.Take(Math.Max(8, optionBudget))];
        var tokenIds = new List<int>(maximumLength) { LayaTokenizer.ClassTokenId };
        tokenIds.AddRange(head);
        tokenIds.Add(LayaTokenizer.SeparatorTokenId);
        var markers = new List<int>(options.Length);
        foreach (List<int> option in options)
        {
            markers.Add(tokenIds.Count);
            tokenIds.AddRange(option);
        }

        tokenIds.Add(LayaTokenizer.SeparatorTokenId);
        int stateBudget = Math.Max(0, maximumLength - tokenIds.Count - 1);
        return new LayaFrame([.. tokenIds], [.. markers], optionIds, questionType, stateBudget);
    }

    private static (string Type, string[] OptionIds, string[] OptionText, int QuestionType) Render(
        DecisionQuestion question) => question switch
        {
            ChoiceDecisionQuestion choice => (
                "choice",
                [.. choice.Choices.Select(static item => item.Id)],
                [.. choice.Choices.Select(static item => string.IsNullOrEmpty(item.Description)
                    ? item.Id
                    : $"{item.Id}: {item.Description}")],
                0),
            ScoreDecisionQuestion score => (
                "score",
                [.. score.Levels.Select((_, index) => index.ToString(System.Globalization.CultureInfo.InvariantCulture))],
                [.. score.Levels.Select((item, index) => $"level {index}: {item.Description}")],
                1),
            BooleanDecisionQuestion boolean => (
                "noul",
                ["false", "true"],
                [
                    "false: " + (string.IsNullOrEmpty(boolean.FalseDescription)
                        ? "no, the statement does not hold"
                        : boolean.FalseDescription),
                    "true: " + (string.IsNullOrEmpty(boolean.TrueDescription)
                        ? "yes, the statement holds"
                        : boolean.TrueDescription)
                ],
                2),
            _ => throw new ArgumentOutOfRangeException(nameof(question))
        };
}
