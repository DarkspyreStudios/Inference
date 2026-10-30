using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Embedding;
using Darkspyre.Inference.Common.LLM;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class SelfHostCatalogTests : IDisposable
{
    private readonly TestDataFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task UniversalConversationExecutesToolsAndKeepsContextAcrossTurns()
    {
        var engine = new Engine { CallTool = true };
        await using ServiceProvider provider = Services(engine);
        LlmAgentProviderService agents = provider.GetRequiredService<LlmAgentProviderService>();
        await using ILlmAgentSession session = await agents.StartSessionAsync("connection", new()
        {
            ModelId = "model",
            Tools = [new LlmAgentToolDefinition { Name = "read_fact", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }],
            ToolHandler = (_, _) => Task.FromResult(LlmToolOutput.FromText("the answer is 42"))
        });
        var events = new List<LlmAgentEvent>();
        await foreach (var item in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("Read the fact")] })) events.Add(item);
        Assert.Equal(LlmAgentTurnStatus.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Status);
        Assert.Equal(2, engine.Requests.Count);
        Assert.Contains(engine.Requests[1].Messages, message => message.ToolResults.OfType<LlmToolResult>()
            .Any(result => result.Output.Parts.OfType<LlmToolOutputText>().Any(part => part.Text == "the answer is 42")));

        await foreach (var _ in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("Remember that")] })) { }
        Assert.Contains(engine.Requests[^1].Messages, message => message.Content == "Read the fact");
        Assert.Contains(engine.Requests[^1].Messages, message => message.Content == "Remember that");
    }

    [Fact]
    public async Task UniversalNormalizerUsesLoadedEngineCapabilities()
    {
        var engine = new Engine();
        await using ServiceProvider provider = Services(engine);
        await using ILlmAgentSession session = await provider.GetRequiredService<LlmAgentProviderService>()
            .StartSessionAsync("connection", new() { ModelId = "model" });
        await foreach (var _ in session.RunTurnAsync(new()
        {
            Input = [new LlmAgentTextContent("Describe")],
            Temperature = 0.4,
            MaxOutputTokens = 16,
            ReasoningLevel = LlmReasoningLevel.High,
            Attachments = [new LlmAttachment { Kind = LlmBinaryKind.Image, FileName = "photo.png", MediaType = "image/png", Data = new byte[] { 1 } }]
        })) { }
        LlmInferenceRequest request = Assert.Single(engine.Requests);
        Assert.Null(request.Temperature);
        Assert.Null(request.MaxOutputTokens);
        Assert.Equal(LlmReasoningLevel.Low, request.ReasoningLevel);
        Assert.All(request.Messages, message => Assert.Empty(message.Attachments));
        Assert.Contains(request.Messages, message => message.Content.Contains("This content was not supplied to the model.", StringComparison.Ordinal));
    }

    /// <summary>The engine decides which continuation state it can replay, so a SelfHost request keeps state that no
    /// Connect codec binds, even when the request asks Connect to omit unbound state.</summary>
    [Fact]
    public async Task EngineReceivesContinuationStateNoCodecBinds()
    {
        var engine = new Engine();
        await using ServiceProvider provider = Services(engine);
        var state = new LlmContinuationState { ImplementationId = "engine", ModelId = "model", ScopeId = "engine-scope", Payload = "{}" };

        await provider.GetRequiredService<LlmProviderService>().CompleteAsync("connection", new()
        {
            ModelId = "model",
            UnboundContinuationState = LlmUnboundContinuationState.Omit,
            Messages = [new("user", "first"), new("assistant", "reply") { ContinuationState = state }, new("user", "second")]
        });

        Assert.Equal(state, Assert.Single(engine.Requests).Messages.Single(message => message.Role == "assistant").ContinuationState);
    }

    [Fact]
    public async Task ExplicitRefreshReplacesTheConnectCatalog()
    {
        var engine = new Engine { Models = [new("first", "First") { MaxContextTokens = 4096 }] };
        await using ServiceProvider provider = Services(engine);
        var service = provider.GetRequiredService<LlmProviderService>();
        Assert.Equal("first", Assert.Single(await service.ListModelsAsync("connection")).Id);
        engine.Models = [new("second", "Second") { MaxContextTokens = 8192 }];
        Assert.Equal("first", Assert.Single(await service.ListModelsAsync("connection")).Id);
        var refreshed = Assert.Single(await service.RefreshModelsAsync("connection"));
        Assert.Equal("second", refreshed.Id);
        Assert.Equal(8192, refreshed.MaxContextTokens);
    }

    /// <summary>A refresh during an entered listing replaces its waiters' result, not their requests.</summary>
    [Fact]
    public async Task RefreshDuringAPendingListingReturnsTheRefreshedModels()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new Engine();
        int calls = 0;
        engine.Listing = async token =>
        {
            if (Interlocked.Increment(ref calls) != 1) return [new("second", "Second")];
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return [new("first", "First")];
        };
        await using ServiceProvider provider = Services(engine);
        var service = provider.GetRequiredService<LlmProviderService>();
        Task<IReadOnlyList<LlmModel>> pending = service.ListModelsAsync("connection");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("second", Assert.Single(await service.RefreshModelsAsync("connection")).Id);
        release.SetResult();
        Assert.Equal("second", Assert.Single(await pending.WaitAsync(TimeSpan.FromSeconds(10))).Id);
        Assert.Equal(2, calls);
    }

    private ServiceProvider Services(Engine engine)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConnectionSource>(new Source());
        services.AddSingleton<ILlmSelfHost>(new Host(engine));
        services.AddSingleton<ILlmDataFolder>(_folder);
        services.AddLlmConnect();

        return services.BuildServiceProvider();
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
        public LlmCapabilities? GetCapabilities(string engineId, string? modelId) => engine.Capabilities;
    }

    private sealed class Engine : ILlmInference
    {
        public bool CallTool { get; init; }
        public Func<CancellationToken, Task<IReadOnlyList<LlmModelInfo>>>? Listing { get; set; }
        public IReadOnlyList<LlmModelInfo> Models { get; set; } = [new("model", "Model") { MaxContextTokens = 4096 }];
        public LlmCapabilities Capabilities { get; } = new()
        {
            SupportsModelListing = InferenceSupport.Supported,
            SupportsStreaming = InferenceSupport.Supported,
            SupportsToolUse = InferenceSupport.Supported,
            SupportsTemperature = InferenceSupport.Unsupported,
            SupportsPromptImages = InferenceSupport.Unsupported,
            OutputLimitEnforcement = LlmOutputLimitEnforcement.Unsupported,
            ReasoningLevels = new HashSet<LlmReasoningLevel> { LlmReasoningLevel.Low }
        };
        public List<LlmInferenceRequest> Requests { get; } = [];
        public InferenceImplementationDescriptor Descriptor { get; } = new("engine", "Engine", InferenceImplementationKind.Tensor, InferenceTask.TextGeneration);
        public Task<LlmCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default) => Task.FromResult(Capabilities);
        public Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default) => Listing?.Invoke(cancellationToken) ?? Task.FromResult(Models);
        public Task<IReadOnlyList<LlmModelInfo>> RefreshModelsAsync(CancellationToken cancellationToken = default) => ListModelsAsync(cancellationToken);
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmInferenceRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            bool tool = CallTool && Requests.Count == 1;
            return LlmInferenceExtensions.CompleteAsStreamAsync(new()
            {
                Text = tool ? "" : "engine answer",
                Status = LlmCompletionStatus.Completed,
                ToolCalls = tool ? [new LlmToolCall("call", "read_fact", "{}")] : [],
                Provenance = new() { ImplementationId = "engine", ModelId = request.ModelId }
            }, cancellationToken);
        }
    }
}
