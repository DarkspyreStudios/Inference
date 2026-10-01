using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.Documents;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockNonStreamingValidationTests
{
    private const string Model = "us.anthropic.claude-sonnet-5-v1:0";
    private const string InputError = "Bedrock returned tool input that is not a JSON object.";

    [Theory]
    [InlineData("array", false)]
    [InlineData("array", true)]
    [InlineData("string", false)]
    [InlineData("string", true)]
    [InlineData("bool", false)]
    [InlineData("bool", true)]
    [InlineData("int", false)]
    [InlineData("int", true)]
    [InlineData("long", false)]
    [InlineData("long", true)]
    [InlineData("double", false)]
    [InlineData("double", true)]
    [InlineData("null", false)]
    [InlineData("null", true)]
    public void NonObjectToolInputFailsBeforePublishingAnyCompleteResponseEvents(string kind, bool reasoning)
    {
        var seen = new List<LlmStreamEvent>();
        LlmProviderException error = Assert.Throws<LlmProviderException>(() =>
        {
            foreach (LlmStreamEvent item in BedrockLlmClient.MapResponse(Connection(), Request(), null,
                         Response(Input(kind), "tool_use", reasoning)))
            { seen.Add(item); }
        });
        Assert.Equal(InputError, error.Message);
        Assert.Equal(LlmTransport.AwsBedrockConverse, error.Transport);
        Assert.Equal("converse-request-1", error.RequestId);
        Assert.Empty(seen);

        var stream = new BedrockStreamAccumulator();
        stream.Observe(new ContentBlockStartEvent
        { ContentBlockIndex = 0, Start = new() { ToolUse = new() { ToolUseId = "call-1", Name = "lookup" } } });
        stream.Observe(new ContentBlockDeltaEvent
        { ContentBlockIndex = 0, Delta = new() { ToolUse = new() { Input = InputJson(kind) } } });
        stream.Observe(new ContentBlockStopEvent { ContentBlockIndex = 0 });
        stream.Observe(new MessageStopEvent { StopReason = new("tool_use") });
        LlmProviderException streamed = Assert.Throws<LlmProviderException>(() =>
            BedrockLlmClient.BuildResult(Connection(), Request(), null, stream.Complete("stream-request-1")));
        Assert.Equal(error.Message, streamed.Message);
        Assert.Equal(error.Transport, streamed.Transport);
    }

    [Theory]
    [InlineData("empty-object", false)]
    [InlineData("empty-object", true)]
    [InlineData("object", false)]
    [InlineData("object", true)]
    public void ObjectToolInputPreservesToolEventsAndSignedContinuation(string kind, bool reasoning)
    {
        LlmStreamEvent[] events = BedrockLlmClient.MapResponse(Connection(), Request(), null,
            Response(Input(kind), "tool_use", reasoning)).ToArray();
        LlmChatResult result = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        Assert.Single(events.OfType<LlmChatCompletionEvent>());
        Assert.Equal(LlmCompletionStatus.RequiresAction, result.Status);
        LlmToolCall call = Assert.Single(result.ToolCalls);
        Assert.Equal("call-1", call.Id);
        Assert.Equal("lookup", call.Name);
        Assert.Equal(kind == "object" ? "{\"path\":\"a.txt\"}" : "{}", call.ArgumentsJson);
        int start = Array.FindIndex(events, item => item is LlmToolCallStartedEvent);
        Assert.Equal(new LlmToolCallStartedEvent(call.Id, call.Name, 0), events[start]);
        Assert.Equal(new LlmToolCallArgumentsDeltaEvent(call.Id, call.ArgumentsJson), events[start + 1]);
        Assert.Equal(new LlmToolCallCompletedEvent(call), events[start + 2]);
        if (reasoning)
        {
            Assert.Equal(new LlmReasoningDeltaEvent("Checking.", false), events[0]);
            Assert.Contains("signature-1", result.ContinuationState!.Payload, StringComparison.Ordinal);
            Assert.Contains("\"input\":" + call.ArgumentsJson, result.ContinuationState.Payload, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(events.OfType<LlmReasoningDeltaEvent>());
            Assert.Null(result.ContinuationState);
        }
    }

    [Theory]
    [InlineData("malformed_tool_use", false)]
    [InlineData("malformed_tool_use", true)]
    [InlineData("malformed_model_output", false)]
    [InlineData("malformed_model_output", true)]
    public void ProviderReportedMalformedStopKeepsTheExistingFailedResult(string stop, bool reasoning)
    {
        LlmStreamEvent[] events = BedrockLlmClient.MapResponse(Connection(), Request(), null,
            Response(Input("array"), stop, reasoning)).ToArray();
        LlmChatResult result = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        Assert.Equal(LlmCompletionStatus.Failed, result.Status);
        Assert.Equal(stop, result.FinishReason);
        Assert.Empty(result.ToolCalls);
        Assert.Null(result.ContinuationState);
        Assert.Empty(events.OfType<LlmToolCallStartedEvent>());
        Assert.Empty(events.OfType<LlmToolCallCompletedEvent>());
    }

    private static LlmConnection Connection() => new()
    { Version = "1", Transport = LlmTransport.AwsBedrockConverse, Endpoint = "https://bedrock.invalid" };

    private static LlmChatRequest Request() => new() { ModelId = Model };

    private static Document Input(string kind) => kind switch
    {
        "null" => default(Document),
        "array" => new Document(new List<Document> { new(1) }),
        "string" => new Document("private-tool-input"),
        "bool" => new Document(true),
        "int" => new Document(1),
        "long" => new Document(long.MaxValue),
        "double" => new Document(0.5),
        "empty-object" => new Document(new Dictionary<string, Document>()),
        "object" => new Document(new Dictionary<string, Document> { ["path"] = new("a.txt") }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string InputJson(string kind) => kind switch
    {
        "null" => "null",
        "array" => "[1]",
        "string" => "\"private-tool-input\"",
        "bool" => "true",
        "int" => "1",
        "long" => "9223372036854775807",
        "double" => "0.5",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static ConverseResponse Response(Document input, string stop, bool reasoning)
    {
        var blocks = new List<ContentBlock>();
        if (reasoning)
        {
            blocks.Add(new()
            {
                ReasoningContent = new()
                { ReasoningText = new() { Text = "Checking.", Signature = "signature-1" } }
            });
        }
        blocks.Add(new() { ToolUse = new() { ToolUseId = "call-1", Name = "lookup", Input = input } });
        return StubConverseInvoker.Converse(blocks, stop);
    }
}
