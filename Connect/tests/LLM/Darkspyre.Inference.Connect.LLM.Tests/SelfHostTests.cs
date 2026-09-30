using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class SelfHostTests
{
    private static readonly LlmCapabilities EngineCapabilities = new()
    {
        SupportsStreaming = true,
        SupportsReasoningText = true,
        SupportsModelListing = true,
        SupportsJsonSchema = true,
        SupportsToolUse = true,
        ReasoningLevels = new HashSet<LlmReasoningLevel> { LlmReasoningLevel.None, LlmReasoningLevel.High }
    };

    [Fact]
    public async Task PortableSelfHostBridgePreservesEngineOwnershipPolicyAndUsageDetails()
    {
        var state = new LlmContinuationState
        { ImplementationId = "engine", ModelId = "qwen", ScopeId = "engine-session", Payload = "protected-engine-state" };
        var usage = new LlmUsage(20, 8, 28) { CachedInputTokens = 5, CacheWriteInputTokens = 2, ReasoningOutputTokens = 3 };
        var engine = new Engine();
        engine.Script.Enqueue([new LlmCompletionEvent(new()
        {
            Text = "answer", Status = LlmCompletionStatus.Completed, ContinuationState = state,
            OmittedContinuationStateCount = 2, Usage = usage,
            Provenance = new() { ImplementationId = "engine", ModelId = "qwen" }
        })]);
        using ServiceProvider services = Services(engine);
        var portable = services.GetRequiredService<LlmProviderService>().Bind("connection");
        var events = new List<LlmStreamEvent>();
        await foreach (var item in portable.StreamAsync(new()
        {
            ModelId = "qwen",
            UnboundContinuationState = LlmUnboundContinuationState.Omit,
            Messages = [new("assistant", "prior") { ContinuationState = state }]
        })) { events.Add(item); }
        var sent = Assert.Single(engine.Requests);
        Assert.Same(state, Assert.Single(sent.Messages).ContinuationState);
        Assert.Equal(LlmUnboundContinuationState.Omit, sent.UnboundContinuationState);
        var result = Assert.IsType<LlmCompletionEvent>(Assert.Single(events)).Result;
        Assert.Same(state, result.ContinuationState);
        Assert.Equal(2, result.OmittedContinuationStateCount);
        Assert.Equal(usage, result.Usage);
    }

    [Fact]
    public async Task ChatStreamsTheEngineAndMapsTheRequestAndCompletion()
    {
        var engine = new Engine();
        engine.Script.Enqueue([new LlmReasoningDeltaEvent("thinking", false), new LlmTextDeltaEvent("hello"), Completion("hello")]);
        using ServiceProvider services = Services(engine);
        LlmProviderService llms = services.GetRequiredService<LlmProviderService>();

        List<LlmStreamEvent> events = [];
        await foreach (LlmStreamEvent item in llms.StreamAsync("connection", new LlmChatRequest
        {
            ModelId = "qwen",
            Instructions = "be brief",
            Messages = [new("user", "hi")],
            ReasoningLevel = LlmReasoningLevel.High
        })) { events.Add(item); }

        LlmInferenceRequest sent = Assert.Single(engine.Requests);
        Assert.Equal("qwen", sent.ModelId);
        Assert.Equal("be brief", sent.Instructions);
        Assert.Equal(LlmReasoningLevel.High, sent.ReasoningLevel);
        Assert.Equal("hi", Assert.Single(sent.Messages).Content);
        Assert.IsType<LlmReasoningDeltaEvent>(events[0]);
        Assert.Equal("hello", Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result.Text);
    }

    [Fact]
    public async Task ModelsAndCapabilitiesComeFromTheHostsLoadedEngine()
    {
        using ServiceProvider services = Services(new Engine());
        LlmProviderService llms = services.GetRequiredService<LlmProviderService>();

        LlmModel model = Assert.Single(await llms.ListModelsAsync("connection"));
        Assert.Equal("qwen", model.Id);
        Assert.Equal(262_144, model.MaxContextTokens);
        LlmProviderCapabilities capabilities = await llms.GetModelCapabilitiesAsync("connection", "qwen");
        Assert.True(capabilities.SupportsToolUse);
        Assert.True(capabilities.SupportsReasoningText);
        Assert.True((await llms.Bind("connection").GetCapabilitiesAsync("qwen")).SupportsReasoningText);
        Assert.True(capabilities.SupportsAgentToolUse);
        Assert.Contains(LlmReasoningLevel.High, capabilities.ReasoningLevels);
        // Connect runs the managed lane, so the host owns context and compaction.
        Assert.True(capabilities.Agent.SupportsCompactionHandoff);
        Assert.Equal(LlmAgentOwnership.Host, capabilities.Agent.ContextOwnership);
    }

    [Fact]
    public async Task AnEngineThatIsNotLoadedIsModelUnavailable()
    {
        using ServiceProvider services = Services(new Engine(), loaded: false);
        LlmProviderService llms = services.GetRequiredService<LlmProviderService>();

        InferenceException missing = await Assert.ThrowsAsync<InferenceException>(() => llms.ListModelsAsync("connection"));
        Assert.Equal(InferenceFailureCode.ModelUnavailable, missing.Code);
        Assert.False((await llms.GetCapabilitiesAsync("connection")).SupportsToolUse);
    }

    [Fact]
    public async Task AnAgentTurnRunsTheToolLoopOnTheEngine()
    {
        var engine = new Engine();
        LlmToolCall call = new("call-1", "get_time", "{}");
        engine.Script.Enqueue([new LlmToolCallCompletedEvent(call), Completion(string.Empty, call)]);
        engine.Script.Enqueue([new LlmTextDeltaEvent("It is noon."), Completion("It is noon.")]);
        using ServiceProvider services = Services(engine);
        LlmAgentProviderService agents = services.GetRequiredService<LlmAgentProviderService>();
        var ran = new List<string>();

        await using ILlmAgentSession session = await agents.StartSessionAsync("connection", new LlmAgentSessionOptions
        {
            ModelId = "qwen",
            Tools = [new() { Name = "get_time", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }],
            ToolHandler = (tool, _) => { ran.Add(tool.Name); return Task.FromResult(LlmToolOutput.FromText("12:00")); }
        });
        var text = new List<string>();
        await foreach (LlmAgentEvent item in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("What time is it?")] }))
        {
            if (item is LlmAgentTextDeltaEvent delta) { text.Add(delta.Delta); }
        }

        Assert.Equal(["get_time"], ran);
        Assert.Equal("It is noon.", string.Concat(text));
        LlmMessage result = engine.Requests[1].Messages.Single(message => message.ToolResults.Count > 0);
        Assert.Equal("call-1", Assert.Single(result.ToolResults).ToolCallId);
    }

    [Fact]
    public async Task SettingsChooseAnEngineAndItsModelAndSaveTheEngineAsTheEndpoint()
    {
        using ServiceProvider services = Services(new Engine());
        LlmProviderService llms = services.GetRequiredService<LlmProviderService>();
        Assert.Contains(llms.SupportedProviders, provider => provider.Transport == LlmTransport.SelfHost);
        var target = new Target();
        LlmSettingsManager manager = llms.CreateSettingsManager(target);
        LlmSettings settings = await manager.OpenAsync(LlmTransport.SelfHost, new(SetupChangeIntent.Create, "local", null));

        Assert.DoesNotContain(Fields(settings), field => field.Description.Id is "endpoint" or "api-key");
        Assert.False(settings.CanSave);
        SetupChoicePage engines = await manager.QueryChoicesAsync(settings, "engine", new());
        Assert.Equal("engine-1", Assert.Single(engines.Choices).Value);
        Field<SetupChoiceField>(settings, "engine").SetValue("engine-1");
        SetupChoicePage models = await manager.QueryChoicesAsync(settings, "model", new());
        Assert.Equal("qwen", Assert.Single(models.Choices).Value);
        Field<SetupChoiceField>(settings, "model").SetValue("qwen");

        Assert.True(settings.CanSave);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Equal("engine-1", target.Changes!.Configuration.Connection.Endpoint);
        Assert.Equal("qwen", target.Changes.Configuration.ModelId);
    }

    private static LlmCompletionEvent Completion(string text, params LlmToolCall[] calls) => new(new LlmInferenceResult
    {
        Text = text,
        Status = LlmCompletionStatus.Completed,
        ToolCalls = calls,
        Provenance = new() { ImplementationId = "engine", ModelId = "qwen" }
    });

    private static ServiceProvider Services(Engine engine, bool loaded = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConnectionSource>(new Source());
        services.AddSingleton<ILlmSelfHost>(new Host(engine, loaded));
        services.AddLlmConnect();
        services.AddLlmConnectSelfHost();
        return services.BuildServiceProvider();
    }

    private static IEnumerable<SetupField> Fields(LlmSettings settings) =>
        settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields);
    private static T Field<T>(LlmSettings settings, string id) where T : SetupField =>
        Assert.IsType<T>(Fields(settings).Single(field => field.Description.Id == id));

    private sealed class Host(Engine engine, bool loaded) : ILlmSelfHost
    {
        public Task<IReadOnlyList<LlmSelfHostEngine>> ListEnginesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmSelfHostEngine>>([new("engine-1", "Swift 27B")]);
        public Task<ILlmInference> GetInferenceAsync(string engineId, CancellationToken cancellationToken = default) =>
            loaded && engineId == "engine-1" ? Task.FromResult<ILlmInference>(engine)
                : throw new InferenceException(InferenceFailureCode.ModelUnavailable, $"Engine '{engineId}' is not loaded.");
        public LlmCapabilities? GetCapabilities(string engineId, string? modelId) =>
            loaded && engineId == "engine-1" ? EngineCapabilities : null;
    }

    private sealed class Engine : ILlmInference
    {
        public Queue<LlmStreamEvent[]> Script { get; } = new();
        public List<LlmInferenceRequest> Requests { get; } = [];
        public InferenceImplementationDescriptor Descriptor { get; } =
            new("engine", "Engine", InferenceImplementationKind.Tensor, InferenceTask.TextGeneration);
        public Task<LlmCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(EngineCapabilities);
        public Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmModelInfo>>([new("qwen", "Qwen", "Qwen", EngineCapabilities) { MaxContextTokens = 262_144 }]);
        public Task<IReadOnlyList<LlmModelInfo>> RefreshModelsAsync(CancellationToken cancellationToken = default) =>
            ListModelsAsync(cancellationToken);
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmInferenceRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            foreach (LlmStreamEvent item in Script.Dequeue()) { yield return item; }
        }
    }

    private sealed class Source : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(new() { Version = "v1", Transport = LlmTransport.SelfHost, Endpoint = "engine-1" });
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A SelfHost connection has no credential.");
    }

    private sealed class Target : ISetupCommitTarget<LlmSetupChanges>
    {
        public LlmSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(LlmSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "v2")); }
    }
}
