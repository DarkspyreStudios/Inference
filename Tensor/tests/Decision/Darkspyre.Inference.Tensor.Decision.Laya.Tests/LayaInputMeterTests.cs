using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Decision;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Decision.Laya.Tests;

public sealed class LayaInputMeterTests
{
    [Fact]
    public async Task MeasuresTheStateAndEachQuestionsBudgetFromTheTwoArtifacts()
    {
        LayaInputMeter meter = await LoadFixtureMeterAsync();
        LayaTokenizer tokenizer = await LayaTokenizer.CreateAsync(Fixture("tokenizer.json"), CancellationToken.None);
        DecisionInferenceRequest request = Request(JsonSerializer.SerializeToElement("Add a retry to the uploader."));

        DecisionInputMeasurement measurement = await meter.MeasureAsync(request);

        Assert.Equal(LayaSequenceBuilder.EncodeState(tokenizer, request.State).Count, measurement.StateTokens);
        Assert.Equal(request.Questions.Select(static question => question.Id), measurement.StateTokenBudgets.Keys.Order(StringComparer.Ordinal));
        foreach (DecisionQuestion question in request.Questions)
        {
            Assert.Equal(
                LayaSequenceBuilder.Frame(tokenizer, question, 512, 192).StateBudget,
                measurement.StateTokenBudgets[question.Id]);
        }
    }

    [Fact]
    public async Task CountsAStringStateVerbatimAndAnObjectStateAsItsJson()
    {
        LayaInputMeter meter = await LoadFixtureMeterAsync();
        LayaTokenizer tokenizer = await LayaTokenizer.CreateAsync(Fixture("tokenizer.json"), CancellationToken.None);
        JsonElement text = JsonSerializer.SerializeToElement("plain unit text");
        JsonElement structured = JsonElement.Parse("{\"unit\":\"plain unit text\"}");

        DecisionInputMeasurement fromText = await meter.MeasureAsync(Request(text));
        DecisionInputMeasurement fromObject = await meter.MeasureAsync(Request(structured));

        Assert.Equal(tokenizer.Encode("plain unit text").Count, fromText.StateTokens);
        Assert.Equal(tokenizer.Encode("{\"unit\":\"plain unit text\"}").Count, fromObject.StateTokens);
    }

    [Fact]
    public async Task AMissingArtifactIsModelUnavailable()
    {
        InferenceException failure = await Assert.ThrowsAsync<InferenceException>(() =>
            LayaInputMeter.LoadAsync(Fixture("missing-tokenizer.json"), Fixture("laya-agent-config.json")));

        Assert.Equal(InferenceFailureCode.ModelUnavailable, failure.Code);
    }

    [Fact]
    public async Task AMalformedConfigurationIsModelCorrupt()
    {
        string root = Directory.CreateTempSubdirectory("laya-meter-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "fixture"));
            await File.WriteAllTextAsync(Path.Combine(root, "fixture", "config.json"), "{ not json");
            InferenceException failure = await Assert.ThrowsAsync<InferenceException>(() =>
                LayaInputMeter.LoadAsync(Fixture("tokenizer.json"), Path.Combine(root, "fixture", "config.json")));

            Assert.Equal(InferenceFailureCode.ModelCorrupt, failure.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AnInvalidRequestIsRefusedAsDecideAsyncRefusesIt()
    {
        LayaInputMeter meter = await LoadFixtureMeterAsync();
        JsonElement state = JsonSerializer.SerializeToElement("unit");
        DecisionInferenceRequest[] invalid =
        [
            new() { ModelId = "fixture", State = state, Questions = [] },
            new() { ModelId = "fixture", State = state, Questions = [Boolean("same"), Boolean("same")] },
            new() { ModelId = "fixture", State = state, Questions = [new BooleanDecisionQuestion { Id = "blank", Instructions = " " }] }
        ];

        foreach (DecisionInferenceRequest request in invalid)
        {
            InferenceException failure = await Assert.ThrowsAsync<InferenceException>(() => meter.MeasureAsync(request));
            Assert.Equal(InferenceFailureCode.InvalidRequest, failure.Code);
        }
    }

    internal static DecisionInferenceRequest Request(JsonElement state) => new()
    {
        ModelId = "fixture",
        State = state,
        Questions = [Boolean("repo_changes"), Boolean("security_sensitive")]
    };

    private static BooleanDecisionQuestion Boolean(string id) => new()
    {
        Id = id,
        Instructions = $"Does the {id.Replace('_', ' ')} statement hold for this unit?"
    };

    private static Task<LayaInputMeter> LoadFixtureMeterAsync() =>
        LayaInputMeter.LoadAsync(Fixture("tokenizer.json"), Fixture("laya-agent-config.json"));

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
