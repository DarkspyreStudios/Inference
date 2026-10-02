using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Azure.Tests;

public sealed class AzureExplicitCacheTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 3)]
    public async Task CacheSelectionRefusesBeforeAzureHeaders(bool responses, int operation) => await Refuse(responses, operation, true);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 3)]
    public async Task ProviderControlsRefuseBeforeAzureHeaders(bool responses, int operation) => await Refuse(responses, operation, false);

    private static async Task Refuse(bool responses, int operation, bool cache)
    {
        using var handler = new AzureProtocolTests.Handler(responses, operation == 0);
        using var http = new HttpClient(handler);
        var client = AzureProtocolTests.Client(http, responses);
        var connection = AzureProtocolTests.Connection(responses, operation == 0) with { Credential = "" };
        var request = JsonSerializer.Deserialize("{\"ModelId\":\"deployment-alias\",\"ExplicitCache\":{\"ResourceName\":\"cachedContents/test\",\"ModelId\":\"deployment-alias\",\"ScopeId\":\"scope\"}}", ConnectLlmJson.ChatRequest)!;
        if (!cache) request = request with { ExplicitCache = null, ProviderControls = new XaiMultiAgentControls(4) };
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (operation == 3) ((ILlmContextEstimator)client).EstimateInputTokens(connection, request);
            else await AzureProtocolTests.Complete(client, connection, request);
        });
        Assert.Equal(0, handler.Calls); Assert.Null(handler.Key); Assert.Null(handler.Authorization);
    }
}
