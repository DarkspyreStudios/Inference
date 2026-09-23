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
        Assert.False(session.Snapshot.IsDirty);
        Choice("aws-profile").SetValue("temporary-profile");
        Assert.True(session.Snapshot.IsDirty);
        Choice("aws-profile").SetValue(string.Empty);
        Assert.False(session.Snapshot.IsDirty);
        Assert.Equal(SetupEditState.Applied, Choice("aws-profile").SetValue("fixture-profile").State);
        Assert.Equal(SetupEditState.Applied, Choice("aws-region").SetValue("us-east-1").State);
        Assert.True(session.Snapshot.IsDirty);
        if (transport == LlmTransport.AwsBedrockAgentCore)
        {
            SetupTextField arn = Assert.Single(session.Snapshot.Steps.SelectMany(step => step.Sections)
                .SelectMany(section => section.Fields).OfType<SetupTextField>());
            Assert.Equal(SetupEditState.Applied, arn.SetValue("arn:aws:bedrock-agentcore:us-east-1:123456789012:harness/fixture").State);
        }
        Assert.True((await session.ValidateAsync()).IsValid);
        Assert.Equal(SetupCommitState.Committed, (await session.CommitAsync()).State);
        LlmAwsCredentials aws = LlmCredential.Parse(target.Changes!.Credential.Value)!.Aws!;
        Assert.False(session.Snapshot.IsDirty);
        Assert.Equal("fixture-profile", aws.Profile);
        Assert.Equal("us-east-1", aws.Region);
        Assert.Null(session.Snapshot.Status.Ready.Value);
    }

    [Fact]
    public async Task EveryRegisteredProviderCompletesTheSameCreateAndEditCommitFlowWithoutConnecting()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConnectionSource>(new NoSavedConnectionAccess());
        services.AddLlmConnect();
        services.AddLlmConnectBedrock();
        using ServiceProvider container = services.BuildServiceProvider();
        using IServiceScope scope = container.CreateScope();
        LlmProviderService providers = scope.ServiceProvider.GetRequiredService<LlmProviderService>();
        Assert.Equal(8, providers.SupportedProviders.Count);
        foreach (LlmProviderDefinition definition in providers.SupportedProviders)
        {
            var created = new SetupTarget();
            await using LlmSetupSession draft = providers.CreateSetupSession(definition.Transport,
                new(SetupChangeIntent.Create, "configuration", null), created);
            string[] ids = Fields(draft).Select(field => field.Description.Id).ToArray();
            // Fixture inputs exercise the real provider-owned fields. This is test data, not renderer dispatch.
            foreach (string id in ids)
            {
                SetupField field = Fields(draft).Single(field => field.Description.Id == id);
                SetupEditResult? result = field switch
                {
                    SetupSecretField secret => secret.SetValue(SetupSecretEdit.Replace("fixture-api-key")),
                    SetupExecutableField executable => executable.SetValue(new("fixture-cli")),
                    SetupTextField text when id == "harness-arn" => text.SetValue("arn:aws:bedrock-agentcore:us-east-1:123456789012:harness/fixture"),
                    SetupChoiceField choice when id == "aws-profile" => choice.SetValue("fixture-profile"),
                    SetupChoiceField choice when id == "aws-region" => choice.SetValue("us-east-1"),
                    SetupChoiceField choice when id == "model" => choice.SetValue("fixture-model"),
                    _ => null
                };
                if (result is not null) Assert.Equal(SetupEditState.Applied, result.State);
            }
            Assert.True((await draft.ValidateAsync()).IsValid);
            Assert.Equal(SetupCommitState.Committed, (await draft.CommitAsync()).State);
            Assert.Null(created.Changes!.Configuration.Connection.Credential);
            Assert.Equal("fixture-model", created.Changes.Configuration.ModelId);
            Assert.Null(draft.Snapshot.Status.Ready.Value);

            LlmConnection saved = created.Changes.Configuration.Connection with
            { Version = "saved-v1", Credential = created.Changes.Credential.Value };
            var edited = new SetupTarget();
            await using LlmSetupSession edit = providers.CreateSetupSession(definition.Transport,
                new(SetupChangeIntent.Edit, "configuration", "saved-v1"), edited, saved, "fixture-model");
            Assert.Equal("fixture-model", Assert.IsType<SetupChoiceField>(Fields(edit).Single(field => field.Description.Id == "model")).Value);
            Assert.All(Fields(edit).OfType<SetupSecretField>(), field => Assert.True(field.HasValue));
            Assert.Equal(SetupEditState.Applied, Assert.IsType<SetupChoiceField>(Fields(edit).Single(field => field.Description.Id == "model")).SetValue("edited-model").State);
            Assert.Equal(SetupCommitState.Committed, (await edit.CommitAsync()).State);
            Assert.Equal("saved-v1", edited.Changes!.Identity.ExpectedVersion);
            Assert.Equal("edited-model", edited.Changes.Configuration.ModelId);
            Assert.Equal(SetupSecretEditKind.Keep, edited.Changes.Credential.Kind);
            Assert.True(edited.Changes.RequiresRetest);
        }
    }

    private static IEnumerable<SetupField> Fields(LlmSetupSession session) =>
        session.Snapshot.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields);

    private sealed class NoSavedConnectionAccess : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Setup must use only its draft, not saved connections.");
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Setup must commit only through the typed host target.");
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
