using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class LlmSettingsTests
{
    [Theory]
    [InlineData(LlmTransport.AnthropicClaudeCli)]
    [InlineData(LlmTransport.GoogleAntigravityCli)]
    public async Task BlankCliExecutableUsesProviderDefaultForChecksAndSave(LlmTransport transport)
    {
        LlmProviderDefinition definition = transport == LlmTransport.AnthropicClaudeCli
            ? ProviderDefinitions.ClaudeSubscription : ProviderDefinitions.GeminiSubscription;
        using var provider = new LlmProviderService(new UnusedSource(), [new Client(definition)]);
        var target = new Target();
        var (manager, settings) = await OpenAsync(provider, transport,
            new(SetupChangeIntent.Create, "id", null), target, modelId: "model");
        using (manager)
        {
            SetupExecutableField executable = Assert.IsType<SetupExecutableField>(Fields(settings)
                .Single(field => field.Description.Id == "executable"));
            Assert.Equal(SetupEditState.Applied, executable.SetValue(new(string.Empty)).State);

            Assert.True(settings.Validate().IsValid);
            Assert.True(settings.CanSave);
            Assert.Empty(Assert.IsType<SetupExecutableField>(Fields(settings)
                .Single(field => field.Description.Id == "executable")).Value.Value);
            Assert.Equal(definition.DefaultEndpoint, settings.CurrentConnection().Endpoint);
            Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
            Assert.Equal(definition.DefaultEndpoint, target.Changes!.Configuration.Connection.Endpoint);
        }
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
