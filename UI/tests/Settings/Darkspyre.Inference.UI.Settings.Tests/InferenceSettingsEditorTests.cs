using Avalonia.Controls;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.UI.Settings;
using Xunit;

namespace Darkspyre.Inference.UI.Settings.Tests;

public sealed class InferenceSettingsEditorTests
{
    [Fact]
    public Task HostsWithTheirOwnFrameCanHideTheHeadingAndEffectLabels() => EditorSession.RunAsync(() =>
    {
        var settings = new FakeSettings();
        var manager = new FakeManager();
        InferenceSettingsEditor shown = new(settings, manager, new FakeHost());
        EditorSession.Show(shown);
        Assert.Equal("Example provider", Assert.Single(EditorSession.All<TextBlock>(shown, "h2")).Text);
        Assert.Equal("Network", Assert.Single(EditorSession.All<TextBlock>(shown, "inference-settings-effects")).Text);
        Assert.Equal("Connect", Assert.Single(EditorSession.All<TextBlock>(shown, "h3")).Text); // One step has no step list.

        InferenceSettingsEditor framed = new(settings, manager, new FakeHost()) { ShowHeading = false, ShowActionEffects = false };
        EditorSession.Show(framed);
        Assert.Empty(EditorSession.All<TextBlock>(framed, "h2"));
        Assert.Empty(EditorSession.All<TextBlock>(framed, "inference-settings-effects"));
        Assert.NotNull(EditorSession.Button(framed, "Check connection"));
        // Status facts read as chips, never as enum names.
        Assert.Equal(["Configured", "Not verified yet", "Readiness not checked"],
            EditorSession.All<ContentControl>(framed, "inference-settings-chip").Select(chip => ((TextBlock)chip.Content!).Text));
        return Task.CompletedTask;
    });

