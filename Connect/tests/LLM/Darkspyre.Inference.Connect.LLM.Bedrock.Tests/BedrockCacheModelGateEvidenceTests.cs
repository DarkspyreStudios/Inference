using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.BedrockRuntime.Model;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockCacheModelGateEvidenceTests
{
    private static readonly string[] UnlistedModels =
    [
        "anthropic.claude-v2",
        "anthropic.claude-v2:1",
        "anthropic.claude-instant-v1",
        "anthropic.claude-3-haiku-20240307-v1:0",
        "anthropic.claude-3-opus-20240229-v1:0",
        "anthropic.claude-3-5-sonnet-20240620-v1:0",
        "us.anthropic.claude-3-5-sonnet-20240620-v1:0",
        "anthropic.claude-future-v1:0",
        "anthropic.claude-sonnet-4-5-unproven-v1:0"
    ];

    public static TheoryData<string, bool> ModelsAndBoundaries
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (string model in UnlistedModels)
            {
                data.Add(model, false);
                data.Add(model, true);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ModelsAndBoundaries))]
    public async Task CurrentFamilyGateEmitsManagedCachePointsForModelsAbsentFromAwsTable(string model, bool toolsOnly)
    {
        using var factory = new BedrockClientFactory();
        using var guard = new BedrockLoggingGuard();
        var client = new BedrockLlmClient(factory, guard, new StubConverseInvoker());
        var connection = new LlmConnection { Version = "1", Transport = LlmTransport.AwsBedrockConverse, Endpoint = "aws://bedrock" };
        var recorder = new ProjectionRecorder(connection);
        await using var session = new ManagedAgentSession(connection, new()
        {
            ModelId = model,
            Instructions = toolsOnly ? null : "static instructions",
            Tools = [new() { Name = "run", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }],
            ToolHandler = (_, _) => Task.FromResult(LlmToolOutput.FromText("unused"))
        }, [new(model, "Controlled model") { MaxContextTokens = 200_000 }], client, recorder.Stream,
            id => client.GetCapabilities(connection, id));
        var events = new List<LlmAgentEvent>();
        await foreach (LlmAgentEvent item in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("hello")] })) { events.Add(item); }

        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        LlmChatRequest generated = Assert.Single(recorder.Requests);
        Assert.Equal(LlmPromptCacheScope.Conversation, generated.PromptCache!.Scope);
        Assert.Equal(LlmPromptCacheRetention.Default, generated.PromptCache.Retention);
        Assert.True(client.GetCapabilities(connection, model).PromptCache.AcceptsHints);
        ConverseRequest built = Assert.Single(recorder.Built);
        Assert.NotNull(built.Messages[^1].Content[^1].CachePoint);
        if (toolsOnly)
        {
            Assert.Null(built.System);
            Assert.NotNull(built.ToolConfig.Tools[^1].CachePoint);
        }
        else
        {
            Assert.NotNull(built.System[^1].CachePoint);
            Assert.Null(Assert.Single(built.ToolConfig.Tools).CachePoint);
        }
    }

    private sealed class ProjectionRecorder(LlmConnection connection)
    {
        public List<LlmChatRequest> Requests { get; } = [];
        public List<ConverseRequest> Built { get; } = [];

        public async IAsyncEnumerable<LlmStreamEvent> Stream(LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            Built.Add(BedrockLlmClient.BuildRequest(connection, request, request.ModelId));
            await Task.CompletedTask;
            yield return new LlmChatCompletionEvent(new() { Text = "done", Status = LlmCompletionStatus.Completed, Diagnostics = new(200, null, null) });
        }
    }
}
