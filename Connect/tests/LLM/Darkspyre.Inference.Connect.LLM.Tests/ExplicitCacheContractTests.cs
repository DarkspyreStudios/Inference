using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ExplicitCacheContractTests
{
    private const string Selection = "{\"ResourceName\":\"cachedContents/cache\",\"ModelId\":\"model\",\"ScopeId\":\"scope\",\"ExpiresAt\":null}";

    [Fact]
    public void BothOwnedRequestsCarrySelectionIndependentlyOfAdvisoryHints()
    {
        string json = "{\"ModelId\":\"model\",\"ExplicitCache\":" + Selection + ",\"PromptCache\":{\"Scope\":0,\"Retention\":1}}";
        var common = JsonSerializer.Deserialize(json, InferenceJson.LlmRequest)!;
        var connect = JsonSerializer.Deserialize(json, ConnectLlmJson.ChatRequest)!;
        foreach (object request in new object[] { common, connect })
        {
            PropertyInfo property = Assert.IsType<PropertyInfo>(request.GetType().GetProperty("ExplicitCache"), exactMatch: false);
            Assert.Equal(typeof(LlmExplicitCacheReference), property.PropertyType);
            var reference = Assert.IsType<LlmExplicitCacheReference>(property.GetValue(request));
            Assert.Equal("cachedContents/cache", reference.ResourceName);
            Assert.Equal("model", reference.ModelId); Assert.Equal("scope", reference.ScopeId); Assert.Null(reference.ExpiresAt);
        }
        Assert.Equal((int)common.PromptCache!.Scope, (int)connect.PromptCache!.Scope);
        Assert.Equal((int)common.PromptCache.Retention, (int)connect.PromptCache.Retention);
        using JsonDocument written = JsonDocument.Parse(JsonSerializer.Serialize(connect, ConnectLlmJson.ChatRequest));
        Assert.Equal(JsonSerializer.Serialize(new LlmExplicitCacheReference("cachedContents/cache", "model", "scope"), InferenceJson.LlmExplicitCacheReference),
            written.RootElement.GetProperty("ExplicitCache").GetRawText());
        var empty = JsonSerializer.Deserialize("{\"ModelId\":\"model\",\"ExplicitCache\":null}", InferenceJson.LlmRequest)!;
        Assert.Null(empty.GetType().GetProperty("ExplicitCache")!.GetValue(empty));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"ModelId\":\"model\",\"ExplicitCache\":{}}", ConnectLlmJson.ChatRequest));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"ModelId\":\"model\",\"ExplicitCache\":{}}", InferenceJson.LlmRequest));
    }

    [Fact]
    public void CapabilitiesDefaultToUnavailableAndOverridesCannotEnableThem()
    {
        foreach (object capabilities in new object[] { new LlmCapabilities(), new LlmProviderCapabilities(), new LlmProviderCapabilities().Apply(new() { SupportsStreaming = true }) })
        {
            var property = Assert.IsType<PropertyInfo>(capabilities.GetType().GetProperty("SupportsExplicitCache"), exactMatch: false);
            Assert.Equal(typeof(bool), property.PropertyType);
            Assert.False(Assert.IsType<bool>(property.GetValue(capabilities)));
        }
        var common = JsonSerializer.Deserialize("{\"SupportsExplicitCache\":true}", InferenceJson.LlmCapabilities)!;
        Assert.True(Assert.IsType<bool>(common.GetType().GetProperty("SupportsExplicitCache")!.GetValue(common)));
    }

    public static IEnumerable<object[]> Cases() => HostedPreflightTests.Cases().Where(item => !(bool)item[2])
        .Select(item => new object[] { item[0], item[1] });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task DirectUnsupportedSelectionRefusesBeforeProviderEffects(string route, int operation)
    {
        using var handler = new RawSamplingGuardsTests.Handler(route);
        using var http = new HttpClient(handler);
        ILlmClient client = RawSamplingGuardsTests.Client(http, route);
        var request = Select(RawSamplingGuardsTests.Request(route));
        await Assert.ThrowsAsync<NotSupportedException>(() => Run(client, RawSamplingGuardsTests.Connection(route, operation == 0), request, operation));
        Assert.Equal(0, handler.Calls); Assert.Empty(handler.Bodies);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FacadeRefusesBeforeRefreshCatalogCountingAndGeneration(string route, int operation)
    {
        using var handler = new RawSamplingGuardsTests.Handler(route);
        using var http = new HttpClient(handler);
        var connection = RawSamplingGuardsTests.Connection(route, operation == 0);
        var source = new Source(connection);
        var authenticator = new Authenticator(connection.Transport);
        await using var facade = new LlmProviderService(source, [RawSamplingGuardsTests.Client(http, route)], [authenticator]);
        var request = Select(RawSamplingGuardsTests.Request(route));
        async Task RunFacade()
        {
            if (operation == 2) await facade.CountInputTokensAsync("connection", request);
            else if (operation == 3) await facade.MeasureRequestAsync("connection", request);
            else await foreach (var unused in facade.StreamAsync("connection", request)) { }
        }
        if (route == "gemini") await Assert.ThrowsAsync<ArgumentException>(RunFacade);
        else await Assert.ThrowsAsync<NotSupportedException>(RunFacade);
        Assert.Equal(1, source.Reads); Assert.Equal(0, source.Saves);
        Assert.Equal(0, authenticator.Calls); Assert.Equal(0, handler.Calls);
        Assert.Equal(connection.Credential, source.Connection.Credential);
    }

    [Fact]
    public async Task PortableProjectionDoesNotLoseExplicitSelection()
    {
        using var handler = new RawSamplingGuardsTests.Handler("responses");
        using var http = new HttpClient(handler);
        var source = new Source(RawSamplingGuardsTests.Connection("responses", true));
        await using var facade = new LlmProviderService(source, [RawSamplingGuardsTests.Client(http, "responses")]);
        var request = JsonSerializer.Deserialize("{\"ModelId\":\"gpt-4.1\",\"Messages\":[{\"Role\":\"user\",\"Content\":\"hello\"}],\"ExplicitCache\":" + Selection + "}", InferenceJson.LlmRequest)!;
        var error = await Assert.ThrowsAsync<InferenceException>(async () =>
        { await foreach (var unused in facade.Bind("connection").StreamAsync(request)) { } });
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, error.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortableCapabilityAndSelectionRespectTheRegisteredImplementation(bool supported)
    {
        var source = new Source(new() { Version = "1", Transport = LlmTransport.OpenAiResponses, Endpoint = "https://example.invalid" });
        var client = new Client(supported);
        await using var facade = new LlmProviderService(source, [client]);
        var portable = facade.Bind("connection");
        var common = JsonSerializer.Deserialize("{\"ModelId\":\"model\",\"ExplicitCache\":" + Selection + "}", InferenceJson.LlmRequest)!;
        Assert.Equal(supported, (await portable.GetCapabilitiesAsync("model")).SupportsExplicitCache);
        var capabilities = client.GetCapabilities(source.Connection);
        Assert.Equal(supported, capabilities.Apply(new() { SupportsStreaming = false }).SupportsExplicitCache);
        if (supported)
        {
            await foreach (var unused in portable.StreamAsync(common)) { }
            Assert.Same(common.ExplicitCache, Assert.Single(client.Requests).ExplicitCache);
        }
        else
        {
            bool refused = false;
            try { await foreach (var unused in portable.StreamAsync(common)) { } }
            catch (InferenceException failure)
            {
                Assert.Equal(InferenceFailureCode.UnsupportedFeature, failure.Code);
                refused = true;
            }
            Assert.True(refused);
            Assert.Equal(0, client.RequestCount);
        }
    }

    [Theory]
    [InlineData("claude", false)]
    [InlineData("claude", true)]
    [InlineData("antigravity", false)]
    [InlineData("antigravity", true)]
    [InlineData("apple", false)]
    [InlineData("apple", true)]
    [InlineData("selfhost", false)]
    [InlineData("selfhost", true)]
    public async Task LocalRoutesRefuseBeforeProcessOrEngineEntry(string route, bool estimate)
        => await RefuseLocalAsync(route, estimate, cache: true);

    [Theory]
    [InlineData("claude", false)]
    [InlineData("claude", true)]
    [InlineData("antigravity", false)]
    [InlineData("antigravity", true)]
    [InlineData("apple", false)]
    [InlineData("apple", true)]
    [InlineData("selfhost", false)]
    [InlineData("selfhost", true)]
    public async Task ProviderControlsRefuseBeforeProcessOrEngineEntry(string route, bool estimate)
        => await RefuseLocalAsync(route, estimate, cache: false);

    private static async Task RefuseLocalAsync(string route, bool estimate, bool cache)
    {
        var runner = new Runner();
        var host = new Host();
        using var http = new HttpClient(new RawSamplingGuardsTests.Handler("responses"));
        ILlmClient client = route switch
        {
            "claude" => new ClaudeCliLlmClient(http, runner),
            "antigravity" => new AntigravityCliLlmClient(runner),
            "apple" => new AppleFoundationModelsClient(http, runner),
            _ => new SelfHostLlmClient(host)
        };
        var connection = new LlmConnection { Version = "1", Transport = client.Transport, Endpoint = "controlled-executable" };
        var request = new LlmChatRequest { ModelId = route == "apple" ? AppleProtocol.ModelId : "model", Messages = [new("user", "hello")] };
        request = cache ? Select(request) : request with { ProviderControls = new XaiMultiAgentControls(4) };
        Assert.False(client.GetCapabilities(connection, request.ModelId).SupportsExplicitCache);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (estimate) ((ILlmContextEstimator)client).EstimateInputTokens(connection, request);
            else await foreach (var unused in client.StreamAsync(connection, request)) { }
        });
        Assert.Equal(0, runner.Calls); Assert.Equal(0, host.Entries);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task LateResolutionPreservesCancellationAndDrainsBeforeRetirement(int operation, bool retire)
        => await LateResolutionAsync(operation, retire, cache: true);

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task ProviderControlsPreserveLateResolutionCancellationAndDrain(int operation, bool retire)
        => await LateResolutionAsync(operation, retire, cache: false);

    private static async Task LateResolutionAsync(int operation, bool retire, bool cache)
    {
        var source = new HeldSource();
        var client = new Client(false);
        var facade = new LlmProviderService(source, [client]);
        using var cancellation = new CancellationTokenSource();
        Task? run = null;
        Task? retirement = null;
        try
        {
            var request = new LlmChatRequest { ModelId = "model" };
            request = cache ? Select(request) : request with { ProviderControls = new XaiMultiAgentControls(4) };
            run = RunFacade();
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (retire) retirement = facade.DisposeAsync().AsTask();
            else cancellation.Cancel();
            Assert.False(run.IsCompleted);
            if (retirement is not null) Assert.False(retirement.IsCompleted);
            source.Release.TrySetResult();
            bool canceled = false;
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { canceled = true; }
            Assert.True(canceled);
            await (retirement ??= facade.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, client.RequestCount); Assert.False(source.Disposed);

            async Task RunFacade()
            {
                if (operation == 2) await facade.CountInputTokensAsync("connection", request, cancellation.Token);
                else if (operation == 3) await facade.MeasureRequestAsync("connection", request, cancellationToken: cancellation.Token);
                else await foreach (var unused in facade.StreamAsync("connection", request, cancellation.Token)) { }
            }
        }
        finally
        {
            retirement ??= facade.DisposeAsync().AsTask();
            source.Release.TrySetResult();
            try
            {
                if (run is not null)
                {
                    try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (OperationCanceledException) when (run.IsCanceled) { }
                }
            }
            finally { await retirement.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void ActualForeignOwnedNestedMetadataAndRefusalCallerRetire(int lane)
    {
        WeakReference weak = ExerciseForeign(lane);
        for (int attempt = 0; weak.IsAlive && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign(int lane)
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(ExplicitCacheContractTests).Assembly.Location)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly? fixture = null;
            foreach (Assembly original in new[] { typeof(InferenceJson).Assembly, typeof(ConnectLlmJson).Assembly, typeof(ExplicitCacheContractTests).Assembly })
            {
                Assembly loaded = generation.LoadFromAssemblyPath(original.Location);
                Assert.NotSame(original, loaded); Assert.Equal(original.ManifestModule.ModuleVersionId, loaded.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(loaded));
                if (original == typeof(ExplicitCacheContractTests).Assembly) fixture = loaded;
            }
            Type caller = fixture!.GetType(typeof(ExplicitCacheContractTests).FullName!, true)!;
            var operation = (Task)caller.GetMethod(nameof(ForeignCaller), BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [lane])!;
            Assert.True(operation.Wait(TimeSpan.FromSeconds(10)));
            operation.GetAwaiter().GetResult();
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static async Task ForeignCaller(int lane)
    {
        var generation = AssemblyLoadContext.GetLoadContext(typeof(ExplicitCacheContractTests).Assembly);
        Assert.True(generation!.IsCollectible);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(InferenceJson.LlmRequest.Type.Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(ConnectLlmJson.ChatRequest.Type.Assembly));
        var fixture = new ExplicitCacheContractTests();
        switch (lane)
        {
            case 0:
                fixture.BothOwnedRequestsCarrySelectionIndependentlyOfAdvisoryHints();
                fixture.CapabilitiesDefaultToUnavailableAndOverridesCannotEnableThem(); break;
            case 1: await fixture.FacadeRefusesBeforeRefreshCatalogCountingAndGeneration("gemini", 2); break;
            case 2: await fixture.PortableProjectionDoesNotLoseExplicitSelection(); break;
            case 3: await fixture.PortableCapabilityAndSelectionRespectTheRegisteredImplementation(false); break;
            case 4: await fixture.PortableCapabilityAndSelectionRespectTheRegisteredImplementation(true); break;
            case 5: await fixture.LateResolutionPreservesCancellationAndDrainsBeforeRetirement(0, true); break;
            case 6: await fixture.LateResolutionPreservesCancellationAndDrainsBeforeRetirement(2, false); break;
        }
    }

    private static LlmChatRequest Select(LlmChatRequest request)
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(request, ConnectLlmJson.ChatRequest))!;
        json["ExplicitCache"] = JsonNode.Parse(Selection);
        return JsonSerializer.Deserialize(json.ToJsonString(), ConnectLlmJson.ChatRequest)!;
    }

    private static async Task Run(ILlmClient client, LlmConnection connection, LlmChatRequest request, int operation)
    {
        if (operation == 2) await ((ILlmProviderTokenCounter)client).CountInputTokensAsync(connection, request);
        else if (operation == 3) ((ILlmContextEstimator)client).EstimateInputTokens(connection, request);
        else await foreach (var unused in client.StreamAsync(connection, request)) { }
    }

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        internal LlmConnection Connection { get; } = connection;
        internal int Reads { get; private set; }
        internal int Saves { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult<LlmConnection?>(Connection); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        { Saves++; throw new InvalidOperationException("No credential save during cache refusal."); }
    }

    private sealed class HeldSource : ILlmConnectionSource, IDisposable
    {
        internal bool Disposed { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(); await Release.Task;
            return new() { Version = "1", Transport = LlmTransport.OpenAiResponses, Endpoint = "https://example.invalid" };
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No credential save during cache refusal.");
        public void Dispose() => Disposed = true;
    }

    private sealed class Authenticator(LlmTransport transport) : ILlmAuthenticator
    {
        public LlmTransport Transport => transport;
        internal int Calls { get; private set; }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("No refresh during cache refusal."); }
    }

    private sealed class Client(bool supportsExplicitCache) : ILlmClient
    {
        internal List<LlmChatRequest> Requests { get; } = [];
        // Scalar assertions avoid external assertion caches inspecting collectible element types.
        internal int RequestCount => Requests.Count;
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        public LlmProviderDefinition ProviderDefinition => ProviderDefinitions.OpenAiApiKey;
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) =>
            new() { SupportsStreaming = true, SupportsExplicitCache = supportsExplicitCache };
        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This fixture does not offer discovery.");
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request);
            await Task.CompletedTask;
            yield return new LlmChatCompletionEvent(new() { Text = "done", Status = LlmCompletionStatus.Completed, Diagnostics = new(200, null, null) });
        }
    }

    private sealed class Runner : IClaudeCliRunner, IAntigravityCliRunner, IAppleProcessRunner
    {
        internal int Calls { get; private set; }
        public Task<ClaudeCliProcessResult> RunAsync(ClaudeCliInvocation invocation, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("No process entry during cache refusal."); }
        public Task<AntigravityCliProcessResult> RunAsync(AntigravityCliInvocation invocation, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("No process entry during cache refusal."); }
        public IAsyncEnumerable<JsonElement> RunAsync(string executable, AppleCommand command, AppleProcessLimits limits, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("No helper entry during cache refusal."); }
    }

    private sealed class Host : ILlmSelfHost
    {
        internal int Entries { get; private set; }
        public LlmCapabilities? GetCapabilities(string engineId, string? modelId) => new() { SupportsStreaming = true, SupportsExplicitCache = true };
        public Task<ILlmInference> GetInferenceAsync(string engineId, CancellationToken cancellationToken = default)
        { Entries++; throw new InvalidOperationException("Native provider cache receipts do not transfer to an engine."); }
        public Task<IReadOnlyList<LlmSelfHostEngine>> ListEnginesAsync(CancellationToken cancellationToken = default)
        { Entries++; throw new InvalidOperationException("No engine discovery during cache refusal."); }
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
}
