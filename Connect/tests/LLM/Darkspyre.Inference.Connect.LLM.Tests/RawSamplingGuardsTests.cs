using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class RawSamplingGuardsTests
{
    private static readonly string[] Fields =
        ["temperature", "top_p", "top_k", "seed", "presence_penalty", "frequency_penalty", "stop", "stop_sequences",
         "topP", "topK", "presencePenalty", "frequencyPenalty", "stopSequences"];

    public static IEnumerable<object[]> Cases()
    {
        foreach (string route in new[] { "responses", "compatible", "anthropic", "gemini", "xai" })
            foreach (int operation in Operations(route))
                foreach (bool connectionScope in new[] { false, true })
                    foreach (string field in Fields)
                        yield return [route, operation, connectionScope, field];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RawSamplingIsRejectedBeforeGenerationCountingOrEstimation(
        string route, int operation, bool connectionScope, string field)
    {
        using var handler = new Handler(route);
        using var http = new HttpClient(handler);
        ILlmClient client = Client(http, route);
        var options = Options(field, Value(field));
        LlmConnection connection = Connection(route, operation == 0) with
        {
            ProviderOptions = connectionScope ? options : new Dictionary<string, JsonElement>(),
            CapabilityOverrides = new()
            {
                SupportsStreaming = operation == 0,
                SupportsTemperature = false,
                SupportsTopP = false,
                SupportsTopK = false,
                SupportsSeed = false,
                SupportsPresencePenalty = false,
                SupportsFrequencyPenalty = false,
                MaxStopSequences = 0
            }
        };
        LlmChatRequest request = Request(route) with
        { ProviderOptions = connectionScope ? new Dictionary<string, JsonElement>() : options };
        if (route == "anthropic" && (field == "temperature" || operation == 2 && !CountFields.Contains(field, StringComparer.Ordinal)))
            await Assert.ThrowsAsync<NotSupportedException>(() => Invoke(client, connection, request, operation));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(client, connection, request, operation));

        Assert.Empty(handler.Bodies);
        Assert.Same(options, connectionScope ? connection.ProviderOptions : request.ProviderOptions);
        Assert.Equal(Value(field), Assert.Single(options).Value.GetRawText());
        Assert.Empty(connectionScope ? request.ProviderOptions : connection.ProviderOptions);
        Assert.Null(request.Temperature);
        Assert.Null(request.TopP);
        Assert.Null(request.TopK);
        Assert.Null(request.Seed);
        Assert.Null(request.PresencePenalty);
        Assert.Null(request.FrequencyPenalty);
        Assert.Empty(request.StopSequences);
    }

    [Theory]
    [InlineData("temperature", "\"NaN\"")]
    [InlineData("temperature", "99")]
    [InlineData("topP", "2")]
    [InlineData("topK", "0")]
    [InlineData("seed", "-1")]
    [InlineData("presencePenalty", "\"Infinity\"")]
    [InlineData("frequencyPenalty", "99")]
    [InlineData("stopSequences", "[\"\"]")]
    public async Task InvalidRawValuesCannotEvadeTypedFiniteOrRangeValidation(string field, string json)
    {
        using var handler = new Handler("gemini");
        using var http = new HttpClient(handler);
        var options = Options(field, json);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(new GeminiApiLlmClient(http),
            Connection("gemini", false), Request("gemini") with { ProviderOptions = options }, 1));
        Assert.Empty(handler.Bodies);
        Assert.Equal(json, options[field].GetRawText());
    }

    [Theory]
    [InlineData("responses", true)]
    [InlineData("responses", false)]
    [InlineData("compatible", true)]
    [InlineData("compatible", false)]
    [InlineData("anthropic", true)]
    [InlineData("anthropic", false)]
    [InlineData("gemini", true)]
    [InlineData("gemini", false)]
    [InlineData("xai", true)]
    [InlineData("xai", false)]
    public async Task TypedControlsAndNonstandardRequestPrecedenceRemainUnchanged(string route, bool streaming)
    {
        using var handler = new Handler(route);
        using var http = new HttpClient(handler);
        LlmChatRequest request = Request(route) with
        { Temperature = .5, TopP = .8, ProviderOptions = Options("vendor_extension", "{\"request\":true}") };
        LlmConnection connection = Connection(route, streaming) with
        { ProviderOptions = Options("vendor_extension", "{\"connection\":true}") };
        await Invoke(Client(http, route), connection, request, streaming ? 0 : 1);
        JsonElement root = JsonElement.Parse(Assert.Single(handler.Bodies));
        JsonElement payload = route == "gemini" ? root.GetProperty("generationConfig") : root;
        Assert.Equal(.5, payload.GetProperty("temperature").GetDouble());
        Assert.Equal(.8, payload.GetProperty(route == "gemini" ? "topP" : "top_p").GetDouble());
        Assert.True(payload.GetProperty("vendor_extension").GetProperty("request").GetBoolean());
        Assert.Equal("{\"connection\":true}", connection.ProviderOptions["vendor_extension"].GetRawText());
        Assert.Equal("{\"request\":true}", request.ProviderOptions["vendor_extension"].GetRawText());
    }

    [Theory]
    [InlineData("TopP")]
    [InlineData("TopK")]
    [InlineData("top-p")]
    [InlineData("presence-penalty")]
    public void NonWireNamesRemainOpaqueExtensions(string field)
    {
        var payload = new JsonObject();
        var connection = Connection("compatible", false) with { ProviderOptions = Options(field, "1") };
        var request = Request("compatible") with { ProviderOptions = Options(field, "2") };
        RequestJson.ApplyProviderOptions(payload, connection, request);
        Assert.Equal(2, payload[field]!.GetValue<int>());
        Assert.Equal("1", connection.ProviderOptions[field].GetRawText());
        Assert.Equal("2", request.ProviderOptions[field].GetRawText());
    }

    private static readonly string[] CountFields = ["top_p", "top_k", "stop_sequences"];
    private static IEnumerable<int> Operations(string route) => route switch
    {
        "responses" or "xai" => [0, 1, 3],
        "compatible" => [0, 1],
        "anthropic" => [0, 1, 2],
        _ => [0, 1, 2, 3]
    };
    private static string Value(string field) => field switch
    {
        "stop" or "stop_sequences" or "stopSequences" => "[\"stop\"]",
        "seed" or "top_k" or "topK" => "1",
        _ => "0.5"
    };
    private static Dictionary<string, JsonElement> Options(string field, string json) => new(StringComparer.Ordinal)
    { [field] = JsonElement.Parse(json) };
    internal static LlmChatRequest Request(string route) => new()
    {
        ModelId = route switch { "anthropic" => "claude-sonnet-4-6", "gemini" => "gemini-2.5-flash", "xai" => "grok-4.7", _ => "gpt-4o" },
        ReasoningLevel = route == "anthropic" ? LlmReasoningLevel.None : null,
        Messages = [new("user", "hello")]
    };
    internal static LlmConnection Connection(string route, bool streaming) => new()
    {
        Version = "1",
        Transport = route switch
        {
            "responses" => LlmTransport.OpenAiResponses,
            "compatible" => LlmTransport.OpenAiCompatibleChat,
            "anthropic" => LlmTransport.AnthropicMessages,
            "gemini" => LlmTransport.GoogleGeminiApi,
            _ => LlmTransport.XaiResponses
        },
        Endpoint = route switch { "gemini" => GeminiApiLlmClient.DefaultEndpoint, "xai" => new XaiConnectionOptions().GetEndpoint(), _ => "https://provider.invalid/v1" },
        Credential = route == "xai" ? new LlmCredential { OAuth = new() { AccessToken = "fixture-token" } }.Serialize()
            : new LlmCredential { ApiKey = "fixture-key" }.Serialize(),
        CapabilityOverrides = new() { SupportsStreaming = streaming }
    };
    internal static ILlmClient Client(HttpClient http, string route) => route switch
    {
        "responses" => new OpenAiResponsesLlmClient(http),
        "compatible" => new OpenAiCompatibleChatLlmClient(http),
        "anthropic" => new AnthropicMessagesLlmClient(http),
        "gemini" => new GeminiApiLlmClient(http),
        _ => new XaiResponsesLlmClient(http)
    };
    private static async Task Invoke(ILlmClient client, LlmConnection connection, LlmChatRequest request, int operation)
    {
        if (operation == 2) { await ((ILlmProviderTokenCounter)client).CountInputTokensAsync(connection, request); return; }
        if (operation == 3) { ((ILlmContextEstimator)client).EstimateInputTokens(connection, request); return; }
        await foreach (LlmStreamEvent unused in client.StreamAsync(connection, request)) { }
    }

    internal sealed class Handler(string route) : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        internal int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"gpt-4o\"}],\"models\":[{\"name\":\"models/gemini-2.5-flash\",\"supportedGenerationMethods\":[\"generateContent\",\"countTokens\"]}]}", Encoding.UTF8, "application/json") };
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            if (request.RequestUri!.AbsolutePath.Contains("count", StringComparison.OrdinalIgnoreCase))
                return new(HttpStatusCode.OK) { Content = new StringContent(route == "anthropic" ? "{\"input_tokens\":37}" : "{\"totalTokens\":37}", Encoding.UTF8, "application/json") };
            JsonElement payload = JsonElement.Parse(body);
            bool stream = route == "gemini" ? request.RequestUri.AbsolutePath.EndsWith(":streamGenerateContent", StringComparison.Ordinal)
                : payload.GetProperty("stream").GetBoolean();
            string response = route switch
            {
                "responses" or "xai" => "{\"id\":\"r\",\"status\":\"completed\",\"output\":[]}",
                "compatible" => "{\"choices\":[{\"message\":{\"content\":\"ok\"},\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}",
                "anthropic" => "{\"id\":\"m\",\"type\":\"message\",\"content\":[],\"stop_reason\":\"end_turn\"}",
                _ => "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}"
            };
            if (stream) response = route switch
            {
                "responses" or "xai" => "data: {\"type\":\"response.completed\",\"response\":" + response + "}\n\n",
                "anthropic" => "data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\"}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n",
                "compatible" => "data: " + response + "\n\ndata: [DONE]\n\n",
                _ => "data: " + response + "\n\n"
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, stream ? "text/event-stream" : "application/json") };
        }
    }
}
