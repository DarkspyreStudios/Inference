using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ProviderControlContractTests
{
    private static readonly string[] AgentCountLabels = ["Provider default", "4 agents", "16 agents"];
    [Theory]
    [InlineData(typeof(LlmInferenceRequest))]
    [InlineData(typeof(LlmChatRequest))]
    [InlineData(typeof(LlmAgentTurnRequest))]
    public void RequestsExposeOneOptionalProviderControl(Type requestType)
    {
        Type controls = RequiredType(typeof(LlmInferenceRequest).Assembly, "Darkspyre.Inference.Common.LLM.LlmProviderControls");
        PropertyInfo property = Assert.IsType<PropertyInfo>(requestType.GetProperty("ProviderControls"), exactMatch: false);
        Assert.Equal(controls, property.PropertyType);
        Assert.Null(property.GetValue(Activator.CreateInstance(requestType)));
    }

    [Fact]
    public void SavedDefaultsUseTheFixedConnectControl()
    {
        Type controls = RequiredType(typeof(LlmChatRequest).Assembly, "Darkspyre.Inference.Connect.LLM.XaiMultiAgentControls");
        PropertyInfo property = Assert.IsType<PropertyInfo>(typeof(XaiConnectionOptions).GetProperty("MultiAgent"), exactMatch: false);
        Assert.Equal(controls, property.PropertyType);
        Assert.Null(property.GetValue(new XaiConnectionOptions()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(4)]
    [InlineData(16)]
    public void ConnectMetadataPreservesTheFixedDiscriminatorAndNullCount(int? count)
    {
        Type controls = RequiredType(typeof(LlmChatRequest).Assembly, "Darkspyre.Inference.Connect.LLM.XaiMultiAgentControls");
        object value = Activator.CreateInstance(controls, [count])!;
        var request = new LlmChatRequest { ModelId = "grok-4.20-multi-agent" };
        typeof(LlmChatRequest).GetProperty("ProviderControls")!.SetValue(request, value);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(request, ConnectLlmJson.ChatRequest));
        JsonElement control = json.RootElement.GetProperty("ProviderControls");
        Assert.Equal("xai-multi-agent", control.GetProperty("type").GetString());
        if (count is null) Assert.Equal(JsonValueKind.Null, control.GetProperty("AgentCount").ValueKind);
        else Assert.Equal(count, control.GetProperty("AgentCount").GetInt32());
        var restored = JsonSerializer.Deserialize(json.RootElement.GetRawText(), ConnectLlmJson.ChatRequest)!;
        object? restoredControl = typeof(LlmChatRequest).GetProperty("ProviderControls")!.GetValue(restored);
        Assert.Equal(value, restoredControl);
        var portable = new LlmInferenceRequest { ModelId = request.ModelId, ProviderControls = (LlmProviderControls)value };
        string portableJson = JsonSerializer.Serialize(portable, ConnectLlmJson.PortableRequest);
        Assert.Equal(value, JsonSerializer.Deserialize(portableJson, ConnectLlmJson.PortableRequest)!.ProviderControls);
        PropertyInfo getter = Assert.IsType<PropertyInfo>(typeof(ConnectLlmJson).GetProperty("XaiMultiAgentControls"), exactMatch: false);
        Assert.Equal(controls, Assert.IsAssignableFrom<JsonTypeInfo>(getter.GetValue(null)).Type);
    }

    [Fact]
    public void CommonMetadataRefusesTheConnectSubtypeAndExportsItsOwnBase()
    {
        Type controls = RequiredType(typeof(LlmChatRequest).Assembly, "Darkspyre.Inference.Connect.LLM.XaiMultiAgentControls");
        var request = new LlmInferenceRequest { ModelId = "grok-4.20-multi-agent" };
        typeof(LlmInferenceRequest).GetProperty("ProviderControls")!.SetValue(request, Activator.CreateInstance(controls, [(int?)4]));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(request, InferenceJson.LlmRequest));
        PropertyInfo getter = Assert.IsType<PropertyInfo>(typeof(InferenceJson).GetProperty("LlmProviderControls"), exactMatch: false);
        Assert.Equal(controls.BaseType, Assert.IsAssignableFrom<JsonTypeInfo>(getter.GetValue(null)).Type);
    }

    [Theory]
    [InlineData(null, false, null, null)]
    [InlineData(16, false, null, "high")]
    [InlineData(16, true, null, null)]
    [InlineData(16, true, 4, "low")]
    [InlineData(4, true, 16, "high")]
    public async Task ActualDirectAndPortableWirePreserveDefaultPrecedence(int? saved, bool supplied, int? count, string? effort)
    {
        foreach (bool streaming in new[] { false, true })
        {
            using var handler = new RawSamplingGuardsTests.Handler("xai");
            using var http = new HttpClient(handler);
            var connection = Connection(streaming) with
            {
                Xai = new() { MultiAgent = saved is null ? null : new(saved) },
                CapabilityOverrides = new() { SupportsStreaming = streaming, SupportsModelListing = false }
            };
            var request = Request() with { ProviderControls = supplied ? new XaiMultiAgentControls(count) : null };
            var client = new XaiResponsesLlmClient(http);
            await Read(client.StreamAsync(connection, request));
            var source = new Source(connection);
            await using var facade = new LlmProviderService(source, [client]);
            await Read(facade.Bind("id").StreamAsync(new LlmInferenceRequest
            {
                ModelId = request.ModelId,
                Messages = [new("user", "question")],
                ProviderControls = request.ProviderControls
            }));
            Assert.Equal(2, handler.Calls);
            foreach (string body in handler.Bodies)
            {
                using JsonDocument json = JsonDocument.Parse(body);
                Assert.False(json.RootElement.TryGetProperty("agent_count", out _));
                if (effort is null) Assert.False(json.RootElement.TryGetProperty("reasoning", out _));
                else Assert.Equal(effort, json.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
                Assert.Equal(streaming, json.RootElement.GetProperty("stream").GetBoolean());
            }
            Assert.Equal(saved, connection.Xai!.MultiAgent?.AgentCount);
            Assert.Equal(supplied, request.ProviderControls is not null);
        }
    }

    public static IEnumerable<object[]> RefusalCases() => ExplicitCacheContractTests.Cases();

    [Theory]
    [MemberData(nameof(RefusalCases))]
    public async Task UnsupportedDirectAndFacadeControlsRefuseBeforeEffects(string route, int operation)
    {
        using var handler = new RawSamplingGuardsTests.Handler(route);
        using var http = new HttpClient(handler);
        ILlmClient client = RawSamplingGuardsTests.Client(http, route);
        var connection = RawSamplingGuardsTests.Connection(route, operation == 0);
        var source = new Source(connection);
        var auth = new Authenticator(connection.Transport);
        await using var facade = new LlmProviderService(source, [client], [auth]);
        var request = RawSamplingGuardsTests.Request(route) with { ProviderControls = new XaiMultiAgentControls(4) };
        await Assert.ThrowsAsync<NotSupportedException>(() => Invoke(client, connection, request, operation));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (operation == 2) await facade.CountInputTokensAsync("id", request);
            else if (operation == 3) await facade.MeasureRequestAsync("id", request);
            else await Read(facade.StreamAsync("id", request));
        });
        Assert.Equal(1, source.Reads); Assert.Equal(0, source.Saves);
        Assert.Equal(0, auth.Calls); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("grok-4.20-multi-agent", 0)]
    [InlineData("grok-4.20-multi-agent", 8)]
    [InlineData("grok-4.7", 4)]
    [InlineData("manual-model", 4)]
    public async Task UnsupportedModelOrCountRefusesBeforeRefreshAndHttp(string model, int count)
    {
        using var handler = new RawSamplingGuardsTests.Handler("xai");
        using var http = new HttpClient(handler);
        var connection = Connection(); var source = new Source(connection);
        var auth = new Authenticator(connection.Transport);
        await using var facade = new LlmProviderService(source, [new XaiResponsesLlmClient(http)], [auth]);
        await Assert.ThrowsAsync<NotSupportedException>(() => Read(facade.StreamAsync("id", Request() with
        { ModelId = model, ProviderControls = new XaiMultiAgentControls(count) })));
        Assert.Equal(0, auth.Calls); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false, "reasoning")]
    [InlineData(true, "reasoning")]
    [InlineData(false, "reasoning_effort")]
    [InlineData(true, "reasoning_effort")]
    [InlineData(false, "agent_count")]
    [InlineData(true, "agent_count")]
    public async Task RawConflictsRefuseBeforeRefreshWithoutMutatingInputs(bool onConnection, string key)
    {
        using var handler = new RawSamplingGuardsTests.Handler("xai");
        using var http = new HttpClient(handler);
        var raw = new Dictionary<string, JsonElement> { [key] = JsonElement.Parse("4") };
        var connection = Connection() with { ProviderOptions = onConnection ? raw : new Dictionary<string, JsonElement>() };
        var request = Request() with { ProviderControls = new XaiMultiAgentControls(), ProviderOptions = onConnection ? new Dictionary<string, JsonElement>() : raw };
        var auth = new Authenticator(connection.Transport);
        await using var facade = new LlmProviderService(new Source(connection), [new XaiResponsesLlmClient(http)], [auth]);
        await Assert.ThrowsAsync<ArgumentException>(() => Read(facade.StreamAsync("id", request)));
        Assert.Equal("4", raw[key].GetRawText()); Assert.Equal(0, auth.Calls); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void NullCountStillRejectsNeutralReasoningAndUnknownSubtypes()
    {
        Assert.Throws<ArgumentException>(() => ProviderControlPolicy.Resolve(Connection(), Request() with
        { ProviderControls = new XaiMultiAgentControls(), ReasoningLevel = LlmReasoningLevel.None }));
        Assert.Throws<NotSupportedException>(() => ProviderControlPolicy.Resolve(Connection(), Request() with { ProviderControls = new UnknownControls() }));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(Request() with { ProviderControls = new UnknownControls() }, ConnectLlmJson.ChatRequest));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"ModelId\":\"m\",\"ProviderControls\":{\"type\":\"unknown\"}}", ConnectLlmJson.ChatRequest));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"ModelId\":\"m\",\"ProviderControls\":{\"type\":\"xai-multi-agent\"}}", InferenceJson.LlmRequest));
        Assert.Null(JsonSerializer.Deserialize("{\"ModelId\":\"m\",\"ProviderControls\":null}", InferenceJson.LlmRequest)!.ProviderControls);
        Assert.Equal(new XaiMultiAgentControls(), JsonSerializer.Deserialize("{\"ModelId\":\"m\",\"ProviderControls\":{\"type\":\"xai-multi-agent\"}}", ConnectLlmJson.ChatRequest)!.ProviderControls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedRunFreezesEffectiveControlThroughToolIterations(bool suppressSaved)
    {
        var probe = new ManagedProbe();
        var connection = Connection() with { Xai = new() { MultiAgent = new(16) } };
        var options = new LlmAgentSessionOptions
        {
            ModelId = Request().ModelId,
            Tools = [new LlmAgentToolDefinition { Name = "echo", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }],
            ToolHandler = (_, _) => Task.FromResult(LlmToolOutput.FromText("actual tool result"))
        };
        await using var session = new ManagedAgentSession(connection, options, [new(options.ModelId, "Multi-agent")], probe, probe.Stream,
            _ => new() { SupportsAgentSessions = true, SupportsAgentToolUse = true, Agent = ManagedAgentPolicy.Capabilities });
        await Read(session.RunTurnAsync(new()
        {
            Input = [new LlmAgentTextContent("question")],
            ProviderControls = suppressSaved ? new XaiMultiAgentControls() : null
        }));
        Assert.Equal(2, probe.RequestCount);
        foreach (LlmChatRequest request in probe.Requests)
            Assert.Equal(suppressSaved ? null : (int?)16, Assert.IsType<XaiMultiAgentControls>(request.ProviderControls).AgentCount);
        Assert.Equal(16, connection.Xai!.MultiAgent!.AgentCount);
        var captured = await session.CaptureContextAsync();
        bool hasToolResult = captured.Entries.Any(entry => entry.Message.ToolResults.Count > 0);
        bool hasEffectiveEstimate = probe.Estimates.Any(request => request.ProviderControls is XaiMultiAgentControls { AgentCount: 16 } == !suppressSaved);
        Assert.True(hasToolResult);
        Assert.True(hasEffectiveEstimate);
    }

    [Fact]
    public async Task ManagedProviderDefaultDoesNotInheritLaterSettingDuringToolExecution()
    {
        var probe = new ManagedProbe();
        var current = Connection() with { Xai = new() };
        var options = new LlmAgentSessionOptions
        {
            ModelId = Request().ModelId,
            Tools = [new LlmAgentToolDefinition { Name = "echo", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }],
            ToolHandler = (_, _) =>
            {
                current = current with { Xai = new() { MultiAgent = new(16) } };
                return Task.FromResult(LlmToolOutput.FromText("actual tool result"));
            }
        };
        await using var session = new ManagedAgentSession(current, options, [new(options.ModelId, "Multi-agent")], probe,
            (request, token) => probe.Stream(ProviderControlPolicy.Resolve(current, request), token),
            _ => new() { SupportsAgentSessions = true, SupportsAgentToolUse = true, Agent = ManagedAgentPolicy.Capabilities });
        await Read(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("question")] }));
        Assert.Equal(2, probe.RequestCount);
        Assert.Equal(16, current.Xai!.MultiAgent!.AgentCount);
        foreach (LlmChatRequest request in probe.Requests)
            Assert.Null(Assert.IsType<XaiMultiAgentControls>(request.ProviderControls).AgentCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactionContinuationPreservesAdmittedControl(bool suppressSaved)
    {
        var probe = new ManagedProbe { MeasureText = true };
        var current = Connection() with { Xai = new() { MultiAgent = new(16) } };
        var options = new LlmAgentSessionOptions
        {
            ModelId = Request().ModelId,
            Context = new() { Entries = [new("old", "previous", new("user", new string('x', 90)))] },
            ExecutionOptions = new()
            {
                ContextPolicy = new() { MaxContextTokens = 100, ReservedOutputTokens = 10, SafetyMarginTokens = 0, CompactionThresholdRatio = .8 }
            },
            Tools = [new LlmAgentToolDefinition { Name = "echo", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }],
            ToolHandler = (_, _) => Task.FromResult(LlmToolOutput.FromText("actual tool result"))
        };
        await using var session = new ManagedAgentSession(current, options, [new(options.ModelId, "Multi-agent")], probe,
            (request, token) => probe.Stream(ProviderControlPolicy.Resolve(current, request), token),
            _ => new() { SupportsAgentSessions = true, SupportsAgentToolUse = true, Agent = ManagedAgentPolicy.Capabilities });
        var events = new List<LlmAgentEvent>();
        await foreach (LlmAgentEvent item in session.RunTurnAsync(new()
        {
            Input = [new LlmAgentTextContent("question")],
            ProviderControls = suppressSaved ? new XaiMultiAgentControls() : null
        })) events.Add(item);
        var pause = Assert.Single(events.OfType<LlmAgentCompactionRequiredEvent>());
        Assert.Equal(0, probe.RequestCount);
        LlmAgentContext captured = await session.CaptureContextAsync();
        current = current with { Xai = new() { MultiAgent = new(4) } };
        await Read(session.ContinueTurnAsync(new(pause.TurnId, pause.PauseId, captured.Revision,
            captured with { Entries = captured.Entries.Skip(1).ToArray() })));
        Assert.Equal(2, probe.RequestCount);
        foreach (LlmChatRequest request in probe.Requests)
            Assert.Equal(suppressSaved ? null : (int?)16, Assert.IsType<XaiMultiAgentControls>(request.ProviderControls).AgentCount);
    }

    [Fact]
    public async Task SetupOffersProfileCountsAndConditionallySavesTypedDefaults()
    {
        using var handler = new RawSamplingGuardsTests.Handler("xai");
        using var http = new HttpClient(handler);
        var target = new Target(); var client = new XaiResponsesLlmClient(http);
        await using var manager = new LlmSettingsManager(new Dictionary<LlmTransport, ILlmClient> { [client.Transport] = client }, [],
            new Dictionary<LlmTransport, ILlmSetupEnvironment>(), target);
        var saved = Connection() with { Xai = new() { MultiAgent = new(16) } };
        LlmSettings settings = await manager.OpenAsync(client.Transport, new(SetupChangeIntent.Edit, "id", saved.Version), saved, Request().ModelId);
        SetupChoiceField field = Assert.Single(settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields).OfType<SetupChoiceField>(),
            value => value.Description.Id == "xai-agent-count");
        Assert.Equal("16", field.Value);
        var page = await manager.QueryChoicesAsync(settings, "xai-agent-count", new());
        Assert.Equal(AgentCountLabels, page.Choices.Select(choice => choice.DisplayName));
        Assert.Equal(SetupEditState.Invalid, field.SetValue("8").State);
        Assert.Equal(SetupEditState.Applied, field.SetValue("4").State);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Equal(4, target.Changes!.Configuration.Connection.Xai!.MultiAgent!.AgentCount);
        Assert.Equal(saved.Version, target.Changes.Identity.ExpectedVersion);
        Assert.Equal(16, saved.Xai!.MultiAgent!.AgentCount); Assert.Equal(0, handler.Calls);
        LlmConnection restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(target.Changes.Configuration.Connection, ConnectLlmJson.Connection), ConnectLlmJson.Connection)!;
        Assert.Equal(4, restored.Xai!.MultiAgent!.AgentCount);
    }

    [Fact]
    public async Task MisplacedSavedControlsRefuseBeforeManagedDiscoveryAndNativeOpening()
    {
        using var handler = new RawSamplingGuardsTests.Handler("codex");
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        var connection = RawSamplingGuardsTests.Connection("codex", true) with { Xai = new() { MultiAgent = new(4) } };
        var source = new Source(connection);
        await using var facade = new LlmProviderService(source, [client]);
        var managed = new ManagedCodexAgentClient(client, facade);
        await Assert.ThrowsAsync<NotSupportedException>(() => managed.StartSessionAsync("id", connection, new() { ModelId = "gpt-5" }));
        Assert.Throws<NotSupportedException>(() => NativeAgentContract.ValidateConnection(
            connection, NativeAgentContract.Claude, true, "model", [], null));
        Assert.Throws<NotSupportedException>(() => NativeAgentContract.ValidateTurn(new()
        {
            Input = [new LlmAgentTextContent("question")],
            ProviderControls = new XaiMultiAgentControls()
        }));
        Assert.Equal(0, handler.Calls); Assert.Equal(0, source.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualForeignCodecAndRefusalCallerRetire(bool dispatch)
    {
        WeakReference weak = ExerciseForeign(dispatch);
        for (int attempt = 0; weak.IsAlive && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign(bool dispatch)
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(ProviderControlContractTests).Assembly.Location)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly? fixture = null;
            foreach (Assembly original in new[] { typeof(InferenceJson).Assembly, typeof(ConnectLlmJson).Assembly, typeof(ProviderControlContractTests).Assembly })
            {
                Assembly loaded = generation.LoadFromAssemblyPath(original.Location);
                Assert.NotSame(original, loaded); Assert.Equal(original.ManifestModule.ModuleVersionId, loaded.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(loaded));
                if (original == typeof(ProviderControlContractTests).Assembly) fixture = loaded;
            }
            var caller = fixture!.GetType(typeof(ProviderControlContractTests).FullName!, true)!;
            Task operation = (Task)caller.GetMethod(nameof(ForeignCaller), BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [dispatch])!;
            Assert.True(operation.Wait(TimeSpan.FromSeconds(10))); operation.GetAwaiter().GetResult();
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static async Task ForeignCaller(bool dispatch)
    {
        var generation = AssemblyLoadContext.GetLoadContext(typeof(ProviderControlContractTests).Assembly);
        Assert.True(generation!.IsCollectible);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(InferenceJson.LlmProviderControls.Type.Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(ConnectLlmJson.XaiMultiAgentControls.Type.Assembly));
        var fixture = new ProviderControlContractTests();
        fixture.ConnectMetadataPreservesTheFixedDiscriminatorAndNullCount(null);
        fixture.ConnectMetadataPreservesTheFixedDiscriminatorAndNullCount(16);
        fixture.CommonMetadataRefusesTheConnectSubtypeAndExportsItsOwnBase();
        fixture.NullCountStillRejectsNeutralReasoningAndUnknownSubtypes();
        if (dispatch) await fixture.UnsupportedDirectAndFacadeControlsRefuseBeforeEffects("gemini", 2);
        if (dispatch)
        {
            await fixture.ManagedRunFreezesEffectiveControlThroughToolIterations(true);
            await fixture.ManagedProviderDefaultDoesNotInheritLaterSettingDuringToolExecution();
            await fixture.CompactionContinuationPreservesAdmittedControl(true);
            var local = new ExplicitCacheContractTests();
            await local.ProviderControlsRefuseBeforeProcessOrEngineEntry("selfhost", false);
            await local.ProviderControlsPreserveLateResolutionCancellationAndDrain(0, true);
        }
    }

    private static LlmConnection Connection(bool streaming = true) => RawSamplingGuardsTests.Connection("xai", streaming);
    private static LlmChatRequest Request() => new() { ModelId = "grok-4.20-multi-agent", Messages = [new("user", "question")] };
    private static async Task Read<T>(IAsyncEnumerable<T> stream) { await foreach (T unused in stream) { } }
    private static async Task Invoke(ILlmClient client, LlmConnection connection, LlmChatRequest request, int operation)
    {
        if (operation == 2) await ((ILlmProviderTokenCounter)client).CountInputTokensAsync(connection, request);
        else if (operation == 3) ((ILlmContextEstimator)client).EstimateInputTokens(connection, request);
        else await Read(client.StreamAsync(connection, request));
    }
    private sealed record UnknownControls : LlmProviderControls;
    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        internal int Reads { get; private set; }
        internal int Saves { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult<LlmConnection?>(connection); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        { Saves++; throw new InvalidOperationException("No credential save during provider-control refusal."); }
    }
    private sealed class Authenticator(LlmTransport transport) : ILlmAuthenticator
    {
        public LlmTransport Transport => transport;
        internal int Calls { get; private set; }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("No refresh during provider-control refusal."); }
    }
    private sealed class Target : ISetupCommitTarget<LlmSetupChanges>
    {
        internal LlmSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(LlmSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "next")); }
    }
    private sealed class ManagedProbe : ILlmContextEstimator
    {
        internal bool MeasureText { get; init; }
        internal List<LlmChatRequest> Requests { get; } = [];
        internal int RequestCount => Requests.Count;
        internal List<LlmChatRequest> Estimates { get; } = [];
        public string? EstimateDiagnostic => null;
        public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request)
        { Estimates.Add(request); return new(MeasureText ? request.Messages.Sum(message => message.Content?.Length ?? 0) : 1); }
        internal async IAsyncEnumerable<LlmStreamEvent> Stream(LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request); await Task.CompletedTask;
            yield return new LlmChatCompletionEvent(new()
            {
                Text = "",
                Status = Requests.Count == 1 ? LlmCompletionStatus.RequiresAction : LlmCompletionStatus.Completed,
                ToolCalls = Requests.Count == 1 ? [new LlmToolCall("call", "echo", "{}")] : [],
                Diagnostics = new(200, null, null)
            });
        }
    }
    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (!name.Name!.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal)) return null;
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
    private static Type RequiredType(Assembly assembly, string name) => Assert.IsAssignableFrom<Type>(assembly.GetType(name));
}
