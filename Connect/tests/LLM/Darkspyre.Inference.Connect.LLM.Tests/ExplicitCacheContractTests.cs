using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

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
        Assert.Equal(common.PromptCache, connect.PromptCache);
        using JsonDocument written = JsonDocument.Parse(JsonSerializer.Serialize(connect, ConnectLlmJson.ChatRequest));
        Assert.Equal(Selection, written.RootElement.GetProperty("ExplicitCache").GetRawText());
        var empty = JsonSerializer.Deserialize("{\"ModelId\":\"model\",\"ExplicitCache\":null}", InferenceJson.LlmRequest)!;
        Assert.Null(empty.GetType().GetProperty("ExplicitCache")!.GetValue(empty));
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize("{\"ModelId\":\"model\",\"ExplicitCache\":{}}", ConnectLlmJson.ChatRequest));
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
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (operation == 2) await facade.CountInputTokensAsync("connection", request);
            else if (operation == 3) await facade.MeasureRequestAsync("connection", request);
            else await foreach (var unused in facade.StreamAsync("connection", request)) { }
        });
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

    [Fact]
    public void ActualForeignOwnedNestedMetadataAndRefusalCallerRetire()
    {
        WeakReference weak = ExerciseForeign();
        for (int attempt = 0; weak.IsAlive && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign()
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
            ((Task)caller.GetMethod(nameof(ForeignCaller), BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!)
                .WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static async Task ForeignCaller()
    {
        var generation = AssemblyLoadContext.GetLoadContext(typeof(ExplicitCacheContractTests).Assembly);
        Assert.True(generation!.IsCollectible);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(InferenceJson.LlmRequest.Type.Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(ConnectLlmJson.ChatRequest.Type.Assembly));
        var fixture = new ExplicitCacheContractTests();
        fixture.BothOwnedRequestsCarrySelectionIndependentlyOfAdvisoryHints();
        fixture.CapabilitiesDefaultToUnavailableAndOverridesCannotEnableThem();
        await fixture.FacadeRefusesBeforeRefreshCatalogCountingAndGeneration("gemini", 2);
        await fixture.PortableProjectionDoesNotLoseExplicitSelection();
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

    private sealed class Authenticator(LlmTransport transport) : ILlmAuthenticator
    {
        public LlmTransport Transport => transport;
        internal int Calls { get; private set; }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("No refresh during cache refusal."); }
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
