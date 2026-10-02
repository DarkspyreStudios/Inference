using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class VertexCachedCountPreflightTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualDirectCounterRefusesBeforeAuthenticationOrHttp(bool claude)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var authentication = new Authentication();
        var client = new VertexLlmClient(http, authentication);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.CountInputTokensAsync(Connection(false), Request(claude, true)));
        Assert.Equal(0, authentication.Calls); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualFacadeCounterRefusesBeforeRefreshAuthenticationOrHttp(bool claude)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var authentication = new Authentication(); var refresh = new Refresher();
        var source = new Source(Connection(false));
        var facade = new LlmProviderService(source, [new VertexLlmClient(http, authentication)], [refresh]);
        try
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => facade.CountInputTokensAsync("connection", Request(claude, true)));
            Assert.Equal(1, source.Reads); Assert.Equal(0, source.Saves);
            Assert.Equal(0, refresh.Calls); Assert.Equal(0, authentication.Calls); Assert.Equal(0, handler.Calls);
        }
        finally { await facade.DisposeAsync(); }
        Assert.Equal(0, source.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerationCacheSupportDoesNotPermitExactCachedVertexCount(bool claude)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var source = new Source(Connection()); var refresh = new Refresher();
        var client = new Client(http, LlmTransport.GoogleVertexAi);
        var facade = new LlmProviderService(source, [client], [refresh]);
        try
        {
            Exception? error = await Record.ExceptionAsync(() => facade.CountInputTokensAsync("connection", Request(claude, true)));
            Assert.Equal(1, source.Reads); Assert.Equal(0, refresh.Calls); Assert.Equal(0, source.Saves);
            Assert.Equal(0, client.Preparations); Assert.Equal(0, client.Catalogs); Assert.Equal(0, client.Counts);
            Assert.Equal(0, handler.Calls);
            Assert.IsType<NotSupportedException>(error);
        }
        finally { await facade.DisposeAsync(); }
        Assert.Equal(0, source.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncachedVertexCountStillTraversesRealFacadeBoundaries(bool claude)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var source = new Source(Connection()); var refresh = new Refresher(); var client = new Client(http, LlmTransport.GoogleVertexAi);
        await using var facade = new LlmProviderService(source, [client], [refresh]);
        LlmProviderInputTokenCount result = await facade.CountInputTokensAsync("connection", Request(claude, false));
        Assert.Equal(7, result.InputTokens); Assert.Null(result.CachedInputTokens);
        Assert.Equal(1, source.Reads); Assert.Equal(1, refresh.Calls); Assert.Equal(1, source.Saves);
        Assert.Equal(1, client.Preparations); Assert.Equal(1, client.Catalogs); Assert.Equal(1, client.Counts); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupportedCachedGenerationStillReportsActualUsage(bool claude)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var source = new Source(Connection()); var client = new Client(http, LlmTransport.GoogleVertexAi);
        await using var facade = new LlmProviderService(source, [client], [new Refresher()]);
        LlmChatRequest request = Request(claude, true);
        LlmChatResult result = await facade.CompleteAsync("connection", request);
        Assert.Equal("done", result.Text); Assert.Equal(7, result.Usage!.InputTokens); Assert.Equal(5, result.Usage.CachedInputTokens);
        Assert.Same(request.ExplicitCache, client.LastRequest!.ExplicitCache);
        Assert.Equal(1, client.Generations); Assert.Equal(0, client.Counts); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedAccountingStaysUnknownWithoutCachedPrefixAndDoesNotRefresh(bool claude)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var source = new Source(Connection()); var refresh = new Refresher(); var client = new Client(http, LlmTransport.GoogleVertexAi);
        await using var facade = new LlmProviderService(source, [client], [refresh]);
        LlmChatRequest request = Request(claude, true);
        LlmRequestAccounting result = await facade.MeasureRequestAsync("connection", request);
        Assert.Null(result.UsedContextTokens); Assert.Equal(1, client.Estimates);
        Assert.Same(request.ExplicitCache, client.LastRequest!.ExplicitCache);
        Assert.Equal(1, source.Reads); Assert.Equal(0, refresh.Calls); Assert.Equal(0, source.Saves);
        Assert.Equal(0, client.Preparations); Assert.Equal(0, client.Counts); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(LlmTransport.OpenAiResponses)]
    [InlineData(LlmTransport.GoogleGeminiApi)]
    public async Task OtherCacheCapableTransportsAreNotGloballySuppressed(LlmTransport transport)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var source = new Source(Connection() with { Transport = transport, Credential = null, Vertex = null });
        var client = new Client(http, transport);
        await using var facade = new LlmProviderService(source, [client]);
        Assert.Equal(7, (await facade.CountInputTokensAsync("connection", Request(false, true))).InputTokens);
        Assert.Equal(1, client.Counts); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AlreadyCancelledRequestDoesNotResolveOrChangeExceptionSemantics()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var source = new Source(Connection());
        await using var facade = new LlmProviderService(source, [new Client(http, LlmTransport.GoogleVertexAi)]);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => facade.CountInputTokensAsync("connection", Request(false, true), cancellation.Token));
        Assert.Equal(0, source.Reads); Assert.Equal(0, handler.Calls);
    }

    public static async Task<int> ExerciseActualForeignPathsAsync()
    {
        var test = new VertexCachedCountPreflightTests();
        foreach (bool claude in new[] { false, true })
        {
            await test.ActualDirectCounterRefusesBeforeAuthenticationOrHttp(claude);
            await test.ActualFacadeCounterRefusesBeforeRefreshAuthenticationOrHttp(claude);
            await test.GenerationCacheSupportDoesNotPermitExactCachedVertexCount(claude);
            await test.UncachedVertexCountStillTraversesRealFacadeBoundaries(claude);
            await test.SupportedCachedGenerationStillReportsActualUsage(claude);
            await test.CachedAccountingStaysUnknownWithoutCachedPrefixAndDoesNotRefresh(claude);
        }
        await test.OtherCacheCapableTransportsAreNotGloballySuppressed(LlmTransport.OpenAiResponses);
        await test.OtherCacheCapableTransportsAreNotGloballySuppressed(LlmTransport.GoogleGeminiApi);
        await test.AlreadyCancelledRequestDoesNotResolveOrChangeExceptionSemantics();
        return 15;
    }

    [Fact]
    public void ActualForeignCounterFacadeAndOwnedMetadataExecuteAndRetire()
    {
        WeakReference[] roots = ExerciseForeign();
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        foreach (WeakReference root in roots) Assert.False(root.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeign()
    {
        var generation = new Generation(); var roots = new List<WeakReference> { new(generation) };
        try
        {
            Assembly? core = null; Assembly? fixture = null;
            foreach (Assembly source in new[] { typeof(InferenceJson).Assembly, typeof(ConnectLlmJson).Assembly,
                typeof(VertexLlmClient).Assembly, typeof(VertexCachedCountPreflightTests).Assembly })
            {
                Assembly actual = generation.LoadFromAssemblyPath(source.Location);
                Assert.NotSame(source, actual); Assert.Equal(source.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual)); roots.Add(new(actual));
                if (source == typeof(ConnectLlmJson).Assembly) core = actual;
                if (source == typeof(VertexCachedCountPreflightTests).Assembly) fixture = actual;
            }
            Type facade = core!.GetType(typeof(ConnectLlmJson).FullName!, true)!;
            PropertyInfo getter = facade.GetProperty(nameof(ConnectLlmJson.ChatRequest))!;
            Assert.Same(core, getter.GetMethod!.Module.Assembly);
            object metadata = getter.GetValue(null)!; roots.Add(new(facade)); roots.Add(new(metadata));
            Type proofType = fixture!.GetType(typeof(VertexCachedCountPreflightTests).FullName!, true)!;
            MethodInfo proof = proofType.GetMethod(nameof(ExerciseActualForeignPathsAsync))!;
            Assert.Same(fixture, proof.Module.Assembly);
            var task = (Task<int>)proof.Invoke(null, null)!;
            Assert.Equal(15, task.GetAwaiter().GetResult());
            roots.Add(new(proofType)); roots.Add(new(proof)); roots.Add(new(task));
        }
        finally { generation.Unload(); }
        return roots.ToArray();
    }

    private static LlmConnection Connection(bool refreshable = true) => new()
    {
        Transport = LlmTransport.GoogleVertexAi, Version = "1", Endpoint = "https://aiplatform.googleapis.com",
        Vertex = new() { ProjectId = "project", Location = "global" },
        Credential = refreshable ? new LlmCredential { OAuth = new() { AccessToken = "controlled-old" } }.Serialize() : null
    };

    private static LlmChatRequest Request(bool claude, bool cache)
    {
        string model = claude ? "anthropic/claude-sonnet-4-6" : "google/gemini-2.5-flash";
        return new() { ModelId = model, Messages = [new("user", "suffix")],
            ExplicitCache = cache ? new("cachedContents/test", model, "scope") : null };
    }

    private sealed class Authentication : IVertexAuthentication
    {
        internal int Calls { get; private set; }
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("No authentication during cached count refusal."); }
    }

    private sealed class Refresher : ILlmAuthenticator
    {
        public LlmTransport Transport => LlmTransport.GoogleVertexAi;
        internal int Calls { get; private set; }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(credential with { OAuth = credential.OAuth! with { AccessToken = "controlled-new" } }); }
    }

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource, IDisposable
    {
        internal int Reads { get; private set; }
        internal int Saves { get; private set; }
        internal int DisposeCalls { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult<LlmConnection?>(connection); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        { Saves++; Assert.Equal(connection.Version, expectedVersion); return Task.CompletedTask; }
        public void Dispose() => DisposeCalls++;
    }

    private sealed class Client(HttpClient http, LlmTransport transport) : ILlmClient, ILlmProviderTokenCounter,
        ILlmConnectionPreparation, ILlmCatalogCapabilities, ILlmContextEstimator
    {
        public LlmTransport Transport => transport;
        public LlmProviderDefinition ProviderDefinition => transport switch
        {
            LlmTransport.GoogleVertexAi => VertexProviderDefinition.Value,
            LlmTransport.OpenAiResponses => ProviderDefinitions.OpenAiApiKey,
            _ => ProviderDefinitions.GeminiApi
        };
        internal int Preparations { get; private set; }
        internal int Catalogs { get; private set; }
        internal int Counts { get; private set; }
        internal int Generations { get; private set; }
        internal int Estimates { get; private set; }
        internal LlmChatRequest? LastRequest { get; private set; }
        public string? EstimateDiagnostic => null;
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) =>
            new() { SupportsStreaming = true, SupportsExplicitCache = true, SupportsModelListing = true };
        public Task<LlmConnection> PrepareAsync(LlmConnection connection, CancellationToken cancellationToken)
        { Preparations++; return Task.FromResult(connection); }
        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
        { Catalogs++; return Task.FromResult<IReadOnlyList<LlmModel>>([]); }
        public async Task<LlmProviderInputTokenCount> CountInputTokensAsync(LlmConnection connection, LlmChatRequest request, CancellationToken cancellationToken = default)
        {
            Counts++;
            using var message = new HttpRequestMessage(HttpMethod.Post, "https://controlled.invalid/count");
            return await ProviderTokenCounting.SendAsync(http, message, Transport, request.ModelId, false, cancellationToken);
        }
        public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request)
        { Estimates++; LastRequest = request; return null; }
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Generations++; LastRequest = request; await Task.CompletedTask;
            yield return new LlmChatCompletionEvent(new() { Text = "done", Status = LlmCompletionStatus.Completed,
                Usage = new(7, 2, 9) { CachedInputTokens = 5 }, Diagnostics = new(200, null, null) });
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"totalTokens\":7}") }); }
    }

    private sealed class Generation() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()))
            ?? Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()));
    }
}
