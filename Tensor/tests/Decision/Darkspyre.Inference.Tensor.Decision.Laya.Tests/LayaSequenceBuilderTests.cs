using System.Text.Json;
using Darkspyre.Inference.Common.Decision;

namespace Darkspyre.Inference.Tensor.Decision.Laya.Tests;

public sealed class LayaSequenceBuilderTests
{
    [Fact]
    public void ChoiceSequenceUsesTheCheckpointFramingAndOrderedMarkers()
    {
        var question = new ChoiceDecisionQuestion
        {
            Id = "route",
            Instructions = "Choose [MASK] safely.",
            Choices =
            [
                new DecisionChoice { Id = "left", Description = "Left path" },
                new DecisionChoice { Id = "right" }
            ]
        };

        LayaSequence sequence = LayaSequenceBuilder.Build(
            new CharacterTokenizer(),
            JsonElement.Parse("{\"turn\":1}"),
            question, 512, 192);

        Assert.Equal(LayaTokenizer.ClassTokenId, sequence.TokenIds[0]);
        Assert.Equal(LayaTokenizer.SeparatorTokenId, sequence.TokenIds[^1]);
        Assert.Equal(["left", "right"], sequence.OptionIds);
        Assert.Equal(2, sequence.MarkerPositions.Length);
        Assert.All(sequence.MarkerPositions, marker =>
            Assert.Equal(LayaTokenizer.MaskTokenId, sequence.TokenIds[marker]));
        Assert.Equal(0, sequence.QuestionType);
    }

    [Fact]
    public void LongStateIsTruncatedToTheModelWindow()
    {
        var question = new BooleanDecisionQuestion
        {
            Id = "valid",
            Instructions = "Is this valid?"
        };
        string state = JsonSerializer.Serialize(new string('x', 5_000));

        LayaSequence sequence = LayaSequenceBuilder.Build(
            new CharacterTokenizer(),
            JsonElement.Parse(state),
            question, 512, 192);

        Assert.Equal(512, sequence.TokenIds.Length);
        Assert.Equal(2, sequence.MarkerPositions.Length);
        Assert.Equal(2, sequence.QuestionType);
    }

    private sealed class CharacterTokenizer : ILayaTokenizer
    {
        public IReadOnlyList<int> Encode(string text) =>
            [.. text.Select(static character => 100 + character % 100)];
    }
}
