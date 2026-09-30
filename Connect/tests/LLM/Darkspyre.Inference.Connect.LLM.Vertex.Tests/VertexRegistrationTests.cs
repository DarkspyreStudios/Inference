using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Bedrock;
using Darkspyre.Inference.Connect.LLM.Vertex;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class VertexRegistrationTests
{
    [Fact]
    public async Task RegistryIncludesEveryChatAndAgentAdapterAndProviderOwnedSetup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConnectionSource, UnusedSource>();
        services.AddSingleton<ILlmSelfHost, UnloadedHost>();
        services.AddLlmConnect();
        services.AddLlmConnectBedrock();
        services.AddLlmConnectSelfHost();
        services.AddLlmConnectVertex();
        using var container = services.BuildServiceProvider();
        var clients = container.GetServices<ILlmClient>().ToArray();
        Assert.Equal(Enum.GetValues<LlmTransport>().Order(), clients.Select(client => client.Transport).Order());
        var expectedAgents = new[] { LlmTransport.OpenAiCodex, LlmTransport.AwsBedrockConverse,
            LlmTransport.GoogleAntigravityCli, LlmTransport.AnthropicClaudeCli, LlmTransport.AwsBedrockAgentCore, LlmTransport.SelfHost,
            LlmTransport.AppleFoundationModels, LlmTransport.GoogleVertexAi, LlmTransport.GoogleGeminiApi };
        Assert.Equal(expectedAgents.Order(), container.GetServices<ILlmAgentClient>().Select(client => client.Transport).Order());
        var provider = container.GetRequiredService<LlmProviderService>();
        var manager = provider.CreateSettingsManager(new Target());
        foreach (var client in clients)
        {
            var definition = client.ProviderDefinition;
            var settings = await manager.OpenAsync(client.Transport, new(SetupChangeIntent.Create, "fixture", null));
            Assert.Equal(definition.Id, settings.Definition.Implementation.Id);
            Assert.NotEmpty(definition.SetupSteps);
            var connection = new LlmConnection
            {
                Version = "1",
                Transport = client.Transport,
                Endpoint = client.Transport == LlmTransport.AwsBedrockAgentCore
                    ? "arn:aws:bedrock-agentcore:us-east-1:123456789012:harness/test-harness" : definition.DefaultEndpoint,
                Credential = client.Transport is LlmTransport.AwsBedrockAgentCore or LlmTransport.AwsBedrockConverse
                    ? new LlmCredential { Aws = new() { Region = "us-east-1", Profile = "fixture" } }.Serialize() : null,
                Vertex = client.Transport == LlmTransport.GoogleVertexAi ? new() { ProjectId = "fixture", Location = "global" } : null
            };
            var capabilities = client.GetCapabilities(connection);
            Assert.True(Enum.IsDefined(capabilities.Tools.StrictInputSchema));
            Assert.False(new LlmToolDefinition { Name = "ordinary", InputSchema = System.Text.Json.JsonElement.Parse("{}") }.Strict);
        }
    }

    [Fact]
    public async Task VertexSetupKeepsProjectLocationAndProtectedCredentialSeparateAtSave()
    {
        using var http = new HttpClient();
        await using var authentication = new VertexAuthentication();
        var client = new VertexLlmClient(http, authentication);
        await using var service = new LlmProviderService(new UnusedSource(), [client], [], [new VertexSetupEnvironment()]);
        var target = new Target();
        var manager = service.CreateSettingsManager(target);
        var settings = await manager.OpenAsync(LlmTransport.GoogleVertexAi, new(SetupChangeIntent.Create, "vertex", null));
        Field<SetupTextField>(settings, "google-project").SetValue("my-project");
        Field<SetupTextField>(settings, "google-location").SetValue("global");
        Field<SetupChoiceField>(settings, "google-authentication").SetValue(nameof(LlmGoogleAuthenticationMode.ServiceAccount));
        const string key = """{"type":"service_account","client_email":"fixture@example.invalid","private_key":"fixture-key","token_uri":"https://oauth2.googleapis.com/token"}""";
        Field<SetupSecretField>(settings, "google-service-account").SetValue(SetupSecretEdit.Replace(key));
        Field<SetupChoiceField>(settings, "model").SetValue("anthropic/claude-sonnet-4-6");
        Assert.True(settings.Validate().IsValid);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Null(target.Changes!.Configuration.Connection.Credential);
        Assert.Equal("my-project", target.Changes.Configuration.Connection.Vertex!.ProjectId);
        Assert.Equal(key, LlmCredential.Parse(target.Changes.Credential.Value)!.Google!.ServiceAccountJson);
        Assert.Null(settings.Status.Ready.Value);
    }

    [Fact]
    public async Task GeminiApiSetupStoresOnlyTheKeyAsProtectedCredentialAndHidesTheEndpoint()
    {
        var client = new Darkspyre.Inference.Connect.LLM.Providers.GeminiApiLlmClient(new HttpClient());
        var service = new LlmProviderService(new UnusedSource(), [client]);
        var target = new Target();
        var manager = service.CreateSettingsManager(target);
        var settings = await manager.OpenAsync(LlmTransport.GoogleGeminiApi, new(SetupChangeIntent.Create, "gemini", null));
        var ids = settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields).Select(field => field.Description.Id).ToArray();
        Assert.DoesNotContain("endpoint", ids);
        Assert.DoesNotContain("google-project", ids);
        Assert.False(settings.Validate().IsValid);
        Field<SetupSecretField>(settings, "api-key").SetValue(SetupSecretEdit.Replace("fixture-key"));
        Field<SetupChoiceField>(settings, "model").SetValue("gemini-3-flash-preview");
        Assert.True(settings.Validate().IsValid);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Null(target.Changes!.Configuration.Connection.Credential);
        Assert.Null(target.Changes.Configuration.Connection.Vertex);
        Assert.Equal("https://generativelanguage.googleapis.com", target.Changes.Configuration.Connection.Endpoint);
        Assert.Equal("fixture-key", LlmCredential.Parse(target.Changes.Credential.Value)!.ApiKey);
    }

    private static T Field<T>(LlmSettings settings, string id) where T : SetupField =>
        Assert.IsType<T>(settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields).Single(field => field.Description.Id == id));

    private sealed class Target : ISetupCommitTarget<LlmSetupChanges>
    {
        public LlmSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(LlmSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "saved")); }
    }
    private sealed class UnusedSource : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No connection I/O expected.");
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No credential writes expected.");
    }
    private sealed class UnloadedHost : ILlmSelfHost
    {
        public Task<IReadOnlyList<LlmSelfHostEngine>> ListEnginesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LlmSelfHostEngine>>([]);
        public Task<ILlmInference> GetInferenceAsync(string engineId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No engine I/O expected.");
        public LlmCapabilities? GetCapabilities(string engineId, string? modelId) => null;
    }
}
