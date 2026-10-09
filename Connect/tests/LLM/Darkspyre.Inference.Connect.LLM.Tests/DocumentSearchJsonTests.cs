using Darkspyre.Inference.Common;
using System.Net;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class DocumentSearchJsonTests
{
    private const string SearchResponse = "{\"matches\":[{\"file_id\":\"file-1\",\"chunk_id\":\"chunk-1\","
        + "\"chunk_content\":\"reference text\",\"score\":0.5,\"collection_ids\":[\"collection-b\",\"collection-a\"]}]}";

    [Theory]
    [InlineData(XaiServiceTier.Default)]
    [InlineData(XaiServiceTier.Priority)]
    public async Task OrderedCollectionAndGroupingKeysReachTheSearchProtocol(XaiServiceTier tier)
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Xai = new() { ServiceTier = tier },
            Endpoint = "https://api.x.ai/v1",
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize()
        };
        var request = new DocumentSearchRequest("reference", ["collection-b", "collection-a"],
            GroupBy: new(["author", "topic"], new DocumentMinK(2, ["score"])));
        DocumentSearchRequest restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(request, ConnectLlmJson.DocumentSearchRequest), ConnectLlmJson.DocumentSearchRequest)!;

        DocumentSearchResult result = await XaiDocumentSearch.SearchAsync(http, connection, restored, () => { }, CancellationToken.None);

        JsonElement sent = handler.Body;
        Assert.False(sent.TryGetProperty("service_tier", out _));
        Assert.Equal(tier, connection.Xai.ServiceTier);
        Assert.Equal(["collection-b", "collection-a"], sent.GetProperty("source").GetProperty("collection_ids")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(["author", "topic"], sent.GetProperty("group_by").GetProperty("keys")
            .EnumerateArray().Select(value => value.GetString()));
        JsonElement aggregate = sent.GetProperty("group_by").GetProperty("aggregate").GetProperty("min_k");
        Assert.Equal(2, aggregate.GetProperty("k").GetInt32());
        Assert.Equal("score", Assert.Single(aggregate.GetProperty("keys").EnumerateArray()).GetString());
        Assert.Equal(new Uri("https://api.x.ai/v1/documents/search"), handler.Endpoint);

        DocumentSearchResult portable = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(result, ConnectLlmJson.DocumentSearchResult), ConnectLlmJson.DocumentSearchResult)!;
        DocumentSearchMatch match = Assert.Single(portable.Matches);
        Assert.Equal("file-1", match.FileId);
        Assert.Equal("chunk-1", match.ChunkId);
        Assert.Equal("reference text", match.Content);
        Assert.Equal(0.5, match.Score);
        Assert.Equal(["collection-b", "collection-a"], match.CollectionIds);
        Assert.Null(match.Fields);
        Assert.Null(match.PageNumber);
    }

    [Fact]
    public async Task HostedToolIncludesReachTheNonstreamingChatProtocol()
    {
        var handler = new Handler("{\"id\":\"response-1\",\"model\":\"reported-model\",\"status\":\"completed\",\"output\":["
            + "{\"id\":\"message-1\",\"type\":\"message\",\"role\":\"assistant\","
            + "\"content\":[{\"type\":\"output_text\",\"text\":\"done\",\"annotations\":[]}]}]}");
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Xai = new(),
            Endpoint = "https://api.x.ai/v1",
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize(),
            Catalog = [],
            CapabilityOverrides = new() { SupportsStreaming = InferenceSupport.Unsupported }
        };
        var request = new LlmChatRequest
        {
            ModelId = "requested-model",
            Messages = [new("user", "reference")],
            HostedTools = [new LlmDocumentSearchTool(["collection-a"]), new LlmWebSearchTool(), new LlmCodeExecutionTool()],
            MaxHostedTurns = 2
        };
        LlmChatRequest restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(request, ConnectLlmJson.ChatRequest), ConnectLlmJson.ChatRequest)!;

        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, restored)) events.Add(item);

        string?[] includes = [.. handler.Body.GetProperty("include").EnumerateArray().Select(value => value.GetString())];
        Assert.Contains("file_search_call.results", includes);
        Assert.Contains("web_search_call.action.sources", includes);
        Assert.Contains("code_interpreter_call.outputs", includes);
        Assert.False(handler.Body.GetProperty("stream").GetBoolean());
        Assert.Equal(new Uri("https://api.x.ai/v1/responses"), handler.Endpoint);
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("done", result.Text);
        Assert.Equal("reported-model", result.ModelId);
    }

    private sealed class Handler(string response = SearchResponse) : HttpMessageHandler
    {
        public JsonElement Body { get; private set; }
        public Uri? Endpoint { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Endpoint = request.RequestUri;
            Body = JsonElement.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(response)
            };
        }
    }
}
