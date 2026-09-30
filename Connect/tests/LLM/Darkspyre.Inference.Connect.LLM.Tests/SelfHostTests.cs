using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Providers;
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
        Assert.False((await llms.GetCapabilitiesAsync("connection")).SupportsStreamingUsage);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task StreamingAndUsageCapabilitiesMirrorTheEngine(bool streaming, bool finalUsage, bool usageDeltas)
    {
        var engine = new Engine
        {
            Capabilities = EngineCapabilities with
            {
                SupportsStreaming = streaming,
                SupportsStreamingUsage = finalUsage,
                SupportsUsageDeltas = usageDeltas
            }
        };
        using ServiceProvider services = Services(engine);
        LlmProviderService llms = services.GetRequiredService<LlmProviderService>();
        LlmProviderCapabilities capabilities = await llms.GetModelCapabilitiesAsync("connection", "qwen");
        LlmCapabilities portable = await llms.Bind("connection").GetCapabilitiesAsync("qwen");
        LlmProviderCapabilities listed = Assert.Single(await llms.ListModelsAsync("connection")).Capabilities!;
        Assert.Equal(streaming, capabilities.SupportsStreaming);
        Assert.Equal(finalUsage, capabilities.SupportsStreamingUsage);
        Assert.Equal(usageDeltas, capabilities.SupportsUsageDeltas);
        Assert.Equal(streaming, portable.SupportsStreaming);
        Assert.Equal(finalUsage, portable.SupportsStreamingUsage);
        Assert.Equal(usageDeltas, portable.SupportsUsageDeltas);
        Assert.Equal(streaming, listed.SupportsStreaming);
        Assert.Equal(finalUsage, listed.SupportsStreamingUsage);
        Assert.Equal(usageDeltas, listed.SupportsUsageDeltas);
    }

    [Fact]
    public async Task RequestProviderOptionsAreRejectedBeforeResolvingTheEngine()
    {
        var engine = new Engine();
        using ServiceProvider services = Services(engine);
        LlmProviderService llms = services.GetRequiredService<LlmProviderService>();
        var request = new LlmChatRequest
        {
            ModelId = "qwen",
            ProviderOptions = new Dictionary<string, JsonElement> { ["private-option"] = JsonSerializer.SerializeToElement("private-value") }
        };
        NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(() => llms.CompleteAsync("connection", request));
        Assert.Contains("ProviderOptions", error.Message);
        Assert.DoesNotContain("private-value", error.Message);
        LlmRequestAccounting accounting = await llms.MeasureRequestAsync("connection", request);
        Assert.False(accounting.IsSupported);
        Assert.Null(accounting.UsedContextTokens);
        Assert.Equal("This route cannot project the requested features for accounting.", accounting.Diagnostic);
        Assert.Equal(0, ((Host)services.GetRequiredService<ILlmSelfHost>()).ResolutionCount);
        Assert.Empty(engine.Requests);
    }

    [Theory]
    [InlineData("provider-options")]
    [InlineData("headers")]
    [InlineData("capabilities")]
    [InlineData("empty-capabilities")]
    [InlineData("model-capabilities")]
    public async Task InvalidConnectionOptionsFailGenerationAndAccountingButRemainDiscoverable(string kind)
    {
        var engine = new Engine();
        using ServiceProvider services = Services(engine, connection: UnsupportedConnection(kind));
        LlmProviderService llms = services.GetRequiredService<LlmProviderService>();
        await Assert.ThrowsAsync<NotSupportedException>(() => llms.CompleteAsync("connection", new() { ModelId = "qwen" }));
        InferenceException portable = await Assert.ThrowsAsync<InferenceException>(async () =>
        {
            await foreach (LlmStreamEvent _ in llms.Bind("connection").StreamAsync(new() { ModelId = "qwen" })) { }
        });
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, portable.Code);
        LlmRequestAccounting accounting = await llms.MeasureRequestAsync("connection", new() { ModelId = "qwen" });
        Assert.False(accounting.IsSupported);
        Assert.Null(accounting.UsedContextTokens);
        Assert.Null(accounting.NeedsCompaction);
        Assert.Equal(0, ((Host)services.GetRequiredService<ILlmSelfHost>()).ResolutionCount);
        Assert.Empty(engine.Requests);
        Assert.True((await llms.GetModelCapabilitiesAsync("connection", "qwen")).SupportsToolUse);
        Assert.Equal("qwen", Assert.Single(await llms.ListModelsAsync("connection")).Id);
    }

    [Theory]
    [InlineData("provider-options", "self-host-provider-options")]
    [InlineData("headers", "self-host-headers")]
    [InlineData("capabilities", "self-host-capability-overrides")]
    [InlineData("empty-capabilities", "self-host-capability-overrides")]
    [InlineData("model-capabilities", "self-host-capability-overrides")]
    public async Task SettingsRejectIgnoredConnectionOptions(string kind, string issueCode)
    {
        LlmConnection connection = UnsupportedConnection(kind);
        using ServiceProvider services = Services(new Engine(), connection: connection);
        var target = new Target();
        LlmSettingsManager manager = services.GetRequiredService<LlmProviderService>().CreateSettingsManager(target);
        LlmSettings settings = await manager.OpenAsync(LlmTransport.SelfHost,
            new(SetupChangeIntent.Edit, "connection", "v1"), connection, "qwen");
        Assert.False(settings.CanSave);
        SetupCommitResult result = await manager.SaveAsync(settings);
        Assert.Equal(SetupCommitState.Invalid, result.State);
        Assert.NotNull(result.Issues);
        Assert.Contains(result.Issues, issue => issue.Code == issueCode);
        Assert.Null(target.Changes);
        Assert.Equal("engine-1", Assert.Single((await manager.QueryChoicesAsync(settings, "engine", new())).Choices).Value);
    }

    [Fact]
    public async Task ManagedTurnRejectsIgnoredOptionsBeforeResolvingTheEngine()
    {
        var engine = new Engine();
        using ServiceProvider services = Services(engine);
        await using ILlmAgentSession session = await services.GetRequiredService<LlmAgentProviderService>()
            .StartSessionAsync("connection", new() { ModelId = "qwen" });
        int resolutions = ((Host)services.GetRequiredService<ILlmSelfHost>()).ResolutionCount;
        ((Source)services.GetRequiredService<ILlmConnectionSource>()).Connection = UnsupportedConnection("provider-options") with { Version = "v2" };
        var events = new List<LlmAgentEvent>();
        await foreach (LlmAgentEvent item in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("hello")] }))
        { events.Add(item); }
        LlmAgentTurnCompletedEvent terminal = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
        Assert.Equal(LlmAgentCompletionReason.ProviderFailure, terminal.Reason);
        Assert.Empty(events.OfType<LlmAgentTextDeltaEvent>());
        Assert.Equal(resolutions, ((Host)services.GetRequiredService<ILlmSelfHost>()).ResolutionCount);
        Assert.Empty(engine.Requests);
    }

    [Fact]
    public void EstimatorRejectsUnsupportedOptionsWithoutResolvingTheEngine()
    {
        var host = new Host(new Engine(), true);
        var client = new SelfHostLlmClient(host);
        Assert.Throws<NotSupportedException>(() => client.EstimateInputTokens(UnsupportedConnection("headers"), new() { ModelId = "qwen" }));
        Assert.Equal(0, host.ResolutionCount);
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

    private static ServiceProvider Services(Engine engine, bool loaded = true, LlmConnection? connection = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConnectionSource>(new Source(connection));
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
        public int ResolutionCount { get; private set; }
        public Task<IReadOnlyList<LlmSelfHostEngine>> ListEnginesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmSelfHostEngine>>([new("engine-1", "Swift 27B")]);
        public Task<ILlmInference> GetInferenceAsync(string engineId, CancellationToken cancellationToken = default)
        {
            ResolutionCount++;
            return loaded && engineId == "engine-1" ? Task.FromResult<ILlmInference>(engine)
                : throw new InferenceException(InferenceFailureCode.ModelUnavailable, $"Engine '{engineId}' is not loaded.");
        }
        public LlmCapabilities? GetCapabilities(string engineId, string? modelId) =>
            loaded && engineId == "engine-1" ? engine.Capabilities : null;
    }

    private sealed class Engine : ILlmInference
    {
        public LlmCapabilities Capabilities { get; init; } = EngineCapabilities;
        public Queue<LlmStreamEvent[]> Script { get; } = new();
        public List<LlmInferenceRequest> Requests { get; } = [];
        public InferenceImplementationDescriptor Descriptor { get; } =
            new("engine", "Engine", InferenceImplementationKind.Tensor, InferenceTask.TextGeneration);
        public Task<LlmCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Capabilities);
        public Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmModelInfo>>([new("qwen", "Qwen", "Qwen", Capabilities) { MaxContextTokens = 262_144 }]);
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

    private static LlmConnection Connection() => new() { Version = "v1", Transport = LlmTransport.SelfHost, Endpoint = "engine-1" };

    private static LlmConnection UnsupportedConnection(string kind) => kind switch
    {
        "provider-options" => Connection() with { ProviderOptions = new Dictionary<string, JsonElement> { ["ignored"] = JsonSerializer.SerializeToElement(true) } },
        "headers" => Connection() with { AdditionalHeaders = new Dictionary<string, string> { ["X-Ignored"] = "private-value" } },
        "capabilities" => Connection() with { CapabilityOverrides = new() { SupportsToolUse = false } },
        "empty-capabilities" => Connection() with { CapabilityOverrides = new() },
        "model-capabilities" => Connection() with { ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides> { ["qwen"] = new() { SupportsToolUse = false } } },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private sealed class Source(LlmConnection? connection) : ILlmConnectionSource
    {
        public LlmConnection Connection { get; set; } = connection ?? SelfHostTests.Connection();
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(Connection);
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
