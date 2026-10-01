using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Vertex;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class VertexRawSamplingTests
{
    private static readonly string[] Fields =
        ["temperature", "top_p", "top_k", "seed", "presence_penalty", "frequency_penalty", "stop", "stop_sequences",
         "topP", "topK", "presencePenalty", "frequencyPenalty", "stopSequences"];
    private static readonly string[] CountFields = ["top_p", "top_k", "stop_sequences"];

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool claude in new[] { false, true })
            foreach (int operation in new[] { 0, 1, 2, 3 })
                foreach (bool connectionScope in new[] { false, true })
                    foreach (string field in Fields)
                        yield return [claude, operation, connectionScope, field];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RawSamplingIsRejectedBeforeAuthenticationDispatchOrEstimate(
        bool claude, int operation, bool connectionScope, string field)
    {
        using var handler = new Handler(claude);
        using var http = new HttpClient(handler);
        var authentication = new Authentication();
        var client = new VertexLlmClient(http, authentication);
        var options = Options(field);
        var connection = Connection(operation == 0) with
        {
            ProviderOptions = connectionScope ? options : new Dictionary<string, JsonElement>(),
            CapabilityOverrides = new()
            {
                SupportsStreaming = operation == 0, SupportsTemperature = false, SupportsTopP = false,
                SupportsTopK = false, SupportsSeed = false, SupportsPresencePenalty = false,
                SupportsFrequencyPenalty = false, MaxStopSequences = 0
            }
        };
        var request = Request(claude) with
        { ProviderOptions = connectionScope ? new Dictionary<string, JsonElement>() : options };
        if (claude && (field == "temperature" || operation == 2 && !CountFields.Contains(field, StringComparer.Ordinal)))
            await Assert.ThrowsAsync<NotSupportedException>(() => Invoke(client, connection, request, operation));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(client, connection, request, operation));
        Assert.Equal(0, authentication.Calls);
        Assert.Empty(handler.Bodies);
        Assert.Same(options, connectionScope ? connection.ProviderOptions : request.ProviderOptions);
        Assert.Equal(Value(field), Assert.Single(options).Value.GetRawText());
        Assert.Empty(connectionScope ? request.ProviderOptions : connection.ProviderOptions);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public async Task TypedSamplingKeepsTheExistingWrapperPayloadAndEstimate(bool claude, int operation)
    {
        using var handler = new Handler(claude);
        using var http = new HttpClient(handler);
        var authentication = new Authentication();
        var client = new VertexLlmClient(http, authentication);
        await Invoke(client, Connection(operation == 0), Request(claude) with { Temperature = .5, TopP = .8 }, operation);
        Assert.Equal(operation == 3 ? 0 : 1, authentication.Calls);
        if (operation == 3) { Assert.Empty(handler.Bodies); return; }
        JsonElement root = JsonElement.Parse(Assert.Single(handler.Bodies));
        JsonElement payload = claude ? root : root.GetProperty("generationConfig");
        if (claude && operation == 2) Assert.False(payload.TryGetProperty("temperature", out _));
        else
        {
            Assert.Equal(.5, payload.GetProperty("temperature").GetDouble());
            Assert.Equal(.8, payload.GetProperty(claude ? "top_p" : "topP").GetDouble());
        }
    }

    private static string Value(string field) => field switch
    {
        "stop" or "stop_sequences" or "stopSequences" => "[\"stop\"]",
        "seed" or "top_k" or "topK" => "1",
        _ => "0.5"
    };
    private static Dictionary<string, JsonElement> Options(string field) => new(StringComparer.Ordinal)
    { [field] = JsonElement.Parse(Value(field)) };
    private static LlmConnection Connection(bool streaming) => new()
    {
        Version = "1", Transport = LlmTransport.GoogleVertexAi, Endpoint = "https://aiplatform.googleapis.com",
        Vertex = new() { ProjectId = "test-project", Location = "global" },
        CapabilityOverrides = new() { SupportsStreaming = streaming }
    };
    private static LlmChatRequest Request(bool claude) => new()
    {
        ModelId = claude ? "anthropic/claude-sonnet-4-6" : "google/gemini-2.5-flash",
        ReasoningLevel = claude ? LlmReasoningLevel.None : null, Messages = [new("user", "hello")]
    };
    private static async Task Invoke(VertexLlmClient client, LlmConnection connection, LlmChatRequest request, int operation)
    {
        if (operation == 2) { await client.CountInputTokensAsync(connection, request); return; }
        if (operation == 3) { Assert.NotNull(client.EstimateInputTokens(connection, request)); return; }
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
    private sealed class Handler(bool claude) : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            if (request.RequestUri!.AbsolutePath.Contains("count", StringComparison.OrdinalIgnoreCase))
                return new(HttpStatusCode.OK) { Content = new StringContent(claude ? "{\"input_tokens\":37}" : "{\"totalTokens\":37}", Encoding.UTF8, "application/json") };
            bool stream = claude ? JsonElement.Parse(body).GetProperty("stream").GetBoolean()
                : request.RequestUri.AbsolutePath.Contains("streamGenerateContent", StringComparison.Ordinal);
            string response = claude
                ? stream ? "data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\"}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n"
                    : "{\"id\":\"m\",\"type\":\"message\",\"content\":[],\"stop_reason\":\"end_turn\"}"
                : "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}";
            if (stream && !claude) response = "data: " + response + "\n\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, stream ? "text/event-stream" : "application/json") };
        }
    }
}
