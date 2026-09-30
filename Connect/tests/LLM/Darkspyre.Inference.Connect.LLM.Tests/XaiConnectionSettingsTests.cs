using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiConnectionSettingsTests
{
    [Fact]
    public void IdentityAndOptionsDoNotEnableAnUnimplementedAdapter()
    {
        LlmProviderDefinition definition = ProviderDefinitions.GrokSubscription;
        Assert.Equal(13, (int)LlmTransport.XaiResponses);
        Assert.Equal("grok-subscription", definition.Id);
        Assert.Equal("Grok (SuperGrok / X Premium)", definition.DisplayName);
        Assert.Equal("grok-4.7", definition.DefaultModelId);
        Assert.Equal("https://api.x.ai/v1", definition.DefaultEndpoint);
        Assert.True(definition.IsGuidedSetupOption);
        Assert.Equal(["authorize-grok", "select-model", "test-connection"], definition.SetupSteps.Select(step => step.Id));
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConnectionSource, UnusedSource>();
        services.AddLlmConnect();
        using ServiceProvider container = services.BuildServiceProvider();
        using IServiceScope scope = container.CreateScope();
        Assert.DoesNotContain(container.GetRequiredService<LlmProviderCatalog>().SupportedProviders,
            provider => provider.Transport == LlmTransport.XaiResponses);
        Assert.DoesNotContain(scope.ServiceProvider.GetRequiredService<LlmProviderService>().SupportedTransports,
            transport => transport == LlmTransport.XaiResponses);
    }

    [Theory]
    [InlineData(XaiRegion.Global, "https://api.x.ai/v1")]
    [InlineData(XaiRegion.UnitedStates, "https://us.api.x.ai/v1")]
    public void ConnectionOptionsRoundTripWithoutProviderOptions(XaiRegion region, string endpoint)
    {
        var options = new XaiConnectionOptions { Region = region, ServiceTier = XaiServiceTier.Priority };
        Assert.Equal(endpoint, options.GetEndpoint());
        LlmConnection original = Saved() with { Xai = options, Endpoint = endpoint };
        LlmConnection restored = JsonSerializer.Deserialize<LlmConnection>(JsonSerializer.Serialize(original))!;
        Assert.Equal(options, restored.Xai);
        Assert.Empty(restored.ProviderOptions);
        Assert.Equal(endpoint, restored.Xai!.GetEndpoint());
        Assert.Throws<ArgumentOutOfRangeException>(() => new XaiConnectionOptions { Region = (XaiRegion)99 }.GetEndpoint());
    }

    [Fact]
    public async Task CreateExposesOnlyClosedRegionTierAndModelAndRequiresOAuth()
    {
        var (manager, settings, target, auth, client) = await OpenAsync();
        Assert.Equal(["xai-region", "xai-service-tier", "model"], Fields(settings).Select(field => field.Description.Id));
        Assert.DoesNotContain(Fields(settings), field => field is SetupSecretField or SetupUriField or SetupExecutableField);
        Assert.All(Fields(settings).OfType<SetupChoiceField>().Where(field => field.Description.Id != "model"),
            field => Assert.Equal(SetupChoiceCapabilities.Bounded, field.Capabilities));
        Assert.Equal("Global", Choice(settings, "xai-region").Value);
        Assert.Equal("Default", Choice(settings, "xai-service-tier").Value);
        Assert.Contains("1.1x", Choice(settings, "xai-region").Description.HelpText);
        Assert.Contains("2x", Choice(settings, "xai-service-tier").Description.HelpText);
        Assert.Equal(["Global", "UnitedStates"], (await manager.QueryChoicesAsync(settings, "xai-region", new())).Choices.Select(choice => choice.Value));
        Assert.Equal(["Default", "Priority"], (await manager.QueryChoicesAsync(settings, "xai-service-tier", new())).Choices.Select(choice => choice.Value));
        Assert.False(settings.HasUnsavedChanges);
        Assert.False(settings.CanSave);
        Assert.Equal(SetupCommitState.Invalid, (await manager.SaveAsync(settings)).State);
        Assert.Equal(0, auth.Starts);
        Assert.Equal(0, client.Calls);
        Assert.Equal(0, target.Calls);
    }

    [Theory]
    [InlineData("xai-region", "https://elsewhere.test/v1")]
    [InlineData("xai-region", "99")]
    [InlineData("xai-region", "1")]
    [InlineData("xai-region", "global")]
    [InlineData("xai-service-tier", "1")]
    [InlineData("xai-service-tier", "flex")]
    public async Task InvalidChoiceDoesNotMutateTheDraft(string field, string value)
    {
        var (_, settings, _, _, _) = await OpenAsync();
        Assert.Equal(SetupEditState.Invalid, Choice(settings, field).SetValue(value).State);
        Assert.False(settings.HasUnsavedChanges);
    }

    [Fact]
    public async Task SignInRoutesToXaiAndOnlySaveCommitsTypedOptionsAndCredential()
    {
        var (manager, settings, target, auth, client) = await OpenAsync();
        Choice(settings, "xai-region").SetValue("UnitedStates");
        Choice(settings, "xai-service-tier").SetValue("Priority");
        Assert.Equal(SetupActionState.NeedsInteraction, (await manager.RunAsync(settings, "authorize")).State);
        SetupDeviceAuthorization interaction = Assert.IsType<SetupDeviceAuthorization>(settings.Interaction);
        Assert.Equal("user-code", interaction.UserCode);
        Assert.Equal(auth.Code.ExpiresAt, interaction.ExpiresAt);
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, interaction.Continue.Id)).State);
        Assert.True(settings.Status.Authenticated.Value);
        Assert.Null(settings.Interaction);
        Assert.True(settings.CanSave);
        Assert.Equal(1, auth.Starts);
        Assert.Equal(1, auth.Completions);
        Assert.Equal(0, target.Calls);
        Assert.Equal(0, client.Calls);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        LlmSetupChanges changes = target.Changes!;
        Assert.Equal(new XaiConnectionOptions { Region = XaiRegion.UnitedStates, ServiceTier = XaiServiceTier.Priority }, changes.Configuration.Connection.Xai);
        Assert.Equal("https://us.api.x.ai/v1", changes.Configuration.Connection.Endpoint);
        Assert.Null(changes.Configuration.Connection.Credential);
        Assert.Equal("xai-token", LlmCredential.Parse(changes.Credential.Value)!.OAuth!.AccessToken);
        Assert.Empty(changes.Configuration.Connection.ProviderOptions);
    }

    [Fact]
    public async Task SlowDownChangesOnlyTheCurrentAuthorizationPollInterval()
    {
        var (manager, settings, target, auth, _) = await OpenAsync();
        auth.Pending = new("Wait for approval.") { SlowDown = true };
        await manager.RunAsync(settings, "authorize");
        Assert.Equal(SetupActionState.NeedsInteraction, (await manager.RunAsync(settings, "continue-authorization")).State);
        Assert.Equal(6, settings.DeviceCode!.IntervalSeconds);
        Assert.Equal(auth.Code.ExpiresAt, Assert.IsType<SetupDeviceAuthorization>(settings.Interaction).ExpiresAt);
        Assert.False(settings.CanSave);
        Assert.Equal(0, target.Calls);
        await manager.RunAsync(settings, "cancel-authorization");
        Assert.Null(settings.Interaction);
        Assert.Null(settings.DeviceCode);
        await manager.RunAsync(settings, "authorize");
        Assert.Equal(1, settings.DeviceCode!.IntervalSeconds);
    }

    [Fact]
    public async Task RegionEditCancelsPendingAuthorizationAndReturningDefaultsRestoresCleanDraft()
    {
        var (manager, settings, _, auth, _) = await OpenAsync();
        await manager.RunAsync(settings, "authorize");
        Choice(settings, "xai-region").SetValue("UnitedStates");
        Assert.Null(settings.Interaction);
        Assert.True(settings.HasUnsavedChanges);
        Assert.Equal(SetupActionState.Unavailable, (await manager.RunAsync(settings, "continue-authorization")).State);
        Choice(settings, "xai-region").SetValue("Global");
        Choice(settings, "xai-service-tier").SetValue("Priority");
        Assert.True(settings.HasUnsavedChanges);
        Choice(settings, "xai-service-tier").SetValue("Default");
        Assert.False(settings.HasUnsavedChanges);
        Assert.Equal(0, auth.Completions);
    }

    [Fact]
    public async Task SavedOAuthAllowsSaveWithoutReauthenticationAndTierChangeRequiresRetest()
    {
        var (manager, settings, target, auth, _) = await OpenAsync(Saved());
        Assert.False(settings.HasUnsavedChanges);
        Assert.True(settings.CanSave);
        Choice(settings, "xai-service-tier").SetValue("Priority");
        await manager.SaveAsync(settings);
        Assert.Equal(SetupSecretEditKind.Keep, target.Changes!.Credential.Kind);
        Assert.Equal("saved-version", target.Changes.Identity.ExpectedVersion);
        Assert.False(target.Changes.RequiresReauthentication);
        Assert.True(target.Changes.RequiresRetest);
        Assert.Equal(0, auth.Starts);
    }

    [Fact]
    public async Task OptionsEditsRetainUnrelatedConnectionDataAndDoNotMutateTheSavedSnapshot()
    {
        LlmConnection original = Saved() with
        {
            AdditionalHeaders = new Dictionary<string, string> { ["X-Trace"] = "trace" },
            ProviderOptions = new Dictionary<string, JsonElement> { ["custom"] = JsonSerializer.SerializeToElement(new { enabled = true }) },
            CapabilityOverrides = new() { SupportsStreaming = false },
            ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides> { ["grok-4.7"] = new() { SupportsTemperature = false } }
        };
        var (manager, settings, target, _, _) = await OpenAsync(original);
        Choice(settings, "xai-region").SetValue("UnitedStates");
        Choice(settings, "xai-service-tier").SetValue("Priority");
        await manager.SaveAsync(settings);
        LlmConnection saved = target.Changes!.Configuration.Connection;
        Assert.Equal("trace", saved.AdditionalHeaders["X-Trace"]);
        Assert.True(saved.ProviderOptions["custom"].GetProperty("enabled").GetBoolean());
        Assert.Equal(original.CapabilityOverrides, saved.CapabilityOverrides);
        Assert.Equal(original.ModelCapabilityOverrides["grok-4.7"], saved.ModelCapabilityOverrides["grok-4.7"]);
        Assert.Equal(XaiRegion.Global, original.Xai!.Region);
        Assert.Equal(XaiServiceTier.Default, original.Xai.ServiceTier);
        Assert.Equal("https://api.x.ai/v1", original.Endpoint);
        Assert.True(target.Changes.RequiresReauthentication);
        Assert.True(target.Changes.RequiresRetest);
    }

    [Fact]
    public async Task ExpiredAuthorizationEndsTheSetupAttemptWithoutPolling()
    {
        var (manager, settings, target, auth, _) = await OpenAsync();
        auth.Code = auth.Code with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await manager.RunAsync(settings, "authorize");
        Assert.Equal(SetupActionState.Failed, (await manager.RunAsync(settings, "continue-authorization")).State);
        Assert.Null(settings.DeviceCode);
        Assert.Null(settings.Interaction);
        Assert.Equal(0, auth.Completions);
        Assert.Equal(0, target.Calls);
        Assert.False(settings.CanSave);
    }

    [Theory]
    [InlineData("access_denied")]
    [InlineData("expired_token")]
    public async Task TerminalDeviceFailureEndsTheSetupAttempt(string code)
    {
        var (manager, settings, target, auth, _) = await OpenAsync();
        auth.Failure = new("Device authorization ended.", LlmTransport.XaiResponses, providerCode: code);
        await manager.RunAsync(settings, "authorize");
        Assert.Equal(SetupActionState.Failed, (await manager.RunAsync(settings, "continue-authorization")).State);
        Assert.Null(settings.DeviceCode);
        Assert.Null(settings.Interaction);
        Assert.False(settings.CanSave);
        Assert.Equal(0, target.Calls);
    }

    [Fact]
    public async Task TypedRegionReplacesAnArbitrarySavedEndpointWithoutGuessingItsRegion()
    {
        LlmConnection original = Saved() with { Endpoint = "https://other.test/v1", Xai = new() { Region = XaiRegion.UnitedStates } };
        var (manager, settings, target, _, _) = await OpenAsync(original);
        Assert.True(settings.HasUnsavedChanges);
        Assert.Equal("UnitedStates", Choice(settings, "xai-region").Value);
        await manager.SaveAsync(settings);
        Assert.Equal("https://us.api.x.ai/v1", target.Changes!.Configuration.Connection.Endpoint);
        Assert.True(target.Changes.RequiresReauthentication);
        Assert.Equal("https://other.test/v1", original.Endpoint);
    }

    [Theory]
    [InlineData("api-key")]
    [InlineData("static-header")]
    [InlineData("malformed")]
    [InlineData("empty-oauth")]
    public async Task SavedNonOAuthCannotSatisfySignIn(string kind)
    {
        string credential = kind switch
        {
            "api-key" => new LlmCredential { ApiKey = "key" }.Serialize(),
            "static-header" => new LlmCredential { SecretHeaders = new Dictionary<string, string> { ["X-Secret"] = "secret" } }.Serialize(),
            "malformed" => "not json",
            _ => new LlmCredential { OAuth = new() { AccessToken = "" } }.Serialize()
        };
        var (manager, settings, target, _, _) = await OpenAsync(Saved() with { Credential = credential });
        Assert.False(settings.CanSave);
        Assert.Equal(SetupCommitState.Invalid, (await manager.SaveAsync(settings)).State);
        Assert.Equal(0, target.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidPersistedEnumsFailValidationWithoutEndpointOrKeyInputs(bool region)
    {
        var (_, settings, _, _, _) = await OpenAsync(Saved() with
        { Xai = region ? new() { Region = (XaiRegion)99 } : new() { ServiceTier = (XaiServiceTier)99 } });
        Assert.False(settings.Validate().IsValid);
        Assert.Contains(settings.Validate().Issues, issue => issue.Code == (region ? "xai-region-invalid" : "xai-tier-invalid"));
        Assert.False(settings.CanSave);
    }

    private static async Task<(LlmSettingsManager, LlmSettings, Target, Authenticator, Client)> OpenAsync(LlmConnection? existing = null)
    {
        var auth = new Authenticator();
        var client = new Client();
        var target = new Target();
        var manager = new LlmSettingsManager(new Dictionary<LlmTransport, ILlmClient> { [client.Transport] = client }, [auth],
            new Dictionary<LlmTransport, ILlmSetupEnvironment>(), target);
        LlmSettings settings = await manager.OpenAsync(LlmTransport.XaiResponses,
            new(existing is null ? SetupChangeIntent.Create : SetupChangeIntent.Edit, "id", existing?.Version), existing);
        return (manager, settings, target, auth, client);
    }

    private static LlmConnection Saved() => new()
    {
        Version = "saved-version",
        Transport = LlmTransport.XaiResponses,
        Endpoint = "https://api.x.ai/v1",
        Xai = new(),
        Credential = new LlmCredential { OAuth = new() { AccessToken = "saved-token" } }.Serialize()
    };
    private static IEnumerable<SetupField> Fields(LlmSettings settings) => settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields);
    private static SetupChoiceField Choice(LlmSettings settings, string id) => Assert.Single(Fields(settings).OfType<SetupChoiceField>(), field => field.Description.Id == id);

    private sealed class UnusedSource : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Draft sign-in must not read saved connections.");
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Draft sign-in must not save directly.");
    }
    private sealed class Target : ISetupCommitTarget<LlmSetupChanges>
    {
        public int Calls { get; private set; }
        public LlmSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(LlmSetupChanges changes, CancellationToken cancellationToken = default)
        { Calls++; Changes = changes; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "next-version")); }
    }
    private sealed class Authenticator : IXaiDeviceLogin
    {
        public LlmTransport Transport => LlmTransport.XaiResponses;
        public int Starts { get; private set; }
        public int Completions { get; private set; }
        public LlmAuthPendingException? Pending { get; set; }
        public LlmProviderException? Failure { get; set; }
        public LlmDeviceCode Code { get; set; } = new("https://auth.x.ai/authorize", "user-code", "device-secret", 1)
        { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
        public Task<LlmDeviceCode> StartAsync(CancellationToken cancellationToken = default)
        { Starts++; return Task.FromResult(Code); }
        public Task<LlmCredential> CompleteAsync(string deviceAuthId, string userCode, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Code.DeviceAuthId, deviceAuthId);
            Assert.Equal(Code.UserCode, userCode);
            Completions++;
            if (Failure is { } failure) throw failure;
            if (Pending is { } pending) throw pending;
            return Task.FromResult(new LlmCredential { OAuth = new() { AccessToken = "xai-token" } });
        }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default) => Task.FromResult(credential);
    }
    private sealed class Client : ILlmClient
    {
        public LlmTransport Transport => LlmTransport.XaiResponses;
        public LlmProviderDefinition ProviderDefinition => ProviderDefinitions.GrokSubscription;
        public int Calls { get; private set; }
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new() { SupportsModelListing = true };
        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<IReadOnlyList<LlmModel>>([new("grok-4.7", "Grok")]); }
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.CompletedTask;
            yield return new LlmChatCompletionEvent(new LlmChatResult { Text = "OK", Status = LlmCompletionStatus.Completed, Diagnostics = new(200, null, null) });
        }
    }
}
