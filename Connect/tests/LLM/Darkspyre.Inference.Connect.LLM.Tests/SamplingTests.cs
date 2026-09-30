using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class SamplingTests
{
    private static readonly LlmProviderCapabilities Supported = new()
    {
        SupportsTopP = true,
        SupportsTopK = true,
        SupportsSeed = true,
        SupportsPresencePenalty = true,
        SupportsFrequencyPenalty = true,
        MaxStopSequences = 5
    };

    private static LlmChatRequest Request => new() { ModelId = "model", Messages = [new("user", "hello")] };

    public static TheoryData<LlmChatRequest> ExplicitControls => new()
    {
        Request with { TopP = .8 }, Request with { TopK = 20 }, Request with { Seed = 0 },
        Request with { PresencePenalty = 0 }, Request with { FrequencyPenalty = 0 }, Request with { StopSequences = ["stop"] }
    };

    [Theory]
    [MemberData(nameof(ExplicitControls))]
    public async Task UnsupportedCliAndCodexControlsFailBeforeProcessOrHttpDispatch(LlmChatRequest request)
    {
        using var http = new HttpClient(new Capture("", false));
        var runner = new RejectingCliRunner();
        var claude = new ClaudeCliLlmClient(http, runner);
        var antigravity = new AntigravityCliLlmClient(runner);
        var codex = new OpenAiCodexLlmClient(http);
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(claude.StreamAsync(
            new() { Version = "1", Transport = LlmTransport.AnthropicClaudeCli, Endpoint = "claude" }, request)));
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(antigravity.StreamAsync(
            new() { Version = "1", Transport = LlmTransport.GoogleAntigravityCli, Endpoint = "agy" }, request)));
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(codex.StreamAsync(
            new() { Version = "1", Transport = LlmTransport.OpenAiCodex, Endpoint = OpenAiCodexLlmClient.DefaultEndpoint }, request)));
    }

    [Theory]
    [MemberData(nameof(ExplicitControls))]
    public void UnsupportedExplicitControlsAreNeverDefaults(LlmChatRequest request)
    {
        Assert.Throws<NotSupportedException>(() => LlmClientBase.ValidateSampling(request, new()));
        LlmClientBase.ValidateSampling(request, Supported);
    }

    public static TheoryData<LlmChatRequest> InvalidControls => new()
    {
        Request with { TopP = 0 }, Request with { TopP = -1 }, Request with { TopP = 1.01 },
        Request with { TopP = double.NaN }, Request with { TopP = double.PositiveInfinity },
        Request with { TopK = 0 }, Request with { TopK = -1 }, Request with { Seed = -1 },
        Request with { PresencePenalty = double.NaN }, Request with { PresencePenalty = double.NegativeInfinity },
        Request with { FrequencyPenalty = double.NaN }, Request with { FrequencyPenalty = double.PositiveInfinity }
    };

    [Theory]
    [MemberData(nameof(InvalidControls))]
    public void InvalidValuesFailBeforeDispatch(LlmChatRequest request) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LlmClientBase.ValidateSampling(request, Supported));

    [Fact]
    public void StopValidationDoesNotMutateTheCaller()
    {
        Assert.Throws<ArgumentException>(() => LlmClientBase.ValidateSampling(Request with { StopSequences = [""] }, Supported));
        Assert.Throws<ArgumentException>(() => LlmClientBase.ValidateSampling(Request with { StopSequences = [null!] }, Supported));
        Assert.Throws<ArgumentNullException>(() => LlmClientBase.ValidateSampling(Request with { StopSequences = null! }, Supported));
        Assert.Throws<NotSupportedException>(() => LlmClientBase.ValidateSampling(Request with { StopSequences = ["1", "2", "3", "4", "5", "6"] }, Supported));
        Assert.Throws<ArgumentOutOfRangeException>(() => Supported.Apply(new LlmCapabilityOverrides { MaxStopSequences = -1 }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompatibleChatMapsEverySamplingControl(bool stream)
    {
        var capture = new Capture(stream ? "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
            : "{\"choices\":[{\"message\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}]}", stream);
        using var http = new HttpClient(capture);
        var client = new OpenAiCompatibleChatLlmClient(http);
        LlmConnection connection = Connection(LlmTransport.OpenAiCompatibleChat) with
        { CapabilityOverrides = new() { SupportsTopK = true, SupportsStreaming = stream } };
        await Drain(client.StreamAsync(connection, Request with
        { TopP = .8, TopK = 17, Seed = 4, PresencePenalty = -.5, FrequencyPenalty = .7, StopSequences = ["first", "second"] }));
        JsonElement body = JsonElement.Parse(Assert.Single(capture.Bodies));
        Assert.Equal(.8, body.GetProperty("top_p").GetDouble());
        Assert.Equal(17, body.GetProperty("top_k").GetInt32());
        Assert.Equal(4, body.GetProperty("seed").GetInt32());
        Assert.Equal(-.5, body.GetProperty("presence_penalty").GetDouble());
        Assert.Equal(.7, body.GetProperty("frequency_penalty").GetDouble());
        Assert.Equal(["first", "second"], body.GetProperty("stop").EnumerateArray().Select(value => value.GetString()));
    }

    [Theory]
    [InlineData("top_p")]
    [InlineData("top_k")]
    [InlineData("seed")]
    [InlineData("presence_penalty")]
    [InlineData("frequency_penalty")]
    [InlineData("stop")]
    public async Task ProviderOptionsCannotBypassTypedSamplingValidation(string field)
    {
        var capture = new Capture("", false);
        using var http = new HttpClient(capture);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Drain(new OpenAiCompatibleChatLlmClient(http).StreamAsync(
            Connection(LlmTransport.OpenAiCompatibleChat), Request with
            { ProviderOptions = new Dictionary<string, JsonElement> { [field] = JsonSerializer.SerializeToElement(1) } })));
        Assert.Empty(capture.Bodies);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResponsesMapsTopPWithReasoningDisabled(bool stream)
    {
        const string complete = "{\"id\":\"r\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"hi\"}]}]}";
        var capture = new Capture(stream ? "data: {\"type\":\"response.completed\",\"response\":" + complete + "}\n\n" : complete, stream);
        using var http = new HttpClient(capture);
        var client = new OpenAiResponsesLlmClient(http);
        LlmConnection connection = Connection(LlmTransport.OpenAiResponses) with { CapabilityOverrides = new() { SupportsStreaming = stream } };
        var request = Request with { ModelId = "gpt-5.6", TopP = .8 };
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(client.StreamAsync(connection, request)));
        Assert.Empty(capture.Bodies);
        await Drain(client.StreamAsync(connection, request with { ReasoningLevel = LlmReasoningLevel.None }));
        Assert.Equal(.8, JsonElement.Parse(Assert.Single(capture.Bodies)).GetProperty("top_p").GetDouble());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnthropicMapsStopsWithoutAnInventedLimitAndSamplingWithoutThinking(bool stream)
    {
        const string reply = "{\"id\":\"m\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";
        const string events = "data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"usage\":{\"input_tokens\":1}}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\ndata: {\"type\":\"message_stop\"}\n\n";
        var capture = new Capture(stream ? events : reply, stream);
        using var http = new HttpClient(capture);
        var client = new AnthropicMessagesLlmClient(http);
        var connection = Connection(LlmTransport.AnthropicMessages) with { CapabilityOverrides = new() { SupportsStreaming = stream } };
        await Drain(client.StreamAsync(connection, Request with
        { ModelId = "claude-sonnet-4-6", ReasoningLevel = LlmReasoningLevel.None, TopP = .8, TopK = 17, StopSequences = Enumerable.Range(0, 101).Select(index => "stop" + index).ToArray() }));
        JsonElement body = JsonElement.Parse(Assert.Single(capture.Bodies));
        Assert.Equal(.8, body.GetProperty("top_p").GetDouble());
        Assert.Equal(17, body.GetProperty("top_k").GetInt32());
        Assert.Equal(101, body.GetProperty("stop_sequences").GetArrayLength());
        Assert.Equal(int.MaxValue, client.GetCapabilities(connection, "claude-sonnet-4-6").MaxStopSequences);
    }

    [Fact]
    public void ClaudeThinkingRestrictsTopKAndTopP()
    {
        var profile = AnthropicModelProfiles.Resolve("claude-sonnet-4-6", null);
        Assert.Throws<NotSupportedException>(() => AnthropicModelProfiles.ValidateSampling(Request with { ReasoningLevel = LlmReasoningLevel.High, TopP = .94 }, profile));
        Assert.Throws<NotSupportedException>(() => AnthropicModelProfiles.ValidateSampling(Request with { ReasoningLevel = LlmReasoningLevel.High, TopK = 10 }, profile));
        AnthropicModelProfiles.ValidateSampling(Request with { ReasoningLevel = LlmReasoningLevel.High, TopP = .95 }, profile);
        Assert.Throws<NotSupportedException>(() => AnthropicModelProfiles.ValidateSampling(Request with { TopK = 10 }, AnthropicModelProfiles.Resolve("claude-opus-4-7", null)));
    }

    [Theory]
    [InlineData("gemini-3.6-flash")]
    [InlineData("gemini-3.8-flash-preview")]
    [InlineData("gemini-3.10-flash-preview")]
    [InlineData("gemini-4-flash")]
    public void GeminiIgnoredSamplingCannotBeEnabledByOverrides(string model)
    {
        using var http = new HttpClient();
        var client = new GeminiApiLlmClient(http);
        LlmConnection connection = GeminiConnection;
        var profile = GeminiModelProfiles.Get(model, catalogThinking: false);
        Assert.False(profile.AllowsSampling);
        Assert.Empty(profile.Levels);
        LlmProviderCapabilities capabilities = client.GetCapabilities(connection, model);
        Assert.False(capabilities.SupportsTemperature);
        Assert.False(capabilities.SupportsTopP);
        Assert.False(capabilities.SupportsTopK);
        Assert.False(capabilities.SupportsPresencePenalty);
        Assert.False(capabilities.SupportsFrequencyPenalty);
        Assert.True(capabilities.SupportsSeed);
        Assert.Throws<NotSupportedException>(() => client.GetCapabilities(connection with { CapabilityOverrides = new() { SupportsTopP = true } }, model));
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"topK\":null}", false)]
    [InlineData("{\"topK\":\"40\"}", false)]
    [InlineData("{\"topK\":0}", false)]
    [InlineData("{\"topK\":40}", true)]
    public void GeminiTopKRequiresCatalogEvidence(string catalog, bool expected)
    {
        using var http = new HttpClient();
        var client = new GeminiApiLlmClient(http);
        var connection = GeminiConnection with { Catalog = [new("gemini-3-flash-preview", "Flash") { CatalogEntry = JsonElement.Parse(catalog) }] };
        Assert.Equal(expected, client.GetCapabilities(connection, "gemini-3-flash-preview").SupportsTopK);
        if (!expected)
            Assert.Throws<NotSupportedException>(() => client.GetCapabilities(connection with { CapabilityOverrides = new() { SupportsTopK = true } }, "gemini-3-flash-preview"));
    }

    private static LlmConnection GeminiConnection => Connection(LlmTransport.GoogleGeminiApi) with { Endpoint = GeminiApiLlmClient.DefaultEndpoint };
    private static LlmConnection Connection(LlmTransport transport) => new()
    { Version = "1", Transport = transport, Endpoint = "https://provider.test/v1", Credential = new LlmCredential { ApiKey = "fixture" }.Serialize() };

    private static async Task Drain(IAsyncEnumerable<LlmStreamEvent> stream)
    { await foreach (var item in stream) { _ = item; } }

    private sealed class Capture(string response, bool stream) : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, stream ? "text/event-stream" : "application/json") };
        }
    }

    private sealed class RejectingCliRunner : IClaudeCliRunner, IAntigravityCliRunner
    {
        public Task<ClaudeCliProcessResult> RunAsync(ClaudeCliInvocation invocation, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Sampling must be rejected before process execution.");
        public Task<AntigravityCliProcessResult> RunAsync(AntigravityCliInvocation invocation, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Sampling must be rejected before process execution.");
    }
}
