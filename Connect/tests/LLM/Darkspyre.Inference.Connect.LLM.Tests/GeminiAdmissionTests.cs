using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.Inference.Connect.LLM.Vertex;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GeminiAdmissionTests
{
    [Theory]
    [InlineData("000000186674797068656963000000006d69663168656963", "image/heic", false)]
    [InlineData("000000186674797068656963000000006d69663168656963", "image/heic", true)]
    [InlineData("00000014667479706d696631000000006d696631", "image/heif", false)]
    [InlineData("00000014667479706d696631000000006d696631", "image/heif", true)]
    [InlineData("0000000166747970000000000000002068656963000000006d69663168656963", "image/heic", false)]
    public async Task GeminiApiPromptHeifMediaPreservesDeclaredOrDetectedMimeAndBytes(string hex, string mime, bool declared)
    {
        byte[] data = Convert.FromHexString(hex);
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new GeminiApiLlmClient(http);
        var request = new LlmChatRequest
        {
            ModelId = "gemini-3-flash-preview",
            Messages = [new("user", "describe")
            { Attachments = [new LlmAttachment { Kind = LlmBinaryKind.Image, FileName = "photo.bin", Data = data, MediaType = declared ? mime : null }] }]
        };

        await foreach (LlmStreamEvent _ in client.StreamAsync(Connection(false), request)) { }

        using var sent = JsonDocument.Parse(handler.Body!);
        JsonElement inline = sent.RootElement.GetProperty("contents")[0].GetProperty("parts")[1].GetProperty("inlineData");
        Assert.Equal(mime, inline.GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String(data), inline.GetProperty("data").GetString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false, LlmBinaryKind.Image)]
    [InlineData(false, LlmBinaryKind.Document)]
    [InlineData(true, LlmBinaryKind.Document)]
    public async Task InlineMediaWithoutAPerPartLimitIsNotRefusedAtSevenMiB(bool vertex, LlmBinaryKind kind)
    {
        var data = new byte[8 * 1024 * 1024];
        if (kind == LlmBinaryKind.Image) new byte[] { 0xff, 0xd8, 0xff }.CopyTo(data, 0);
        else Array.Fill(data, (byte)'a');
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        ILlmClient client = Client(http, vertex);
        var request = new LlmChatRequest
        {
            ModelId = vertex ? "google/gemini-3-flash-preview" : "gemini-3-flash-preview",
            Messages = [new("user", "read")
            { Attachments = [new LlmAttachment { Kind = kind, FileName = kind == LlmBinaryKind.Image ? "photo.jpg" : "notes.txt", Data = data }] }]
        };

        await foreach (LlmStreamEvent _ in client.StreamAsync(Connection(vertex), request)) { }

        using var sent = JsonDocument.Parse(handler.Body!);
        JsonElement inline = sent.RootElement.GetProperty("contents")[0].GetProperty("parts")[1].GetProperty("inlineData");
        Assert.Equal(kind == LlmBinaryKind.Image ? "image/jpeg" : "text/plain", inline.GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String(data), inline.GetProperty("data").GetString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GeminiApiImageLimitsDoNotReplaceVertexsDocumentedInlineImageLimit()
    {
        var image = new LlmAttachment { Kind = LlmBinaryKind.Image, FileName = "photo.jpg", Data = new byte[8 * 1024 * 1024], MediaType = "image/jpeg" };
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new VertexLlmClient(http, new VertexAuthenticationBoundary());
        var request = new LlmChatRequest
        { ModelId = "google/gemini-3-flash-preview", Messages = [new("user", "describe") { Attachments = [image] }] };

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        { await foreach (LlmStreamEvent _ in client.StreamAsync(Connection(true), request)) { } });

        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("000000206674797068656963000000006d69663168656963")]
    [InlineData("00000010667479706d703432000000006d69663168656963")]
    public void ImageMimeDetectionDoesNotReadCompatibleBrandsOutsideTheFileTypeBox(string hex)
    {
        var image = new LlmAttachment { Kind = LlmBinaryKind.Image, FileName = "photo.heic", Data = Convert.FromHexString(hex) };
        Assert.Throws<NotSupportedException>(() => LlmAttachments.ResolveMediaType(image));
    }

    [Theory]
    [InlineData(false, "gemini-unfamiliar")]
    [InlineData(true, "gemini-unfamiliar")]
    [InlineData(false, "gemini-3-flash-preview")]
    [InlineData(true, "gemini-3-flash-preview")]
    public async Task NativeToolMediaIsNotOmittedForMissingModelFactsOrAnUnevidencedSizeLimit(bool vertex, string model)
    {
        // This real tool-result size crosses the removed 7 MiB cutoff.
        var pdf = new byte[7 * 1024 * 1024 + 1];
        "%PDF-"u8.CopyTo(pdf);
        byte[] png = [0x89, 0x50, 0x4e, 0x47];
        var output = new LlmToolOutput
        {
            Parts = [new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", png, "chart.png"),
                new LlmToolOutputBinary(LlmBinaryKind.Document, "application/pdf", pdf, "report.pdf")]
        };
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = (LlmClientBase)Client(http, vertex);
        LlmConnection connection = Connection(vertex);
        string modelId = vertex ? "google/" + model : model;
        Assert.Equal(model == "gemini-unfamiliar" ? InferenceSupport.Unknown : InferenceSupport.Supported,
            GeminiModelProfiles.Get(model, null).MultimodalFunctionResponses);
        ProjectedToolOutput projected = client.ProjectToolOutput(connection, modelId, output);
        Assert.All(projected.Report.Parts, part => Assert.Equal(LlmToolPartDelivery.Delivered, part.Delivery));
        var request = new LlmChatRequest
        {
            ModelId = modelId, UnboundContinuationState = LlmUnboundContinuationState.Omit,
            Messages = [new("user", "read the files"),
                new("assistant", "") { ToolCalls = [new LlmToolCall("call", "files", "{}")] },
                new("user", "") { ToolResults = [new LlmToolResult("call", output)] }]
        };

        await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { }

        using var sent = JsonDocument.Parse(handler.Body!);
        JsonElement function = sent.RootElement.GetProperty("contents")[2].GetProperty("parts")[0].GetProperty("functionResponse");
        JsonElement references = function.GetProperty("response").GetProperty("attachments");
        JsonElement parts = function.GetProperty("parts");
        Assert.Equal(2, parts.GetArrayLength());
        Assert.Equal("image/png", parts[0].GetProperty("inlineData").GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String(png), parts[0].GetProperty("inlineData").GetProperty("data").GetString());
        Assert.Equal("application/pdf", parts[1].GetProperty("inlineData").GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String(pdf), parts[1].GetProperty("inlineData").GetProperty("data").GetString());
        for (int index = 0; index < parts.GetArrayLength(); index++)
            Assert.Equal(parts[index].GetProperty("inlineData").GetProperty("displayName").GetString(), references[index].GetProperty("$ref").GetString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitToolMediaRestrictionsRemainEffective(bool vertex)
    {
        using var http = new HttpClient();
        var client = (LlmClientBase)Client(http, vertex);
        LlmConnection connection = Connection(vertex);
        var output = new LlmToolOutput
        { Parts = [new LlmToolOutputBinary(LlmBinaryKind.Document, "application/pdf", "%PDF-"u8.ToArray(), "report.pdf")] };
        string modelId = vertex ? "google/gemini-unfamiliar" : "gemini-unfamiliar";
        ProjectedToolOutput disabled = client.ProjectToolOutput(connection with
        { CapabilityOverrides = connection.CapabilityOverrides! with { SupportsToolResultDocuments = InferenceSupport.Unsupported } }, modelId, output);
        Assert.Equal(LlmToolPartOmission.UnsupportedByRoute, Assert.Single(disabled.Report.Parts).Omission);
        Assert.Empty(disabled.Parts);

        LlmRichContentSupported route = client.GetCapabilities(connection, modelId).RichContentSupported;
        ProjectedToolOutput limited = ToolOutputProjector.Project(output, route, route with { MaxBinaryBytes = 4 });
        Assert.Equal(LlmToolPartOmission.InvalidContent, Assert.Single(limited.Report.Parts).Omission);
        Assert.Empty(limited.Parts);

        string olderModel = vertex ? "google/gemini-2.5-flash" : "gemini-2.5-flash";
        ProjectedToolOutput unsupported = client.ProjectToolOutput(connection, olderModel, output);
        Assert.Equal(LlmToolPartOmission.UnsupportedByModel, Assert.Single(unsupported.Report.Parts).Omission);
        Assert.Empty(unsupported.Parts);
    }

    [Fact]
    public async Task GenerateContentCatalogEntriesDoNotRequireTheGeminiBrandPrefix()
    {
        const string catalog = """
            {"models":[
              {"name":"models/gemma-4-31b-it","displayName":"Gemma 4","thinking":true,"supportedGenerationMethods":["generateContent"]},
              {"name":"models/future-text","supportedGenerationMethods":["generateContent"]},
              {"name":"models/embedding-only","supportedGenerationMethods":["embedContent"]},
              {"name":"models/gemini-2.5-flash-preview-tts","supportedGenerationMethods":["generateContent"]},
              {"name":"models/invalid/name","supportedGenerationMethods":["generateContent"]}
            ]}
            """;
        using var handler = new Handler(catalog: catalog);
        using var http = new HttpClient(handler);
        var client = new GeminiApiLlmClient(http);
        LlmConnection connection = Connection(false);

        IReadOnlyList<LlmModel> models = await client.ListModelsAsync(connection);

        Assert.Collection(models, model => Assert.Equal("gemma-4-31b-it", model.Id), model => Assert.Equal("future-text", model.Id));
        Assert.Equal(InferenceSupport.Unknown, models[1].Capabilities!.SupportsReasoning);
        Assert.Equal(InferenceSupport.Unknown, models[0].Capabilities!.SupportsJsonSchema);
        var request = new LlmChatRequest
        { ModelId = models[0].Id, Messages = [new("user", "answer")], ReasoningLevel = LlmReasoningLevel.Medium };
        await foreach (LlmStreamEvent _ in client.StreamAsync(connection with { Catalog = models }, request)) { }

        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.EndsWith("/models/gemma-4-31b-it:generateContent", handler.Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("MINIMAL", sent.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
        Assert.Equal(LlmReasoningLevel.Medium, request.ReasoningLevel);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(false, """{"type":"object","$comment":"retained","examples":[{"enabled":true}],"x-provider-note":"retained","properties":{"enabled":{"type":"boolean"}}}""", LlmStructuredOutputFallback.None)]
    [InlineData(true, """{"type":"object","$comment":"retained","examples":[{"enabled":true}],"x-provider-note":"retained","properties":{"enabled":{"type":"boolean"}}}""", LlmStructuredOutputFallback.LocalValidation)]
    [InlineData(false, """{"type":"object","properties":{"enabled":{"enum":[true,null]}}}""", LlmStructuredOutputFallback.None)]
    [InlineData(true, """{"type":"object","properties":{"enabled":{"enum":[true,null]}}}""", LlmStructuredOutputFallback.None)]
    [InlineData(false, """{"$defs":{"answer":{"type":"object"}},"$ref":"#/$defs/answer","description":"retained sibling"}""", LlmStructuredOutputFallback.None)]
    [InlineData(true, """{"$defs":{"answer":{"type":"object"}},"$ref":"#/$defs/answer","description":"retained sibling"}""", LlmStructuredOutputFallback.None)]
    public async Task UnqualifiedSchemaConstructsReachTheCurrentNativeFieldUnchanged(
        bool vertex, string schema, LlmStructuredOutputFallback fallback)
    {
        const string answer = """{"enabled":true}""";
        using var handler = new Handler(answer);
        using var http = new HttpClient(handler);
        ILlmClient client = Client(http, vertex);
        LlmConnection connection = Connection(vertex);
        var request = new LlmChatRequest
        {
            ModelId = vertex ? "google/gemini-unfamiliar" : "gemini-unfamiliar", Messages = [new("user", "answer")],
            StructuredOutput = new() { Name = "answer", Schema = JsonElement.Parse(schema), Fallback = fallback }
        };
        Assert.Equal(InferenceSupport.Unknown, client.GetCapabilities(connection, request.ModelId).SupportsJsonSchema);

        LlmChatResult? result = null;
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request))
            if (item is LlmChatCompletionEvent completion) result = completion.Result;

        using var sent = JsonDocument.Parse(handler.Body!);
        JsonElement format = sent.RootElement.GetProperty("generationConfig").GetProperty("responseFormat");
        if (vertex) format = format[0];
        Assert.Equal("APPLICATION_JSON", format.GetProperty("text").GetProperty("mimeType").GetString());
        Assert.True(JsonElement.DeepEquals(request.StructuredOutput.Schema, format.GetProperty("text").GetProperty("schema")));
        Assert.Equal(LlmStructuredOutputEnforcement.ProviderJsonSchema, result!.StructuredOutput!.Enforcement);
        Assert.Equal(answer, result.Text);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitUnsupportedSchemaUsesOnlyTheCallerPermittedLocalMechanism(bool vertex)
    {
        using var handler = new Handler("{}");
        using var http = new HttpClient(handler);
        ILlmClient client = Client(http, vertex);
        LlmConnection connection = Connection(vertex) with
        { CapabilityOverrides = Connection(vertex).CapabilityOverrides! with { SupportsJsonSchema = InferenceSupport.Unsupported } };
        var request = new LlmChatRequest
        {
            ModelId = vertex ? "google/gemini-3-flash-preview" : "gemini-3-flash-preview", Messages = [new("user", "answer")],
            StructuredOutput = new() { Name = "answer", Schema = JsonElement.Parse("""{"type":"object"}""") }
        };
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        { await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { } });
        Assert.Equal(0, handler.Calls);

        request = request with { StructuredOutput = request.StructuredOutput with { Fallback = LlmStructuredOutputFallback.LocalValidation } };
        LlmChatResult? result = null;
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request))
            if (item is LlmChatCompletionEvent completion) result = completion.Result;

        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.False(sent.RootElement.GetProperty("generationConfig").TryGetProperty("responseFormat", out _));
        Assert.Equal(LlmStructuredOutputEnforcement.LocalValidationOnly, result!.StructuredOutput!.Enforcement);
        Assert.True(result.StructuredOutput.IsValid);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false, "gemini-3-pro-preview", LlmReasoningLevel.Medium, "LOW", null)]
    [InlineData(false, "gemini-3-flash-preview", LlmReasoningLevel.None, "MINIMAL", null)]
    [InlineData(false, "gemini-3.8-flash", LlmReasoningLevel.Minimal, "LOW", null)]
    [InlineData(false, "gemini-2.5-flash", LlmReasoningLevel.ExtraHigh, null, 24576)]
    [InlineData(false, "gemini-2.5-flash-lite", LlmReasoningLevel.Minimal, null, 0)]
    [InlineData(false, "gemini-2.5-pro", LlmReasoningLevel.None, null, 1024)]
    [InlineData(false, "gemini-unfamiliar", LlmReasoningLevel.Maximum, "HIGH", null)]
    [InlineData(true, "gemini-3-pro-preview", LlmReasoningLevel.Medium, "LOW", null)]
    [InlineData(true, "gemini-3-flash-preview", LlmReasoningLevel.None, "MINIMAL", null)]
    [InlineData(true, "gemini-2.5-flash", LlmReasoningLevel.ExtraHigh, null, 24576)]
    [InlineData(true, "gemini-2.5-pro", LlmReasoningLevel.None, null, 1024)]
    [InlineData(true, "gemini-unfamiliar", LlmReasoningLevel.Maximum, "HIGH", null)]
    [InlineData(true, "gemma-4-26b-a4b-it", LlmReasoningLevel.Low, "MINIMAL", null)]
    [InlineData(true, "future-text", LlmReasoningLevel.Maximum, "HIGH", null)]
    public async Task DesiredEffortReachesGenerationInTheModelsNativeForm(
        bool vertex, string model, LlmReasoningLevel effort, string? word, int? budget)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        ILlmClient client = Client(http, vertex);
        LlmConnection connection = Connection(vertex);
        var request = new LlmChatRequest
        { ModelId = vertex ? "google/" + model : model, Messages = [new("user", "answer")], ReasoningLevel = effort, MaxOutputTokens = 123 };

        LlmChatResult? result = null;
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request))
            if (item is LlmChatCompletionEvent completion) result = completion.Result;

        using var sent = JsonDocument.Parse(handler.Body!);
        JsonElement generation = sent.RootElement.GetProperty("generationConfig");
        JsonElement thinking = generation.GetProperty("thinkingConfig");
        Assert.Equal(word is not null, thinking.TryGetProperty("thinkingLevel", out JsonElement level));
        if (word is not null) Assert.Equal(word, level.GetString());
        Assert.Equal(budget is not null, thinking.TryGetProperty("thinkingBudget", out JsonElement tokens));
        if (budget is not null) Assert.Equal(budget, tokens.GetInt32());
        Assert.Equal(budget != 0, thinking.TryGetProperty("includeThoughts", out JsonElement include) && include.GetBoolean());
        Assert.Equal(123, generation.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal(effort, request.ReasoningLevel);
        Assert.Equal("OK", result!.Text);
        Assert.Equal(1, handler.Calls);
        if (model == "gemini-unfamiliar") Assert.Equal(InferenceSupport.Unknown, client.GetCapabilities(connection, request.ModelId).SupportsReasoning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CountingAndGenerationUseTheSameEffectiveNativeChoices(bool vertex)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        ILlmClient client = Client(http, vertex);
        LlmConnection connection = Connection(vertex) with
        { CapabilityOverrides = Connection(vertex).CapabilityOverrides! with { ReasoningLevels = LlmReasoningEffort.Levels(LlmReasoningLevel.Low) } };
        var request = new LlmChatRequest
        { ModelId = vertex ? "google/gemini-unfamiliar" : "gemini-unfamiliar", Messages = [new("user", "answer")], ReasoningLevel = LlmReasoningLevel.Maximum };

        await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { }
        using var generation = JsonDocument.Parse(handler.Body!);
        JsonElement expected = generation.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig");
        Assert.Equal("LOW", expected.GetProperty("thinkingLevel").GetString());

        _ = await ((ILlmProviderTokenCounter)client).CountInputTokensAsync(connection, request);
        using var counting = JsonDocument.Parse(handler.Body!);
        JsonElement counted = vertex ? counting.RootElement : counting.RootElement.GetProperty("generateContentRequest");
        Assert.Equal(expected.GetRawText(), counted.GetProperty("generationConfig").GetProperty("thinkingConfig").GetRawText());
        Assert.Equal(2, handler.Calls);
        Assert.Equal(LlmReasoningLevel.Maximum, request.ReasoningLevel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCatalogThinkingDoesNotBecomeAnExplicitNoThinkingFact(bool negative)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new GeminiApiLlmClient(http);
        LlmConnection connection = Connection(false) with
        {
            Catalog = [new("gemini-unfamiliar", "Model")
            { CatalogEntry = JsonElement.Parse(negative ? """{"thinking":false}""" : """{"name":"models/gemini-unfamiliar"}""") }],
            CapabilityOverrides = Connection(false).CapabilityOverrides! with
            { ReasoningLevels = negative ? LlmReasoningEffort.Levels(LlmReasoningLevel.Low) : null }
        };
        var request = new LlmChatRequest
        { ModelId = "gemini-unfamiliar", Messages = [new("user", "answer")], ReasoningLevel = LlmReasoningLevel.High };
        Assert.Equal(negative ? InferenceSupport.Unsupported : InferenceSupport.Unknown, client.GetCapabilities(connection, request.ModelId).SupportsReasoning);

        await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { }

        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal(!negative, sent.RootElement.GetProperty("generationConfig").TryGetProperty("thinkingConfig", out _));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullEffortRetainsTheProviderDefaultWithoutSelectingALevel(bool vertex)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        ILlmClient client = Client(http, vertex);
        var request = new LlmChatRequest
        { ModelId = vertex ? "google/gemini-2.5-pro" : "gemini-2.5-pro", Messages = [new("user", "answer")] };

        await foreach (LlmStreamEvent _ in client.StreamAsync(Connection(vertex), request)) { }

        using var sent = JsonDocument.Parse(handler.Body!);
        JsonElement thinking = sent.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig");
        Assert.False(thinking.TryGetProperty("thinkingLevel", out _));
        Assert.False(thinking.TryGetProperty("thinkingBudget", out _));
        Assert.True(thinking.GetProperty("includeThoughts").GetBoolean());
        Assert.Null(request.ReasoningLevel);
    }

    private static ILlmClient Client(HttpClient http, bool vertex) => vertex
        ? new VertexLlmClient(http, new VertexAuthenticationBoundary()) : new GeminiApiLlmClient(http);

    private static LlmConnection Connection(bool vertex) => new()
    {
        Version = "1", Transport = vertex ? LlmTransport.GoogleVertexAi : LlmTransport.GoogleGeminiApi,
        Endpoint = vertex ? "https://aiplatform.googleapis.com" : GeminiApiLlmClient.DefaultEndpoint,
        Credential = vertex ? null : new LlmCredential { ApiKey = "test-key" }.Serialize(),
        Vertex = vertex ? new() { ProjectId = "project", Location = "global", AuthenticationMode = LlmGoogleAuthenticationMode.ApplicationDefault } : null,
        CapabilityOverrides = new() { SupportsStreaming = InferenceSupport.Unsupported }
    };

    private sealed class VertexAuthenticationBoundary : IVertexAuthentication
    {
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken) =>
            Task.FromResult(new VertexAccess("test-token", "test-scope"));
    }

    private sealed class Handler(string text = "OK", string? catalog = null) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public int Calls { get; private set; }
        public Uri? Uri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            string response = request.Method == HttpMethod.Get ? catalog!
                : request.RequestUri!.AbsolutePath.EndsWith(":countTokens", StringComparison.Ordinal)
                ? """{"totalTokens":12}"""
                : $$$"""{"candidates":[{"finishReason":"STOP","content":{"role":"model","parts":[{"text":{{{JsonValue.Create(text)!.ToJsonString()}}}}]}}]}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
