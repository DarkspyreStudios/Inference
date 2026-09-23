using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class LlmSetupSessionTests
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
            await using LlmSetupSession session = provider.CreateSetupSession(definition.Transport,
                new(SetupChangeIntent.Create, "new", null), new Target());
            Assert.Equal(definition.Id, session.Snapshot.Definition.Implementation.Id);
            Assert.NotEmpty(Fields(session));
            Assert.Contains(session.Snapshot.Actions, action => action.Descriptor.Purpose == SetupActionPurpose.Test
                && action.Descriptor.SideEffects.HasFlag(SetupSideEffect.PotentialBilling));
            Assert.Null(session.Snapshot.Status.Ready.Value);
        }
    }

    [Fact]
    public async Task ApiKeyEditStaysInDraftAndPreservesVersionAtCommit()
    {
        var target = new Target();
        var client = new Client(ProviderDefinitions.OpenAiApiKey);
        var provider = new LlmProviderService(new UnusedSource(), [client]);
        LlmConnection original = Connection(LlmTransport.OpenAiResponses, "saved-key");
        await using LlmSetupSession session = provider.CreateSetupSession(client.Transport,
            new(SetupChangeIntent.Edit, "id", "v1"), target, original, "model");
        SetupSecretField key = Assert.Single(Fields(session).OfType<SetupSecretField>());
        Assert.True(key.HasValue);
        Assert.Equal(SetupEditState.Applied, key.SetValue(SetupSecretEdit.Replace("replacement-key")).State);
        Assert.Equal("saved-key", LlmCredential.Parse(original.Credential)!.ApiKey);
        Assert.Equal(0, target.Calls);
        Assert.Equal(SetupCommitState.Committed, (await session.CommitAsync()).State);
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
        await using LlmSetupSession session = provider.CreateSetupSession(LlmTransport.OpenAiResponses,
            new(SetupChangeIntent.Edit, "id", "v1"), target, Connection(LlmTransport.OpenAiResponses, "saved-key"), "model");
        Assert.Single(Fields(session).OfType<SetupSecretField>()).SetValue(SetupSecretEdit.Clear);
        Assert.True(session.Snapshot.IsDirty);
        Assert.False((await session.ValidateAsync()).IsValid);
        Assert.Single(Fields(session).OfType<SetupSecretField>()).SetValue(SetupSecretEdit.Keep);
        Assert.False(session.Snapshot.IsDirty);
        Assert.True((await session.ValidateAsync()).IsValid);
        await session.CommitAsync();
        Assert.Equal(SetupSecretEditKind.Keep, target.Changes!.Credential.Kind);
    }

    [Fact]
    public async Task TestIsExplicitAndUsesTheDraftWithoutSaving()
    {
        var target = new Target();
        var client = new Client(ProviderDefinitions.OpenAiApiKey);
        var provider = new LlmProviderService(new UnusedSource(), [client]);
        await using LlmSetupSession session = provider.CreateSetupSession(client.Transport,
            new(SetupChangeIntent.Edit, "id", "v1"), target, Connection(client.Transport, "saved-key"), "model");
        Assert.Equal(0, client.Calls);
        SetupActionResult result = await Action(session, SetupActionPurpose.Test).ExecuteAsync();
        Assert.Equal(SetupActionState.Completed, result.State);
        Assert.True(session.Snapshot.Status.Ready.Value);
        Assert.False(session.Snapshot.IsDirty);
        Assert.Equal(0, target.Calls);
    }

    [Fact]
    public async Task BrowserAuthorizationCanContinueFromRefreshedSnapshotAndOnlyCommitsThroughHost()
    {
        var target = new Target();
        var auth = new BrowserAuth();
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ProviderDefinitions.ClaudeSubscription)], [auth]);
        await using LlmSetupSession session = provider.CreateSetupSession(LlmTransport.AnthropicClaudeCli,
            new(SetupChangeIntent.Create, "id", null), target, modelId: "model");
        Assert.Equal(SetupActionState.NeedsInteraction, (await Action(session, SetupActionPurpose.Authenticate).ExecuteAsync()).State);
        SetupBrowserAuthorization interaction = Assert.IsType<SetupBrowserAuthorization>(session.Snapshot.Interaction);
        Assert.Equal(SetupEditState.Applied, interaction.Callback.SetValue("code#state").State);
        Assert.Equal(SetupActionState.Stale, (await interaction.Continue.ExecuteAsync()).State);
        SetupBrowserAuthorization current = Assert.IsType<SetupBrowserAuthorization>(session.Snapshot.Interaction);
        Assert.Equal(interaction.Callback.Description.Id, current.Callback.Description.Id);
        Assert.Empty(current.Callback.Value);
        Assert.True(session.Snapshot.IsDirty);
        Assert.Equal(SetupActionState.Completed, (await current.Continue.ExecuteAsync()).State);
        Assert.Equal(1, auth.Completed);
        Assert.Null(session.Snapshot.Interaction);
        Assert.True(session.Snapshot.IsDirty); // The acquired credential is still an unsaved draft mutation.
        Assert.Null(session.Snapshot.Status.Authenticated.Value); // Catalog OAuth is not CLI login.
        Assert.Equal(0, target.Calls);
        await session.CommitAsync();
        Assert.Equal("catalog-token", LlmCredential.Parse(target.Changes!.Credential.Value)!.OAuth!.AccessToken);
    }

    [Fact]
    public async Task EndpointEditInvalidatesPendingAuthorizationEvenAfterCallbackWasEntered()
    {
        var auth = new BrowserAuth();
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ProviderDefinitions.ClaudeSubscription)], [auth]);
        await using LlmSetupSession session = provider.CreateSetupSession(LlmTransport.AnthropicClaudeCli,
            new(SetupChangeIntent.Create, "id", null), new Target(), modelId: "model");
        await Action(session, SetupActionPurpose.Authenticate).ExecuteAsync();
        Assert.IsType<SetupBrowserAuthorization>(session.Snapshot.Interaction).Callback.SetValue("code#state");
        Assert.Single(Fields(session).OfType<SetupExecutableField>()).SetValue(new("different-cli"));
        Assert.Null(session.Snapshot.Interaction);
        Assert.DoesNotContain(session.Snapshot.Actions, action => action.Descriptor.Purpose == SetupActionPurpose.ContinueAuthentication);
        Assert.Equal(0, auth.Completed);
    }

    [Fact]
    public async Task HostConflictDoesNotRetryOrDiscardTheDraft()
    {
        var target = new Target { Result = new(SetupCommitState.Conflict) };
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ProviderDefinitions.OpenAiApiKey)]);
        await using LlmSetupSession session = provider.CreateSetupSession(LlmTransport.OpenAiResponses,
            new(SetupChangeIntent.Edit, "id", "v1"), target, Connection(LlmTransport.OpenAiResponses, "saved-key"), "model");
        Assert.Equal(SetupCommitState.Conflict, (await session.CommitAsync()).State);
        Assert.Equal(1, target.Calls);
        Assert.False(session.Snapshot.IsClosed);
    }

    [Fact]
    public async Task DeviceAuthorizationUsesTheDraftAndLeavesSavedConnectionsAlone()
    {
        var target = new Target();
        var auth = new DeviceAuth();
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ProviderDefinitions.ChatGptSubscription)], [auth]);
        await using LlmSetupSession session = provider.CreateSetupSession(LlmTransport.OpenAiCodex,
            new(SetupChangeIntent.Create, "id", null), target, modelId: "model");
        await Action(session, SetupActionPurpose.Authenticate).ExecuteAsync();
        SetupDeviceAuthorization interaction = Assert.IsType<SetupDeviceAuthorization>(session.Snapshot.Interaction);
        Assert.Equal("code", interaction.UserCode);
        Assert.Null(interaction.ExpiresAt); // The existing upstream contract does not report expiry.
        // A status-only publication must not invalidate the authorization identity.
        await Action(session, SetupActionPurpose.CheckPrerequisites).ExecuteAsync();
        SetupDeviceAuthorization current = Assert.IsType<SetupDeviceAuthorization>(session.Snapshot.Interaction);
        Assert.Equal(SetupActionState.Completed, (await current.Continue.ExecuteAsync()).State);
        Assert.True(session.Snapshot.Status.Authenticated.Value);
        Assert.Equal(0, target.Calls);
        await session.CommitAsync();
        Assert.Equal("device-token", LlmCredential.Parse(target.Changes!.Credential.Value)!.OAuth!.AccessToken);
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
        await using LlmSetupSession session = provider.CreateSetupSession(existing.Transport,
            new(SetupChangeIntent.Edit, "id", "v1"), target, existing, "model");
        SetupChoiceField Field(string id) => Assert.Single(Fields(session).OfType<SetupChoiceField>(), field => field.Description.Id == id);
        SetupChoiceField oldField = Field("model-listing");
        Assert.Equal(SetupEditState.Invalid, Field("model-listing").SetValue("unexpected").State);
        Assert.Equal(SetupEditState.Applied, Field("model-listing").SetValue("disabled").State);
        Assert.Equal(SetupChoiceState.Stale, (await oldField.GetChoicesAsync(new(session.Snapshot.Revision))).State);
        Assert.Equal(SetupEditState.Applied, Field("temperature").SetValue("default").State);
        Assert.Equal(SetupCommitState.Committed, (await session.CommitAsync()).State);
        LlmCapabilityOverrides overrides = target.Changes!.Configuration.Connection.CapabilityOverrides!;
        Assert.False(overrides.SupportsModelListing);
        Assert.Null(overrides.SupportsTemperature);
        Assert.True(overrides.SupportsToolUse);
        Assert.True(target.Changes.RequiresRetest);
    }
    private static IEnumerable<SetupField> Fields(LlmSetupSession session) => session.Snapshot.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields);
    private static ISetupAction Action(LlmSetupSession session, SetupActionPurpose purpose) => Assert.Single(session.Snapshot.Actions, action => action.Descriptor.Purpose == purpose);

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
    public async Task CliPrerequisiteIsPublishedAndReboundAfterOtherDraftRevisions()
    {
        var client = new Client(ProviderDefinitions.ClaudeSubscription) { Authenticated = false };
        var provider = new LlmProviderService(new UnusedSource(), [client]);
        await using LlmSetupSession session = provider.CreateSetupSession(client.Transport,
            new(SetupChangeIntent.Create, "id", null), new Target(), modelId: "model");
        Assert.Equal(SetupActionState.NeedsInteraction, (await Action(session, SetupActionPurpose.CheckPrerequisites).ExecuteAsync()).State);
        SetupExternalPrerequisite first = Assert.IsType<SetupExternalPrerequisite>(session.Snapshot.Interaction);
        Assert.False(string.IsNullOrWhiteSpace(first.Instructions));
        Assert.False(session.Snapshot.IsDirty);
        await first.Recheck.ExecuteAsync();
        Assert.Equal(SetupActionState.Stale, (await first.Recheck.ExecuteAsync()).State);
        client.Authenticated = true;
        SetupExternalPrerequisite current = Assert.IsType<SetupExternalPrerequisite>(session.Snapshot.Interaction);
        Assert.Equal(SetupActionState.Completed, (await current.Recheck.ExecuteAsync()).State);
        Assert.Null(session.Snapshot.Interaction);
        Assert.False(session.Snapshot.IsDirty);
    }

    [Fact]
    public async Task CallbackRestartChangesIdentityAndNeverEchoesEnteredCode()
    {
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ProviderDefinitions.ClaudeSubscription)], [new BrowserAuth()]);
        await using LlmSetupSession session = provider.CreateSetupSession(LlmTransport.AnthropicClaudeCli,
            new(SetupChangeIntent.Create, "id", null), new Target(), modelId: "model");
        await Action(session, SetupActionPurpose.Authenticate).ExecuteAsync();
        SetupBrowserAuthorization first = Assert.IsType<SetupBrowserAuthorization>(session.Snapshot.Interaction);
        first.Callback.SetValue("sensitive-code");
        Assert.Empty(Assert.IsType<SetupBrowserAuthorization>(session.Snapshot.Interaction).Callback.Value);
        await Action(session, SetupActionPurpose.CancelAuthentication).ExecuteAsync();
        Assert.False(session.Snapshot.IsDirty);
        await Action(session, SetupActionPurpose.Authenticate).ExecuteAsync();
        Assert.NotEqual(first.Callback.Description.Id, Assert.IsType<SetupBrowserAuthorization>(session.Snapshot.Interaction).Callback.Description.Id);
    }

    [Fact]
    public async Task ReturningCapabilityOverrideToDefaultRestoresCleanDraft()
    {
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ProviderDefinitions.OpenAiCompatible)]);
        await using LlmSetupSession session = provider.CreateSetupSession(LlmTransport.OpenAiCompatibleChat,
            new(SetupChangeIntent.Create, "id", null), new Target());
        SetupChoiceField Field() => Assert.Single(Fields(session).OfType<SetupChoiceField>(), field => field.Description.Id == "streaming");
        Field().SetValue("enabled");
        Assert.True(session.Snapshot.IsDirty);
        Field().SetValue("default");
        Assert.False(session.Snapshot.IsDirty);
        Assert.Equal(SetupChoiceCapabilities.Bounded, Field().Capabilities);
    }

    private static readonly string[] DefaultedLevels = ["none", "low", "high", "extrahigh"];

    [Fact]
    public async Task ReasoningLevelOverrideBecomesExplicitAndReturnsToDefault()
    {
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ProviderDefinitions.OpenAiCompatible)]);
        await using LlmSetupSession session = provider.CreateSetupSession(LlmTransport.OpenAiCompatibleChat,
            new(SetupChangeIntent.Create, "id", null), new Target());
        SetupChoiceField Level(string name) =>
            Assert.Single(Fields(session).OfType<SetupChoiceField>(), field => field.Description.Id == "reasoning-level-" + name);

        Assert.Equal(7, Fields(session).OfType<SetupChoiceField>().Count(field => field.Description.Id.StartsWith("reasoning-level-", StringComparison.Ordinal)));
        foreach (string name in DefaultedLevels) { Assert.Equal("default", Level(name).Value); }

        Level("low").SetValue("enabled");
        Assert.True(session.Snapshot.IsDirty);
        Assert.Equal("enabled", Level("low").Value);
        Assert.Equal("disabled", Level("high").Value);

        Level("low").SetValue("default");
        Assert.False(session.Snapshot.IsDirty);
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
            yield return new LlmCompletionEvent(new LlmChatResult { Text = "OK", Status = LlmCompletionStatus.Completed, Diagnostics = new(200, null, null) });
            await Task.CompletedTask;
        }
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
