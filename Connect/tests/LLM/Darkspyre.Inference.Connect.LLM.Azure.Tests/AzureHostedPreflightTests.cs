using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Azure.Tests;

public sealed class AzureHostedPreflightTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (bool responses in new[] { false, true })
            foreach (int operation in responses ? new[] { 0, 1, 3 } : new[] { 0, 1 })
                foreach (bool limitOnly in new[] { false, true }) yield return [responses, operation, limitOnly];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task HostedRequestsRefuseBeforeAzureHeaderAcquisition(bool responses, int operation, bool limitOnly)
    {
        using var handler = new AzureProtocolTests.Handler(responses, operation == 0);
        using var http = new HttpClient(handler);
        var client = AzureProtocolTests.Client(http, responses);
        var connection = AzureProtocolTests.Connection(responses, operation == 0) with { Credential = "" };
        var request = AzureProtocolTests.Request with
        { HostedTools = limitOnly ? [] : [new LlmCodeExecutionTool()], MaxHostedTurns = limitOnly ? 1 : null };
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (operation == 3) ((ILlmContextEstimator)client).EstimateInputTokens(connection, request);
            else await AzureProtocolTests.Complete(client, connection, request);
        });
        Assert.Equal(0, handler.Calls);
        Assert.Null(handler.Key); Assert.Null(handler.Authorization);
    }
}