    [Fact]
    public Task EnteringTheModelAfterNextRefreshesValidationAndEnablesSave() => EditorSession.RunAsync(() =>
    {
        InferenceSettingsEditor editor = new(new ModelStepSettings(), new ModelStepManager(), new FakeHost());
        EditorSession.Show(editor);

        EditorSession.Click(EditorSession.Button(editor, "Next"));
        Assert.False(EditorSession.Button(editor, "Save settings").IsEnabled);

        EditorSession.Type(EditorSession.Field(editor, "model"), "model-a");

        Assert.True(EditorSession.Button(editor, "Save settings").IsEnabled);
        Assert.DoesNotContain("Select a model.", EditorSession.TextOf(editor), StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(SetupInstallState.Installed)]
    [InlineData(SetupInstallState.NotDownloaded)]
    [InlineData(SetupInstallState.FilesMissing)]
    public Task InstallCardShowsEachStateWithTheMockupTextAndButtons(SetupInstallState state) => EditorSession.RunAsync(() =>
    {
        var settings = new InstallSettings("small", state).Unobserved();
        InferenceSettingsEditor editor = ShowInstallEditor(settings, new InstallManager());

        SetupInstallStatusCard view = Assert.Single(EditorSession.All<SetupInstallStatusCard>(editor));
        Border card = Assert.Single(EditorSession.All<Border>(view, "inference-install-card"));
        Assert.Equal("Install state", Avalonia.Automation.AutomationProperties.GetName(card));
        string text = Squash(EditorSession.TextOf(card));
        Assert.Contains("small", text, StringComparison.Ordinal);
        string[] buttons = [.. EditorSession.All<Button>(card).Select(button => (string)button.Content!)];
        switch (state)
        {
            case SetupInstallState.Installed:
                Assert.Contains("Installed in /models", text, StringComparison.Ordinal);
                Assert.Contains("Ready to load. 1 file, 465 MB, MIT.", text, StringComparison.Ordinal);
                Assert.Equal(["Show"], buttons);
                break;
            case SetupInstallState.NotDownloaded:
                Assert.Contains("Not downloaded", text, StringComparison.Ordinal);
                Assert.Contains("This model is not in /models. The engine cannot load it until it is downloaded.", text, StringComparison.Ordinal);
                Assert.Equal(["Download (465 MB)"], buttons);
                break;
            default:
                Assert.Contains("Files missing", text, StringComparison.Ordinal);
                Assert.Contains("ggml-small.bin is missing from the model store.", text, StringComparison.Ordinal);
                Assert.Equal(["Download again", "Delete download"], buttons);
                break;
        }
        // No observation applies, so no status line.
        Assert.Empty(EditorSession.All<WrapPanel>(editor, "inference-settings-status"));
        return Task.CompletedTask;
    });

    [Fact]
    public Task NotSupportedShowsItsTextAndReasonOnTheCardAndOnTheChoiceChip() => EditorSession.RunAsync(async () =>
    {
        InferenceSettingsEditor editor = ShowInstallEditor(new InstallSettings("small", SetupInstallState.NotSupported).Unobserved(), new InstallManager());
        await EditorSession.WaitForAsync(() => Cards(editor).Length == 2);

        Border card = Assert.Single(EditorSession.All<Border>(Assert.Single(EditorSession.All<SetupInstallStatusCard>(editor)), "inference-install-card"));
        Assert.Equal(["small", "Not supported", InstallSettings.UnsupportedReason, "Delete download"], EditorSession.TextOf(card).Split('\n'));
        Assert.Equal(["Delete download"], EditorSession.All<Button>(card).Select(button => (string)button.Content!));
        Assert.Contains("destructive", card.Classes);

        Assert.Equal(["small 465 MB download Not supported", "tiny 465 MB download Not downloaded"], Cards(editor).Select(pack => Squash(EditorSession.TextOf(pack))));
        foreach (Control root in new Control[] { card, Cards(editor)[0] })
        {
            ContentControl chip = Assert.Single(EditorSession.All<ContentControl>(root, "inference-install-chip"));
            Assert.Contains("destructive", chip.Classes);
            Assert.Empty(EditorSession.All<Avalonia.Controls.Shapes.Path>(chip));
        }
        Assert.Empty(EditorSession.All<Button>(editor, "inference-model-pack-card", "selected"));
    });

    [Fact]
    public Task ClickingDownloadRunsTheDownloadActionThroughTheHost() => EditorSession.RunAsync(async () =>
    {
        var manager = new InstallManager();
        var host = new FakeHost();
        InferenceSettingsEditor editor = ShowInstallEditor(new InstallSettings("small", SetupInstallState.NotDownloaded), manager, host);

        EditorSession.Click(EditorSession.Button(Assert.Single(EditorSession.All<SetupInstallStatusCard>(editor)), "Download (465 MB)"));

        await EditorSession.WaitForAsync(() => manager.Runs.SequenceEqual(["download"]));
        Assert.Equal(1, host.ExecutionCount);
    });

    [Fact]
    public Task PackCardsShowSizeAndStateAndFollowAStatusChange() => EditorSession.RunAsync(async () =>
    {
        var manager = new InstallManager();
        InferenceSettingsEditor editor = ShowInstallEditor(new InstallSettings("small", SetupInstallState.Installed), manager);
        await EditorSession.WaitForAsync(() => Cards(editor).Length == 2);

        // The cards replace the list.
        Assert.False(Assert.Single(EditorSession.All<AutoCompleteBox>(EditorSession.Field(editor, "model"))).IsVisible);
        Assert.Equal(["small 465 MB download Installed", "tiny 465 MB download Not downloaded"], Cards(editor).Select(card => Squash(EditorSession.TextOf(card))));

        manager.Next = SetupInstallState.NotDownloaded;
        manager.RaiseStatusChanged();

        await EditorSession.WaitForAsync(() => Cards(editor).Length == 2 && Squash(EditorSession.TextOf(Cards(editor)[0])).EndsWith("Not downloaded", StringComparison.Ordinal));
        Assert.Equal(1, manager.RefreshCount);
    });

    [Theory]
    [InlineData(SetupInstallState.NotDownloaded)]
    [InlineData(SetupInstallState.FilesMissing)]
    public Task AModelThatIsNotInstalledIsNeverTheShownSelection(SetupInstallState state) => EditorSession.RunAsync(async () =>
    {
        InferenceSettingsEditor editor = ShowInstallEditor(new InstallSettings("small", state), new InstallManager());
        await EditorSession.WaitForAsync(() => Cards(editor).Length == 2);

        Assert.Empty(EditorSession.All<Button>(editor, "inference-model-pack-card", "selected"));
        Assert.Contains("small", EditorSession.TextOf(Assert.Single(EditorSession.All<SetupInstallStatusCard>(editor))), StringComparison.Ordinal);
    });

    [Fact]
    public Task AnInstalledSelectionIsTheSelectedCardAndAClickChoosesAnother() => EditorSession.RunAsync(async () =>
    {
        var settings = new InstallSettings("small", SetupInstallState.Installed);
        var host = new FakeHost();
        InferenceSettingsEditor editor = ShowInstallEditor(settings, new InstallManager(), host);
        await EditorSession.WaitForAsync(() => Cards(editor).Length == 2);
        Assert.Equal("small", Avalonia.Automation.AutomationProperties.GetName(
            Assert.Single(EditorSession.All<Button>(editor, "inference-model-pack-card", "selected"))));

        EditorSession.Click(EditorSession.Button(Assert.Single(EditorSession.All<SetupInstallStatusCard>(editor)), "Show the model folder"));
        Assert.Equal(["/models"], host.Revealed);

        EditorSession.Click(EditorSession.Button(editor, "tiny"));
        Assert.Equal("tiny", settings.Model);
    });

    private static InferenceSettingsEditor ShowInstallEditor(InstallSettings settings, InstallManager manager, FakeHost? host = null)
    {
        InferenceSettingsEditor editor = new(settings, manager, host ?? new FakeHost());
        EditorSession.Show(editor);
        return editor;
    }

    // The model pack cards the model field shows.
    private static ModelPackCard[] Cards(InferenceSettingsEditor editor) => [.. EditorSession.All<ModelPackCard>(EditorSession.Field(editor, "model"))];

    private static string Squash(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed class FakeSettings : InferenceSettings<string>
    {
        public FakeSettings() : base(
            new(new("example", "Example provider", InferenceImplementationKind.Connect, InferenceTask.TextGeneration), "Example description"),
            new(SetupChangeIntent.Create, "entry", null)) =>
            Status = new SetupStatus { Configured = SetupObservation.Known(true) };

        public string Url { get; set; } = "https://example.test";
        public bool SignedIn { get; init; } = true;
        protected override IReadOnlyList<SetupStep> CreateSteps() =>
        [new("connect", "Connect", "Enter settings", [new("endpoint", "Endpoint",
            [new SetupTextField(new("url", "URL") { IsRequired = true }, Url, string.Empty, value => Edit(() => Url = value))])])];
        protected override IReadOnlyList<SetupActionDescriptor> CreateActions() =>
            [new("check", "Check connection", SetupActionPurpose.CheckReadiness, SetupSideEffect.Network)];
        protected override SetupValidation ValidateDraft() => SetupValidation.Valid;
        protected override string CreateChanges() => Url;
        protected override bool IsDraftDirty => false;
        protected override bool IsSignInSatisfied => SignedIn;
    }

    private sealed class ModelStepSettings() : InferenceSettings<string>(
        new(new("example", "Example provider", InferenceImplementationKind.Connect, InferenceTask.TextGeneration), "Example description"),
        new(SetupChangeIntent.Create, "entry", null))
    {
        private string _url = "https://example.test", _model = string.Empty;
        protected override IReadOnlyList<SetupStep> CreateSteps() =>
        [
            new("connect", "Connect", "Enter the endpoint", [new("endpoint", "Endpoint",
                [new SetupTextField(new("url", "URL") { IsRequired = true }, _url, string.Empty, value => Edit(() => _url = value))])]),
            new("model", "Model", "Choose a model", [new("selection", "Selection",
                [new SetupTextField(new("model", "Model") { IsRequired = true }, _model, string.Empty, value => Edit(() => _model = value))])])
        ];
        protected override IReadOnlyList<SetupActionDescriptor> CreateActions() => [];
        protected override SetupValidation ValidateDraft() => _model.Length == 0
            ? new([new("model-required", "Select a model.", "model")]) : SetupValidation.Valid;
        protected override string CreateChanges() => _model;
        protected override bool IsDraftDirty => _model.Length != 0;
    }

    private sealed class ModelStepManager() : InferenceSettingsManager<ModelStepSettings, string>(new FakeTarget())
    {
        protected override Task<SetupActionResult> RunCoreAsync(ModelStepSettings settings, SetupActionDescriptor action,
            IProgress<SetupProgress>? progress, CancellationToken cancellationToken) => Task.FromResult(SetupActionResult.Completed);
    }

    private class FakeManager(FakeTarget target) : InferenceSettingsManager<FakeSettings, string>(target)
    {
        public FakeManager() : this(new FakeTarget()) { }
        public FakeTarget Target { get; } = target;
        protected override Task<SetupActionResult> RunCoreAsync(FakeSettings settings, SetupActionDescriptor action,
            IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
        {
            settings.Status = settings.Status with { Ready = SetupObservation.Known(true) };
            return Task.FromResult(SetupActionResult.Completed);
        }
    }

    private sealed class FakeStatusManager : FakeManager, IInferenceSettingsStatusSource
    {
        private EventHandler? _statusChanged;
        public int ListenerCount => _statusChanged?.GetInvocationList().Length ?? 0;
        public int RefreshCount { get; private set; }
        public event EventHandler? StatusChanged { add => _statusChanged += value; remove => _statusChanged -= value; }
        public void RaiseStatusChanged() => _statusChanged?.Invoke(this, EventArgs.Empty);
        public Task RefreshStatusAsync(InferenceSettings settings, CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            settings.Status = settings.Status with { Ready = SetupObservation.Known(true) };
            return Task.CompletedTask;
        }
    }

    // A model choice of two 465 MB models and the install card of the selected one, like the Tensor engine editor.
    private sealed class InstallSettings(string model, SetupInstallState state) : InferenceSettings<string>(
        new(new("tensor", "Tensor engine", InferenceImplementationKind.Tensor, InferenceTask.SpeechTranscription), "Example engine"),
        new(SetupChangeIntent.Create, "entry", null))
    {
        public const string UnsupportedReason = "This engine release no longer loads this model.";
        private const long Size = 487_601_967;
        private static readonly string[] Keys = ["small", "tiny"];
        private string _model = model;
        public string Model => _model;
        public SetupInstallState State { get; set; } = state;
        public InstallSettings Unobserved()
        {
            Status = new() { Configured = SetupObservation.NotApplicable, Verified = SetupObservation.NotApplicable, Ready = SetupObservation.NotApplicable };
            return this;
        }
        private SetupInstallState StateOf(string key) => key == "small" ? State : SetupInstallState.NotDownloaded;

        protected override IReadOnlyList<SetupStep> CreateSteps() =>
        [new("model", "Model", "Choose a model", [new("selection", "Selection",
        [
            new SetupChoiceField(new("model", "Model pack") { IsRequired = true }, _model, string.Empty, value => Edit(() => _model = value), false,
                capabilities: new(InferenceSupport.Supported, InferenceSupport.Supported), showValue: StateOf(_model) == SetupInstallState.Installed),
            new SetupInstallStatusField(new("install-status", "Install state") { IsReadOnly = true }, new(StateOf(_model), _model, "/models", 1, Size, "MIT",
                StateOf(_model) == SetupInstallState.FilesMissing ? "ggml-small.bin" : null,
                StateOf(_model) == SetupInstallState.NotSupported ? UnsupportedReason : null, StateOf(_model) switch
                {
                    SetupInstallState.NotDownloaded => [new("download", $"Download ({SetupByteSize.Format(Size)})", SetupActionPurpose.Install, SetupSideEffect.Download)],
                    SetupInstallState.NotSupported => [new("remove", "Delete download", SetupActionPurpose.Remove, SetupSideEffect.DeleteFiles)],
                    SetupInstallState.FilesMissing =>
                    [
                        new("download", "Download again", SetupActionPurpose.Install, SetupSideEffect.Download),
                        new("remove", "Delete download", SetupActionPurpose.Remove, SetupSideEffect.DeleteFiles)
                    ],
                    _ => []
                }))
        ])])];
        protected override IReadOnlyList<SetupActionDescriptor> CreateActions() => [];
        protected override SetupChoicePage? GetLocalChoices(string fieldId, SetupChoiceQuery query) => fieldId == "model"
            ? new(Keys.Select(key => new SetupChoice(key, key, Install: new(StateOf(key) == SetupInstallState.FilesMissing ? SetupInstallState.NotDownloaded : StateOf(key), Size))).ToArray())
            : null;
        protected override SetupValidation ValidateDraft() => SetupValidation.Valid;
        protected override string CreateChanges() => _model;
        protected override bool IsDraftDirty => false;
    }

    private sealed class InstallManager() : InferenceSettingsManager<InstallSettings, string>(new FakeTarget()), IInferenceSettingsStatusSource
    {
        private EventHandler? _statusChanged;
        public List<string> Runs { get; } = [];
        public SetupInstallState? Next { get; set; }
        public int RefreshCount { get; private set; }
        public event EventHandler? StatusChanged { add => _statusChanged += value; remove => _statusChanged -= value; }
        public void RaiseStatusChanged() => _statusChanged?.Invoke(this, EventArgs.Empty);
        public Task RefreshStatusAsync(InferenceSettings settings, CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            if (Next is { } next) { ((InstallSettings)settings).State = next; }
            return Task.CompletedTask;
        }
        protected override Task<SetupActionResult> RunCoreAsync(InstallSettings settings, SetupActionDescriptor action,
            IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
        {
            Runs.Add(action.Id);
            return Task.FromResult(SetupActionResult.Completed);
        }
    }

    private sealed class FakeTarget : ISetupCommitTarget<string>
    {
        public int Calls { get; private set; }
        public Task<SetupCommitResult> CommitAsync(string changes, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed));
        }
    }

    private sealed class FakeHost : IInferenceSettingsInteractionHost
    {
        public int AuthorizationCount { get; private set; }
        public int ExecutionCount { get; private set; }
        public ValueTask<bool> AuthorizeAsync(string operation, SetupSideEffect sideEffects,
            CancellationToken cancellationToken = default)
        {
            AuthorizationCount++;
            return ValueTask.FromResult(true);
        }
        public Task<SetupActionResult> ExecuteAsync(IInferenceSettingsManager manager, InferenceSettings settings,
            SetupActionDescriptor action, IProgress<SetupProgress> progress, CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return manager.RunAsync(settings, action.Id, progress, cancellationToken);
        }
        public ValueTask OpenBrowserAsync(Uri uri, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
        public List<string> Revealed { get; } = [];
        public Task RevealFolderAsync(string folder, CancellationToken cancellationToken)
        {
            Revealed.Add(folder);
            return Task.CompletedTask;
        }
        public Task CopyToClipboardAsync(Avalonia.Visual source, string text, string copied) => Task.CompletedTask;
    }
}
