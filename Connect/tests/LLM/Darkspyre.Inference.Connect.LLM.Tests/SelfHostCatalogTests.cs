using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Embedding;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class SelfHostCatalogTests
{
    [Theory]
    [InlineData("tools", false)]
    [InlineData("tools", true)]
    [InlineData("strict", false)]
    [InlineData("strict", true)]
    [InlineData("image", false)]
    [InlineData("image", true)]
    [InlineData("schema", false)]
    [InlineData("schema", true)]
    public async Task UnsupportedContentRefusesBeforeGenerationOrAccounting(string feature, bool accounting)
    {
        var engine = new Engine
        {
            Capabilities = new()
            {
                SupportsToolUse = feature == "strict",
                Tools = new() { StrictInputSchema = LlmStrictInputSchemaSupport.Unsupported }
            }
        };
        await using var service = new LlmProviderService(new Source(), [new SelfHostLlmClient(new Host(engine))]);
        var request = new LlmChatRequest
        {
            ModelId = "model",
            Messages = [new("user", "hello")
            {
                Attachments = feature == "image"
                    ? [new LlmAttachment
                    {
                        Kind = LlmBinaryKind.Image, FileName = "image.png", MediaType = "image/png",
                        Data = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }
                    }] : []
            }],
            Tools = feature is "tools" or "strict"
                ? [new LlmToolDefinition
                {
                    Name = "echo", InputSchema = JsonElement.Parse("{\"type\":\"object\"}"),
                    Strict = feature == "strict"
                }] : [],
            StructuredOutput = feature == "schema" ? new()
            { Name = "answer", Schema = JsonElement.Parse("{\"type\":\"object\"}") } : null
        };
        if (accounting)
        {
            LlmRequestAccounting measurement = await service.MeasureRequestAsync("connection", request);
            Assert.False(measurement.IsSupported);
            Assert.Null(measurement.UsedContextTokens);
        }
        else
            await Assert.ThrowsAsync<NotSupportedException>(() => service.CompleteAsync("connection", request));
        Assert.Null(engine.LastRequest);

        LlmChatRequest admitted = request with
        {
            Messages = [new("user", "hello")],
            Tools = feature == "strict"
                ? [Assert.IsType<LlmToolDefinition>(Assert.Single(request.Tools)) with { Strict = false }] : [],
            StructuredOutput = feature == "schema"
                ? request.StructuredOutput! with { Fallback = LlmStructuredOutputFallback.LocalValidation } : null
        };
        LlmChatResult result = await service.CompleteAsync("connection", admitted);
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("engine answer", result.Text);
        if (feature == "strict")
            Assert.False(Assert.IsType<LlmToolDefinition>(Assert.Single(engine.LastRequest!.Tools)).Strict);
        if (feature == "schema")
        {
            Assert.Equal(LlmStructuredOutputFallback.LocalValidation, engine.LastRequest!.StructuredOutput!.Fallback);
            Assert.Equal(request.StructuredOutput!.Schema.GetRawText(), engine.LastRequest.StructuredOutput.Schema.GetRawText());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemperatureAdmissionUsesTheEngineSupportFlag(bool supported)
    {
        var engine = new Engine { Capabilities = new() { SupportsTemperature = supported } };
        await using var service = new LlmProviderService(new Source(), [new SelfHostLlmClient(new Host(engine))]);
        ILlmInference inference = service.Bind("connection");
        LlmRequestAccounting measurement = await service.MeasureRequestAsync("connection", new()
        { ModelId = "model", Messages = [new("user", "hello")], Temperature = 0.4 });
        Assert.Equal(supported, measurement.IsSupported);
        Assert.Null(engine.LastRequest);
        if (!supported)
        {
            Assert.Null(measurement.UsedContextTokens);
            InferenceException failure = await Assert.ThrowsAsync<InferenceException>(() => inference.CompleteAsync(new()
            { ModelId = "model", Messages = [new("user", "hello")], Temperature = 0.4 }));
            Assert.Equal(InferenceFailureCode.UnsupportedFeature, failure.Code);
            Assert.Null(engine.LastRequest);
        }
        LlmInferenceResult result = await inference.CompleteAsync(new()
        { ModelId = "model", Messages = [new("user", "hello")], Temperature = supported ? 0.4 : null });
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("engine answer", result.Text);
        Assert.Equal(supported ? 0.4 : (double?)null, engine.LastRequest!.Temperature);
    }

    [Theory]
    [InlineData(LlmOutputLimitEnforcement.Unsupported)]
    [InlineData(LlmOutputLimitEnforcement.Unknown)]
    [InlineData(LlmOutputLimitEnforcement.ProviderEnforced)]
    public async Task OutputCapAdmissionPreservesTheEngineEnforcementFact(LlmOutputLimitEnforcement enforcement)
    {
        var engine = new Engine { Capabilities = new() { OutputLimitEnforcement = enforcement } };
        await using var service = new LlmProviderService(new Source(), [new SelfHostLlmClient(new Host(engine))]);
        ILlmInference inference = service.Bind("connection");
        bool supported = enforcement != LlmOutputLimitEnforcement.Unsupported;
        LlmRequestAccounting measurement = await service.MeasureRequestAsync("connection", new()
        { ModelId = "model", Messages = [new("user", "hello")], MaxOutputTokens = 16 });
        Assert.Equal(supported, measurement.IsSupported);
        Assert.Equal(enforcement, measurement.OutputLimitEnforcement);
        Assert.Null(engine.LastRequest);
        if (!supported)
        {
            InferenceException failure = await Assert.ThrowsAsync<InferenceException>(() => inference.CompleteAsync(new()
            { ModelId = "model", Messages = [new("user", "hello")], MaxOutputTokens = 16 }));
            Assert.Equal(InferenceFailureCode.UnsupportedFeature, failure.Code);
            Assert.Null(engine.LastRequest);
        }
        LlmInferenceResult result = await inference.CompleteAsync(new()
        { ModelId = "model", Messages = [new("user", "hello")], MaxOutputTokens = supported ? 16 : null });
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("engine answer", result.Text);
        Assert.Equal(supported ? 16 : (int?)null, engine.LastRequest!.MaxOutputTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedReasoningRefusesBeforeGenerationOrAccounting(bool accounting)
    {
        var engine = new Engine
        {
            Capabilities = new() { ReasoningLevels = LlmReasoningEffort.Levels(LlmReasoningLevel.Low) }
        };
        await using var service = new LlmProviderService(new Source(), [new SelfHostLlmClient(new Host(engine))]);
        ILlmInference inference = service.Bind("connection");

        InferenceException failure = await Assert.ThrowsAsync<InferenceException>(async () =>
        {
            if (accounting)
                await service.MeasureRequestAsync("connection", new()
                { ModelId = "model", Messages = [new("user", "hello")], ReasoningLevel = LlmReasoningLevel.High });
            else
                await inference.CompleteAsync(new()
                { ModelId = "model", Messages = [new("user", "hello")], ReasoningLevel = LlmReasoningLevel.High });
        });
        Assert.Equal(InferenceFailureCode.InvalidRequest, failure.Code);
        Assert.Null(engine.LastRequest);

        LlmRequestAccounting measurement = await service.MeasureRequestAsync("connection", new()
        { ModelId = "model", Messages = [new("user", "hello")], ReasoningLevel = LlmReasoningLevel.Low });
        Assert.True(measurement.IsSupported);
        Assert.True(measurement.UsedContextTokens > 0);
        Assert.Null(engine.LastRequest);

        LlmInferenceResult result = await inference.CompleteAsync(new()
        { ModelId = "model", Messages = [new("user", "hello")], ReasoningLevel = LlmReasoningLevel.Low });
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("engine answer", result.Text);
        Assert.Equal(LlmReasoningLevel.Low, engine.LastRequest!.ReasoningLevel);
        Assert.Equal("model", engine.LastRequest.ModelId);
    }

    [Fact]
    public async Task ExplicitRefreshReplacesTheKeptEngineCatalogAndTheConnectCatalog()
    {
        var engine = new Engine { Models = [new("first", "First") { MaxContextTokens = 4096 }] };
        await using var service = new LlmProviderService(new Source(), [new SelfHostLlmClient(new Host(engine))]);
        ILlmInference inference = service.Bind("connection");

        Assert.Equal("first", Assert.Single(await inference.ListModelsAsync()).Id);
        engine.Models = [new("second", "Second") { MaxContextTokens = 8192 }, new("unknown", "Unknown")];
        Assert.Equal("first", Assert.Single(await inference.ListModelsAsync()).Id);

        IReadOnlyList<LlmModelInfo> refreshed = await inference.RefreshModelsAsync();
        Assert.Equal(["second", "unknown"], refreshed.Select(model => model.Id));
        Assert.Equal(8192, refreshed[0].MaxContextTokens);
        Assert.Null(refreshed[1].MaxContextTokens);
        Assert.Equal(["second", "unknown"], (await inference.ListModelsAsync()).Select(model => model.Id));
    }

    [Theory]
    [InlineData("image")]
    [InlineData("tool-binary")]
    [InlineData("tool-json")]
    public async Task AccountingDoesNotReportTextOnlyUsageForUnmeasuredContent(string content)
    {
        var engine = new Engine
        {
            Models = [new("model", "Model") { MaxContextTokens = 4096 }],
            Capabilities = new() { SupportsModelListing = true, SupportsPromptImages = true, SupportsToolUse = true }
        };
        await using var service = new LlmProviderService(new Source(), [new SelfHostLlmClient(new Host(engine))]);
        var message = new LlmChatMessage("user", "Read this content");
        message = content != "image" ? message with
        {
            ToolResults = [new LlmToolResult("call", content == "tool-json"
                ? new() { StructuredContent = JsonElement.Parse("{\"answer\":42}") }
                : new() { Parts = [new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", new byte[] { 1, 2, 3 }, "image.png")] })]
        } : message with
        {
            Attachments = [new LlmAttachment { Kind = LlmBinaryKind.Image, FileName = "image.png", MediaType = "image/png", Data = new byte[] { 1, 2, 3 } }]
        };
        LlmRequestAccounting result = await service.MeasureRequestAsync("connection", new()
        {
            ModelId = "model",
            Messages = content == "image" ? [message]
                : [new("assistant", string.Empty) { ToolCalls = [new LlmToolCall("call", "create", "{}")] }, message]
        });
        Assert.True(result.IsSupported);
        Assert.Null(result.UsedContextTokens);
        Assert.Null(result.AvailableInputTokens);
        Assert.Null(result.NeedsCompaction);
        Assert.Equal(4096, result.ModelMaxContextTokens);

        LlmRequestAccounting text = await service.MeasureRequestAsync("connection", new()
        { ModelId = "model", Messages = [new("user", "Only text")] });
        Assert.True(text.UsedContextTokens > 0);
    }

    private sealed class Source : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(new() { Version = "1", Transport = LlmTransport.SelfHost, Endpoint = "engine" });

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Host(Engine engine) : ILlmSelfHost
    {
        public Task<IReadOnlyList<LlmSelfHostEngine>> ListEnginesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmSelfHostEngine>>([new("engine", "Engine")]);
        public Task<ILlmInference> GetInferenceAsync(string engineId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ILlmInference>(engine);
        public Task<IEmbeddingInference> GetEmbeddingInferenceAsync(string engineId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public LlmCapabilities? GetCapabilities(string engineId, string? modelId) =>
            engine.Capabilities;
    }

    private sealed class Engine : ILlmInference
    {
        private IReadOnlyList<LlmModelInfo>? _kept;
        public IReadOnlyList<LlmModelInfo> Models { get; set; } = [];
        public LlmCapabilities Capabilities { get; init; } = new() { SupportsModelListing = true, SupportsPromptImages = true };
        public LlmInferenceRequest? LastRequest { get; private set; }
        public InferenceImplementationDescriptor Descriptor { get; } = new("engine", "Engine",
            InferenceImplementationKind.Tensor, InferenceTask.TextGeneration);
        public Task<LlmCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Capabilities);
        public Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_kept ??= Models);
        public Task<IReadOnlyList<LlmModelInfo>> RefreshModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_kept = Models);
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmInferenceRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return LlmInferenceExtensions.CompleteAsStreamAsync(new()
            {
                Text = "engine answer",
                Status = LlmCompletionStatus.Completed,
                Provenance = new() { ImplementationId = "engine", ModelId = request.ModelId }
            }, cancellationToken);
        }
    }
}
