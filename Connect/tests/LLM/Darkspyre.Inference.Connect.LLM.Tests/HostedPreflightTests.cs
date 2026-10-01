using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HostedPreflightTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string route in new[] { "responses", "compatible", "anthropic", "gemini", "xai" })
            foreach (int operation in route == "gemini" ? new[] { 0, 1, 2, 3 } : route == "anthropic" ? new[] { 0, 1, 2 } : route is "responses" or "xai" ? new[] { 0, 1, 3 } : new[] { 0, 1 })
                foreach (bool limitOnly in new[] { false, true })
                    yield return [route, operation, limitOnly];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task UnavailableHostedFeaturesRefuseBeforeDirectEffects(string route, int operation, bool limitOnly)
    {
        using var handler = new RawSamplingGuardsTests.Handler(route);
        using var http = new HttpClient(handler);
        ILlmClient client = RawSamplingGuardsTests.Client(http, route);
        var request = RawSamplingGuardsTests.Request(route) with
        { HostedTools = limitOnly ? [] : [new LlmCodeExecutionTool()], MaxHostedTurns = limitOnly ? 1 : null };
        var connection = RawSamplingGuardsTests.Connection(route, operation == 0);
        Assert.Empty(client.GetCapabilities(connection, request.ModelId).HostedTools);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (operation == 2) await ((ILlmProviderTokenCounter)client).CountInputTokensAsync(connection, request);
            else if (operation == 3) ((ILlmContextEstimator)client).EstimateInputTokens(connection, request);
            else await foreach (LlmStreamEvent unused in client.StreamAsync(connection, request)) { }
        });
        Assert.Empty(handler.Bodies);
        Assert.Equal(0, handler.Calls);
        if (!limitOnly) Assert.IsType<LlmCodeExecutionTool>(Assert.Single(request.HostedTools));
    }
}
