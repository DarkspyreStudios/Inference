using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class RuntimeInputAdmissionTests
{
    public static TheoryData<string, string> FacadeCases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (string input in Inputs)
                foreach (string route in new[] { "complete", "count", "measure", "models", "refresh-models", "capabilities", "model-capabilities", "account", "observed", "authenticated", "set-key", "set-aws", "cache", "access", "authorization", "start", "resume", "fork", "tokenize", "embedding", "projection" })
                    data.Add(input, route);
            return data;
        }
    }

    private static readonly string[] Inputs = ["acp", "copilot", "launch", "copilot-token", "runtime-token"];
    private static readonly LlmChatRequest Request = new() { ModelId = "model", Messages = [new("user", "input")] };
    private static LlmConnection Connection => new()
    {
        Version = "v1", Transport = LlmTransport.OpenAiResponses, Endpoint = "https://provider.invalid/v1",
        Credential = new LlmCredential { ApiKey = "existing-key", OAuth = new() { AccessToken = "existing-token", RefreshToken = "refresh", ExpiresAtUnixSeconds = 0 } }.Serialize()
    };

    public static LlmConnection Foreign(string input, LlmConnection? connection = null)
    {
        connection ??= Connection;
        var profile = new LlmAgentRuntimeProfile { Id = "profile", DeclaredRuntimeVersion = "declared" };
        return input switch
        {
            "acp" => connection with { Acp = new() { Profile = profile, State = new LlmAgentAdoptedRuntimeState("/selected/state"), Authentication = new LlmAcpExternalAuthentication() } },
            "copilot" => connection with { Copilot = new() { Profile = profile, Authentication = new LlmCopilotRuntimeAccount() } },
            "launch" => connection with { RuntimeLaunchInputs = new() },
            "copilot-token" => connection with { Credential = ((LlmCredential.Parse(connection.Credential) ?? new()) with { Copilot = new() { AccessToken = "copilot-secret" } }).Serialize() },
            "runtime-token" => connection with { Credential = ((LlmCredential.Parse(connection.Credential) ?? new()) with { CopilotRuntime = new() { ConnectionToken = "runtime-secret" } }).Serialize() },
            _ => throw new ArgumentOutOfRangeException(nameof(input))
        };
    }

    [Theory]
    [MemberData(nameof(FacadeCases))]
    public async Task ExistingFacadeRoutesRefuseForeignInputsBeforeEffects(string input, string route)
    {
        var source = new Source(Foreign(input));
        var client = new Client();
        var auth = new Authenticator();
        await using var facade = new LlmProviderService(source, [client], [auth]);
        await using var access = new LlmConnectionAccess(source, [auth]);
        var agent = new Agent();
        await using var agents = new LlmAgentProviderService(source, [agent]);
        var embedding = new Embedding();
        await using var embeddings = new LlmEmbeddingProviderService(source, [embedding], [auth]);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            switch (route)
            {
                case "complete": await facade.CompleteAsync("id", Request); break;
                case "count": await facade.CountInputTokensAsync("id", Request); break;
                case "measure": await facade.MeasureRequestAsync("id", Request); break;
                case "models": await facade.ListModelsAsync("id"); break;
                case "refresh-models": await facade.RefreshModelsAsync("id"); break;
                case "capabilities": await facade.GetCapabilitiesAsync("id"); break;
                case "model-capabilities": await facade.GetModelCapabilitiesAsync("id", "model"); break;
                case "account": await facade.GetAccountUsageAsync("id"); break;
                case "observed": await facade.GetObservedAccountUsageAsync("id"); break;
                case "authenticated": await facade.IsAuthenticatedAsync("id"); break;
                case "set-key": await facade.SetApiKeyAsync("id", "replacement", "v1"); break;
                case "set-aws": await facade.SetBedrockCredentialsAsync("id", new(), "v1"); break;
                case "cache": await facade.BindContextCacheAsync("id"); break;
                case "access": await access.AcquireAsync("id"); break;
                case "authorization": await new LlmConnectionAuthorization("id", access).GetAsync(); break;
                case "start": await agents.StartSessionAsync("id", new() { ModelId = "model" }); break;
                case "resume": await agents.ResumeSessionAsync("id", "thread"); break;
                case "fork": await agents.ForkSessionAsync("id", "thread"); break;
                case "tokenize": await facade.TokenizeAsync("id", "model", "input"); break;
                case "embedding": await embeddings.EmbedAsync("id", new() { ModelId = "model", Inputs = ["input"] }); break;
                case "projection": await facade.ProjectToolOutputAsync("id", "model", new() { Parts = [] }); break;
                default: throw new ArgumentOutOfRangeException(nameof(route));
            }
        });
        Assert.Equal(0, client.Effects);
        Assert.Equal(0, agent.Effects);
        Assert.Equal(0, auth.Effects);
        Assert.Equal(0, embedding.Effects);
        Assert.Equal(0, source.Writes);
    }

    [Theory]
    [InlineData("acp")]
    [InlineData("copilot")]
    [InlineData("launch")]
    [InlineData("copilot-token")]
    [InlineData("runtime-token")]
    public async Task SetupSaveRefusesForeignInputsBeforeTarget(string input)
    {
        var source = new Source(Connection);
        var target = new Target();
        await using var facade = new LlmProviderService(source, [new Client()]);
        await using var manager = facade.CreateSettingsManager(target);
        LlmSettings settings = await manager.OpenAsync(Connection.Transport, new(SetupChangeIntent.Edit, "id", "v1"), Foreign(input), "model");
        Assert.Equal(SetupCommitState.Invalid, (await manager.SaveAsync(settings)).State);
        Assert.Equal(0, target.Calls);
        Assert.Equal(0, source.Writes);
    }

    [Theory]
    [InlineData("copilot-token", "copilot-secret")]
    [InlineData("runtime-token", "runtime-secret")]
    public void ProtectedRuntimeTokensAreRedactedFromUnstructuredDiagnostics(string input, string secret)
    {
        string safe = SecretRedactor.Sanitize("failure includes " + secret, LlmCredential.Parse(Foreign(input).Credential));
        Assert.DoesNotContain(secret, safe, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", safe, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("acp")]
    [InlineData("copilot")]
    [InlineData("launch")]
    [InlineData("copilot-token")]
    [InlineData("runtime-token")]
    public void DirectLegacyHttpCapabilitiesRefuseForeignInputs(string input)
    {
        using var http = new HttpClient();
        var client = new OpenAiResponsesLlmClient(http);
        Assert.Throws<NotSupportedException>(() => client.GetCapabilities(Foreign(input)));
    }

    [Theory]
    [InlineData("acp")]
    [InlineData("copilot")]
    [InlineData("launch")]
    [InlineData("copilot-token")]
    [InlineData("runtime-token")]
    public void EveryRegisteredCoreAdapterRefusesForeignInputsAtDirectBoundary(string input)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConnectionSource>(new Source(Connection));
        services.AddLlmConnect();
        using ServiceProvider container = services.BuildServiceProvider();
        ILlmClient[] clients = container.GetServices<ILlmClient>().ToArray();
        Assert.NotEmpty(clients);
        foreach (ILlmClient client in clients)
            Assert.Throws<NotSupportedException>(() => client.GetCapabilities(Foreign(input, Connection with { Transport = client.Transport })));
    }

    [Theory]
    [InlineData("acp")]
    [InlineData("copilot")]
    [InlineData("launch")]
    [InlineData("copilot-token")]
    [InlineData("runtime-token")]
    public async Task DirectTokenizerEmbeddingAndSelfHostRefuseBeforeExternalEffects(string input)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var xai = new XaiResponsesLlmClient(http);
        var xaiConnection = Foreign(input, Connection with
        {
            Transport = LlmTransport.XaiResponses, Endpoint = new XaiConnectionOptions().GetEndpoint(),
            Credential = new LlmCredential { OAuth = new() { AccessToken = "existing-token" } }.Serialize()
        });
        await Assert.ThrowsAsync<NotSupportedException>(() => xai.TokenizeAsync(xaiConnection, "model", "input"));
        await Assert.ThrowsAsync<NotSupportedException>(() => new OpenAiEmbeddingClient(http).EmbedAsync(Foreign(input), new() { ModelId = "model", Inputs = ["input"] }));
        var host = new Host();
        var selfHost = new SelfHostLlmClient(host);
        LlmConnection connection = Foreign(input, Connection with { Transport = LlmTransport.SelfHost, Endpoint = "engine" });
        Assert.Throws<NotSupportedException>(() => selfHost.GetCapabilities(connection));
        await Assert.ThrowsAsync<NotSupportedException>(() => selfHost.ListModelsAsync(connection));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, host.Calls);
    }

    [Theory]
    [InlineData("copilot-token")]
    [InlineData("runtime-token")]
    public async Task ForeignRefreshedCredentialNeverReachesSave(string input)
    {
        var source = new Source(Connection);
        var auth = new Authenticator { Result = LlmCredential.Parse(Foreign(input).Credential) };
        await using var access = new LlmConnectionAccess(source, [auth]);
        await Assert.ThrowsAsync<NotSupportedException>(() => access.AcquireAsync("id"));
        Assert.Equal(1, auth.Effects);
        Assert.Equal(0, source.Writes);
    }

    [Theory]
    [InlineData("copilot-token")]
    [InlineData("runtime-token")]
    public async Task DirectLegacyOAuthAuthenticatorsRefuseForeignCredentialsBeforeHttp(string input)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        ILlmAuthenticator[] authenticators = [new CodexAuthenticator(http), new ClaudeAuthenticator(http), new XaiAuthenticator(http)];
        foreach (ILlmAuthenticator authenticator in authenticators)
            await Assert.ThrowsAsync<NotSupportedException>(() => authenticator.EnsureFreshAsync(LlmCredential.Parse(Foreign(input).Credential)!));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("acp")]
    [InlineData("copilot")]
    [InlineData("launch")]
    [InlineData("copilot-token")]
    [InlineData("runtime-token")]
    public async Task InvalidSetupNeverCallsBorrowedEnvironmentOrProvider(string input)
    {
        var client = new Client { SelectedTransport = LlmTransport.GoogleVertexAi };
        var environment = new Environment();
        var target = new Target();
        await using var manager = new LlmSettingsManager(new Dictionary<LlmTransport, ILlmClient> { [client.Transport] = client }, [], new Dictionary<LlmTransport, ILlmSetupEnvironment>
        { [client.Transport] = environment }, target);
        LlmSettings settings = await manager.OpenAsync(client.Transport, new(SetupChangeIntent.Edit, "id", "v1"),
            Foreign(input, Connection with { Transport = client.Transport }), "model");
        Assert.False(settings.Validate().IsValid);
        await manager.QueryChoicesAsync(settings, "model", new());
        await manager.RunAsync(settings, "test");
        Assert.Equal(SetupCommitState.Invalid, (await manager.SaveAsync(settings)).State);
        Assert.Equal(0, environment.Calls);
        Assert.Equal(0, client.Effects);
        Assert.Equal(0, target.Calls);
    }

    [Fact]
    public async Task RuntimeCollectionsAreFrozenBeforeAwaitedResolutionAndNeverSilentlyDropped()
    {
        var arguments = new List<string> { "entered" };
        var names = new HashSet<string>(StringComparer.Ordinal) { "EXPLICIT" };
        var values = new List<LlmAgentEnvironmentVariable> { new("EXPLICIT", "entered-value") };
        var profile = new LlmAgentRuntimeProfile { Id = "profile", DeclaredRuntimeVersion = "declared", Arguments = arguments, EnvironmentAllowlist = names };
        var launch = new LlmAgentRuntimeLaunchInputs { Environment = values };
        LlmConnection connection = Foreign("copilot") with
        { Copilot = new() { Profile = profile, Authentication = new LlmCopilotRuntimeAccount() }, RuntimeLaunchInputs = launch };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new HeldSource(connection, entered, release);
        var client = new Client();
        await using var facade = new LlmProviderService(source, [client]);
        Task? operation = null;
        try
        {
            operation = facade.CountInputTokensAsync("id", Request);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            arguments[0] = "later"; names.Clear(); values[0] = new("EXPLICIT", "later-value");
            release.TrySetResult();
            await Assert.ThrowsAsync<NotSupportedException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("entered", Assert.Single(profile.Arguments));
            Assert.Equal("EXPLICIT", Assert.Single(profile.EnvironmentAllowlist));
            Assert.Equal("entered-value", Assert.Single(launch.Environment).Value);
            Assert.Same(launch, connection.RuntimeLaunchInputs);
            Assert.Equal(0, client.Effects);
        }
        finally
        {
            release.TrySetResult();
            try { if (operation is not null) await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (NotSupportedException) { }
        }
    }

    [Theory]
    [InlineData("facade", false)]
    [InlineData("facade", true)]
    [InlineData("access", false)]
    [InlineData("access", true)]
    [InlineData("agents", false)]
    [InlineData("agents", true)]
    public async Task CancellationAfterResolutionPrecedesForeignInputRefusal(string owner, bool retire)
    {
        var source = new Source(Foreign("launch"));
        var client = new Client();
        var auth = new Authenticator();
        var agent = new Agent();
        await using var facade = new LlmProviderService(source, [client], [auth]);
        await using var access = new LlmConnectionAccess(source, [auth]);
        await using var agents = new LlmAgentProviderService(source, [agent]);
        using var caller = new CancellationTokenSource();
        Task? retirement = null;
        source.OnRead = () =>
        {
            if (!retire) caller.Cancel();
            else retirement = owner switch
            {
                "facade" => facade.DisposeAsync().AsTask(),
                "access" => access.DisposeAsync().AsTask(),
                _ => agents.DisposeAsync().AsTask()
            };
        };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                if (owner == "facade") await facade.CountInputTokensAsync("id", Request, caller.Token);
                else if (owner == "access") await access.AcquireAsync("id", caller.Token);
                else await agents.StartSessionAsync("id", new() { ModelId = "model" }, caller.Token);
            });
            Assert.Equal(0, client.Effects);
            Assert.Equal(0, auth.Effects);
            Assert.Equal(0, agent.Effects);
        }
        finally { if (retirement is not null) await retirement.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public int Writes;
        public Action? OnRead;
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { OnRead?.Invoke(); return Task.FromResult<LlmConnection?>(connection); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        { Writes++; return Task.CompletedTask; }
    }

    private sealed class Target : ISetupCommitTarget<LlmSetupChanges>
    {
        public int Calls;
        public Task<SetupCommitResult> CommitAsync(LlmSetupChanges changes, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "v2")); }
    }

    private sealed class Authenticator : ILlmAuthenticator
    {
        public int Effects;
        public LlmCredential? Result;
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { Effects++; return Result is null ? throw new EffectException() : Task.FromResult(Result); }
    }

    private sealed class Client : ILlmClient, ILlmProviderTokenCounter, ILlmContextEstimator, ILlmAccountUsageClient, ILlmTextTokenizerClient
    {
        public int Effects;
        public LlmTransport SelectedTransport = LlmTransport.OpenAiResponses;
        public LlmTransport Transport => SelectedTransport;
        public LlmProviderDefinition ProviderDefinition => ProviderDefinitions.OpenAiApiKey with { Transport = SelectedTransport };
        public string? EstimateDiagnostic => null;
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
        { Effects++; throw new EffectException(); }
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.Yield(); Effects++; yield return ThrowEffect(); }
        private static LlmStreamEvent ThrowEffect() => throw new EffectException();
        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
        { Effects++; throw new EffectException(); }
        public Task<LlmProviderInputTokenCount> CountInputTokensAsync(LlmConnection connection, LlmChatRequest request, CancellationToken cancellationToken = default)
        { Effects++; throw new EffectException(); }
        public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request)
        { Effects++; throw new EffectException(); }
        public Task<LlmAccountUsageSnapshot> GetAccountUsageAsync(LlmConnection connection, CancellationToken cancellationToken = default)
        { Effects++; throw new EffectException(); }
        public Task<LlmTokenization> TokenizeAsync(LlmConnection connection, string modelId, string text, CancellationToken cancellationToken = default)
        { Effects++; throw new EffectException(); }
    }

    private sealed class Embedding : ILlmEmbeddingClient
    {
        public int Effects;
        public bool Supports(LlmTransport transport) { Effects++; return true; }
        public void ValidateRequest(LlmConnection connection, LlmEmbeddingRequest request) { Effects++; throw new EffectException(); }
        public Task<LlmEmbeddingResult> EmbedAsync(LlmConnection connection, LlmEmbeddingRequest request, CancellationToken cancellationToken = default)
        { Effects++; throw new EffectException(); }
    }

    private sealed class Agent : ILlmAgentResumeClient, ILlmAgentForkClient
    {
        public int Effects;
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        public Task<ILlmAgentSession> StartSessionAsync(string connectionId, LlmConnection connection, LlmAgentSessionOptions options, CancellationToken cancellationToken = default)
        { Effects++; throw new EffectException(); }
        public Task<ILlmAgentSession> ResumeSessionAsync(string connectionId, LlmConnection connection, string threadId, LlmAgentResumeOptions? options = null, CancellationToken cancellationToken = default)
        { Effects++; throw new EffectException(); }
        public Task<ILlmAgentSession> ForkSessionAsync(string connectionId, LlmConnection connection, string threadId, string? lastTurnId = null, LlmAgentResumeOptions? options = null, CancellationToken cancellationToken = default)
        { Effects++; throw new EffectException(); }
    }

    private sealed class EffectException : Exception;

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; throw new EffectException(); }
    }

    private sealed class HeldSource(LlmConnection connection, TaskCompletionSource entered, TaskCompletionSource release) : ILlmConnectionSource
    {
        public async Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { entered.TrySetResult(); await release.Task; return connection; }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
            => throw new EffectException();
    }

    private sealed class Environment : ILlmSetupEnvironment
    {
        public int Calls;
        public LlmTransport Transport => LlmTransport.GoogleVertexAi;
        public SetupValidation Validate(LlmConnection connection) { Calls++; throw new EffectException(); }
        public Task<IReadOnlyList<SetupChoice>> GetProfilesAsync(CancellationToken cancellationToken = default)
        { Calls++; throw new EffectException(); }
        public Task<IReadOnlyList<SetupChoice>> GetRegionsAsync(string? profile, CancellationToken cancellationToken = default)
        { Calls++; throw new EffectException(); }
    }

    private sealed class Host : ILlmSelfHost
    {
        public int Calls;
        public Task<IReadOnlyList<LlmSelfHostEngine>> ListEnginesAsync(CancellationToken cancellationToken = default)
        { Calls++; throw new EffectException(); }
        public Task<Darkspyre.Inference.Common.Embedding.IEmbeddingInference> GetEmbeddingInferenceAsync(string engineId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException("This host runs no embedding engine.");

        public Task<ILlmInference> GetInferenceAsync(string engineId, CancellationToken cancellationToken = default)
        { Calls++; throw new EffectException(); }
        public LlmCapabilities? GetCapabilities(string engineId, string? modelId)
        { Calls++; throw new EffectException(); }
    }
}
