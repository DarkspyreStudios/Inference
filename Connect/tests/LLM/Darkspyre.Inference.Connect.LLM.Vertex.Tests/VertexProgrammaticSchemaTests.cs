using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Vertex;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class VertexProgrammaticSchemaTests
{
    private const string Recursive = """{"type":"object","properties":{"next":{"$ref":"#"}}}""";
    private const string Acyclic = """{ "type":"object", "$defs":{"a+b":true}, "properties":{"next":{"$ref":"#/$defs/a+b"}}, "default":{"$ref":"#"} }""";
    private const string Programmatic = """
        {"server_tools":[{"type":"code_execution_20260120","name":"code_execution"}],
         "client_tools":{"run":{"allowed_callers":["code_execution_20260120"]}}}
        """;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ProgrammaticRecursionFailsBeforeAuthenticationAndGenerationOrCountDispatch(int operation)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var authentication = new Authentication();
        var client = new VertexLlmClient(http, authentication);
        await Assert.ThrowsAsync<NotSupportedException>(() => Invoke(client, Connection(operation == 0), Request(Recursive), operation));
        Assert.Equal(0, authentication.Calls);
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MixedDirectRecursiveAndProgrammaticAcyclicSchemasKeepIdenticalWireBytes(int operation)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var authentication = new Authentication();
        var client = new VertexLlmClient(http, authentication);
        var request = Request(Acyclic) with { Tools = [Request(Acyclic).Tools[0], Request(Recursive).Tools[0] with { Name = "direct" }] };
        var connection = Connection(operation == 0);
        await Invoke(client, connection, request with { ProviderOptions = new Dictionary<string, JsonElement>() }, operation);
        await Invoke(client, connection, request, operation);
        Assert.Equal(2, authentication.Calls);
        var baseline = JsonElement.Parse(handler.Bodies[0]).GetProperty("tools");
        var programmatic = JsonElement.Parse(handler.Bodies[1]).GetProperty("tools");
        for (int index = 0; index < 2; index++)
            Assert.Equal(baseline[index].GetProperty("input_schema").GetRawText(), programmatic[index].GetProperty("input_schema").GetRawText());
        Assert.Equal(Acyclic, request.Tools[0].InputSchema.GetRawText());
        Assert.Equal(Recursive, request.Tools[1].InputSchema.GetRawText());
        Assert.Equal(Programmatic, request.ProviderOptions[AnthropicTools.OptionName].GetRawText());
    }

    private static LlmConnection Connection(bool streaming) => new()
    {
        Version = "1",
        Transport = LlmTransport.GoogleVertexAi,
        Endpoint = "https://aiplatform.googleapis.com",
        Vertex = new() { ProjectId = "test-project", Location = "global" },
        CapabilityOverrides = new() { SupportsStreaming = streaming }
    };

    private static LlmChatRequest Request(string schema) => new()
    {
        ModelId = "anthropic/claude-sonnet-4-6",
        ReasoningLevel = LlmReasoningLevel.None,
        Messages = [new("user", "hello")],
        Tools = [new() { Name = "run", InputSchema = JsonElement.Parse(schema) }],
        ProviderOptions = new Dictionary<string, JsonElement>
        { [AnthropicTools.OptionName] = JsonElement.Parse(Programmatic) }
    };

    private static async Task Invoke(VertexLlmClient client, LlmConnection connection, LlmChatRequest request, int operation)
    {
        if (operation == 2) { await client.CountInputTokensAsync(connection, request); return; }
        await foreach (LlmStreamEvent unused in client.StreamAsync(connection, request)) { }
    }

    private sealed class Authentication : IVertexAuthentication
    {
        internal int Calls { get; private set; }
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new VertexAccess("fixture-token", "fixture-principal"));
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            if (request.RequestUri!.AbsolutePath.Contains("count-tokens", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"input_tokens":37}""", Encoding.UTF8, "application/json") };
            bool streaming = JsonElement.Parse(body).GetProperty("stream").GetBoolean();
            string response = streaming
                ? "data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\"}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n"
                : """{"id":"m","type":"message","content":[],"stop_reason":"end_turn"}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }
}
