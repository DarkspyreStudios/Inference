using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ReasoningTextTests
{
    [Fact]
    public void DisplaySupportIsIndependentOfReasoningControlAndCannotInventAProducer()
    {
        Assert.False(new LlmCapabilities().SupportsReasoningText);
        Assert.False(new LlmProviderCapabilities().SupportsReasoningText);
        var connection = Connection(LlmTransport.OpenAiCompatibleChat, true, false) with
        { ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides> { ["model"] = new() { SupportsReasoningText = true } } };
        var capabilities = LlmCapabilityOverrideLookup.Apply(new() { SupportsReasoningText = true }, connection, "model");
        Assert.True(capabilities.SupportsReasoningText);
        Assert.False(capabilities.SupportsReasoning);
        Assert.Throws<NotSupportedException>(() => LlmCapabilityOverrideLookup.Apply(new(), connection, "model"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ResponsesDisplaySwitchPreservesOpaqueStateAndEffort(bool streaming, bool display)
    {
        const string complete = """{"id":"response-1","status":"completed","output":[{"type":"reasoning","id":"r1","encrypted_content":"opaque","summary":[{"type":"summary_text","text":"Plan."}]},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Answer."}]}]}""";
        string reply = streaming
            ? "data: {\"type\":\"response.reasoning_summary_text.delta\",\"delta\":\"Plan.\"}\n\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"Answer.\"}\n\ndata: {\"type\":\"response.completed\",\"response\":" + complete + "}\n\n"
            : complete;
        var handler = new Handler(reply, streaming);
        using var http = new HttpClient(handler);
        var client = new OpenAiResponsesLlmClient(http);
        var events = await Read(client.StreamAsync(Connection(LlmTransport.OpenAiResponses, streaming, display),
            new() { ModelId = "gpt-5.6", Messages = [new("user", "hi")], ReasoningLevel = LlmReasoningLevel.High }));
        Assert.Equal(display ? 1 : 0, events.OfType<LlmReasoningDeltaEvent>().Count());
        var result = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        Assert.Equal("Answer.", result.Text);
        Assert.Contains("opaque", result.ContinuationState!.Payload, StringComparison.Ordinal);
        Assert.Equal("high", (string?)handler.Body!["reasoning"]!["effort"]);
        Assert.Equal(display ? "auto" : null, (string?)handler.Body["reasoning"]!["summary"]);
        Assert.Equal("reasoning.encrypted_content", (string?)handler.Body["include"]![0]);
    }

    [Theory]
    [InlineData("gpt-5.6", null, true)]
    [InlineData("gpt-5.6", LlmReasoningLevel.None, false)]
    [InlineData("gpt-4.1", null, false)]
    [InlineData("unprofiled-model", null, false)]
    public async Task ResponsesRequestsSummariesOnlyForActiveReasoning(string model, LlmReasoningLevel? level, bool summary)
    {
        var handler = new Handler("""{"status":"completed","output":[]}""", false);
        using var http = new HttpClient(handler);
        await Read(new OpenAiResponsesLlmClient(http).StreamAsync(Connection(LlmTransport.OpenAiResponses, false, true),
            new() { ModelId = model, Messages = [new("user", "hi")], ReasoningLevel = level }));
        Assert.Equal(summary ? "auto" : null, (string?)handler.Body!["reasoning"]?["summary"]);
        Assert.Equal(level is null ? null : "none", (string?)handler.Body["reasoning"]?["effort"]);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task CodexSummarySelectionUsesItsCatalogAndDisplaySwitch(bool display, bool reasons)
    {
        var handler = new Handler("data: {\"type\":\"response.reasoning_summary_text.delta\",\"delta\":\"Plan.\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n", true);
        using var http = new HttpClient(handler);
        var connection = DirectCodexTests.Connection() with
        {
            CapabilityOverrides = new() { SupportsReasoningText = display },
            Catalog = [new LlmModel("catalog-model", "Catalog model")
            { CatalogEntry = System.Text.Json.JsonSerializer.SerializeToElement(new { supported_reasoning_levels = reasons ? new[] { new { effort = "high" } } : [] }) }]
        };
        var events = await Read(new OpenAiCodexLlmClient(http).StreamAsync(connection,
            new() { ModelId = "catalog-model", Messages = [new("user", "hi")] }));
        Assert.Equal(display && reasons ? "auto" : null, (string?)handler.Body!["reasoning"]?["summary"]);
        Assert.Equal(display ? 1 : 0, events.OfType<LlmReasoningDeltaEvent>().Count());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task GeminiDisplaySwitchPreservesSignedThoughtsAndAnswer(bool streaming, bool display)
    {
        const string complete = """{"candidates":[{"content":{"parts":[{"thought":true,"text":"Plan.","thoughtSignature":"signed"},{"text":"Answer."}]},"finishReason":"STOP"}]}""";
        var handler = new Handler(streaming ? "data: " + complete + "\n\n" : complete, streaming);
        using var http = new HttpClient(handler);
        var events = await Read(new GeminiApiLlmClient(http).StreamAsync(Connection(LlmTransport.GoogleGeminiApi, streaming, display),
            new() { ModelId = "gemini-3-flash-preview", Messages = [new("user", "hi")] }));
        Assert.Equal(display ? 1 : 0, events.OfType<LlmReasoningDeltaEvent>().Count());
        var result = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        Assert.Equal("Answer.", result.Text);
        Assert.Contains("signed", result.ContinuationState!.Payload, StringComparison.Ordinal);
        Assert.Equal(display ? true : null, (bool?)handler.Body!["generationConfig"]?["thinkingConfig"]?["includeThoughts"]);
        Assert.Null(handler.Body["generationConfig"]?["thinkingConfig"]?["thinkingLevel"]);
        Assert.Null(handler.Body["generationConfig"]?["thinkingConfig"]?["thinkingBudget"]);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task AnthropicDisplaySwitchPreservesSignedThinking(bool streaming, bool display)
    {
        const string complete = """{"type":"message","content":[{"type":"thinking","thinking":"Plan.","signature":"signed"},{"type":"text","text":"Answer."}],"stop_reason":"end_turn"}""";
        const string streamed = """
            data: {"type":"message_start","message":{"id":"m1","usage":{"input_tokens":1}}}

            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Plan."}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"signed"}}

            data: {"type":"content_block_stop","index":0}

            data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

            data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Answer."}}

            data: {"type":"content_block_stop","index":1}

            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}

            data: {"type":"message_stop"}

            """;
        var handler = new Handler(streaming ? streamed : complete, streaming);
        using var http = new HttpClient(handler);
        var events = await Read(new AnthropicMessagesLlmClient(http).StreamAsync(Connection(LlmTransport.AnthropicMessages, streaming, display),
            new() { ModelId = "claude-opus-5", Messages = [new("user", "hi")], ReasoningLevel = LlmReasoningLevel.High }));
        Assert.Equal(display ? 1 : 0, events.OfType<LlmReasoningDeltaEvent>().Count());
        var result = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        Assert.Equal("Answer.", result.Text);
        Assert.Contains("signed", result.ContinuationState!.Payload, StringComparison.Ordinal);
        Assert.Equal("adaptive", (string?)handler.Body!["thinking"]!["type"]);
        Assert.Equal(display ? "summarized" : null, (string?)handler.Body["thinking"]!["display"]);
        Assert.Equal("high", (string?)handler.Body["output_config"]!["effort"]);
    }

    [Theory]
    [InlineData("claude-fable-5", true)]
    [InlineData("claude-fable-5-1", true)]
    [InlineData("claude-mythos-preview", true)]
    [InlineData("claude-mythos-5-1", true)]
    [InlineData("claude-opus-5", true)]
    [InlineData("claude-opus-5-5", true)]
    [InlineData("claude-sonnet-5", true)]
    [InlineData("claude-sonnet-5-5", true)]
    [InlineData("claude-opus-4-8", false)]
    [InlineData("claude-opus-4-7", false)]
    [InlineData("claude-sonnet-4-6", false)]
    [InlineData("claude-opus-4-5", false)]
    [InlineData("claude-haiku-4-5", false)]
    [InlineData("claude-unprofiled", false)]
    public async Task AnthropicDefaultSummaryRequestsDoNotInventEffort(string model, bool thinksByDefault)
    {
        foreach (bool display in new[] { true, false })
        {
            var handler = new Handler("""{"type":"message","content":[],"stop_reason":"end_turn"}""", false);
            using var http = new HttpClient(handler);
            await Read(new AnthropicMessagesLlmClient(http).StreamAsync(Connection(LlmTransport.AnthropicMessages, false, display),
                new() { ModelId = model, Messages = [new("user", "hi")] }));
            JsonObject body = handler.Body!;
            Assert.Null(body["output_config"]);
            Assert.Equal(8192, (int?)body["max_tokens"]);
            Assert.Equal(thinksByDefault && display ? "adaptive" : null, (string?)body["thinking"]?["type"]);
            Assert.Equal(thinksByDefault && display ? "summarized" : null, (string?)body["thinking"]?["display"]);
        }
    }

    [Fact]
    public void AnthropicNegativeCatalogEvidenceDisablesDefaultThinking()
    {
        using JsonDocument catalog = JsonDocument.Parse("""{"capabilities":{"thinking":{"types":{"adaptive":{"supported":false},"enabled":{"supported":false}}},"effort":{"supported":false}}}""");
        AnthropicModelProfile profile = AnthropicModelProfiles.Resolve("claude-opus-5", catalog.RootElement);
        Assert.False(profile.ThinksByDefault);
        Assert.Empty(profile.ReasoningLevels);
        Assert.Equal(new AnthropicReasoningFields(null, null, null), AnthropicModelProfiles.Fields(profile, null, 8192, true));
    }

    [Fact]
    public void GeminiSummaryOptOutKeepsThinkingBudgetAndCatalogEvidenceAuthoritative()
    {
        var profile = GeminiModelProfiles.Get("gemini-2.5-flash", null);
        var config = GeminiModelProfiles.ThinkingConfig(profile, LlmReasoningLevel.High, false)!;
        Assert.NotNull(config["thinkingBudget"]);
        Assert.Null(config["includeThoughts"]);
        Assert.Null(GeminiModelProfiles.ThinkingConfig(GeminiModelProfiles.Get("gemini-3-flash-preview", false), null, true));
        Assert.Null(GeminiModelProfiles.ThinkingConfig(profile, LlmReasoningLevel.None, true)!["includeThoughts"]);
    }

    private static LlmConnection Connection(LlmTransport transport, bool streaming, bool display) => new()
    {
        Transport = transport,
        Version = "1",
        Endpoint = transport == LlmTransport.GoogleGeminiApi ? GeminiApiLlmClient.DefaultEndpoint : "https://provider.test/v1",
        Credential = new LlmCredential { ApiKey = "fixture" }.Serialize(),
        CapabilityOverrides = new() { SupportsStreaming = streaming, SupportsReasoningText = display }
    };
    private static async Task<List<LlmStreamEvent>> Read(IAsyncEnumerable<LlmStreamEvent> source)
    { var events = new List<LlmStreamEvent>(); await foreach (var item in source) { events.Add(item); } return events; }
    private sealed class Handler(string reply, bool streaming) : HttpMessageHandler
    {
        public JsonObject? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            return new(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }
}
