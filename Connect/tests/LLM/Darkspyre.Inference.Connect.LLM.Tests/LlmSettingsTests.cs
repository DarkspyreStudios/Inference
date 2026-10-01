using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class LlmSettingsTests
{
    [Fact]
    public async Task RegisteredProvidersExposeTypedFieldsAndBoundActionsWithoutConnecting()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConnectionSource, UnusedSource>();
        services.AddLlmConnect();
        using ServiceProvider container = services.BuildServiceProvider();
        using IServiceScope scope = container.CreateScope();
        LlmProviderService provider = scope.ServiceProvider.GetRequiredService<LlmProviderService>();
        foreach (LlmProviderDefinition definition in provider.SupportedProviders)
        {
            var (manager, settings) = await OpenAsync(provider, definition.Transport,
                new(SetupChangeIntent.Create, "new", null), new Target());
            Assert.Equal(definition.Id, settings.Definition.Implementation.Id);
            Assert.NotEmpty(Fields(settings));
            Assert.Contains(settings.Actions, action => action.Purpose == SetupActionPurpose.Test
                && action.SideEffects.HasFlag(definition.Transport == LlmTransport.AppleFoundationModels
                    ? SetupSideEffect.LocalVerification : SetupSideEffect.PotentialBilling));
            Assert.Null(settings.Status.Ready.Value);
        }
    }

    [Fact]
    public async Task ApiKeyEditStaysInDraftAndPreservesVersionAtCommit()
    {
        var target = new Target();
        var client = new Client(ProviderDefinitions.OpenAiApiKey);
        var provider = new LlmProviderService(new UnusedSource(), [client]);
        LlmConnection original = Connection(LlmTransport.OpenAiResponses, "saved-key");
        var (manager, settings) = await OpenAsync(provider, client.Transport,
            new(SetupChangeIntent.Edit, "id", "v1"), target, original, "model");
        SetupSecretField key = Assert.Single(Fields(settings).OfType<SetupSecretField>());
        Assert.True(key.HasValue);
        Assert.Equal(SetupEditState.Applied, key.SetValue(SetupSecretEdit.Replace("replacement-key")).State);
        Assert.Equal("saved-key", LlmCredential.Parse(original.Credential)!.ApiKey);
        Assert.Equal(0, target.Calls);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Equal("v1", target.Changes!.Identity.ExpectedVersion);
        Assert.Null(target.Changes.Configuration.Connection.Credential);
        Assert.Equal("replacement-key", LlmCredential.Parse(target.Changes.Credential.Value)!.ApiKey);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task ClearingAndThenKeepingRestoresTheSavedKey()
    {
        var target = new Target();
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ProviderDefinitions.OpenAiApiKey)]);
        var (manager, settings) = await OpenAsync(provider, LlmTransport.OpenAiResponses,
            new(SetupChangeIntent.Edit, "id", "v1"), target, Connection(LlmTransport.OpenAiResponses, "saved-key"), "model");
        Assert.Single(Fields(settings).OfType<SetupSecretField>()).SetValue(SetupSecretEdit.Clear);
        Assert.True(settings.HasUnsavedChanges);
        Assert.False(settings.Validate().IsValid);
        Assert.Single(Fields(settings).OfType<SetupSecretField>()).SetValue(SetupSecretEdit.Keep);
        Assert.False(settings.HasUnsavedChanges);
        Assert.True(settings.Validate().IsValid);
        await manager.SaveAsync(settings);
        Assert.Equal(SetupSecretEditKind.Keep, target.Changes!.Credential.Kind);
    }

    [Fact]
    public async Task TestIsExplicitAndUsesTheDraftWithoutSaving()
    {
        var target = new Target();
        var client = new Client(ProviderDefinitions.OpenAiApiKey);
        var provider = new LlmProviderService(new UnusedSource(), [client]);
        var (manager, settings) = await OpenAsync(provider, client.Transport,
            new(SetupChangeIntent.Edit, "id", "v1"), target, Connection(client.Transport, "saved-key"), "model");
        Assert.Equal(0, client.Calls);
        SetupActionResult result = await manager.RunAsync(settings, Action(settings, SetupActionPurpose.Test).Id);
        Assert.Equal(SetupActionState.Completed, result.State);
        Assert.True(settings.Status.Ready.Value);
        Assert.False(settings.HasUnsavedChanges);
        Assert.Equal(0, target.Calls);
    }

    [Fact]
    public async Task BrowserAuthorizationKeepsTheCallbackOutOfFieldValuesAndOnlyCommitsThroughHost()
    {
        var target = new Target();
        var auth = new BrowserAuth();
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ProviderDefinitions.ClaudeSubscription)], [auth]);
        var (manager, settings) = await OpenAsync(provider, LlmTransport.AnthropicClaudeCli,
            new(SetupChangeIntent.Create, "id", null), target, modelId: "model");
        Assert.Equal(SetupActionState.NeedsInteraction, (await manager.RunAsync(settings, Action(settings, SetupActionPurpose.Authenticate).Id)).State);
        SetupBrowserAuthorization interaction = Assert.IsType<SetupBrowserAuthorization>(settings.Interaction);
        Assert.Equal(SetupEditState.Applied, interaction.Callback.SetValue("code#state").State);
        SetupBrowserAuthorization current = Assert.IsType<SetupBrowserAuthorization>(settings.Interaction);
        Assert.Equal(interaction.Callback.Description.Id, current.Callback.Description.Id);
        Assert.Empty(current.Callback.Value);
        Assert.True(settings.HasUnsavedChanges);
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, current.Continue.Id)).State);
        Assert.Equal(1, auth.Completed);
        Assert.Null(settings.Interaction);
        Assert.True(settings.HasUnsavedChanges); // The acquired credential is still an unsaved draft mutation.
        Assert.Null(settings.Status.Authenticated.Value); // Catalog OAuth is not CLI login.
        Assert.Equal(0, target.Calls);
        await manager.SaveAsync(settings);
        Assert.Equal("catalog-token", LlmCredential.Parse(target.Changes!.Credential.Value)!.OAuth!.AccessToken);
    }

    [Fact]
    public async Task EndpointEditInvalidatesPendingAuthorizationEvenAfterCallbackWasEntered()
    {
        var auth = new BrowserAuth();
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ProviderDefinitions.ClaudeSubscription)], [auth]);
        var (manager, settings) = await OpenAsync(provider, LlmTransport.AnthropicClaudeCli,
            new(SetupChangeIntent.Create, "id", null), new Target(), modelId: "model");
        await manager.RunAsync(settings, Action(settings, SetupActionPurpose.Authenticate).Id);
        Assert.IsType<SetupBrowserAuthorization>(settings.Interaction).Callback.SetValue("code#state");
        Assert.Single(Fields(settings).OfType<SetupExecutableField>()).SetValue(new("different-cli"));
        Assert.Null(settings.Interaction);
        Assert.DoesNotContain(settings.Actions, action => action.Purpose == SetupActionPurpose.ContinueAuthentication);
        Assert.Equal(SetupActionState.Unavailable, (await manager.RunAsync(settings, "continue-authorization")).State);
        Assert.Equal(0, auth.Completed);
    }

    [Fact]
    public async Task HostConflictDoesNotRetryOrDiscardTheDraft()
    {
        var target = new Target { Result = new(SetupCommitState.Conflict) };
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ProviderDefinitions.OpenAiApiKey)]);
        var (manager, settings) = await OpenAsync(provider, LlmTransport.OpenAiResponses,
            new(SetupChangeIntent.Edit, "id", "v1"), target, Connection(LlmTransport.OpenAiResponses, "saved-key"), "model");
        Assert.Equal(SetupCommitState.Conflict, (await manager.SaveAsync(settings)).State);
        Assert.Equal(1, target.Calls);
        Assert.False(settings.IsSaved);
    }

    [Fact]
    public async Task DeviceAuthorizationUsesTheDraftAndLeavesSavedConnectionsAlone()
    {
        var target = new Target();
        var auth = new DeviceAuth();
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ProviderDefinitions.ChatGptSubscription)], [auth]);
        var (manager, settings) = await OpenAsync(provider, LlmTransport.OpenAiCodex,
            new(SetupChangeIntent.Create, "id", null), target, modelId: "model");
        await manager.RunAsync(settings, Action(settings, SetupActionPurpose.Authenticate).Id);
        SetupDeviceAuthorization interaction = Assert.IsType<SetupDeviceAuthorization>(settings.Interaction);
        Assert.Equal("code", interaction.UserCode);
        Assert.Null(interaction.ExpiresAt); // The existing upstream contract does not report expiry.
        // A status-only publication must not invalidate the authorization identity.
        await manager.RunAsync(settings, Action(settings, SetupActionPurpose.CheckPrerequisites).Id);
        SetupDeviceAuthorization current = Assert.IsType<SetupDeviceAuthorization>(settings.Interaction);
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, current.Continue.Id)).State);
        Assert.True(settings.Status.Authenticated.Value);
        Assert.Equal(0, target.Calls);
        await manager.SaveAsync(settings);
        Assert.Equal("device-token", LlmCredential.Parse(target.Changes!.Credential.Value)!.OAuth!.AccessToken);
    }

    [Fact]
    public async Task DirectCodexHasNoEndpointFieldAndReplacesAFormerEndpointOnSave()
    {
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ProviderDefinitions.ChatGptSubscription)], [new DeviceAuth()]);
        string official = ProviderDefinitions.ChatGptSubscription.DefaultEndpoint;
        var created = new Target();
        var (createManager, create) = await OpenAsync(provider, LlmTransport.OpenAiCodex,
            new(SetupChangeIntent.Create, "id", null), created, modelId: "model");
        Assert.Equal(["model"], Fields(create).Select(field => field.Description.Id));
        Assert.Empty(create.Steps[0].Sections); // Sign-in has instructions and actions, no empty field group.
        Assert.False(create.CanSave); // Direct Codex saves only after its ChatGPT sign-in.
        Assert.Equal(SetupCommitState.Invalid, (await createManager.SaveAsync(create)).State);
        await createManager.RunAsync(create, Action(create, SetupActionPurpose.Authenticate).Id);
        await createManager.RunAsync(create, Assert.IsType<SetupDeviceAuthorization>(create.Interaction).Continue.Id);
        Assert.True(create.CanSave);
        Assert.Equal(SetupCommitState.Committed, (await createManager.SaveAsync(create)).State);
        Assert.Equal(official, created.Changes!.Configuration.Connection.Endpoint);

        LlmConnection former = new()
        {
            Version = "v1",
            Transport = LlmTransport.OpenAiCodex,
            Endpoint = "codex",
            Credential = new LlmCredential { OAuth = new() { AccessToken = "saved-token" } }.Serialize()
        };
        var edited = new Target();
        var (editManager, edit) = await OpenAsync(provider, LlmTransport.OpenAiCodex,
            new(SetupChangeIntent.Edit, "id", "v1"), edited, former, "model");
        Assert.True(edit.HasUnsavedChanges);
        Assert.Equal(SetupCommitState.Committed, (await editManager.SaveAsync(edit)).State);
        Assert.Equal(official, edited.Changes!.Configuration.Connection.Endpoint);
        Assert.True(edited.Changes.RequiresReauthentication);

        var (_, unchanged) = await OpenAsync(provider, LlmTransport.OpenAiCodex,
            new(SetupChangeIntent.Edit, "id", "v1"), new Target(), former with { Endpoint = official }, "model");
        Assert.False(unchanged.HasUnsavedChanges);
    }

    private static LlmConnection Connection(LlmTransport transport, string key) => new()
    { Version = "v1", Transport = transport, Endpoint = "https://example.test/v1", Credential = new LlmCredential { ApiKey = key }.Serialize() };

    [Fact]
    public async Task CompatibleEndpointOverridesAreTypedAndDoNotEraseOtherSettings()
    {
        var target = new Target();
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ProviderDefinitions.OpenAiCompatible)]);
        LlmConnection existing = Connection(LlmTransport.OpenAiCompatibleChat, "saved") with
        { CapabilityOverrides = new() { SupportsTemperature = false, SupportsToolUse = true } };
        var (manager, settings) = await OpenAsync(provider, existing.Transport,
            new(SetupChangeIntent.Edit, "id", "v1"), target, existing, "model");
        SetupChoiceField Field(string id) => Assert.Single(Fields(settings).OfType<SetupChoiceField>(), field => field.Description.Id == id);
        Assert.Equal(SetupEditState.Invalid, Field("model-listing").SetValue("unexpected").State);
        Assert.Equal(SetupEditState.Applied, Field("model-listing").SetValue("disabled").State);
        Assert.Equal(SetupEditState.Applied, Field("temperature").SetValue("default").State);
        Assert.Equal(SetupEditState.Applied, Field("reasoning-text").SetValue("disabled").State);
        foreach (string id in new[] { "top-p", "top-k", "seed", "presence-penalty", "frequency-penalty" })
            Assert.Equal(SetupEditState.Applied, Field(id).SetValue("enabled").State);
        SetupIntegerField StopLimit() => Assert.Single(Fields(settings).OfType<SetupIntegerField>(), field => field.Description.Id == "max-stop-sequences");
        Assert.Null(StopLimit().Value);
        Assert.Equal(SetupEditState.Invalid, StopLimit().SetValue(-1).State);
        Assert.Equal(SetupEditState.Invalid, StopLimit().SetValue((long)int.MaxValue + 1).State);
        Assert.Equal(SetupEditState.Applied, StopLimit().SetValue(0).State);
        Assert.Equal(SetupEditState.Applied, StopLimit().SetValue(null).State);
        Assert.Equal(SetupEditState.Applied, StopLimit().SetValue(12).State);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        LlmCapabilityOverrides overrides = target.Changes!.Configuration.Connection.CapabilityOverrides!;
        Assert.False(overrides.SupportsModelListing);
        Assert.Null(overrides.SupportsTemperature);
        Assert.False(overrides.SupportsReasoningText);
        Assert.True(overrides.SupportsToolUse);
        Assert.True(overrides.SupportsTopP);
        Assert.True(overrides.SupportsTopK);
        Assert.True(overrides.SupportsSeed);
        Assert.True(overrides.SupportsPresencePenalty);
        Assert.True(overrides.SupportsFrequencyPenalty);
        Assert.Equal(12, overrides.MaxStopSequences);
        var (_, reopened) = await OpenAsync(provider, existing.Transport,
            new(SetupChangeIntent.Edit, "id", "v2"), new Target(), target.Changes.Configuration.Connection with { Version = "v2" }, "model");
        Assert.Equal(12, Assert.Single(Fields(reopened).OfType<SetupIntegerField>()).Value);
        Assert.True(target.Changes.RequiresRetest);
    }
    [Theory]
    [InlineData("max_tokens", LlmChatCompletionsOutputLimitField.MaxTokens, "enabled", true)]
    [InlineData("max_completion_tokens", LlmChatCompletionsOutputLimitField.MaxCompletionTokens, "disabled", false)]
    public async Task CompatibleProtocolChoicesRoundTripAndRequireRetest(string field, LlmChatCompletionsOutputLimitField expected,
        string cache, bool enabled)
    {
        var target = new Target();
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ProviderDefinitions.OpenAiCompatible)]);
        LlmConnection original = Connection(LlmTransport.OpenAiCompatibleChat, "saved");
        var (manager, settings) = await OpenAsync(provider, original.Transport,
            new(SetupChangeIntent.Edit, "id", "v1"), target, original, "model");
        SetupChoiceField Field(string id) => Assert.Single(Fields(settings).OfType<SetupChoiceField>(), item => item.Description.Id == id);
        Assert.Equal("default", Field("output-limit-field").Value);
        Assert.Equal("default", Field("prompt-cache-key").Value);
        Assert.Equal(SetupEditState.Invalid, Field("output-limit-field").SetValue("invalid").State);
        Assert.Equal(SetupEditState.Invalid, Field("prompt-cache-key").SetValue("invalid").State);
        Assert.False(settings.HasUnsavedChanges);
        Assert.Equal(SetupEditState.Applied, Field("output-limit-field").SetValue(field).State);
        Assert.Equal(SetupEditState.Applied, Field("prompt-cache-key").SetValue(cache).State);
        Assert.True(settings.HasUnsavedChanges);
        await manager.SaveAsync(settings);
        Assert.Equal(new LlmChatCompletionsConfiguration { OutputLimitField = expected, UsePromptCacheKey = enabled },
            target.Changes!.Configuration.Connection.ChatCompletions);
        Assert.True(target.Changes.RequiresRetest);
        Assert.False(target.Changes.RequiresReauthentication);
        Assert.Null(original.ChatCompletions);
        var (_, reopened) = await OpenAsync(provider, original.Transport,
            new(SetupChangeIntent.Edit, "id", "v2"), new Target(), target.Changes.Configuration.Connection with { Version = "v2" }, "model");
        Assert.Equal(field, Assert.Single(Fields(reopened).OfType<SetupChoiceField>(), item => item.Description.Id == "output-limit-field").Value);
        Assert.Equal(cache, Assert.Single(Fields(reopened).OfType<SetupChoiceField>(), item => item.Description.Id == "prompt-cache-key").Value);
    }

    [Fact]
    public async Task CompatibleProtocolDefaultsRestoreNullAndPreserveIndependentOptions()
    {
        var target = new Target();
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ProviderDefinitions.OpenAiCompatible)]);
        LlmConnection original = Connection(LlmTransport.OpenAiCompatibleChat, "saved") with
        { ChatCompletions = new() { OutputLimitField = LlmChatCompletionsOutputLimitField.MaxTokens, UsePromptCacheKey = true } };
        var (manager, settings) = await OpenAsync(provider, original.Transport,
            new(SetupChangeIntent.Edit, "id", "v1"), target, original, "model");
        SetupChoiceField Field(string id) => Assert.Single(Fields(settings).OfType<SetupChoiceField>(), item => item.Description.Id == id);
        Field("output-limit-field").SetValue("default");
        Assert.True(settings.HasUnsavedChanges);
        Assert.True(settings.CurrentConnection().ChatCompletions!.UsePromptCacheKey);
        Field("output-limit-field").SetValue("max_tokens");
        Assert.False(settings.HasUnsavedChanges);
        Field("output-limit-field").SetValue("default");
        Field("prompt-cache-key").SetValue("default");
        await manager.SaveAsync(settings);
        Assert.Null(target.Changes!.Configuration.Connection.ChatCompletions);
        Assert.True(target.Changes.RequiresRetest);
    }

    private static async Task<(LlmSettingsManager Manager, LlmSettings Settings)> OpenAsync(LlmProviderService provider, LlmTransport transport,
        SetupConfigurationIdentity identity, Target target, LlmConnection? existing = null, string? modelId = null)
    {
        LlmSettingsManager manager = provider.CreateSettingsManager(target);
        return (manager, await manager.OpenAsync(transport, identity, existing, modelId));
    }
    private static IEnumerable<SetupField> Fields(LlmSettings settings) => settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields);
    private static SetupActionDescriptor Action(LlmSettings settings, SetupActionPurpose purpose) => Assert.Single(settings.Actions, action => action.Purpose == purpose);

    private sealed class Target : ISetupCommitTarget<LlmSetupChanges>
    {
        public SetupCommitResult Result { get; init; } = new(SetupCommitState.Committed, "v2");
        public int Calls { get; private set; }
        public LlmSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(LlmSetupChanges changes, CancellationToken cancellationToken = default)
        { Calls++; Changes = changes; return Task.FromResult(Result); }
    }

    private sealed class UnusedSource : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Saved connections must not be used by a setup draft.");
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Setup must not overwrite saved credentials.");
    }

    [Fact]
    public async Task CliPrerequisiteStaysUntilTheRecheckSucceeds()
    {
        var client = new Client(ProviderDefinitions.ClaudeSubscription) { Authenticated = false };
        var provider = new LlmProviderService(new UnusedSource(), [client]);
        var (manager, settings) = await OpenAsync(provider, client.Transport,
            new(SetupChangeIntent.Create, "id", null), new Target(), modelId: "model");
        Assert.Equal(SetupActionState.NeedsInteraction, (await manager.RunAsync(settings, Action(settings, SetupActionPurpose.CheckPrerequisites).Id)).State);
        SetupExternalPrerequisite first = Assert.IsType<SetupExternalPrerequisite>(settings.Interaction);
        Assert.False(string.IsNullOrWhiteSpace(first.Instructions));
        Assert.False(settings.HasUnsavedChanges);
        Assert.Equal(SetupActionState.NeedsInteraction, (await manager.RunAsync(settings, first.Recheck.Id)).State);
        client.Authenticated = true;
        SetupExternalPrerequisite current = Assert.IsType<SetupExternalPrerequisite>(settings.Interaction);
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, current.Recheck.Id)).State);
        Assert.Null(settings.Interaction);
        Assert.False(settings.HasUnsavedChanges);
    }

    [Fact]
    public async Task CallbackRestartChangesIdentityAndNeverEchoesEnteredCode()
    {
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ProviderDefinitions.ClaudeSubscription)], [new BrowserAuth()]);
        var (manager, settings) = await OpenAsync(provider, LlmTransport.AnthropicClaudeCli,
            new(SetupChangeIntent.Create, "id", null), new Target(), modelId: "model");
        await manager.RunAsync(settings, Action(settings, SetupActionPurpose.Authenticate).Id);
        SetupBrowserAuthorization first = Assert.IsType<SetupBrowserAuthorization>(settings.Interaction);
        first.Callback.SetValue("sensitive-code");
        Assert.Empty(Assert.IsType<SetupBrowserAuthorization>(settings.Interaction).Callback.Value);
        await manager.RunAsync(settings, Action(settings, SetupActionPurpose.CancelAuthentication).Id);
        Assert.False(settings.HasUnsavedChanges);
        await manager.RunAsync(settings, Action(settings, SetupActionPurpose.Authenticate).Id);
        Assert.NotEqual(first.Callback.Description.Id, Assert.IsType<SetupBrowserAuthorization>(settings.Interaction).Callback.Description.Id);
    }

    [Fact]
    public async Task ReturningCapabilityOverrideToDefaultRestoresCleanDraft()
    {
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ProviderDefinitions.OpenAiCompatible)]);
        var (manager, settings) = await OpenAsync(provider, LlmTransport.OpenAiCompatibleChat,
            new(SetupChangeIntent.Create, "id", null), new Target());
        SetupChoiceField Field() => Assert.Single(Fields(settings).OfType<SetupChoiceField>(), field => field.Description.Id == "streaming");
        Field().SetValue("enabled");
        Assert.True(settings.HasUnsavedChanges);
        Field().SetValue("default");
        Assert.False(settings.HasUnsavedChanges);
        Assert.Equal(SetupChoiceCapabilities.Bounded, Field().Capabilities);
    }

    private static readonly string[] DefaultedLevels = ["none", "low", "high", "extrahigh"];

    [Fact]
    public async Task ReasoningLevelOverrideBecomesExplicitAndReturnsToDefault()
    {
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ProviderDefinitions.OpenAiCompatible)]);
        var (manager, settings) = await OpenAsync(provider, LlmTransport.OpenAiCompatibleChat,
            new(SetupChangeIntent.Create, "id", null), new Target());
        SetupChoiceField Level(string name) =>
            Assert.Single(Fields(settings).OfType<SetupChoiceField>(), field => field.Description.Id == "reasoning-level-" + name);

        Assert.Equal(7, Fields(settings).OfType<SetupChoiceField>().Count(field => field.Description.Id.StartsWith("reasoning-level-", StringComparison.Ordinal)));
        foreach (string name in DefaultedLevels) { Assert.Equal("default", Level(name).Value); }

        Level("low").SetValue("enabled");
        Assert.True(settings.HasUnsavedChanges);
        Assert.Equal("enabled", Level("low").Value);
        Assert.Equal("disabled", Level("high").Value);

        Level("low").SetValue("default");
        Assert.False(settings.HasUnsavedChanges);
        Assert.Equal("default", Level("high").Value);
    }

    private sealed class Client(LlmProviderDefinition definition) : ILlmClient, IExternalLlmAuthenticationProbe
    {
        public bool Authenticated { get; set; } = true;
        public Task<bool> IsAuthenticatedAsync(LlmConnection connection, CancellationToken cancellationToken = default) => Task.FromResult(Authenticated);
        public int Calls { get; private set; }
        public LlmTransport Transport => definition.Transport;
        public LlmProviderDefinition ProviderDefinition => definition;
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new() { SupportsModelListing = true };
        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<IReadOnlyList<LlmModel>>([new("model", "Model")]); }
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            yield return new LlmChatCompletionEvent(new LlmChatResult { Text = "OK", Status = LlmCompletionStatus.Completed, Diagnostics = new(200, null, null) });
            await Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FinishedSettingsOperationsRetireTheirPrivateCatalogOwner(bool query, bool fail)
    {
        var client = new ListingClient { ReleaseImmediately = true, Failure = fail ? new IOException("controlled listing failure") : null };
        await using var provider = new LlmProviderService(new UnusedSource(), [client]);
        var (manager, settings) = await OpenAsync(provider, client.Transport,
            new(SetupChangeIntent.Edit, "id", "v1"), new Target(), Connection(client.Transport, "key"), "model");

        if (query)
            Assert.Equal(fail ? SetupChoiceState.Failed : SetupChoiceState.Completed, (await manager.QueryChoicesAsync(settings, "model", new())).State);
        else
            Assert.Equal(fail ? SetupActionState.Failed : SetupActionState.Completed, (await manager.RunAsync(settings, "check")).State);

        CancellationToken token = await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(token.IsCancellationRequested);
        Assert.False(client.Disposed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancelledSettingsOperationsDrainTheirPrivateCatalogOwner(bool query, bool ignoreCancellation)
    {
        var client = new ListingClient { IgnoreCancellation = ignoreCancellation };
        await using var provider = new LlmProviderService(new UnusedSource(), [client]);
        var (manager, settings) = await OpenAsync(provider, client.Transport,
            new(SetupChangeIntent.Edit, "id", "v1"), new Target(), Connection(client.Transport, "key"), "model");
        using var caller = new CancellationTokenSource();
        Task<SetupChoicePage>? choices = query ? manager.QueryChoicesAsync(settings, "model", new(), caller.Token) : null;
        Task<SetupActionResult>? action = query ? null : manager.RunAsync(settings, "check", cancellationToken: caller.Token);
        Task operation = (Task?)choices ?? action!;
        CancellationToken token = await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(token.IsCancellationRequested);
        try
        {
            await caller.CancelAsync();
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!token.IsCancellationRequested) await Task.Delay(10, bound.Token);
            if (ignoreCancellation) Assert.False(operation.IsCompleted);
            client.Release.TrySetResult();
            await operation.WaitAsync(bound.Token);
            if (query) Assert.Equal(SetupChoiceState.Cancelled, (await choices!).State);
            else Assert.Equal(SetupActionState.Cancelled, (await action!).State);
            Assert.False(client.Disposed);
            Assert.NotEqual(true, settings.Status.Authenticated.Value);

            Assert.Equal(SetupChoiceState.Completed, (await manager.QueryChoicesAsync(settings, "model", new())).State);
            Assert.Equal(2, client.Calls);
            Assert.False(client.Disposed);
        }
        finally
        {
            client.Release.TrySetResult();
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class ListingClient : ILlmClient, IDisposable
    {
        public TaskCompletionSource<CancellationToken> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReleaseImmediately { get; init; }
        public bool IgnoreCancellation { get; init; }
        public Exception? Failure { get; init; }
        public bool Disposed { get; private set; }
        public int Calls { get; private set; }
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        public LlmProviderDefinition ProviderDefinition => ProviderDefinitions.OpenAiApiKey;
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new() { SupportsModelListing = true };

        public async Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
        {
            Calls++;
            Entered.TrySetResult(cancellationToken);
            if (Failure is not null) throw Failure;
            if (ReleaseImmediately) return [new("model", "Model")];
            if (IgnoreCancellation) await Release.Task;
            else await Release.Task.WaitAsync(cancellationToken);
            return [new("model", "Model")];
        }

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Dispose() => Disposed = true;
    }

    private sealed class BrowserAuth : IClaudePkceLogin
    {
        public int Completed { get; private set; }
        public LlmTransport Transport => LlmTransport.AnthropicClaudeCli;
        public LlmAuthorizationSession Start(string redirectUri) => new()
        { AuthorizeUrl = "https://example.test/authorize", State = "state", CodeVerifier = "private-verifier", RedirectUri = redirectUri };
        public Task<LlmCredential> CompleteAsync(LlmAuthorizationSession session, string code, string state, CancellationToken cancellationToken = default)
        { Completed++; return Task.FromResult(new LlmCredential { OAuth = new() { AccessToken = "catalog-token" } }); }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default) => Task.FromResult(credential);
    }

    private sealed class DeviceAuth : ICodexDeviceLogin
    {
        public LlmTransport Transport => LlmTransport.OpenAiCodex;
        public Task<LlmDeviceCode> StartAsync(CancellationToken cancellationToken = default) => Task.FromResult(new LlmDeviceCode("https://example.test/authorize", "code", "private-id", 1));
        public Task<LlmCredential> CompleteAsync(string deviceAuthId, string userCode, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCredential { OAuth = new() { AccessToken = "device-token" } });
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default) => Task.FromResult(credential);
    }
}
