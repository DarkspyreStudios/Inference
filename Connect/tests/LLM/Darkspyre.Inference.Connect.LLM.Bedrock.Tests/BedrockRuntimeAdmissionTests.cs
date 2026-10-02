using System.Runtime.CompilerServices;
using Amazon.BedrockAgentCore.Model;
using Amazon.Runtime.EventStreams;
using Darkspyre.Inference.Connect.LLM.Tests;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockRuntimeAdmissionTests
{
    [Theory]
    [InlineData("acp")]
    [InlineData("copilot")]
    [InlineData("launch")]
    [InlineData("copilot-token")]
    [InlineData("runtime-token")]
    public async Task ConverseAndAgentCoreRefuseBeforeSdkEffects(string input)
    {
        using var factory = new BedrockClientFactory();
        using var logging = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker();
        var client = new BedrockLlmClient(factory, logging, invoker);
        LlmConnection connection = RuntimeInputAdmissionTests.Foreign(input, new()
        {
            Version = "v1", Transport = LlmTransport.AwsBedrockConverse, Endpoint = "aws://bedrock",
            Credential = new LlmCredential { Aws = new() { Region = "us-east-1", Profile = "fixture" } }.Serialize()
        });
        var request = new LlmChatRequest { ModelId = "anthropic.claude-haiku", Messages = [new("user", "input")] };
        Assert.Throws<NotSupportedException>(() => client.GetCapabilities(connection));
        Assert.Throws<NotSupportedException>(() => client.EstimateInputTokens(connection, request));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.ListModelsAsync(connection));
        await Assert.ThrowsAsync<NotSupportedException>(async () => { await foreach (var unused in client.StreamAsync(connection, request)) { } });
        Assert.Empty(invoker.ConverseRequests);
        Assert.Empty(invoker.StreamRequests);

        var harness = new Harness();
        var native = new BedrockAgentCoreLlmClient(harness);
        connection = connection with { Transport = LlmTransport.AwsBedrockAgentCore, Endpoint = "arn:aws:bedrock-agentcore:us-east-1:123456789012:harness/fixture" };
        Assert.Throws<NotSupportedException>(() => native.GetCapabilities(connection));
        await Assert.ThrowsAsync<NotSupportedException>(() => native.StartSessionAsync("id", connection, new()
        {
            ModelId = "default", Runtime = new() { WorkingDirectory = "/unused", Policy = new LlmAgentHostRuntimePolicy() }
        }));
        await Assert.ThrowsAsync<NotSupportedException>(() => native.ResumeSessionAsync("id", connection, "fixture-thread", new()
        {
            ModelId = "default", Runtime = new() { WorkingDirectory = "/unused", Policy = new LlmAgentHostRuntimePolicy() }
        }));
        Assert.Equal(0, harness.Calls);
    }

    private sealed class Harness : IBedrockAgentCoreInvoker
    {
        public int Calls;
        public async IAsyncEnumerable<IEventStreamEvent> InvokeAsync(LlmAwsCredentials aws, InvokeHarnessRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        { Calls++; await Task.Yield(); yield break; }
    }
}
