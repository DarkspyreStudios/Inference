using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Bedrock;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockRegistrationTests
{
    [Fact]
    public void RegisteredProvidersExposeCanonicalGuidedSetup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConnectionSource>(new StubConnectionSource());
        services.AddLlmConnect();
        services.AddLlmConnectBedrock();
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using IServiceScope scope = provider.CreateScope();
        IServiceProvider scopedProvider = scope.ServiceProvider;

        Assert.Equal(
            [
                LlmTransport.OpenAiCodex,
                LlmTransport.AnthropicClaudeCli,
                LlmTransport.AwsBedrockAgentCore
            ],
            scopedProvider.GetServices<ILlmAgentClient>()
                .Select(agent => agent.Transport)
                .OrderBy(transport => transport));
        Assert.Equal(
            [
                LlmTransport.OpenAiCodex,
                LlmTransport.AnthropicClaudeCli,
                LlmTransport.AwsBedrockAgentCore
            ],
            scopedProvider.GetRequiredService<LlmAgentProviderService>().SupportedTransports);

        LlmProviderService providerService = scopedProvider.GetRequiredService<LlmProviderService>();
        IReadOnlyList<LlmProviderDefinition> guided = providerService.SupportedProviders
            .Where(definition => definition.IsGuidedSetupOption)
            .ToArray();

        Assert.Equal(
            [
                "OpenAI API Key",
                "Anthropic API Key",
                "ChatGPT Subscription",
                "Claude Subscription",
                "Gemini Subscription",
                "Bedrock AWS Profile",
                "Bedrock AgentCore Harness"
            ],
            guided.Select(definition => definition.DisplayName));
        Assert.All(guided, definition =>
        {
            Assert.NotEmpty(definition.SetupSteps);
            Assert.All(definition.SetupSteps, step =>
            {
                Assert.False(string.IsNullOrWhiteSpace(step.Title));
                Assert.False(string.IsNullOrWhiteSpace(step.Instructions));
                Assert.False(string.IsNullOrWhiteSpace(step.CompletionRequirement));
                Assert.False(string.IsNullOrWhiteSpace(step.Troubleshooting));
            });
            Assert.Equal("select-model", definition.SetupSteps[^2].Id);
            Assert.Equal("test-connection", definition.SetupSteps[^1].Id);
        });
        Assert.True(guided.Single(definition => definition.Id == "gemini-subscription")
            .SetupSteps[0].HasPlatformInstructions);
        Assert.True(guided.Single(definition => definition.Id == "bedrock-aws-profile")
            .SetupSteps[0].HasPlatformInstructions);
        Assert.True(guided.Single(definition => definition.Id == "bedrock-agentcore-harness")
            .SetupSteps[0].HasPlatformInstructions);
        LlmProviderDefinition codex = guided.Single(
            definition => definition.Id == "chatgpt-subscription");
        Assert.Equal("authorize-chatgpt", codex.SetupSteps[0].Id);
        Assert.Contains("codex", codex.DefaultEndpoint, StringComparison.OrdinalIgnoreCase);

        LlmProviderCatalog catalog = scopedProvider.GetRequiredService<LlmProviderCatalog>();
        Assert.Equal(providerService.SupportedProviders.Count, catalog.SupportedProviders.Count);
        Assert.All(catalog.SupportedProviders, definition =>
            Assert.Same(
                definition,
                providerService.SupportedProviders.Single(runtime => runtime.Id == definition.Id)));
    }

    [Fact]
    public void ProviderCatalogResolvesWithoutAConnectionSource()
    {
        var services = new ServiceCollection();
        services.AddLlmConnect();
        services.AddLlmConnectBedrock();
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });

        LlmProviderCatalog catalog = provider.GetRequiredService<LlmProviderCatalog>();

        Assert.Equal(
            [
                "OpenAI API Key",
                "Anthropic API Key",
                "ChatGPT Subscription",
                "Claude Subscription",
                "Gemini Subscription",
                "Bedrock AWS Profile",
                "Bedrock AgentCore Harness",
                "OpenAI-Compatible API"
            ],
            catalog.SupportedProviders.Select(definition => definition.DisplayName));
    }

    [Theory]
    [InlineData(LlmTransport.AwsBedrockConverse)]
    [InlineData(LlmTransport.AwsBedrockAgentCore)]
    public async Task AwsSetupBindsTypedProfileAndRegionEditsWithoutAccessingAws(LlmTransport transport)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConnectionSource>(new StubConnectionSource());
        services.AddLlmConnect();
        services.AddLlmConnectBedrock();
        using ServiceProvider container = services.BuildServiceProvider();
        using IServiceScope scope = container.CreateScope();
        var target = new SetupTarget();
        await using LlmSetupSession session = scope.ServiceProvider.GetRequiredService<LlmProviderService>()
            .CreateSetupSession(transport, new(SetupChangeIntent.Create, "new", null), target, modelId: "default");
        Assert.False((await session.ValidateAsync()).IsValid);
        SetupChoiceField Choice(string id) => Assert.Single(session.Snapshot.Steps.SelectMany(step => step.Sections)
            .SelectMany(section => section.Fields).OfType<SetupChoiceField>(), field => field.Description.Id == id);
        Assert.Equal(SetupEditState.Applied, Choice("aws-profile").SetValue("fixture-profile").State);
        Assert.Equal(SetupEditState.Applied, Choice("aws-region").SetValue("us-east-1").State);
        if (transport == LlmTransport.AwsBedrockAgentCore)
        {
            SetupTextField arn = Assert.Single(session.Snapshot.Steps.SelectMany(step => step.Sections)
                .SelectMany(section => section.Fields).OfType<SetupTextField>());
            Assert.Equal(SetupEditState.Applied, arn.SetValue("arn:aws:bedrock-agentcore:us-east-1:123456789012:harness/fixture").State);
        }
        Assert.True((await session.ValidateAsync()).IsValid);
        Assert.Equal(SetupCommitState.Committed, (await session.CommitAsync()).State);
        LlmAwsCredentials aws = LlmCredential.Parse(target.Changes!.Credential.Value)!.Aws!;
        Assert.Equal("fixture-profile", aws.Profile);
        Assert.Equal("us-east-1", aws.Region);
        Assert.Null(session.Snapshot.Status.Ready.Value);
    }

    private sealed class SetupTarget : ISetupCommitTarget<LlmSetupChanges>
    {
        public LlmSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(LlmSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "v1")); }
    }

    private sealed class StubConnectionSource : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(
            string connectionId,
            CancellationToken cancellationToken = default) => Task.FromResult<LlmConnection?>(null);

        public Task SaveCredentialAsync(
            string connectionId,
            string credentialJson,
            string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
