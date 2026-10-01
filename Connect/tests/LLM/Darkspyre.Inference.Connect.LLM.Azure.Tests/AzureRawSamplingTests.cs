using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Azure.Tests;

public sealed class AzureRawSamplingTests
{
    private static readonly string[] Fields =
        ["temperature", "top_p", "top_k", "seed", "presence_penalty", "frequency_penalty", "stop", "stop_sequences",
         "topP", "topK", "presencePenalty", "frequencyPenalty", "stopSequences"];

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool responses in new[] { false, true })
            foreach (int operation in responses ? new[] { 0, 1, 3 } : new[] { 0, 1 })
                foreach (bool connectionScope in new[] { false, true })
                    foreach (string field in Fields)
                        yield return [responses, operation, connectionScope, field];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RawSamplingIsRejectedBeforeAzureHeadersDispatchOrEstimate(
        bool responses, int operation, bool connectionScope, string field)
    {
        using var handler = new AzureProtocolTests.Handler(responses, operation == 0);
        using var http = new HttpClient(handler);
        var client = AzureProtocolTests.Client(http, responses);
        var options = new Dictionary<string, JsonElement> { [field] = JsonElement.Parse(Value(field)) };
        var connection = AzureProtocolTests.Connection(responses, operation == 0) with
        {
            ProviderOptions = connectionScope ? options : new Dictionary<string, JsonElement>(),
            Credential = "",
            CapabilityOverrides = new()
            {
                SupportsStreaming = operation == 0,
                SupportsTemperature = false,
                SupportsTopP = false,
                SupportsTopK = false,
                SupportsSeed = false,
                SupportsPresencePenalty = false,
                SupportsFrequencyPenalty = false,
                MaxStopSequences = 0
            }
        };
        var request = AzureProtocolTests.Request with
        { ProviderOptions = connectionScope ? new Dictionary<string, JsonElement>() : options };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (operation == 3) ((ILlmContextEstimator)client).EstimateInputTokens(connection, request);
            else await AzureProtocolTests.Complete(client, connection, request);
        });
        Assert.Equal(0, handler.Calls);
        Assert.Null(handler.Key);
        Assert.Null(handler.Authorization);
        Assert.Same(options, connectionScope ? connection.ProviderOptions : request.ProviderOptions);
        Assert.Equal(Value(field), Assert.Single(options).Value.GetRawText());
        Assert.Empty(connectionScope ? request.ProviderOptions : connection.ProviderOptions);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task TypedControlsAndExtensionPrecedenceKeepAzureDeploymentIdentity(bool responses, bool streaming)
    {
        using var handler = new AzureProtocolTests.Handler(responses, streaming);
        using var http = new HttpClient(handler);
        var connection = AzureProtocolTests.Connection(responses, streaming) with
        { ProviderOptions = Options("1") };
        var request = AzureProtocolTests.Request with { Temperature = .5, TopP = .8, ProviderOptions = Options("2") };
        await AzureProtocolTests.Complete(AzureProtocolTests.Client(http, responses), connection, request);
        Assert.Equal(.5, handler.Body!["temperature"]!.GetValue<double>());
        Assert.Equal(.8, handler.Body["top_p"]!.GetValue<double>());
        Assert.Equal(2, handler.Body["vendor_extension"]!.GetValue<int>());
        Assert.Equal("deployment-alias", handler.Body["model"]!.GetValue<string>());
        Assert.Equal("1", connection.ProviderOptions["vendor_extension"].GetRawText());
        Assert.Equal("2", request.ProviderOptions["vendor_extension"].GetRawText());
    }

    private static Dictionary<string, JsonElement> Options(string json) => new()
    { ["vendor_extension"] = JsonElement.Parse(json) };
    private static string Value(string field) => field switch
    {
        "stop" or "stop_sequences" or "stopSequences" => "[\"stop\"]",
        "seed" or "top_k" or "topK" => "1",
        _ => "0.5"
    };
}
