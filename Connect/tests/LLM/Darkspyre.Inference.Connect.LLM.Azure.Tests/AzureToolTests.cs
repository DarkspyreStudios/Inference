using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Azure.Tests;

public sealed class AzureToolTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ToolStartsArgumentsUsageAndReplayUseTheSelectedAzureProtocol(bool responses, bool streaming)
    {
        using var handler = new ToolHandler(responses, streaming);
        using var http = new HttpClient(handler);
        var client = AzureProtocolTests.Client(http, responses);
        LlmConnection connection = AzureProtocolTests.Connection(responses, streaming);
        LlmChatRequest request = AzureProtocolTests.Request with
        {
            Tools = [new LlmToolDefinition() { Name = "lookup", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }]
        };
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request)) events.Add(item);
        LlmChatResult first = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        LlmToolCall call = Assert.IsType<LlmToolCall>(Assert.Single(first.ToolCalls));
        Assert.Equal("call_1", call.Id);
        Assert.Equal("lookup", call.Name);
        Assert.Equal("{\"city\":\"Lyon\"}", call.ArgumentsJson);
        Assert.Equal(LlmCompletionStatus.RequiresAction, first.Status);
        Assert.Equal(8, first.Usage!.InputTokens);
        Assert.Equal(4, first.Usage.OutputTokens);
        Assert.Equal("call_1", Assert.Single(events.OfType<LlmToolCallStartedEvent>()).ToolCallId);
        Assert.Equal(call, Assert.Single(events.OfType<LlmToolCallCompletedEvent>()).ToolCall);
        if (streaming)
        {
            Assert.Equal(call.ArgumentsJson, string.Concat(events.OfType<LlmToolCallArgumentsDeltaEvent>().Select(item => item.Delta)));
            Assert.True(events.FindIndex(item => item is LlmToolCallStartedEvent) < events.FindIndex(item => item is LlmToolCallArgumentsDeltaEvent));
        }
        LlmChatRequest next = request with
        {
            Messages = [new("user", "question"), new("assistant", first.Text) { ToolCalls = first.ToolCalls, ContinuationState = first.ContinuationState },
                new("user", "") { ToolResults = [new LlmToolResult(call.Id, LlmToolOutput.FromText("sunny"))] }]
        };
        await AzureProtocolTests.Complete(client, connection, next);
        string replay = handler.Body!.ToJsonString();
        Assert.Contains("call_1", replay, StringComparison.Ordinal);
        Assert.Contains("sunny", replay, StringComparison.Ordinal);
        Assert.Contains(responses ? "function_call_output" : "tool_call_id", replay, StringComparison.Ordinal);
        Assert.Equal("deployment-alias", handler.Body["model"]!.GetValue<string>());
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task NativeTerminalFilteringIsNotMisreportedAsSuccessfulText(bool responses, bool streaming)
    {
        string output = responses
            ? "{\"id\":\"r\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"declined\"}]}]}"
            : "{\"choices\":[{\"index\":0,\"finish_reason\":\"content_filter\",\"message\":{\"role\":\"assistant\",\"content\":null},\"delta\":{}}]}";
        using var handler = new FixedHandler(responses, streaming, output);
        using var http = new HttpClient(handler);
        LlmChatResult result = await AzureProtocolTests.Complete(AzureProtocolTests.Client(http, responses), AzureProtocolTests.Connection(responses, streaming), AzureProtocolTests.Request);
        Assert.Equal(LlmCompletionStatus.Refused, result.Status);
        Assert.Empty(result.Text);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class ToolHandler(bool responses, bool streaming) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public JsonObject? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            const string arguments = "{\"city\":\"Lyon\"}";
            string output = responses
                ? "{\"id\":\"r\",\"status\":\"completed\",\"usage\":{\"input_tokens\":8,\"output_tokens\":4},\"output\":[{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call_1\",\"name\":\"lookup\",\"arguments\":" + JsonSerializer.Serialize(arguments) + "}]}"
                : "{\"usage\":{\"prompt_tokens\":8,\"completion_tokens\":4},\"choices\":[{\"index\":0,\"finish_reason\":\"tool_calls\",\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"lookup\",\"arguments\":" + JsonSerializer.Serialize(arguments) + "}}]}}]}";
            string body = !streaming ? output : responses
                ? "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call_1\",\"name\":\"lookup\",\"arguments\":\"\"}}\n\n"
                    + "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"delta\":" + JsonSerializer.Serialize(arguments) + "}\n\n"
                    + "data: {\"type\":\"response.completed\",\"response\":" + output + "}\n\n"
                : "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"lookup\",\"arguments\":\"\"}}]}}]}\n\n"
                    + "data: {\"choices\":[{\"index\":0,\"finish_reason\":\"tool_calls\",\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":" + JsonSerializer.Serialize(arguments) + "}}]}}],\"usage\":{\"prompt_tokens\":8,\"completion_tokens\":4}}\n\ndata: [DONE]\n\n";
            return Response(body, streaming);
        }
    }

    private sealed class FixedHandler(bool responses, bool streaming, string output) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            string body = !streaming ? output : responses ? "data: {\"type\":\"response.completed\",\"response\":" + output + "}\n\n" : "data: " + output + "\n\ndata: [DONE]\n\n";
            return Task.FromResult(Response(body, streaming));
        }
    }

    private static HttpResponseMessage Response(string body, bool streaming) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
}
