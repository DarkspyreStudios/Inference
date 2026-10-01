using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Vertex;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class VertexBreadthTests
{
    private static LlmConnection Standard => new()
    {
        Version = "1",
        Transport = LlmTransport.GoogleVertexAi,
        Endpoint = VertexEndpoints.BaseEndpoint,
        Vertex = new() { ProjectId = "project", Location = "global" }
    };

    private static LlmConnection Express => Standard with
    {
        Vertex = new() { AuthenticationMode = LlmGoogleAuthenticationMode.ApiKey },
        Credential = new LlmCredential { ApiKey = "protected-key" }.Serialize()
    };

    [Theory]
    [InlineData("google/gemini-3.1-pro-preview", LlmReasoningLevel.Medium)]
    [InlineData("google/gemini-2.5-flash", LlmReasoningLevel.Low)]
    [InlineData("anthropic/claude-opus-4-7@20260101", LlmReasoningLevel.ExtraHigh)]
    public void SharedProfilesAcceptModelsOutsideTheOriginalPair(string model, LlmReasoningLevel level)
    {
        VertexModelProfile profile = VertexModelProfiles.Get(model);
        Assert.Contains(level, profile.ReasoningLevels);
        Assert.True(profile.Capabilities.SupportsStreaming);
        Assert.Null(profile.ContextTokens);
        Assert.Null(profile.OutputTokens);
    }

    [Fact]
    public void ClaudeCapabilitiesPreserveUnknownSchemaAndModelSpecificToolChoice()
    {
        var known = VertexModelProfiles.Get("anthropic/claude-sonnet-4-6");
        Assert.True(known.Capabilities.SupportsTemperature);
        Assert.Equal(LlmStrictInputSchemaSupport.Unknown, known.Capabilities.Tools.StrictInputSchema);
        Assert.False(known.Capabilities.SupportsJsonSchema);
        var noForced = VertexModelProfiles.Get("anthropic/claude-sonnet-5-5");
        Assert.False(noForced.Capabilities.SupportsTemperature);
        Assert.DoesNotContain(LlmReasoningLevel.None, noForced.ReasoningLevels);
        Assert.DoesNotContain(LlmToolChoiceMode.Required, noForced.Capabilities.Tools.ChoiceModes);
        Assert.DoesNotContain(LlmToolChoiceMode.Specific, noForced.Capabilities.Tools.ChoiceModes);
    }

    [Theory]
    [InlineData("google/gemini-3.1-flash-image-preview")]
    [InlineData("google/gemini-2.5-flash-tts")]
    [InlineData("google/gemini-live-model")]
    [InlineData("google/gemini-native-audio")]
    [InlineData("google/gemini-embedding-001")]
    [InlineData("google/gemma-3")]
    [InlineData("anthropic/embedding-model")]
    public void NonChatModelsAreNotAccepted(string model) => Assert.Throws<NotSupportedException>(() => VertexModelProfiles.Get(model));

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.OK)]
    public async Task CatalogListsChatFamiliesAndRetainsFactsWithoutInventingInvocationVersions(HttpStatusCode partnerStatus)
    {
        using var handler = new Handler { PartnerStatus = partnerStatus };
        using var http = new HttpClient(handler);
        var client = new VertexLlmClient(http, new Authentication());
        var models = await client.ListModelsAsync(Standard with { Vertex = Standard.Vertex! with { Location = "us-central1" } });
        Assert.Equal(partnerStatus == HttpStatusCode.OK ? 3 : 2, models.Count);
        Assert.All(models, model => Assert.Equal(LlmModelAvailability.Unknown, model.Availability));
        var pro = models.Single(model => model.Id == "google/gemini-3.1-pro-preview");
        Assert.Null(pro.MaxContextTokens);
        Assert.Contains("PUBLIC_PREVIEW", pro.AvailabilityReason!, StringComparison.Ordinal);
        Assert.Equal("7", pro.CatalogEntry!.Value.GetProperty("versionId").GetString());
        var flash = models.Single(model => model.Id == "google/gemini-3-flash-preview");
        Assert.Equal(1_048_576, flash.MaxContextTokens);
        Assert.Equal(65_536, flash.MaxOutputTokens);
        Assert.Equal(3, handler.Uris.Count);
        Assert.Contains("pageToken=page%2Btwo", handler.Uris[1].Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("google/gemini-2.5-flash", true)]
    [InlineData("google/gemini-2.5-flash", false)]
    [InlineData("anthropic/claude-opus-4-7@20260101", true)]
    [InlineData("anthropic/claude-opus-4-7@20260101", false)]
    public async Task BroaderFamiliesUseTheirSelectedRegionAndNativeProtocol(string model, bool stream)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new VertexLlmClient(http, new Authentication());
        var connection = Standard with
        {
            Vertex = Standard.Vertex! with { Location = "us-central1" },
            CapabilityOverrides = new() { SupportsStreaming = stream }
        };
        var result = await Complete(client, connection, new() { ModelId = model, Messages = [new("user", "hello")] });
        Assert.Equal("done", result.Text);
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.NotNull(result.ContinuationState);
        Uri uri = Assert.Single(handler.Uris);
        Assert.Equal("us-central1-aiplatform.googleapis.com", uri.Host);
        Assert.Contains("/projects/project/locations/us-central1/publishers/" + model.Split('/')[0], uri.AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith(model.StartsWith("google/", StringComparison.Ordinal)
            ? stream ? ":streamGenerateContent" : ":generateContent"
            : stream ? ":streamRawPredict" : ":rawPredict", uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClaudeAcceptsSharedTopLevelOptionsAndRequestPrecedence(bool stream)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var connection = Standard with
        {
            CapabilityOverrides = new() { SupportsStreaming = stream },
            ProviderOptions = new Dictionary<string, JsonElement> { ["metadata"] = JsonElement.Parse("{\"user_id\":\"connection\"}") }
        };
        await Complete(new VertexLlmClient(http, new Authentication()), connection, new()
        {
            ModelId = "anthropic/claude-opus-4-7@20260101",
            Messages = [new("user", "hello")],
            ProviderOptions = new Dictionary<string, JsonElement> { ["metadata"] = JsonElement.Parse("{\"user_id\":\"request\"}") }
        });
        var payload = JsonNode.Parse(Assert.Single(handler.Bodies))!;
        Assert.Equal("request", (string?)payload["metadata"]?["user_id"]);
        Assert.Null(payload["model"]);
        Assert.Equal("vertex-2023-10-16", (string?)payload["anthropic_version"]);
    }

    [Fact]
    public async Task OnlyDocumentedOutputLimitsGateRequests()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new VertexLlmClient(http, new Authentication());
        await Assert.ThrowsAsync<ArgumentException>(() => Complete(client, Standard,
            new() { ModelId = "google/gemini-3-flash-preview", MaxOutputTokens = 65_537 }));
        Assert.Empty(handler.Uris);
        await Complete(client, Standard, new() { ModelId = "google/gemini-future-text", MaxOutputTokens = 1_000_000 });
        Assert.Single(handler.Uris);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task PartnerCatalogFailuresOtherThanForbiddenAndMissingRemainFailures(HttpStatusCode status)
    {
        using var handler = new Handler { PartnerStatus = status };
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => new VertexLlmClient(http, new Authentication()).ListModelsAsync(Standard));
        Assert.Equal(status, error.StatusCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("protected catalog error", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExpressGenerationUsesOnlyTheGlobalApiKeyHeader(bool stream)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var authentication = NoGoogleSdkAuthentication();
        var client = new VertexLlmClient(http, authentication);
        var connection = Express with { CapabilityOverrides = new() { SupportsStreaming = stream } };
        LlmChatResult result = await Complete(client, connection, new() { ModelId = "google/gemini-2.5-flash" });
        Assert.Equal("done", result.Text);
        Assert.Equal(new LlmUsage(12, 3, 15), result.Usage);
        Uri uri = Assert.Single(handler.Uris);
        Assert.Equal("aiplatform.googleapis.com", uri.Host);
        Assert.Equal("/v1/publishers/google/models/gemini-2.5-flash:" + (stream ? "streamGenerateContent" : "generateContent"), uri.AbsolutePath);
        Assert.Null(Assert.Single(handler.Authorization));
        Assert.Equal("protected-key", Assert.Single(handler.Keys));
        Assert.DoesNotContain("key=", uri.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("protected-key", Assert.Single(handler.Bodies), StringComparison.Ordinal);
        Assert.False(client.GetCapabilities(connection, "google/gemini-2.5-flash").SupportsModelListing);
        Assert.False(client.GetCapabilities(connection).SupportsModelListing);
    }

    [Fact]
    public async Task ExpressCountAndListingNeedNoGoogleCredentialDiscovery()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var authentication = NoGoogleSdkAuthentication();
        var client = new VertexLlmClient(http, authentication);
        Assert.Empty(await client.ListModelsAsync(Express));
        Assert.Empty(handler.Uris);
        LlmProviderInputTokenCount count = await client.CountInputTokensAsync(Express,
            new() { ModelId = "google/gemini-2.5-flash", Instructions = "count me", Messages = [new("user", "suffix")] });
        Assert.Equal(12, count.InputTokens);
        Assert.Equal("https://aiplatform.googleapis.com/v1/publishers/google/models/gemini-2.5-flash:countTokens", Assert.Single(handler.Uris).ToString());
        Assert.Null(Assert.Single(handler.Authorization));
        Assert.Equal("protected-key", Assert.Single(handler.Keys));
    }

    [Fact]
    public async Task ExpressContinuationIsBoundToItsKeyAndPreservesSameKeyReplay()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var authentication = NoGoogleSdkAuthentication();
        var client = new VertexLlmClient(http, authentication);
        LlmChatResult first = await Complete(client, Express, new() { ModelId = "google/gemini-2.5-flash" });
        var request = new LlmChatRequest
        {
            ModelId = "google/gemini-2.5-flash",
            Messages = [new("assistant", "done") { ContinuationState = first.ContinuationState }, new("user", "continue")]
        };
        await Complete(client, Express, request);
        await Assert.ThrowsAsync<ArgumentException>(() => Complete(client,
            Express with { Credential = new LlmCredential { ApiKey = "different-key" }.Serialize() }, request));
        Assert.Equal(2, handler.Bodies.Count);
        Assert.DoesNotContain("protected-key", first.ContinuationState!.Payload!, StringComparison.Ordinal);
        Assert.DoesNotContain("protected-key", (await authentication.ResolveAsync(Express, default)).ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("location")]
    [InlineData("empty-project")]
    [InlineData("missing-key")]
    [InlineData("google")]
    [InlineData("oauth")]
    [InlineData("aws")]
    [InlineData("headers")]
    public void ExpressRejectsOtherScopesAndCredentials(string variant)
    {
        var credential = new LlmCredential { ApiKey = "protected-key" };
        LlmConnection connection = variant switch
        {
            "project" => Express with { Vertex = Express.Vertex! with { ProjectId = "project" } },
            "empty-project" => Express with { Vertex = Express.Vertex! with { ProjectId = "" } },
            "location" => Express with { Vertex = Express.Vertex! with { Location = "global" } },
            "missing-key" => Express with { Credential = null },
            "google" => Express with { Credential = (credential with { Google = new() { ServiceAccountJson = "{}" } }).Serialize() },
            "oauth" => Express with { Credential = (credential with { OAuth = new() { AccessToken = "token" } }).Serialize() },
            "aws" => Express with { Credential = (credential with { Aws = new() { Region = "us-east-1" } }).Serialize() },
            _ => Express with { Credential = (credential with { SecretHeaders = new Dictionary<string, string> { ["x-extra"] = "secret" } }).Serialize() }
        };
        Assert.Throws<ArgumentException>(() => VertexEndpoints.Validate(connection));
    }

    [Fact]
    public async Task ExpressRejectsClaudeAndCapabilityListingEscalationBeforeIo()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var authentication = NoGoogleSdkAuthentication();
        var client = new VertexLlmClient(http, authentication);
        await Assert.ThrowsAsync<NotSupportedException>(() => Complete(client, Express, new() { ModelId = "anthropic/claude-sonnet-4-6" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.CountInputTokensAsync(Express, new() { ModelId = "anthropic/claude-sonnet-4-6" }));
        Assert.Throws<NotSupportedException>(() => client.GetCapabilities(Express with { CapabilityOverrides = new() { SupportsModelListing = true } }));
        Assert.Empty(handler.Uris);
    }

    [Fact]
    public async Task ExpressAuthenticationCancellationAndRetirementFenceResolution()
    {
        var authentication = NoGoogleSdkAuthentication();
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authentication.ResolveAsync(Express, stopped.Token));
        await authentication.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => authentication.ResolveAsync(Express, default));
    }

    private static VertexAuthentication NoGoogleSdkAuthentication() => new((_, _) => throw new InvalidOperationException("No SDK credential parsing expected."),
        () => throw new InvalidOperationException("No ADC path expected."),
        () => throw new InvalidOperationException("No ambient credential expected."),
        (_, _) => throw new InvalidOperationException("No access token expected."));

    private static async Task<LlmChatResult> Complete(VertexLlmClient client, LlmConnection connection, LlmChatRequest request)
    {
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request)) events.Add(item);
        Assert.IsType<LlmChatCompletionEvent>(events[^1]);
        return Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
    }

    private sealed class Authentication : IVertexAuthentication
    {
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(new VertexAccess("token", "identity")); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode PartnerStatus { get; init; } = HttpStatusCode.OK;
        public List<Uri> Uris { get; } = [];
        public List<string> Bodies { get; } = [];
        public List<string?> Authorization { get; } = [];
        public List<string?> Keys { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!);
            Authorization.Add(request.Headers.Authorization?.ToString());
            Keys.Add(request.Headers.TryGetValues("x-goog-api-key", out var keys) ? Assert.Single(keys) : null);
            if (request.Method == HttpMethod.Get)
            {
                bool partner = request.RequestUri!.AbsolutePath.Contains("anthropic", StringComparison.Ordinal);
                if (partner && PartnerStatus != HttpStatusCode.OK) return new(PartnerStatus) { Content = new StringContent("protected catalog error") };
                string catalog = partner
                    ? """{"publisherModels":[{"name":"publishers/anthropic/models/claude-opus-4-7@20260101","launchStage":"GA"}]}"""
                    : request.RequestUri.Query.Contains("pageToken", StringComparison.Ordinal)
                        ? """{"publisherModels":[{"name":"publishers/google/models/gemini-3-flash-preview","launchStage":"GA"}]}"""
                        : """{"publisherModels":[{"name":"publishers/google/models/gemini-3.1-pro-preview","versionId":"7","launchStage":"PUBLIC_PREVIEW"},{"name":"publishers/google/models/gemini-3.1-flash-image-preview"}],"nextPageToken":"page+two"}""";
                return Json(catalog);
            }
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (request.RequestUri!.AbsolutePath.EndsWith(":countTokens", StringComparison.Ordinal)) return Json("""{"totalTokens":12}""");
            bool claude = request.RequestUri.AbsolutePath.Contains("anthropic", StringComparison.Ordinal);
            bool stream = request.RequestUri.AbsolutePath.Contains(":stream", StringComparison.Ordinal);
            string body = claude
                ? """{"id":"m","type":"message","content":[{"type":"text","text":"done"}],"stop_reason":"end_turn","usage":{"input_tokens":12,"output_tokens":3}}"""
                : """{"candidates":[{"content":{"parts":[{"text":"done"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":12,"candidatesTokenCount":3,"totalTokenCount":15}}""";
            if (!stream) return Json(body);
            string[] frames = claude
                ? ["""{"type":"message_start","message":{"id":"m","usage":{"input_tokens":12}}}""",
                    """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":"done"}}""",
                    """{"type":"content_block_stop","index":0}""",
                    """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}""",
                    """{"type":"message_stop"}"""] : [body];
            return new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(frames.Select(frame => "data: " + frame + "\n\n")), Encoding.UTF8, "text/event-stream") };
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
