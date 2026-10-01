using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AnthropicRawTemperatureTests
{
    public static IEnumerable<object[]> RawCases()
    {
        foreach (bool connectionScope in new[] { false, true })
            foreach (int operation in new[] { 0, 1, 2 })
                foreach (string scenario in new[] { "thinking", "range", "capability" })
                    yield return [connectionScope, operation, scenario];
    }

    [Theory]
    [MemberData(nameof(RawCases))]
    public async Task RawTemperatureCannotBypassTypedValidation(bool connectionScope, int operation, string scenario)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var options = new Dictionary<string, JsonElement>
        { ["temperature"] = JsonElement.Parse(scenario == "range" ? "2" : "0.5") };
        var connection = Connection(operation == 0) with
        {
            ProviderOptions = connectionScope ? options : new Dictionary<string, JsonElement>(),
            CapabilityOverrides = new()
            { SupportsStreaming = operation == 0, SupportsTemperature = scenario == "capability" ? false : null }
        };
        var request = Request with
        {
            ReasoningLevel = scenario == "thinking" ? LlmReasoningLevel.High : LlmReasoningLevel.None,
            ProviderOptions = connectionScope ? new Dictionary<string, JsonElement>() : options
        };

        await Assert.ThrowsAsync<NotSupportedException>(() => Invoke(client, connection, request, operation));

        Assert.Empty(handler.Bodies);
        Assert.Null(request.Temperature);
        Assert.Same(options, connectionScope ? connection.ProviderOptions : request.ProviderOptions);
        Assert.Equal(scenario == "range" ? "2" : "0.5", Assert.Single(options).Value.GetRawText());
        Assert.Empty(connectionScope ? request.ProviderOptions : connection.ProviderOptions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TypedTemperatureUsesCanonicalGenerationAndCountPayloads(int operation)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var request = Request with { Temperature = 0.5 };
        await Invoke(new AnthropicMessagesLlmClient(http), Connection(operation == 0), request, operation);
        JsonElement payload = JsonElement.Parse(Assert.Single(handler.Bodies));
        if (operation == 2) Assert.False(payload.TryGetProperty("temperature", out _));
        else Assert.Equal(0.5, payload.GetProperty("temperature").GetDouble());
        Assert.Equal(0.5, request.Temperature);
        Assert.Empty(request.ProviderOptions);
    }

    private static LlmConnection Connection(bool streaming) => new()
    {
        Version = "1",
        Transport = LlmTransport.AnthropicMessages,
        Endpoint = "https://api.anthropic.invalid",
        Credential = new LlmCredential { ApiKey = "fixture-key" }.Serialize(),
        CapabilityOverrides = new() { SupportsStreaming = streaming }
    };

    private static LlmChatRequest Request => new()
    {
        ModelId = "claude-sonnet-4-6",
        ReasoningLevel = LlmReasoningLevel.None,
        Messages = [new("user", "hello")]
    };

    private static async Task Invoke(AnthropicMessagesLlmClient client, LlmConnection connection, LlmChatRequest request, int operation)
    {
        if (operation == 2)
        {
            Assert.Equal(37, (await client.CountInputTokensAsync(connection, request)).InputTokens);
            return;
        }
        await foreach (LlmStreamEvent unused in client.StreamAsync(connection, request)) { }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            if (request.RequestUri!.AbsolutePath.Contains("count_tokens", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"input_tokens":37}""", Encoding.UTF8, "application/json") };
            bool streaming = JsonElement.Parse(body).GetProperty("stream").GetBoolean();
            string response = streaming
                ? "data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\"}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n"
                : """{"id":"m","type":"message","content":[],"stop_reason":"end_turn"}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }
}
