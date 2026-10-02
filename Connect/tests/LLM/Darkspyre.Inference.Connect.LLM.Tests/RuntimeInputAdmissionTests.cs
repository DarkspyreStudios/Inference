using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class RuntimeInputAdmissionTests
{
    public static TheoryData<string, string> FacadeCases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (string input in Inputs)
                foreach (string route in new[] { "complete", "count", "measure", "models", "refresh-models", "capabilities", "model-capabilities", "account", "observed", "authenticated", "set-key", "set-aws", "cache", "access", "authorization", "start", "resume", "fork" })
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

    private static LlmConnection Foreign(string input, LlmConnection? connection = null)
    {
        connection ??= Connection;
        var profile = new LlmAgentRuntimeProfile { Id = "profile", DeclaredRuntimeVersion = "declared" };
        return input switch
        {
            "acp" => connection with { Acp = new() { Profile = profile, State = new LlmAgentAdoptedRuntimeState("/selected/state"), Authentication = new LlmAcpExternalAuthentication() } },
            "copilot" => connection with { Copilot = new() { Profile = profile, Authentication = new LlmCopilotRuntimeAccount() } },
            "launch" => connection with { RuntimeLaunchInputs = new() },
            "copilot-token" => connection with { Credential = (LlmCredential.Parse(connection.Credential)! with { Copilot = new() { AccessToken = "copilot-secret" } }).Serialize() },
            "runtime-token" => connection with { Credential = (LlmCredential.Parse(connection.Credential)! with { CopilotRuntime = new() { ConnectionToken = "runtime-secret" } }).Serialize() },
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
                default: throw new ArgumentOutOfRangeException(nameof(route));
            }
        });
        Assert.Equal(0, client.Effects);
        Assert.Equal(0, agent.Effects);
        Assert.Equal(0, auth.Effects);
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

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public int Writes;
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => Task.FromResult<LlmConnection?>(connection);
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
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { Effects++; throw new EffectException(); }
    }

    private sealed class Client : ILlmClient, ILlmProviderTokenCounter, ILlmContextEstimator, ILlmAccountUsageClient
    {
        public int Effects;
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        public LlmProviderDefinition ProviderDefinition => ProviderDefinitions.OpenAiApiKey;
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
}
