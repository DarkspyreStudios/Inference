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
            Assert.Equal(LlmSetupAction.SelectModel, definition.SetupSteps[^2].Action);
            Assert.Equal(LlmSetupAction.TestConnection, definition.SetupSteps[^1].Action);
        });
        Assert.True(guided.Single(definition => definition.Id == "gemini-subscription")
            .SetupSteps[0].HasPlatformInstructions);
        Assert.True(guided.Single(definition => definition.Id == "bedrock-aws-profile")
            .SetupSteps[0].HasPlatformInstructions);
        Assert.True(guided.Single(definition => definition.Id == "bedrock-agentcore-harness")
            .SetupSteps[0].HasPlatformInstructions);
        LlmProviderDefinition codex = guided.Single(
            definition => definition.Id == "chatgpt-subscription");
        Assert.Equal(LlmSetupAction.AuthorizeDeviceCode, codex.SetupSteps[0].Action);
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
