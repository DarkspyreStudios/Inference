using System.Runtime.CompilerServices;
using Amazon.BedrockAgentCore.Model;
using Amazon.Runtime.EventStreams;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockExplicitCacheTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SelectionRefusesBeforeSdkInvocation(bool agentCore, bool estimate)
    {
        using var factory = new BedrockClientFactory();
        using var guard = new BedrockLoggingGuard();
        var converse = new StubConverseInvoker();
        var harness = new Harness();
        ILlmClient client = agentCore ? new BedrockAgentCoreLlmClient(harness) : new BedrockLlmClient(factory, guard, converse);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = client.Transport,
            Endpoint = agentCore ? "arn:aws:bedrock-agentcore:us-east-1:123456789012:harness/test" : "aws://bedrock",
            Credential = agentCore ? new LlmCredential { Aws = new() { Region = "us-east-1", Profile = "fixture" } }.Serialize() : null
        };
        var request = new LlmChatRequest
        {
            ModelId = agentCore ? "default" : "anthropic.claude-haiku",
            Messages = [new("user", "hello")],
            ExplicitCache = new("cachedContents/cache", "model", "scope")
        };
        Assert.False(client.GetCapabilities(connection, request.ModelId).SupportsExplicitCache);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (estimate) ((ILlmContextEstimator)client).EstimateInputTokens(connection, request);
            else await foreach (var unused in client.StreamAsync(connection, request)) { }
        });
        Assert.Empty(converse.ConverseRequests); Assert.Empty(converse.StreamRequests); Assert.Equal(0, harness.Calls);
    }

    private sealed class Harness : IBedrockAgentCoreInvoker
    {
        internal int Calls { get; private set; }
        public async IAsyncEnumerable<IEventStreamEvent> InvokeAsync(LlmAwsCredentials aws, InvokeHarnessRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        { Calls++; await Task.CompletedTask; yield break; }
    }
}
