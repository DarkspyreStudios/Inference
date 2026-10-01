using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AnthropicProgrammaticSchemaTests
{
    private const string Recursive = """{"type":"object","properties":{"next":{"$ref":"#"}}}""";
    private const string Programmatic = """
        {"server_tools":[{"type":"code_execution_20260120","name":"code_execution"}],
         "client_tools":{"run":{"allowed_callers":["code_execution_20260120"]}}}
        """;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RecursiveProgrammaticSchemaFailsBeforeGenerationOrCountDispatch(int operation)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var connection = Connection(operation == 0);
        var request = Request(Recursive);
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => Invoke(client, connection, request, operation));
        Assert.DoesNotContain(Recursive, error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Bodies);
    }

    private static LlmConnection Connection(bool streaming) => new()
    {
        Version = "1",
        Transport = LlmTransport.AnthropicMessages,
        Endpoint = "https://api.anthropic.invalid",
        Credential = new LlmCredential { ApiKey = "fixture-key" }.Serialize(),
        CapabilityOverrides = new() { SupportsStreaming = streaming }
    };

    private static LlmChatRequest Request(string schema) => new()
    {
        ModelId = "claude-sonnet-4-6",
        ReasoningLevel = LlmReasoningLevel.None,
        Messages = [new("user", "hello")],
        Tools = [new() { Name = "run", InputSchema = JsonElement.Parse(schema) }],
        ProviderOptions = new Dictionary<string, JsonElement>
        { [AnthropicTools.OptionName] = JsonElement.Parse(Programmatic) }
    };

    private static async Task Invoke(AnthropicMessagesLlmClient client, LlmConnection connection,
        LlmChatRequest request, int operation)
    {
        if (operation == 2) { await client.CountInputTokensAsync(connection, request); return; }
        await foreach (LlmStreamEvent unused in client.StreamAsync(connection, request)) { }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            if (request.RequestUri!.AbsolutePath.EndsWith("count_tokens", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"input_tokens":37}""", Encoding.UTF8, "application/json") };
            bool streaming = JsonElement.Parse(body).GetProperty("stream").GetBoolean();
            string response = streaming
                ? "data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\"}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n"
                : """{"id":"m","type":"message","content":[],"stop_reason":"end_turn"}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }
}
