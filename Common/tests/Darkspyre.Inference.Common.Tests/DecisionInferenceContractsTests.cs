using System.Text.Json;
using Darkspyre.Inference.Common.Decision;

namespace Darkspyre.Inference.Common.Tests;

public sealed class DecisionInferenceContractsTests
{
    [Fact]
    public void DecisionQuestionsRoundTripWithTypedDiscriminators()
    {
        DecisionQuestion original = new ChoiceDecisionQuestion
        {
            Id = "route",
            Instructions = "Choose a route.",
            Choices =
            [
                new DecisionChoice { Id = "left", Description = "Take the left path." },
                new DecisionChoice { Id = "right", Description = "Take the right path." }
            ]
        };

        string json = JsonSerializer.Serialize(original);
        DecisionQuestion? restored = JsonSerializer.Deserialize<DecisionQuestion>(json);

        ChoiceDecisionQuestion choice = Assert.IsType<ChoiceDecisionQuestion>(restored);
        Assert.Equal(DecisionQuestionKind.Choice, choice.Kind);
        Assert.Equal(["left", "right"], choice.Choices.Select(static item => item.Id));
    }

    [Fact]
    public void DecisionAnswersRoundTripWithTypedDiscriminators()
    {
        DecisionAnswer original = new BooleanDecisionAnswer
        {
            QuestionId = "safe",
            Value = true,
            TrueProbability = 0.8,
            Confidence = 0.8,
            ActionProbability = 0.25
        };

        string json = JsonSerializer.Serialize(original);
        DecisionAnswer? restored = JsonSerializer.Deserialize<DecisionAnswer>(json);

        BooleanDecisionAnswer answer = Assert.IsType<BooleanDecisionAnswer>(restored);
        Assert.True(answer.Value);
        Assert.Equal(0.8, answer.TrueProbability);
    }
}
