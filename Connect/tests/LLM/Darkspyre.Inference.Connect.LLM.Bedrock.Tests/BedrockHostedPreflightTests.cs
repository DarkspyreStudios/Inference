using System.Runtime.CompilerServices;
using Amazon.BedrockAgentCore.Model;
using Amazon.Runtime.EventStreams;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockHostedPreflightTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task ConverseRefusesBeforeCredentialsLoggingOrSdk(int operation, bool limitOnly)
    {
        using var factory = new BedrockClientFactory();
        using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker();
        var client = new BedrockLlmClient(factory, guard, invoker);
        var connection = new LlmConnection
        { Version = "1", Transport = LlmTransport.AwsBedrockConverse, Endpoint = "aws://bedrock", CapabilityOverrides = new() { SupportsStreaming = operation == 0 } };
        var request = Request(limitOnly) with { ModelId = "anthropic.claude-haiku" };
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (operation == 2) client.EstimateInputTokens(connection, request);
            else await foreach (var unused in client.StreamAsync(connection, request)) { }
        });
        Assert.Empty(invoker.ConverseRequests); Assert.Empty(invoker.StreamRequests); Assert.False(invoker.Disposed);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task AgentCoreChatStartAndResumeRefuseBeforeHarness(int operation, bool limitOnly)
    {
        var invoker = new HarnessInvoker();
        var client = new BedrockAgentCoreLlmClient(invoker);
        var connection = new LlmConnection
        {
            Version = "1", Transport = LlmTransport.AwsBedrockAgentCore,
            Endpoint = "arn:aws:bedrock-agentcore:us-east-1:123456789012:harness/test",
            Credential = new LlmCredential { Aws = new() { Region = "us-east-1", Profile = "fixture" } }.Serialize()
        };
        var request = Request(limitOnly);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (operation == 1)
                await client.StartSessionAsync("connection", connection, new()
                { ModelId = "default", Runtime = new() { WorkingDirectory = "/unused" }, HostedTools = request.HostedTools, MaxHostedTurns = request.MaxHostedTurns });
            else if (operation == 2)
                await client.ResumeSessionAsync("connection", connection, "darkspyre-inference-connect-fixture-session", new()
                { ModelId = "default", Runtime = new() { WorkingDirectory = "/unused" }, HostedTools = request.HostedTools, MaxHostedTurns = request.MaxHostedTurns });
            else await foreach (var unused in client.StreamAsync(connection, request)) { }
        });
        Assert.Equal(0, invoker.Calls);
    }

    private static LlmChatRequest Request(bool limitOnly) => new()
    { ModelId = "default", Messages = [new("user", "hello")], HostedTools = limitOnly ? [] : [new LlmWebSearchTool()], MaxHostedTurns = limitOnly ? 1 : null };

    private sealed class HarnessInvoker : IBedrockAgentCoreInvoker
    {
        internal int Calls { get; private set; }
        public async IAsyncEnumerable<IEventStreamEvent> InvokeAsync(LlmAwsCredentials aws, InvokeHarnessRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        { Calls++; await Task.Yield(); yield break; }
    }
}
