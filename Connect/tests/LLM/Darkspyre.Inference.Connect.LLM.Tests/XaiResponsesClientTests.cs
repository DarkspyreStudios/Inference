using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiResponsesClientTests
{
    private static readonly string[] ModelAliases = ["my-model"];
    private static readonly string[] ImageModalities = ["text", "image"];
    private const string Answer = """
        {"id":"response-id","status":"completed","service_tier":"priority","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}]}
        """;

    [Theory]
    [InlineData(true, XaiRegion.Global, XaiServiceTier.Default)]
    [InlineData(false, XaiRegion.UnitedStates, XaiServiceTier.Priority)]
    public async Task ConcreteClientUsesOAuthFixedRegionAndTypedPayload(bool streaming, XaiRegion region, XaiServiceTier tier)
    {
        using var handler = new Handler(_ => Response(streaming));
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        LlmConnection connection = Connection(streaming, region, tier) with
        {
            AdditionalHeaders = new Dictionary<string, string> { ["x-client-request-id"] = "request" }
        };
        LlmChatRequest request = Request() with { Temperature = 0.7, TopP = 0.9, ConversationKey = "conversation", MaxOutputTokens = 128 };
        List<LlmStreamEvent> events = await Read(client.StreamAsync(connection, request));
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal("answer", result.Text);
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("priority", result.Diagnostics!.ServiceTier);
        Assert.Equal("Bearer oauth-token", handler.Authorization);
        Assert.Equal(connection.Endpoint + "/responses", handler.Uri);
        Assert.Equal(streaming ? "text/event-stream" : "application/json", handler.Accept);
        using JsonDocument payload = JsonDocument.Parse(handler.Body!);
        JsonElement root = payload.RootElement;
        Assert.Equal(streaming, root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal("reasoning.encrypted_content", root.GetProperty("include")[0].GetString());
        Assert.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("auto", root.GetProperty("reasoning").GetProperty("summary").GetString());
        Assert.Equal(tier == XaiServiceTier.Priority ? "priority" : "default", root.GetProperty("service_tier").GetString());
        Assert.Equal(0.7, root.GetProperty("temperature").GetDouble());
        Assert.Equal(0.9, root.GetProperty("top_p").GetDouble());
        Assert.Equal("conversation", root.GetProperty("prompt_cache_key").GetString());
        Assert.Equal(128, root.GetProperty("max_output_tokens").GetInt32());
        Assert.False(root.TryGetProperty("previous_response_id", out _));
        Assert.IsAssignableFrom<ILlmContextEstimator>(client);
        Assert.False((object)client is ILlmProviderTokenCounter);
        Assert.False(client.GetCapabilities(connection).SupportsUsageDeltas);
    }

    [Theory]
    [InlineData("https://example.test/v1", XaiRegion.Global)]
    [InlineData("https://us.api.x.ai/v1", XaiRegion.Global)]
    [InlineData("https://api.x.ai/v1", XaiRegion.UnitedStates)]
    [InlineData("http://api.x.ai/v1", XaiRegion.Global)]
    [InlineData("https://api.x.ai/v1?redirect=other", XaiRegion.Global)]
    [InlineData("https://api.x.ai/v1/../private", XaiRegion.Global)]
    public async Task EndpointMismatchFailsWithoutSendingCredentials(string endpoint, XaiRegion region)
    {
        using var handler = new Handler(_ => Response());
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        await Assert.ThrowsAsync<ArgumentException>(() => Read(client.StreamAsync(Connection(region: region) with { Endpoint = endpoint }, Request())));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("api-key")]
    [InlineData("none")]
    [InlineData("mixed")]
    [InlineData("whitespace")]
    [InlineData("newline")]
    [InlineData("malformed")]
    public async Task NoApiKeyFallbackOrInvalidBearerBeforeNetwork(string kind)
    {
        using var handler = new Handler(_ => Response());
        using var http = new HttpClient(handler);
        LlmConnection connection = Connection() with
        {
            Credential = kind switch
            {
                "api-key" => new LlmCredential { ApiKey = "not-used" }.Serialize(),
                "none" => null,
                "mixed" => new LlmCredential { ApiKey = "not-used", OAuth = new() { AccessToken = "oauth-token" } }.Serialize(),
                "whitespace" => new LlmCredential { OAuth = new() { AccessToken = "bad token" } }.Serialize(),
                "newline" => new LlmCredential { OAuth = new() { AccessToken = "token\r\nnew" } }.Serialize(),
                _ => "{invalid"
            }
        };
        var exception = await Assert.ThrowsAsync<InferenceException>(() => Read(new XaiResponsesLlmClient(http).StreamAsync(connection, Request())));
        Assert.Equal(InferenceFailureCode.AuthenticationRequired, exception.Code);
        Assert.DoesNotContain("not-used", exception.ToString());
        Assert.DoesNotContain("bad token", exception.ToString());
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("previous_response_id")]
    [InlineData("store")]
    [InlineData("include")]
    [InlineData("service_tier")]
    [InlineData("reasoning")]
    [InlineData("reasoning_effort")]
    [InlineData("tools")]
    [InlineData("tool_choice")]
    [InlineData("parallel_tool_calls")]
    [InlineData("text")]
    [InlineData("temperature")]
    [InlineData("max_output_tokens")]
    [InlineData("background")]
    [InlineData("deferred")]
    [InlineData("prompt_cache_key")]
    public async Task TypedControlsCannotBeInjectedWhenAbsent(string field)
    {
        using var handler = new Handler(_ => Response());
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        var options = new Dictionary<string, JsonElement> { [field] = Json("true") };
        await Assert.ThrowsAsync<ArgumentException>(() => Read(client.StreamAsync(Connection(), Request() with { ProviderOptions = options })));
        await Assert.ThrowsAsync<ArgumentException>(() => Read(client.StreamAsync(Connection() with { ProviderOptions = options }, Request())));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("top-k")]
    [InlineData("seed")]
    [InlineData("presence")]
    [InlineData("frequency")]
    [InlineData("stop")]
    public async Task UnsupportedControlsCannotBeEnabledByOverrides(string control)
    {
        using var handler = new Handler(_ => Response());
        using var http = new HttpClient(handler);
        LlmChatRequest request = control switch
        {
            "top-k" => Request() with { TopK = 10 },
            "seed" => Request() with { Seed = 4 },
            "presence" => Request() with { PresencePenalty = 0.2 },
            "frequency" => Request() with { FrequencyPenalty = 0.2 },
            _ => Request() with { StopSequences = ["END"] }
        };
        LlmConnection connection = Connection() with
        {
            CapabilityOverrides = new()
            { SupportsTopK = true, SupportsSeed = true, SupportsPresencePenalty = true, SupportsFrequencyPenalty = true, MaxStopSequences = 4 }
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => Read(new XaiResponsesLlmClient(http).StreamAsync(connection, request)));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(2.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task TemperatureRangeFailsBeforeNetwork(double temperature)
    {
        using var handler = new Handler(_ => Response());
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Read(new XaiResponsesLlmClient(http).StreamAsync(Connection(), Request() with { Temperature = temperature })));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("grok-4.7", 4)]
    [InlineData("grok-4.6-latest", 4)]
    [InlineData("grok-4.5-20261001", 3)]
    [InlineData("grok-4.20-non-reasoning", 0)]
    [InlineData("grok-4.20-multi-agent", 0)]
    [InlineData("manual-unlisted-model", 0)]
    public void FallbackProfilesDoNotBorrowOpenAiReasoningRules(string model, int count)
    {
        using var http = new HttpClient();
        LlmProviderCapabilities capabilities = new XaiResponsesLlmClient(http).GetCapabilities(Connection(), model);
        Assert.Equal(count, capabilities.ReasoningLevels.Count);
        Assert.DoesNotContain(LlmReasoningLevel.None, capabilities.ReasoningLevels);
        Assert.False(capabilities.SupportsPromptImages);
    }

    [Fact]
    public void MultiAgentProfileReportsCountsWithoutReasoningLabels()
    {
        XaiModelProfile profile = XaiModelProfiles.Get(Connection(), "grok-4.20-multi-agent");
        Assert.Empty(profile.ReasoningLevels);
        Assert.Null(profile.DefaultReasoningLevel);
        Assert.Equal(4, profile.MultiAgent!.AgentCounts["low"]);
        Assert.Equal(4, profile.MultiAgent.AgentCounts["medium"]);
        Assert.Equal(16, profile.MultiAgent.AgentCounts["high"]);
        Assert.Equal(16, profile.MultiAgent.AgentCounts["xhigh"]);
    }

    [Theory]
    [InlineData("[]", null, 0)]
    [InlineData("[\"low\",\"high\",\"unknown\",\"none\"]", "low", 2)]
    [InlineData("[\"xhigh\"]", "low", 1)]
    public void CatalogEvidenceAndAliasesOverrideFamilyFallback(string efforts, string? defaultEffort, int count)
    {
        JsonElement metadata = JsonSerializer.SerializeToElement(new
        {
            aliases = ModelAliases,
            input_modalities = ImageModalities,
            capabilities = new { reasoning_effort = Json(efforts), default_reasoning_effort = defaultEffort }
        });
        LlmConnection connection = Connection() with { Catalog = [new("grok-4.7", "model") { CatalogEntry = metadata }] };
        XaiModelProfile profile = XaiModelProfiles.Get(connection, "my-model");
        Assert.Equal(count, profile.ReasoningLevels.Count);
        Assert.Equal(count == 2 ? LlmReasoningLevel.Low : null, profile.DefaultReasoningLevel);
        Assert.True(profile.AcceptsImages);
        Assert.True(profile.AllowsSampling);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownManualModelStillAttemptsWithoutFakeCatalog(bool streaming)
    {
        using var handler = new Handler(_ => Response(streaming));
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        await Read(client.StreamAsync(Connection(streaming), Request() with { ModelId = "manual-unlisted-model" }));
        using JsonDocument body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("manual-unlisted-model", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.TryGetProperty("reasoning", out _));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(350, false)]
    [InlineData(351, true)]
    public async Task ToolCountLimitDoesNotSilentlyTruncate(int count, bool rejected)
    {
        using var handler = new Handler(_ => Response());
        using var http = new HttpClient(handler);
        var request = Request() with { Tools = [.. Enumerable.Range(0, count).Select(index => new LlmToolDefinition { Name = "tool" + index, InputSchema = Json("{\"type\":\"object\"}") })] };
        var client = new XaiResponsesLlmClient(http);
        if (rejected)
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => Read(client.StreamAsync(Connection(), request)));
            Assert.Equal(0, handler.Calls);
        }
        else
        {
            await Read(client.StreamAsync(Connection(), request));
            using JsonDocument body = JsonDocument.Parse(handler.Body!);
            Assert.Equal(350, body.RootElement.GetProperty("tools").GetArrayLength());
        }
    }

    [Fact]
    public async Task ReservedClientToolNameFailsBeforeNetwork()
    {
        using var handler = new Handler(_ => Response());
        using var http = new HttpClient(handler);
        var request = Request() with { Tools = [new() { Name = "web_search", InputSchema = Json("{}") }] };
        await Assert.ThrowsAsync<ArgumentException>(() => Read(new XaiResponsesLlmClient(http).StreamAsync(Connection(), request)));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("minLength", 2048, true)]
    [InlineData("maxLength", 2049, false)]
    [InlineData("minItems", 256, true)]
    [InlineData("maxItems", 257, false)]
    [InlineData("minProperties", 64, true)]
    [InlineData("maxProperties", 65, false)]
    public void SchemaChecksDeclaredConstraintsNotLiteralLength(string keyword, int value, bool fits)
    {
        JsonElement schema = JsonSerializer.SerializeToElement(new
        { properties = new { value = new Dictionary<string, int> { [keyword] = value } } });
        Assert.Equal(fits, XaiSchemaLimits.Fits(schema));
    }

    [Theory]
    [InlineData("{\"enum\":[{\"maxLength\":9999}]}", true)]
    [InlineData("{\"default\":{\"maxItems\":9999}}", true)]
    [InlineData("{\"examples\":[{\"maxProperties\":9999}]}", true)]
    [InlineData("{\"$defs\":{\"unused\":{\"maxLength\":9999}}}", true)]
    [InlineData("{\"$ref\":\"#/$defs/value\",\"$defs\":{\"value\":{\"maxLength\":9999}}}", false)]
    [InlineData("{\"$ref\":\"#/$defs/a~1b\",\"$defs\":{\"a/b\":{\"maxItems\":9999}}}", false)]
    [InlineData("{\"allOf\":[{\"items\":{\"maxLength\":9999}}]}", false)]
    [InlineData("{\"$ref\":\"#\"}", false)]
    [InlineData("{\"$ref\":\"#/$defs/self\",\"$defs\":{\"self\":{\"items\":{\"$ref\":\"#/$defs/self\"}}}}", false)]
    [InlineData("{\"$defs\":{\"value\":{\"maxLength\":2048}},\"properties\":{\"a\":{\"$ref\":\"#/$defs/value\"},\"b\":{\"$ref\":\"#/$defs/value\"}}}", true)]
    public void SchemaTraversalUsesSchemaPositionsAndReferenceParser(string schema, bool fits) => Assert.Equal(fits, XaiSchemaLimits.Fits(Json(schema)));

    [Theory]
    [InlineData(LlmStructuredOutputFallback.None)]
    [InlineData(LlmStructuredOutputFallback.JsonObject)]
    public async Task OversizedSchemaDoesNotInventFallbackPermission(LlmStructuredOutputFallback fallback)
    {
        using var handler = new Handler(_ => Response());
        using var http = new HttpClient(handler);
        var request = Request() with { StructuredOutput = Structured("{\"type\":\"string\",\"maxLength\":2049}", fallback) };
        await Assert.ThrowsAsync<NotSupportedException>(() => Read(new XaiResponsesLlmClient(http).StreamAsync(Connection(), request)));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedSchemaUsesOriginalLocalValidationWhenAuthorized(bool matches)
    {
        string answer = Answer.Replace("answer", matches ? "\\\"ok\\\"" : "7", StringComparison.Ordinal);
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler);
        var request = Request() with { StructuredOutput = Structured("{\"type\":\"string\",\"maxLength\":2049}", LlmStructuredOutputFallback.LocalValidation) };
        var client = new XaiResponsesLlmClient(http);
        Assert.NotNull(client.EstimateInputTokens(Connection(false), request));
        LlmChatResult result = Assert.Single((await Read(client.StreamAsync(Connection(false), request))).OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmStructuredOutputEnforcement.LocalValidationOnly, result.StructuredOutput!.Enforcement);
        Assert.Equal(matches, result.StructuredOutput.IsValid);
        using JsonDocument body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("text", out _));
    }

    [Fact]
    public async Task NativeStructuredSchemaAndStrictToolAreNotRewritten()
    {
        using var handler = new Handler(_ => Response());
        using var http = new HttpClient(handler);
        JsonElement schema = Json("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\",\"maxLength\":2048}},\"additionalProperties\":false,\"required\":[\"value\"]}");
        var request = Request() with
        {
            StructuredOutput = new() { Name = "output", Schema = schema },
            Tools = [new() { Name = "lookup", Strict = true, InputSchema = schema }],
            ToolChoice = new() { Mode = LlmToolChoiceMode.Specific, Name = "lookup" },
            AllowParallelToolCalls = false
        };
        await Read(new XaiResponsesLlmClient(http).StreamAsync(Connection(), request));
        using JsonDocument body = JsonDocument.Parse(handler.Body!);
        JsonElement format = body.RootElement.GetProperty("text").GetProperty("format");
        Assert.Equal(schema.GetRawText(), format.GetProperty("schema").GetRawText());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.True(body.RootElement.GetProperty("tools")[0].GetProperty("strict").GetBoolean());
        Assert.False(body.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Equal("lookup", body.RootElement.GetProperty("tool_choice").GetProperty("name").GetString());
    }

    [Fact]
    public async Task OversizedStrictToolFailsWithoutWeakening()
    {
        using var handler = new Handler(_ => Response());
        using var http = new HttpClient(handler);
        var request = Request() with { Tools = [new() { Name = "lookup", Strict = true, InputSchema = Json("{\"type\":\"object\",\"maxProperties\":65}") }] };
        await Assert.ThrowsAsync<NotSupportedException>(() => Read(new XaiResponsesLlmClient(http).StreamAsync(Connection(), request)));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ImageInputRequiresCatalogModalityAndFitsLimit(bool supportsImages, bool overLimit)
    {
        using var handler = new Handler(_ => Response());
        using var http = new HttpClient(handler);
        byte[] bytes = overLimit ? new byte[20 * 1024 * 1024 + 1] : [1, 2, 3];
        LlmConnection connection = WithImageCatalog(supportsImages);
        var request = Request() with { Messages = [new("user", "image") { Attachments = [new() { Kind = LlmBinaryKind.Image, FileName = "image.png", MediaType = "image/png", Data = bytes }] }] };
        if (overLimit)
            await Assert.ThrowsAsync<ArgumentException>(() => Read(new XaiResponsesLlmClient(http).StreamAsync(connection, request)));
        else
            await Assert.ThrowsAsync<NotSupportedException>(() => Read(new XaiResponsesLlmClient(http).StreamAsync(connection, request)));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(supportsImages, new XaiResponsesLlmClient(http).GetCapabilities(connection, request.ModelId).SupportsPromptImages);
    }

    [Fact]
    public async Task ImagesAndDocumentsUseProviderContentItems()
    {
        using var handler = new Handler(_ => Response());
        using var http = new HttpClient(handler);
        var request = Request() with
        {
            Messages = [new("user", "inspect") { Attachments = [
            new() { Kind = LlmBinaryKind.Image, FileName = "image.png", MediaType = "image/png", Data = new byte[] { 1, 2 } },
            new() { Kind = LlmBinaryKind.Document, FileName = "document.pdf", MediaType = "application/pdf", Data = new byte[] { 3, 4 } }
        ] }]
        };
        await Read(new XaiResponsesLlmClient(http).StreamAsync(WithImageCatalog(true), request));
        using JsonDocument body = JsonDocument.Parse(handler.Body!);
        JsonElement content = body.RootElement.GetProperty("input")[0].GetProperty("content");
        Assert.Equal("input_image", content[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,AQI=", content[1].GetProperty("image_url").GetString());
        Assert.Equal("input_file", content[2].GetProperty("type").GetString());
        Assert.Equal("document.pdf", content[2].GetProperty("filename").GetString());
        Assert.Equal("data:application/pdf;base64,AwQ=", content[2].GetProperty("file_data").GetString());
    }

    [Fact]
    public async Task CatalogJoinsOnlyEntitledLanguageModelsAndPreservesEvidence()
    {
        using var handler = new Handler(request => new(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("language-models", StringComparison.Ordinal)
                ? """{"models":[{"id":"grok-4.7","aliases":["grok-latest"],"input_modalities":["text","image"],"capabilities":{"reasoning_effort":["low"],"default_reasoning_effort":"low"}}]}"""
                : """{"data":[{"id":"grok-4.7","context_length":1000000},{"id":"image-only","context_length":200}]}""", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler);
        LlmModel model = Assert.Single(await new XaiResponsesLlmClient(http).ListModelsAsync(Connection()));
        Assert.Equal("grok-4.7", model.Id);
        Assert.Equal(1000000, model.MaxContextTokens);
        Assert.Equal("grok-latest", model.CatalogEntry!.Value.GetProperty("aliases")[0].GetString());
        Assert.True(model.Capabilities!.SupportsPromptImages);
        Assert.Equal(LlmReasoningLevel.Low, Assert.Single(model.Capabilities.ReasoningLevels));
        Assert.Equal("Bearer oauth-token", handler.Authorization);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":[null]}")]
    public async Task InvalidCatalogFailsSafely(string body)
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<LlmProviderException>(() => new XaiResponsesLlmClient(http).ListModelsAsync(Connection()));
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void OverridesCannotInventReasoningWordsOrAnImageModality()
    {
        using var http = new HttpClient();
        var connection = Connection() with
        {
            CapabilityOverrides = new()
            {
                ReasoningLevels = LlmReasoningEffort.Levels(LlmReasoningLevel.None, LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Maximum),
                SupportsPromptImages = true
            }
        };
        LlmProviderCapabilities capabilities = new XaiResponsesLlmClient(http).GetCapabilities(connection, "grok-4.7");
        Assert.Equal(LlmReasoningLevel.Low, Assert.Single(capabilities.ReasoningLevels));
        Assert.False(capabilities.SupportsPromptImages);
        Assert.Null(capabilities.RichContentSupported.MaxBinaryBytes);
        Assert.Empty(new XaiResponsesLlmClient(http).GetCapabilities(connection, "grok-4.20-multi-agent").ReasoningLevels);
    }

    [Fact]
    public async Task CatalogMergesAliasedIdentityAndNestedFactsWithoutReplacingContradictions()
    {
        using var handler = new Handler(request => new(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("language-models", StringComparison.Ordinal)
                ? """{"models":[{"id":"grok-4.7","aliases":["grok-latest"],"capabilities":{"reasoning_effort":["low"]}}]}"""
                : """{"data":[{"id":"grok-latest","aliases":["grok-4.7","generic-alias"],"context_length":1000000,"capabilities":{"reasoning_effort":["high"],"default_reasoning_effort":"low"}}]}""", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler);
        LlmModel model = Assert.Single(await new XaiResponsesLlmClient(http).ListModelsAsync(Connection()));
        Assert.Equal(1000000, model.MaxContextTokens);
        Assert.Equal(LlmReasoningLevel.Low, Assert.Single(model.Capabilities!.ReasoningLevels));
        XaiModelProfile profile = XaiModelProfiles.Get(Connection() with { Catalog = [model] }, "generic-alias");
        Assert.Equal(LlmReasoningLevel.Low, profile.DefaultReasoningLevel);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeImageLimitAlsoAppliesToToolBinaryAndEmbeddedResources(bool resource)
    {
        using var handler = new Handler(_ => Response());
        using var http = new HttpClient(handler);
        var bytes = new byte[20 * 1024 * 1024 + 1];
        LlmToolOutputPart part = resource ? new LlmToolOutputResource("https://resource.test/image.png") { MediaType = "image/png", Data = bytes }
            : new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", bytes);
        var request = Request() with { Messages = [new("tool", "") { ToolResults = [new("call", new() { Parts = [part] })] }] };
        await Assert.ThrowsAsync<ArgumentException>(() => Read(new XaiResponsesLlmClient(http).StreamAsync(WithImageCatalog(true), request)));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BadRegionalOrTierEnumFailsBeforeNetwork(bool region)
    {
        using var handler = new Handler(_ => Response());
        using var http = new HttpClient(handler);
        var connection = Connection() with { Xai = region ? new() { Region = (XaiRegion)99 } : new() { ServiceTier = (XaiServiceTier)99 } };
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Read(new XaiResponsesLlmClient(http).StreamAsync(connection, Request())));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task UnrelatedAdvancedOptionsRemainAvailable()
    {
        using var handler = new Handler(_ => Response());
        using var http = new HttpClient(handler);
        var request = Request() with { ProviderOptions = new Dictionary<string, JsonElement> { ["metadata"] = Json("{\"request\":\"advanced\"}") } };
        await Read(new XaiResponsesLlmClient(http).StreamAsync(Connection(), request));
        using JsonDocument body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("advanced", body.RootElement.GetProperty("metadata").GetProperty("request").GetString());
    }

    private static LlmStructuredOutputRequest Structured(string schema, LlmStructuredOutputFallback fallback) => new() { Name = "output", Schema = Json(schema), Fallback = fallback };
    private static LlmChatRequest Request() => new() { ModelId = "grok-4.7", Messages = [new("user", "question")] };
    private static LlmConnection Connection(bool streaming = true, XaiRegion region = XaiRegion.Global, XaiServiceTier tier = XaiServiceTier.Default) => new()
    {
        Version = "v1",
        Transport = LlmTransport.XaiResponses,
        Endpoint = new XaiConnectionOptions { Region = region }.GetEndpoint(),
        Xai = new() { Region = region, ServiceTier = tier },
        Credential = new LlmCredential { OAuth = new() { AccessToken = "oauth-token", AccountId = "account" } }.Serialize(),
        CapabilityOverrides = new() { SupportsStreaming = streaming }
    };
    private static LlmConnection WithImageCatalog(bool images) => Connection() with
    { Catalog = [new("grok-4.7", "Grok") { CatalogEntry = Json(images ? "{\"input_modalities\":[\"text\",\"image\"]}" : "{\"input_modalities\":[\"text\"]}") }] };
    private static JsonElement Json(string text) { using JsonDocument document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static async Task<List<LlmStreamEvent>> Read(IAsyncEnumerable<LlmStreamEvent> events)
    { var result = new List<LlmStreamEvent>(); await foreach (LlmStreamEvent item in events) result.Add(item); return result; }
    private static HttpResponseMessage Response(bool streaming = true) => new(HttpStatusCode.OK)
    { Content = new StringContent(streaming ? "data: {\"type\":\"response.completed\",\"response\":" + Answer + "}\n\n" : Answer, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal string? Body { get; private set; }
        internal string? Authorization { get; private set; }
        internal string? Uri { get; private set; }
        internal string? Accept { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            Accept = request.Headers.Accept.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }
}
