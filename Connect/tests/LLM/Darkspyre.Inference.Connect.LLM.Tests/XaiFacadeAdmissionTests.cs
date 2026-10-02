using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiFacadeAdmissionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    public static TheoryData<string, string, bool> InvalidRoutes
    {
        get
        {
            var data = new TheoryData<string, string, bool>();
            foreach (string route in new[] { "stream", "complete", "tokenize", "models", "refresh-models", "capabilities", "model-capabilities" })
                foreach (string kind in new[] { "api-key", "aws", "google", "azure", "headers", "malformed", "copilot" })
                    foreach (bool borrowed in new[] { false, true }) data.Add(route, kind, borrowed);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(InvalidRoutes))]
    public async Task InitialInvalidCredentialsRefuseBeforeAllEffects(string route, string kind, bool borrowed)
    {
        var source = new Source(Invalid(kind));
        var auth = new Authenticator();
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var access = new LlmConnectionAccess(source, [auth]);
        await using var service = new LlmProviderService(source, [new XaiResponsesLlmClient(http)], [auth],
            connectionAccess: borrowed ? access : null);
        Exception? error = await Record.ExceptionAsync(() => Invoke(service, route));
        Assert.Equal(0, auth.Calls);
        Assert.Equal(0, source.Writes);
        Assert.Equal(0, handler.Calls);
        Refusal(error, kind);
    }

    [Theory]
    [MemberData(nameof(InvalidRoutes))]
    public async Task RefreshedInvalidCredentialsRefuseBeforeSaveOrHttp(string route, string kind, bool borrowed)
    {
        var source = new Source(OAuth());
        var auth = new Authenticator { Result = Invalid(kind) };
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var access = new LlmConnectionAccess(source, [auth]);
        await using var service = new LlmProviderService(source, [new XaiResponsesLlmClient(http)], [auth],
            connectionAccess: borrowed ? access : null);
        Exception? error = await Record.ExceptionAsync(() => Invoke(service, route));
        Assert.Equal(1, auth.Calls);
        Assert.Equal(0, source.Writes);
        Assert.Equal(0, handler.Calls);
        Refusal(error, kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidRefreshUsesEnteredVersionAndOneResolvedSource(bool unchanged)
    {
        var source = new Source(OAuth());
        var auth = new Authenticator { Unchanged = unchanged };
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var access = new LlmConnectionAccess(source, [auth]);
        await using var service = new LlmProviderService(source, [new XaiResponsesLlmClient(http)], [auth], connectionAccess: access);
        await service.ListModelsAsync("connection");
        Assert.Equal(1, source.Reads);
        Assert.Equal(1, auth.Calls);
        Assert.Equal(unchanged ? 0 : 1, source.Writes);
        Assert.Equal(unchanged ? null : "v1", source.ExpectedVersion);
        Assert.Equal(unchanged ? "old-token" : "fresh-token", handler.Bearer);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("measure")]
    [InlineData("projection")]
    public async Task CatalogPolicyRunsOutsideFallbackBeforeCatalogCallback(string route)
    {
        var source = new Source(Invalid("azure"));
        var client = new PolicyClient();
        await using var service = new LlmProviderService(source, [client]);
        Exception? error = await Record.ExceptionAsync(() => Invoke(service, route));
        Assert.Equal(0, client.CatalogCalls);
        Assert.Equal(1, client.Validations);
        Assert.Equal(0, source.Writes);
        Refusal(error, "azure");
    }

    [Fact]
    public async Task InvalidEndpointStillRefusesBeforeCredentialRefresh()
    {
        var source = new Source(Invalid("api-key")) { Endpoint = "https://other.test/v1" };
        var auth = new Authenticator();
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var service = new LlmProviderService(source, [new XaiResponsesLlmClient(http)], [auth]);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListModelsAsync("connection"));
        Assert.Equal(0, auth.Calls);
        Assert.Equal(0, source.Writes);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DirectCapabilitiesStayCredentialFreeAndUnsupportedFacadeRoutesStayInert()
    {
        var source = new Source(null);
        var auth = new Authenticator();
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        Assert.True(client.GetCapabilities(source.Connection).SupportsModelListing);
        await using var service = new LlmProviderService(source, [client], [auth]);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.CountInputTokensAsync("connection", Request));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.GetAccountUsageAsync("connection"));
        Assert.Equal(0, auth.Calls);
        Assert.Equal(0, source.Writes);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldRefreshCancellationAndRetirementPrecedeInvalidResult(bool retire)
    {
        var source = new Source(OAuth());
        var auth = new Authenticator { Result = Invalid("azure"), Hold = true };
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var service = new LlmProviderService(source, [new XaiResponsesLlmClient(http)], [auth]);
        using var caller = new CancellationTokenSource();
        Task? operation = null;
        Task? stopping = null;
        try
        {
            operation = service.ListModelsAsync("connection", caller.Token);
            await auth.Entered.Task.WaitAsync(Bound);
            stopping = retire ? service.DisposeAsync().AsTask() : caller.CancelAsync();
            Assert.False(operation.IsCompleted);
            if (retire) Assert.False(stopping.IsCompleted);
            auth.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(Bound));
            await stopping.WaitAsync(Bound);
            Assert.Equal(0, source.Writes);
            Assert.Equal(0, handler.Calls);
        }
        finally
        {
            Task disposal = service.DisposeAsync().AsTask();
            auth.Release.TrySetResult();
            if (operation is not null) await Record.ExceptionAsync(() => operation.WaitAsync(Bound));
            if (stopping is not null) await Record.ExceptionAsync(() => stopping.WaitAsync(Bound));
            await disposal.WaitAsync(Bound);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReentrantSelectedPolicyFencesBeforeRefreshAndCatalog(bool catalog)
    {
        var source = new Source(OAuth());
        var auth = new Authenticator();
        var client = new PolicyClient();
        await using var service = new LlmProviderService(source, [client], [auth]);
        Task? stopping = null;
        client.AfterValidation = () => stopping = service.DisposeAsync().AsTask();
        Exception? error = await Record.ExceptionAsync(() => Invoke(service, catalog ? "projection" : "models"));
        Assert.Equal(0, auth.Calls);
        Assert.Equal(0, source.Writes);
        Assert.Equal(0, client.CatalogCalls);
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        if (stopping is not null) await stopping.WaitAsync(Bound);
    }

    [Fact]
    public void RetiredRetainedFacadeDropsActualForeignSelectedPolicyAndBorrowedAccess()
    {
        (WeakReference generation, WeakReference client, object retained) = ExerciseForeign();
        try
        {
            for (int i = 0; i < 30 && (generation.IsAlive || client.IsAlive); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Sleep(10);
            }
            Assert.False(client.IsAlive);
            Assert.False(generation.IsAlive);
        }
        finally { GC.KeepAlive(retained); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference, WeakReference, object) ExerciseForeign()
    {
        var generation = new PolicyGeneration();
        try
        {
            Assembly assembly = generation.LoadFromAssemblyPath(typeof(XaiFacadeAdmissionTests).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(assembly));
            Assert.Equal(typeof(XaiFacadeAdmissionTests).Assembly.ManifestModule.ModuleVersionId, assembly.ManifestModule.ModuleVersionId);
            var client = (ILlmClient)Activator.CreateInstance(assembly.GetType(typeof(PolicyClient).FullName!, true)!)!;
            Assert.IsAssignableFrom<ILlmCredentialAdmissionPolicy>(client);
            var source = new Source(OAuth());
            var access = new LlmConnectionAccess(source, []);
            var service = new LlmProviderService(source, [client], [], connectionAccess: access);
            service.GetCapabilitiesAsync("connection").GetAwaiter().GetResult();
            service.Dispose();
            // The access remains caller-owned and usable until its own explicit retirement.
            Assert.Equal("v1", access.AcquireAsync("connection").GetAwaiter().GetResult().Version);
            access.Dispose();
            return (new(generation), new(client), new object[] { service, access });
        }
        finally { generation.Unload(); }
    }

    private static LlmChatRequest Request => new() { ModelId = "grok-4", Messages = [new("user", "hello")] };
    private static async Task Invoke(LlmProviderService service, string route)
    {
        switch (route)
        {
            case "stream": await foreach (LlmStreamEvent _ in service.StreamAsync("connection", Request)) { } break;
            case "complete": await service.CompleteAsync("connection", Request); break;
            case "tokenize": await service.TokenizeAsync("connection", "grok-4", "hello"); break;
            case "models": await service.ListModelsAsync("connection"); break;
            case "refresh-models": await service.RefreshModelsAsync("connection"); break;
            case "capabilities": await service.GetCapabilitiesAsync("connection"); break;
            case "model-capabilities": await service.GetModelCapabilitiesAsync("connection", "grok-4"); break;
            case "measure": await service.MeasureRequestAsync("connection", Request); break;
            case "projection": await service.ProjectToolOutputAsync("connection", "grok-4", LlmToolOutput.FromText("result")); break;
            default: throw new ArgumentOutOfRangeException(nameof(route));
        }
    }

    private static LlmCredential OAuth() => new() { OAuth = new() { AccessToken = "old-token" } };
    private static LlmCredential Invalid(string kind) => kind switch
    {
        "api-key" => OAuth() with { ApiKey = "secret" },
        "aws" => OAuth() with { Aws = new() { SecretAccessKey = "secret" } },
        "google" => OAuth() with { Google = new() { ServiceAccountJson = "secret" } },
        "azure" => OAuth() with { Azure = new LlmAzureSecret.ClientSecret("secret") },
        "headers" => OAuth() with { SecretHeaders = new Dictionary<string, string> { ["X-Key"] = "secret" } },
        "malformed" => OAuth() with { OAuth = new() { AccessToken = "bad token" } },
        "copilot" => OAuth() with { Copilot = new() { AccessToken = "secret" } },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private static void Refusal(Exception? error, string kind)
    {
        if (kind == "copilot") Assert.IsType<NotSupportedException>(error);
        else Assert.Equal(InferenceFailureCode.AuthenticationRequired, Assert.IsType<InferenceException>(error).Code);
    }

    private sealed class Source(LlmCredential? credential) : ILlmConnectionSource
    {
        public string Endpoint { get; init; } = "https://api.x.ai/v1";
        public LlmConnection Connection => new() { Transport = LlmTransport.XaiResponses, Endpoint = Endpoint, Version = "v1", Credential = credential?.Serialize() };
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public string? ExpectedVersion { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult<LlmConnection?>(Connection); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        { Writes++; ExpectedVersion = expectedVersion; return Task.CompletedTask; }
    }

    private sealed class Authenticator : ILlmAuthenticator
    {
        public LlmTransport Transport => LlmTransport.XaiResponses;
        public LlmCredential Result { get; init; } = new() { OAuth = new() { AccessToken = "fresh-token" } };
        public bool Unchanged { get; init; }
        public bool Hold { get; init; }
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { Calls++; Entered.TrySetResult(); if (Hold) await Release.Task; return Unchanged ? credential : Result; }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Bearer { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Bearer = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"data\":[],\"models\":[]}", Encoding.UTF8, "application/json") });
        }
    }

    private sealed class PolicyGeneration() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => Default.Assemblies.FirstOrDefault(a => AssemblyName.ReferenceMatchesDefinition(name, a.GetName()));
    }

    public sealed class PolicyClient : ILlmClient, ILlmCatalogCapabilities, ILlmCredentialAdmissionPolicy
    {
        public LlmTransport Transport => LlmTransport.XaiResponses;
        public LlmProviderDefinition ProviderDefinition => ProviderDefinitions.GrokSubscription;
        public int CatalogCalls { get; private set; }
        public int Validations { get; private set; }
        public Action? AfterValidation { get; set; }
        public void ValidateCredential(LlmConnection connection)
        { Validations++; _ = XaiAuthorization.Require(connection); AfterValidation?.Invoke(); }
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new();
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
        { CatalogCalls++; return Task.FromResult<IReadOnlyList<LlmModel>>([]); }
    }
}
