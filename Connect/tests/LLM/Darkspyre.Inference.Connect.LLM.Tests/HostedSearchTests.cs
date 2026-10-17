using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>Hosted search on the shared Responses client: the OpenAI wire shapes, the shared observer and the refusal of
/// a hosted tool the route does not offer (DSA-1070 B13, B14, S4).</summary>
public sealed class HostedSearchTests
{
    /// <summary>The OpenAI API payload writes web search, code execution, document search and MCP in OpenAI's shape,
    /// with the include entry of each tool that returns sources, results or outputs.</summary>
    [Fact]
    public async Task OpenAiPayloadWritesEachHostedToolInOpenAiShape()
    {
        using var handler = new DirectCodexTests.Handler(_ => DirectCodexTests.Sse(DirectCodexTests.Completed));
        var client = new OpenAiResponsesLlmClient(new HttpClient(handler));
        LlmConnection connection = DirectCodexTests.OpenAiConnection(OpenAiResponsesLlmClient.DefaultEndpoint);

        await foreach (LlmStreamEvent _ in client.StreamAsync(connection, Request(
            new LlmWebSearchTool(AllowedDomains: ["example.com"]),
            new LlmCodeExecutionTool(),
            new LlmDocumentSearchTool(["vs_1"], MaxResults: 3),
            new LlmRemoteMcpTool("https://mcp.example.test", "tools", AllowedTools: ["lookup"],
                Headers: new Dictionary<string, string> { ["X-Key"] = "value" })))) { }

        JsonNode sent = JsonNode.Parse(handler.Body!)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            [
              {"type":"web_search","filters":{"allowed_domains":["example.com"]}},
              {"type":"code_interpreter","container":{"type":"auto"}},
              {"type":"file_search","vector_store_ids":["vs_1"],"max_num_results":3},
              {"type":"mcp","server_label":"tools","server_url":"https://mcp.example.test","allowed_tools":["lookup"],
               "headers":{"X-Key":"value"},"require_approval":"never"}
            ]
            """), sent["tools"]), sent["tools"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            ["reasoning.encrypted_content","web_search_call.action.sources","file_search_call.results","code_interpreter_call.outputs"]
            """), sent["include"]), sent["include"]!.ToJsonString());
        Assert.False(sent.AsObject().ContainsKey("max_turns"));
    }

    /// <summary>Asking the OpenAI API for X search is refused with the feature's name; web search is offered there.</summary>
    [Fact]
    public async Task OpenAiRefusesXSearchByName()
    {
        using var handler = new DirectCodexTests.Handler(_ => DirectCodexTests.Sse(DirectCodexTests.Completed));
        var client = new OpenAiResponsesLlmClient(new HttpClient(handler));
        LlmConnection connection = DirectCodexTests.OpenAiConnection(OpenAiResponsesLlmClient.DefaultEndpoint);

        NotSupportedException refusal = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (LlmStreamEvent _ in client.StreamAsync(connection, Request(new LlmSocialSearchTool()))) { }
        });

        Assert.Contains("X search", refusal.Message, StringComparison.Ordinal);
        Assert.Null(handler.Body);
        Assert.Equal(
            [LlmHostedToolKind.WebSearch, LlmHostedToolKind.CodeExecution, LlmHostedToolKind.ImageGeneration,
                LlmHostedToolKind.DocumentSearch, LlmHostedToolKind.RemoteMcp],
            client.GetCapabilities(connection, "gpt-5.5").HostedTools.Order());
    }

    /// <summary>The shared observer reports a started and a completed event for each hosted item type, with its output,
    /// and reports each citation once across the streamed item, the terminal output and the top-level list.</summary>
    [Fact]
    public async Task SharedObserverMapsEachHostedItemAndDeduplicatesCitations()
    {
        const string message = """{"type":"message","id":"m","role":"assistant","content":[{"type":"output_text","text":"See.","annotations":[{"type":"url_citation","url":"https://a.test","title":"A"},{"type":"url_citation","url":"https://b.test"}]}]}""";
        string[] items =
        [
            """{"type":"web_search_call","id":"ws","status":"completed","action":{"type":"search","query":"q","sources":[{"type":"url","url":"https://a.test"}]}}""",
            """{"type":"x_search_call","id":"xs","status":"completed"}""",
            """{"type":"custom_tool_call","id":"ct","call_id":"xs_2","name":"x_keyword_search","input":"grok","status":"completed"}""",
            """{"type":"file_search_call","id":"fs","status":"completed","queries":["q"],"results":[{"file_id":"f1","filename":"a.txt","score":0.5,"text":"hit"}]}""",
            """{"type":"code_interpreter_call","id":"ci","status":"completed","code":"print(1)","outputs":[{"type":"logs","logs":"1"}]}""",
            """{"type":"mcp_call","id":"mc","name":"lookup","server_label":"tools","arguments":"{}","output":"done","status":"completed"}"""
        ];
        static JsonElement Item(string type, int index, string item) => JsonElement.Parse(
            "{\"type\":\"response.output_item." + type + "\",\"output_index\":" + index.ToString(CultureInfo.InvariantCulture)
            + ",\"item\":" + item + "}");
        var observer = new ResponsesHostedToolsObserver(() => new InvalidOperationException("malformed"));

        var events = new List<LlmStreamEvent>();
        for (int index = 0; index < items.Length; index++)
        {
            events.AddRange(observer.Observe(Item("added", index, items[index])));
            events.AddRange(observer.Observe(Item("done", index, items[index])));
        }
        events.AddRange(observer.Observe(Item("done", items.Length, message)));
        events.AddRange(await observer.CompleteAsync(JsonElement.Parse(
            """{"type":"response.completed","response":{"id":"r","status":"completed","output":["""
            + string.Join(',', [.. items, message]) + """],"citations":["https://a.test","https://c.test"]}}"""),
            CancellationToken.None));
        LlmChatResult result = observer.Decorate(new LlmChatResult { Diagnostics = new(200, null, null), Text = "See.", Status = LlmCompletionStatus.Completed, Usage = new(1, 1, 2) });

        Assert.Equal(["ws", "xs", "xs_2", "fs", "ci", "mc"], events.OfType<LlmHostedToolCallStartedEvent>().Select(started => started.CallId));
        LlmHostedToolCall[] calls = [.. events.OfType<LlmHostedToolCallCompletedEvent>().Select(completed => completed.Call)];
        Assert.Equal(
            [(LlmHostedToolKind.WebSearch, "web_search"), (LlmHostedToolKind.SocialSearch, "x_search"),
                (LlmHostedToolKind.SocialSearch, "x_keyword_search"), (LlmHostedToolKind.DocumentSearch, "file_search"),
                (LlmHostedToolKind.CodeExecution, "code_interpreter"), (LlmHostedToolKind.RemoteMcp, "lookup")],
            calls.Select(call => (call.Kind, call.Name)));
        Assert.Equal("https://a.test", Assert.Single(Assert.IsType<LlmHostedSearchOutput>(calls[0].Output).Sources!).Url);
        Assert.Equal("grok", calls[2].Input!.Value.GetString());
        Assert.Equal("f1", Assert.Single(Assert.IsType<LlmHostedDocumentSearchOutput>(calls[3].Output).Results!).FileId);
        Assert.Equal(["1"], Assert.IsType<LlmHostedCodeOutput>(calls[4].Output).Logs!);
        Assert.Equal("done", Assert.IsType<LlmHostedMcpOutput>(calls[5].Output).Output!.Value.GetString());
        string[] cited = ["https://a.test", "https://b.test", "https://c.test"];
        Assert.Equal(cited, events.OfType<LlmCitationEvent>().Select(citation => citation.Citation.Url));
        Assert.Equal(cited, result.Citations.Select(citation => citation.Url));
        Assert.All(calls, call => HostedTools.Snapshot(call));
    }

    private static LlmChatRequest Request(params LlmHostedTool[] tools) =>
        new() { ModelId = "gpt-5.5", Messages = [new("user", "hello")], HostedTools = tools };
}
