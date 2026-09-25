using System.Text.Json;
using Darkspyre.Inference.Common.Decision;
using Darkspyre.Inference.Tensor.Common;
using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.Decision.Laya.Tests;

public sealed class LayaFrameTests
{
    private const int MaximumLength = 512;
    private const int MaximumHeadLength = 192;

    public static TheoryData<string> QuestionKinds => ["choice", "score", "boolean"];

    [Theory]
    [MemberData(nameof(QuestionKinds))]
    public void StateBudgetIsTheRoomLeftAfterThePrefix(string kind)
    {
        LayaFrame frame = LayaSequenceBuilder.Frame(new CharacterTokenizer(), Question(kind), MaximumLength, MaximumHeadLength);

        Assert.Equal(LayaTokenizer.ClassTokenId, frame.Prefix[0]);
        Assert.Equal(LayaTokenizer.SeparatorTokenId, frame.Prefix[^1]);
        Assert.Equal(MaximumLength - frame.Prefix.Length - 1, frame.StateBudget);
    }

    [Fact]
    public void AFullWindowLeavesNoStateBudget()
    {
        var question = new ChoiceDecisionQuestion
        {
            Id = "many",
            Instructions = "Pick one.",
            Choices = [.. Enumerable.Range(0, 10).Select(index => new DecisionChoice { Id = $"choice-{index}" })]
        };

        LayaFrame frame = LayaSequenceBuilder.Frame(new CharacterTokenizer(), question, 32, MaximumHeadLength);

        Assert.True(frame.Prefix.Length >= 32);
        Assert.Equal(0, frame.StateBudget);
    }

    [Theory]
    [MemberData(nameof(QuestionKinds))]
    public void BuildKeepsExactlyTheStateBudget(string kind)
    {
        var tokenizer = new CharacterTokenizer();
        DecisionQuestion question = Question(kind);
        LayaFrame frame = LayaSequenceBuilder.Frame(tokenizer, question, MaximumLength, MaximumHeadLength);

        foreach (int length in new[] { frame.StateBudget - 1, frame.StateBudget, frame.StateBudget + 1 })
        {
            JsonElement state = JsonSerializer.SerializeToElement(new string('x', length));
            LayaSequence sequence = LayaSequenceBuilder.Build(tokenizer, state, question, MaximumLength, MaximumHeadLength);
            int kept = Math.Min(length, frame.StateBudget);

            Assert.Equal(frame.Prefix, sequence.TokenIds[..frame.Prefix.Length]);
            Assert.Equal(
                LayaSequenceBuilder.EncodeState(tokenizer, state).Take(kept),
                sequence.TokenIds[frame.Prefix.Length..^1]);
            Assert.Equal(LayaTokenizer.SeparatorTokenId, sequence.TokenIds[^1]);
        }
    }

    [Fact]
    public void EncodeStateReadsAStringVerbatimAndAnyOtherStateAsItsJson()
    {
        var tokenizer = new CharacterTokenizer();

        Assert.Equal(tokenizer.Encode("a b"), LayaSequenceBuilder.EncodeState(tokenizer, JsonSerializer.SerializeToElement("a[MASK]b")));
        Assert.Equal(tokenizer.Encode("{\"turn\":1}"), LayaSequenceBuilder.EncodeState(tokenizer, JsonElement.Parse("{\"turn\":1}")));
    }

    [Fact]
    public async Task TheV7Onnx1ShapeKeepsFiveHundredTwelveMinusTheFramedQuestion()
    {
        LayaTokenizer tokenizer = await LayaTokenizer.CreateAsync(
            new PersistenceFileReference(new TensorDirectFilePersistStore(AppContext.BaseDirectory), "Fixtures", "tokenizer.json"),
            CancellationToken.None);
        var question = new BooleanDecisionQuestion
        {
            Id = "repo_changes",
            Instructions = "Does this unit require committed changes to source-controlled files?"
        };
        int head = tokenizer.Encode($"noul question: {question.Instructions}").Count;
        int falseOption = 1 + tokenizer.Encode(" false: no, the statement does not hold").Count;
        int trueOption = 1 + tokenizer.Encode(" true: yes, the statement holds").Count;

        LayaFrame frame = LayaSequenceBuilder.Frame(tokenizer, question, MaximumLength, MaximumHeadLength);

        Assert.Equal(10, falseOption);
        Assert.Equal(8, trueOption);
        Assert.Equal(MaximumLength - 22 - head, frame.StateBudget);

        JsonElement over = StateOfTokens(tokenizer, frame.StateBudget + 1);
        LayaSequence cut = LayaSequenceBuilder.Build(tokenizer, over, question, MaximumLength, MaximumHeadLength);
        Assert.Equal(MaximumLength, cut.TokenIds.Length);
        Assert.Equal(frame.StateBudget, cut.TokenIds.Length - frame.Prefix.Length - 1);

        JsonElement whole = StateOfTokens(tokenizer, frame.StateBudget);
        LayaSequence kept = LayaSequenceBuilder.Build(tokenizer, whole, question, MaximumLength, MaximumHeadLength);
        Assert.Equal(LayaSequenceBuilder.EncodeState(tokenizer, whole), kept.TokenIds[frame.Prefix.Length..^1]);
    }

    private static JsonElement StateOfTokens(LayaTokenizer tokenizer, int tokens)
    {
        var text = new System.Text.StringBuilder();
        while (tokenizer.Encode(text.ToString()).Count < tokens)
        {
            text.Append(" unit");
        }

        JsonElement state = JsonSerializer.SerializeToElement(text.ToString());
        Assert.Equal(tokens, LayaSequenceBuilder.EncodeState(tokenizer, state).Count);
        return state;
    }

    private static DecisionQuestion Question(string kind) => kind switch
    {
        "choice" => new ChoiceDecisionQuestion
        {
            Id = "route",
            Instructions = "Choose a route.",
            Choices = [new DecisionChoice { Id = "left", Description = "Left path" }, new DecisionChoice { Id = "right" }]
        },
        "score" => new ScoreDecisionQuestion
        {
            Id = "effort",
            Instructions = "How much effort?",
            Levels = [new DecisionScoreLevel { Id = "low", Description = "little" }, new DecisionScoreLevel { Id = "high", Description = "much" }]
        },
        _ => new BooleanDecisionQuestion { Id = "valid", Instructions = "Is this valid?" }
    };

    private sealed class CharacterTokenizer : ILayaTokenizer
    {
        public IReadOnlyList<int> Encode(string text) =>
            [.. text.Select(static character => 100 + character % 100)];
    }
}
