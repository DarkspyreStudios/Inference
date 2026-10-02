using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Azure.Tests;

public sealed class AzureSettingsTests
{
    [Theory]
    [InlineData(LlmTransport.OpenAiResponses)]
    [InlineData(LlmTransport.OpenAiCompatibleChat)]
    [InlineData(LlmTransport.GoogleGeminiApi)]
    public void InertAzureConfigurationDoesNotSelectAnotherProvidersModel(LlmTransport transport)
    {
        LlmProviderDefinition definition = transport switch
        {
            LlmTransport.OpenAiResponses => ProviderDefinitions.OpenAiApiKey,
            LlmTransport.OpenAiCompatibleChat => ProviderDefinitions.OpenAiCompatible,
            _ => ProviderDefinitions.GeminiApi
        };
        LlmConnection connection = new()
        {
            Version = "v1",
            Transport = transport,
            Endpoint = definition.DefaultEndpoint,
            Credential = new LlmCredential { ApiKey = "protected-key" }.Serialize(),
            Azure = new() { DeploymentId = "inert-azure-deployment" }
        };
        var settings = new LlmSettings(definition, transport, new(SetupChangeIntent.Edit, "id", "v1"), connection, null, null);
        Assert.Equal(definition.DefaultModelId, Assert.IsType<SetupChoiceField>(Field(settings, "model")).Value);
        Assert.False(settings.HasUnsavedChanges);
        var explicitSelection = new LlmSettings(definition, transport, new(SetupChangeIntent.Edit, "id", "v1"), connection, "explicit-model", null);
        Assert.Equal("explicit-model", Assert.IsType<SetupChoiceField>(Field(explicitSelection, "model")).Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AzureSettingsStillUseTheSavedDeploymentWhenNoModelWasSelected(bool responses)
    {
        LlmConnection connection = AzureProtocolTests.Connection(responses, false);
        LlmProviderDefinition definition = responses ? AzureProviderDefinition.Responses : AzureProviderDefinition.Chat;
        var settings = new LlmSettings(definition, connection.Transport, new(SetupChangeIntent.Edit, "id", "v1"), connection, null, new AzureSetupEnvironment(connection.Transport));
        Assert.Equal("deployment-alias", Assert.IsType<SetupChoiceField>(Field(settings, "model")).Value);
        Assert.True(settings.Validate().IsValid);
        Assert.False(settings.HasUnsavedChanges);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManualDeploymentDraftSavesTypedDataAndSeparateProtectedCredentialWithoutCalls(bool responses)
    {
        using var handler = new AzureProtocolTests.Handler(responses, false);
        using var http = new HttpClient(handler);
        var client = AzureProtocolTests.Client(http, responses);
        await using var provider = new LlmProviderService(new Source(), [client], [], [new AzureSetupEnvironment(client.Transport)]);
        var target = new Target();
        var manager = provider.CreateSettingsManager(target);
        LlmSettings settings = await manager.OpenAsync(client.Transport, new(SetupChangeIntent.Create, "new", null));
        SetupUriField endpoint = Assert.IsType<SetupUriField>(Field(settings, "endpoint"));
        endpoint.SetValue(new Uri("https://resource.openai.azure.com"));
        Assert.IsType<SetupChoiceField>(Field(settings, "model")).SetValue("deployment-alias");
        Assert.IsType<SetupTextField>(Field(settings, "azure-model-profile")).SetValue("gpt-4o");
        Assert.IsType<SetupSecretField>(Field(settings, "api-key")).SetValue(SetupSecretEdit.Replace("protected-key"));
        Assert.Equal(SetupSideEffect.None, Assert.IsType<SetupChoiceField>(Field(settings, "model")).ChoiceSideEffects);
        Assert.True(settings.Validate().IsValid);
        Assert.Equal(0, target.Calls);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Equal(1, target.Calls);
        LlmSetupChanges changes = target.Changes!;
        Assert.Null(changes.Configuration.Connection.Credential);
        Assert.Equal("deployment-alias", changes.Configuration.ModelId);
        Assert.Equal("deployment-alias", changes.Configuration.Connection.Azure!.DeploymentId);
        Assert.Equal("gpt-4o", changes.Configuration.Connection.Azure.ModelProfileId);
        Assert.Equal("protected-key", LlmCredential.Parse(changes.Credential.Value)!.ApiKey);
        Assert.Equal(0, handler.Calls);
        Assert.DoesNotContain(Fields(settings), field => field.Description.Id == "model-listing");
        if (responses) Assert.DoesNotContain(Fields(settings), field => field.Description.Id is "output-limit-field" or "prompt-cache-key");
        else Assert.Contains(Fields(settings), field => field.Description.Id == "output-limit-field");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingConflictingDeploymentIsNotSilentlyRewritten(bool responses)
    {
        using var http = new HttpClient(new AzureProtocolTests.Handler(responses, false));
        var client = AzureProtocolTests.Client(http, responses);
        await using var provider = new LlmProviderService(new Source(), [client], [], [new AzureSetupEnvironment(client.Transport)]);
        var manager = provider.CreateSettingsManager(new Target());
        LlmSettings settings = await manager.OpenAsync(client.Transport, new(SetupChangeIntent.Edit, "id", "v1"),
            AzureProtocolTests.Connection(responses, false), "different-deployment");
        Assert.Contains(settings.Validate().Issues, issue => issue.Code == "azure-deployment-conflict");
        Assert.IsType<SetupChoiceField>(Field(settings, "model")).SetValue("deployment-alias");
        Assert.True(settings.Validate().IsValid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProfileEditsInvalidateTheUnsavedTestWithoutWritingCredentials(bool responses)
    {
        using var handler = new AzureProtocolTests.Handler(responses, false);
        using var http = new HttpClient(handler);
        var client = AzureProtocolTests.Client(http, responses);
        await using var provider = new LlmProviderService(new Source(), [client], [], [new AzureSetupEnvironment(client.Transport)]);
        var target = new Target();
        var manager = provider.CreateSettingsManager(target);
        LlmSettings settings = await manager.OpenAsync(client.Transport, new(SetupChangeIntent.Edit, "id", "v1"), AzureProtocolTests.Connection(responses, false));
        Assert.IsType<SetupTextField>(Field(settings, "azure-model-profile")).SetValue("gpt-4.1");
        Assert.True(settings.HasUnsavedChanges);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.True(target.Changes!.RequiresRetest);
        Assert.Equal(SetupSecretEditKind.Keep, target.Changes.Credential.Kind);
        Assert.Equal("v1", target.Changes.Identity.ExpectedVersion);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EntraDraftDoesNotOfferApiKeyOrAcquireOrSaveUnimplementedAuthentication(bool responses)
    {
        using var handler = new AzureProtocolTests.Handler(responses, false);
        using var http = new HttpClient(handler);
        var client = AzureProtocolTests.Client(http, responses);
        await using var provider = new LlmProviderService(new Source(), [client], [], [new AzureSetupEnvironment(client.Transport)]);
        var target = new Target();
        var manager = provider.CreateSettingsManager(target);
        LlmConnection connection = AzureProtocolTests.Connection(responses, false);
        var entra = new LlmAzureEntraConfiguration(LlmAzureCloud.Public, LlmAzureTokenAudience.Foundry,
            new LlmAzureIdentitySelection.ServicePrincipalSecret("tenant", "client"));
        var credential = new LlmCredential { Azure = new LlmAzureSecret.ClientSecret("protected-secret") };
        LlmSettings settings = await manager.OpenAsync(client.Transport, new(SetupChangeIntent.Edit, "id", "v1"),
            connection with { Azure = connection.Azure! with { AuthenticationMode = LlmAzureAuthenticationMode.Entra, Entra = entra },
                Credential = credential.Serialize() });
        Assert.DoesNotContain(Fields(settings), field => field.Description.Id == "api-key");
        Assert.False(settings.Validate().IsValid);
        Assert.NotEqual(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Equal(0, target.Calls);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(SetupObservation.Unknown, settings.Status.Authenticated);
    }

    private static SetupField Field(LlmSettings settings, string id) => Assert.Single(Fields(settings), field => field.Description.Id == id);
    private static IEnumerable<SetupField> Fields(LlmSettings settings) => settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields);
    private sealed class Target : ISetupCommitTarget<LlmSetupChanges>
    {
        public int Calls { get; private set; }
        public LlmSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(LlmSetupChanges changes, CancellationToken cancellationToken = default)
        { Calls++; Changes = changes; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "v2")); }
    }
    private sealed class Source : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Setup must use its unsaved draft.");
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Setup must not write a saved credential.");
    }
}
