using Darkspyre.Inference.Common;
using System.Net;
using System.Text.Json.Nodes;
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
    public async Task DirectCodexHasNoEndpointFieldAndReplacesAFormerEndpointOnSave()
    {
        var provider = new LlmProviderService(new UnusedSource(), [new Client(CodexDefinition.Value)], [new DeviceAuth()]);
        string official = CodexDefinition.Value.DefaultEndpoint;
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

    /// <summary>An OpenAI API connection saves without a key, such as one for a local model server (B1, S10).</summary>
    [Fact]
    public async Task OpenAiApiConnectionSavesWithoutKey()
    {
        using var provider = new LlmProviderService(new UnusedSource(), [new Client(OpenAiApiDefinition.Value)]);
        var target = new Target();
        var (manager, settings) = await OpenAsync(provider, LlmTransport.OpenAiResponses,
            new(SetupChangeIntent.Create, "id", null), target, modelId: "model");
        using (manager)
        {
            SetupSecretField key = Assert.IsType<SetupSecretField>(Fields(settings).Single(field => field.Description.Id == "api-key"));
            Assert.False(key.Description.IsRequired);
            Assert.DoesNotContain(settings.Validate().Issues, issue => issue.Code == "key-required");
            Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
            Assert.Null(LlmCredential.Parse(target.Changes!.Configuration.Connection.Credential)?.ApiKey);
        }
    }

    /// <summary>A SelfHost connection's endpoint is an engine id, so its settings show the engine choice and no
    /// endpoint URL field (its Engine endpoint declaration).</summary>
    [Fact]
    public async Task SelfHostSettingsShowTheEngineChoiceAndNoEndpointField()
    {
        using var provider = new LlmProviderService(new UnusedSource(), [new Client(SelfHostDefinition.Value)]);
        var (manager, settings) = await OpenAsync(provider, LlmTransport.SelfHost,
            new(SetupChangeIntent.Create, "id", null), new Target(), modelId: "model");
        using (manager)
        {
            Assert.IsType<SetupChoiceField>(Fields(settings).Single(field => field.Description.Id == "engine"));
            Assert.DoesNotContain(Fields(settings), field => field.Description.Id == "endpoint");
        }
    }

    /// <summary>Complete context-length rows become model overrides; a removed row clears only its length, and an
    /// incomplete or duplicate row blocks the save (B6).</summary>
    [Fact]
    public async Task ContextLengthRowsWriteModelOverrides()
    {
        using var provider = new LlmProviderService(new UnusedSource(), [new Client(OpenAiApiDefinition.Value)]);
        var target = new Target();
        LlmConnection existing = new()
        {
            Version = "v1",
            Transport = LlmTransport.OpenAiResponses,
            Endpoint = "http://local.test/v1",
            ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides>
            {
                ["a"] = new() { MaxContextTokens = 1000, SupportsTemperature = InferenceSupport.Unsupported },
                ["b"] = new() { MaxContextTokens = 2000 }
            }
        };
        var (manager, settings) = await OpenAsync(provider, LlmTransport.OpenAiResponses,
            new(SetupChangeIntent.Edit, "id", "v1"), target, existing, "model");
        using (manager)
        {
            SetupRepeatableGroupField Group() => Assert.IsType<SetupRepeatableGroupField>(
                Fields(settings).Single(field => field.Description.Id == "context-lengths"));
            SetupFieldRow Added()
            {
                string[] before = [.. Group().Rows.Select(row => row.Id)];
                Assert.Equal(SetupEditState.Applied, Group().Add().State);
                return Group().Rows.Single(row => !before.Contains(row.Id));
            }
            void Set(SetupFieldRow row, string? model, long? length)
            {
                if (model is not null)
                    Assert.Equal(SetupEditState.Applied, Assert.IsType<SetupChoiceField>(row.Fields[0]).SetValue(model).State);
                if (length is not null)
                    Assert.Equal(SetupEditState.Applied, Assert.IsType<SetupIntegerField>(row.Fields[1]).SetValue(length).State);
            }
            IEnumerable<string> Codes() => settings.Validate().Issues.Select(issue => issue.Code);

            Assert.Equal(["a", "b"], Group().Rows.Select(row => Assert.IsType<SetupChoiceField>(row.Fields[0]).Value));
            string a = Group().Rows[0].Id, b = Group().Rows[1].Id;
            Assert.Equal(SetupEditState.Applied, Group().Remove(a).State);
            Assert.Equal(SetupEditState.Applied, Group().Remove(b).State);

            SetupFieldRow c = Added();
            Set(c, "c", null);
            Assert.Contains("context-length-incomplete", Codes());
            Set(c, null, 3000);
            Assert.DoesNotContain("context-length-incomplete", Codes());

            SetupFieldRow duplicate = Added();
            Set(duplicate, "c", 5);
            Assert.Contains("context-length-duplicate", Codes());
            Assert.Equal(SetupEditState.Applied, Group().Remove(duplicate.Id).State);
            Assert.Empty(Codes());

            Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
            IReadOnlyDictionary<string, LlmCapabilityOverrides> saved = target.Changes!.Configuration.Connection.ModelCapabilityOverrides;
            Assert.Equal(["a", "c"], saved.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(new LlmCapabilityOverrides { SupportsTemperature = InferenceSupport.Unsupported }, saved["a"]);
            Assert.Equal(3000, saved["c"].MaxContextTokens);
        }
    }

    /// <summary>Each registered provider carries its add-connection group and its own logo in the subset the host's icon draws.</summary>
    [Fact]
    public void RegisteredProvidersHaveAGroupAndTheirIcon()
    {
        SetupIcon openAi = SetupIcons.FromResource(typeof(SetupIcons).Assembly, SetupIcons.OpenAiResource);
        (LlmProviderDefinition Definition, SetupProviderGroup Group, string? Logo)[] registered =
        [
            (ClaudeDefinition.Value, SetupProviderGroup.Subscription, "claude"),
            (CodexDefinition.Value, SetupProviderGroup.Subscription, "openai"),
            (OpenAiApiDefinition.Value, SetupProviderGroup.ApiKey, "openai"),
            (SelfHostDefinition.Value, SetupProviderGroup.More, null),
        ];
        foreach ((LlmProviderDefinition definition, SetupProviderGroup group, string? logo) in registered)
        {
            InferenceSetupDefinition setup = definition.SetupDefinition;
            Assert.Equal(group, setup.Group);
            Assert.Equal(definition.Icon, setup.Icon);
            if (logo is null) { Assert.Null(setup.Icon); continue; }
            SetupIcon icon = Assert.IsType<SetupIcon>(setup.Icon);
            Assert.Matches(@"^-?[\d.]+ -?[\d.]+ [\d.]+ [\d.]+$", icon.ViewBox);
            System.Xml.Linq.XElement[] elements = [.. System.Xml.Linq.XElement.Parse("<svg>" + icon.Markup + "</svg>").Elements()];
            Assert.NotEmpty(elements);
            Assert.All(elements, element =>
            {
                Assert.True(element.Name.LocalName is "path" or "rect" or "circle", element.Name.LocalName);
                Assert.Empty(element.Elements());
                Assert.Null(element.Attribute("transform"));
                // A provider's own mark keeps its colour, or draws in the theme's text colour where the provider shows it so.
                Assert.Matches("^(#[0-9A-F]{6}|currentColor)$", (string?)element.Attribute("fill") ?? string.Empty);
            });
            if (logo == "openai") Assert.Equal(openAi, icon);
            else Assert.NotEqual(openAi, icon);
        }
    }

    /// <summary>The state table: a signed-out tool with its own sign-in shows installed and offers the sign-in, not the install prompt.</summary>
    [Fact]
    public async Task InstalledButSignedOutShowsInstalledAndSignIn()
    {
        var claude = new Client(ClaudeDefinition.Value) { State = ExternalAuthenticationState.SignedOut };
        var provider = new LlmProviderService(new UnusedSource(), [claude], [new BrowserAuth()]);
        var (manager, settings) = await OpenAsync(provider, LlmTransport.AnthropicClaudeCli,
            new(SetupChangeIntent.Create, "id", null), new Target(), modelId: "model");
        Assert.Equal(SetupObservation.Unknown, settings.Status.Installed);

        SetupActionResult result = await manager.RunAsync(settings, "check");

        Assert.Equal(SetupActionState.Completed, result.State);
        Assert.Equal((true, false), (settings.Status.Installed.Value, settings.Status.Authenticated.Value));
        Assert.Null(settings.Interaction);
        Assert.Contains(settings.Actions, action => action.Id == "sign-in-claude");

        claude.State = ExternalAuthenticationState.NotInstalled;
        result = await manager.RunAsync(settings, "check");
        Assert.Equal(SetupActionState.NeedsInteraction, result.State);
        Assert.Equal((false, false), (settings.Status.Installed.Value, settings.Status.Authenticated.Value));
        Assert.IsType<SetupExternalPrerequisite>(settings.Interaction);

        // The CLI's own login does not sign a Claude connection in; only its own account token does.
        claude.State = ExternalAuthenticationState.SignedIn;
        result = await manager.RunAsync(settings, "check");
        Assert.Equal(SetupActionState.Completed, result.State);
        Assert.Equal((true, false), (settings.Status.Installed.Value, settings.Status.Authenticated.Value));
        Assert.Null(settings.Interaction);
        Assert.Contains(settings.Actions, action => action.Id == "sign-in-claude");

        Assert.Equal(SetupEditState.Applied, Assert.IsType<SetupExecutableField>(Fields(settings)
            .Single(field => field.Description.Id == "executable")).SetValue(new("/opt/claude")).State);
        Assert.True(settings.Status.Installed.IsStale);
    }

    [Fact]
    public async Task ModelChoicesCarryContextImageAndThinkingTags()
    {
        var client = new Client(OpenAiApiDefinition.Value)
        {
            Models =
            [
                new("rich", "Rich", Capabilities: new()
                {
                    SupportsPromptImages = InferenceSupport.Supported,
                    ReasoningLevels = new HashSet<LlmReasoningLevel> { LlmReasoningLevel.High }
                }) { MaxContextTokens = 200_000 },
                new("plain", "Plain", Capabilities: new() { SupportsPromptImages = InferenceSupport.Unsupported }),
                new("unknown", "Unknown") { MaxContextTokens = 8_000 },
            ]
        };
        using var provider = new LlmProviderService(new UnusedSource(), [client]);
        var (manager, settings) = await OpenAsync(provider, LlmTransport.OpenAiResponses, new(SetupChangeIntent.Create, "id", null), new Target());
        using (manager)
        {
            SetupChoicePage page = await manager.QueryChoicesAsync(settings, "model", new());

            Assert.Equal([("plain", []), ("rich", ["200K context", "Images", "Thinking"]), ("unknown", [])],
                page.Choices.Select(choice => (choice.Value, (IReadOnlyList<string>)choice.Tags)));
        }
    }

    [Fact]
    public async Task TestActionReportsTheReplyAndElapsedTime()
    {
        using var provider = new LlmProviderService(new UnusedSource(), [new Client(OpenAiApiDefinition.Value)]);
        var (manager, settings) = await OpenAsync(provider, LlmTransport.OpenAiResponses, new(SetupChangeIntent.Create, "id", null),
            new Target(), modelId: "model");
        using (manager)
        {
            await manager.QueryChoicesAsync(settings, "model", new());

            SetupActionResult result = await manager.RunAsync(settings, "test");

            Assert.Equal(SetupActionState.Completed, result.State);
            Assert.Equal("Model answered \"OK\"", result.Message);
            Assert.NotNull(result.Elapsed);
            Assert.Equal(InferenceTask.TextGeneration, settings.SetupTask);
            Assert.Equal("Test selected text model", Action(settings, SetupActionPurpose.Test).DisplayName);
        }

        using var handler = new DirectCodexTests.Handler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"model":"model","data":[{"index":0,"embedding":[0.1,0.2]}]}""")
        });
        using var http = new HttpClient(handler);
        var target = new Target();
        LlmConnection saved = new()
        {
            Version = "v1", Transport = LlmTransport.OpenAiResponses, Endpoint = "https://saved.example/v1",
            Credential = new LlmCredential { ApiKey = "saved-setup-key" }.Serialize()
        };
        await using var embeddingManager = provider.CreateSettingsManager(target, InferenceTask.Embedding, [new OpenAiEmbeddingClient(http)]);
        LlmSettings embedding = await embeddingManager.OpenAsync(LlmTransport.OpenAiResponses,
            new(SetupChangeIntent.Edit, "id", "v1"), saved, "model");
        Assert.IsType<SetupUriField>(Fields(embedding).Single(field => field.Description.Id == "endpoint"))
            .SetValue(new Uri("https://draft.example/v1"));
        Assert.IsType<SetupSecretField>(Fields(embedding).Single(field => field.Description.Id == "api-key"))
            .SetValue(SetupSecretEdit.Replace("draft-setup-key"));
        await embeddingManager.QueryChoicesAsync(embedding, "model", new());
        Assert.Equal(0, handler.Calls);
        Assert.Equal(InferenceTask.Embedding, embedding.SetupTask);
        SetupActionDescriptor test = Action(embedding, SetupActionPurpose.Test);
        Assert.Equal("Test selected embedding model", test.DisplayName);
        Assert.Equal(SetupSideEffect.Network | SetupSideEffect.PotentialBilling, test.SideEffects);

        SetupActionResult embedded = await embeddingManager.RunAsync(embedding, test.Id);

        Assert.Equal(SetupActionState.Completed, embedded.State);
        Assert.Equal("Embedding test succeeded for Model.", embedded.Message);
        Assert.NotNull(embedded.Elapsed);
        Assert.Equal(true, embedding.Status.Ready.Value);
        Assert.Equal(true, embedding.Status.Verified.Value);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://draft.example/v1/embeddings", handler.Uri!.AbsoluteUri);
        Assert.Equal("Bearer draft-setup-key", handler.Headers["Authorization"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"model":"model","input":["Embedding setup test."]}"""), JsonNode.Parse(handler.Body!)));
        Assert.Equal(0, target.Calls);
        Assert.Equal("https://saved.example/v1", saved.Endpoint);
    }

    private static async Task<(LlmSettingsManager Manager, LlmSettings Settings)> OpenAsync(LlmProviderService provider, LlmTransport transport,
        SetupConfigurationIdentity identity, Target target, LlmConnection? existing = null, string? modelId = null)
    {
        LlmSettingsManager manager = provider.CreateSettingsManager(target, InferenceTask.TextGeneration);
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
    public async Task DeviceLoginKeepsPollingAPendingCodeAndWidensTheIntervalOnSlowDown()
    {
        var login = new DeviceAuth();
        login.Polls.Enqueue(new LlmAuthPendingException("pending"));
        login.Polls.Enqueue(new LlmAuthPendingException("slow down") { SlowDown = true });
        var provider = new LlmProviderService(new UnusedSource(), [new Client(CodexDefinition.Value)], [login]);
        var created = new Target();
        var (manager, settings) = await OpenAsync(provider, LlmTransport.OpenAiCodex,
            new(SetupChangeIntent.Create, "id", null), created, modelId: "model");
        await manager.RunAsync(settings, Action(settings, SetupActionPurpose.Authenticate).Id);
        string check = Assert.IsType<SetupDeviceAuthorization>(settings.Interaction).Continue.Id;

        Assert.Equal(SetupActionState.NeedsInteraction, (await manager.RunAsync(settings, check)).State);
        Assert.Equal(1, settings.DeviceCode!.IntervalSeconds);
        Assert.Equal(SetupActionState.NeedsInteraction, (await manager.RunAsync(settings, check)).State);
        Assert.Equal(6, settings.DeviceCode!.IntervalSeconds);
        Assert.False(settings.CanSave);
        Assert.Equal(2, login.Completed);
    }

    [Fact]
    public async Task ClaudeSignInSavesTheTokenAsTheConnectionCredentialAndMarksItAuthenticated()
    {
        var provider = new LlmProviderService(new UnusedSource(), [new Client(ClaudeDefinition.Value)], [new BrowserAuth()]);
        var created = new Target();
        var (manager, settings) = await OpenAsync(provider, LlmTransport.AnthropicClaudeCli,
            new(SetupChangeIntent.Create, "id", null), created, modelId: "model");
        SetupActionDescriptor signIn = Action(settings, SetupActionPurpose.Authenticate);
        Assert.Equal(("sign-in-claude", "Sign in to a Claude account"), (signIn.Id, signIn.DisplayName));

        // With the CLI logged in but no account token, the connection is not signed in, lists no models and cannot be saved.
        await manager.RunAsync(settings, "check");
        Assert.Equal(false, settings.Status.Authenticated.Value);
        Assert.False(settings.CanSave);
        SetupChoicePage page = await manager.QueryChoicesAsync(settings, "model", new(string.Empty, null, 50));
        Assert.Equal("claude-subscription-sign-in-required", Assert.Single(page.Issues!).Code);

        await manager.RunAsync(settings, signIn.Id);
        Assert.IsType<SetupBrowserAuthorization>(settings.Interaction).Callback.SetValue("code#state");
        await manager.RunAsync(settings, Action(settings, SetupActionPurpose.ContinueAuthentication).Id);

        Assert.Equal(true, settings.Status.Authenticated.Value);
        Assert.Contains((await manager.QueryChoicesAsync(settings, "model", new(string.Empty, null, 50))).Choices, choice => choice.Value == "model");
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Equal("account-token", LlmCredential.Parse(created.Changes!.Credential.Value)!.OAuth!.AccessToken);
    }

    private sealed class Client(LlmProviderDefinition definition) : ILlmClient, IExternalLlmAuthenticationProbe
    {
        public ExternalAuthenticationState State { get; set; } = ExternalAuthenticationState.SignedIn;
        public Task<ExternalAuthenticationState> CheckAuthenticationAsync(LlmConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(State);
        public IReadOnlyList<LlmModel> Models { get; init; } = [new("model", "Model")];
        public int Calls { get; private set; }
        public LlmTransport Transport => definition.Transport;
        public LlmProviderDefinition ProviderDefinition => definition;
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new() { SupportsModelListing = InferenceSupport.Supported };
        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(Models); }
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            yield return new LlmChatCompletionEvent(new LlmChatResult { Text = "OK", Status = LlmCompletionStatus.Completed, Diagnostics = new(200, null, null) });
            await Task.CompletedTask;
        }
    }

    private sealed class BrowserAuth : ILlmBrowserLogin
    {
        public int Completed { get; private set; }
        public LlmTransport Transport => LlmTransport.AnthropicClaudeCli;
        public LlmAuthorizationSession Start() => new()
        { AuthorizeUrl = "https://example.test/authorize", State = "state", CodeVerifier = "private-verifier", RedirectUri = "https://example.test/callback" };
        public Task<LlmCredential> CompleteAsync(LlmAuthorizationSession session, string code, string state, CancellationToken cancellationToken = default)
        { Completed++; return Task.FromResult(new LlmCredential { OAuth = new() { AccessToken = "account-token" } }); }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default) => Task.FromResult(credential);
    }

    private sealed class DeviceAuth : ILlmDeviceLogin
    {
        /// <summary>Exceptions the next polls throw, in order; a poll with none left approves the code.</summary>
        public Queue<Exception> Polls { get; } = new();
        public int Completed { get; private set; }
        public LlmTransport Transport => LlmTransport.OpenAiCodex;
        public Task<LlmDeviceCode> StartAsync(CancellationToken cancellationToken = default) => Task.FromResult(new LlmDeviceCode("https://example.test/authorize", "code", "private-id", 1));
        public Task<LlmCredential> CompleteAsync(string deviceAuthId, string userCode, CancellationToken cancellationToken = default)
        {
            Completed++;
            return Polls.TryDequeue(out Exception? error)
                ? Task.FromException<LlmCredential>(error)
                : Task.FromResult(new LlmCredential { OAuth = new() { AccessToken = "device-token" } });
        }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default) => Task.FromResult(credential);
    }
}
