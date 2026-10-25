using Bunit;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.UI.Settings;
using Lumeo;
using Xunit;

namespace Darkspyre.Inference.UI.Settings.Blazor.Tests;

public sealed class InferenceSettingsEditorTests : BunitContext
{
    public InferenceSettingsEditorTests()
    {
        Services.AddLumeo();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void HostsWithTheirOwnFrameCanHideTheHeadingAndEffectLabels()
    {
        var settings = new FakeSettings();
        var manager = new FakeManager();
        IRenderedComponent<InferenceSettingsEditor> shown = RenderEditor(settings, manager);
        Assert.Equal("Example provider", shown.Find(".inference-settings-editor__heading h2").TextContent);
        Assert.Equal("Network", shown.Find(".inference-settings-editor__action small").TextContent);
        Assert.Equal("Connect", shown.Find(".inference-settings-editor__step h3").TextContent); // One step has no step list.

        IRenderedComponent<InferenceSettingsEditor> framed = Render<InferenceSettingsEditor>(parameters => parameters
            .Add(value => value.Settings, settings).Add(value => value.Manager, manager).Add(value => value.Host, new FakeHost())
            .Add(value => value.ShowHeading, false).Add(value => value.ShowActionEffects, false));
        Assert.Empty(framed.FindAll(".inference-settings-editor__heading"));
        Assert.Empty(framed.FindAll(".inference-settings-editor__action small"));
        Assert.Contains("Check connection", framed.Markup, StringComparison.Ordinal);
        // Status facts read as chips, never as enum names.
        Assert.Equal(["Configured", "Not verified yet", "Readiness not checked"],
            framed.FindAll(".inference-settings-editor__chip").Select(chip => chip.TextContent.Trim()));
    }

    [Fact]
    public void EnteringTheModelAfterNextRefreshesValidationAndEnablesSave()
    {
        var manager = new ModelStepManager();
        IRenderedComponent<InferenceSettingsEditor> editor = Render<InferenceSettingsEditor>(parameters => parameters
            .Add(value => value.Settings, new ModelStepSettings()).Add(value => value.Manager, manager).Add(value => value.Host, new FakeHost()));

        editor.FindAll("button").Single(button => button.TextContent.Trim() == "Next").Click();
        Assert.True(SaveButton(editor).HasAttribute("disabled"));

        editor.FindComponents<SetupFieldInput>().Single(field => field.Instance.Field.Description.Id == "model").Find("input").Input("model-a");

        Assert.False(SaveButton(editor).HasAttribute("disabled"));
        Assert.DoesNotContain("Select a model.", editor.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SetupInstallState.Installed)]
    [InlineData(SetupInstallState.NotDownloaded)]
    [InlineData(SetupInstallState.FilesMissing)]
    public void InstallCardShowsEachStateWithTheMockupTextAndButtons(SetupInstallState state)
    {
        var settings = new InstallSettings("small", state).Unobserved();
        IRenderedComponent<InferenceSettingsEditor> editor = RenderInstallEditor(settings, new InstallManager());

        AngleSharp.Dom.IElement card = editor.Find(".inference-install-card");
        Assert.Equal("Install state", card.GetAttribute("aria-label"));
        Assert.Contains("small", card.TextContent, StringComparison.Ordinal);
        string text = Squash(card.TextContent);
        string[] buttons = card.QuerySelectorAll("button").Select(button => button.TextContent.Trim()).ToArray();
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
        Assert.Empty(editor.FindAll(".inference-settings-editor__status")); // No observation applies, so no status line.
    }

    [Fact]
    public void NotSupportedShowsItsTextAndReasonOnTheCardAndOnTheChoiceChip()
    {
        IRenderedComponent<InferenceSettingsEditor> editor = RenderInstallEditor(new InstallSettings("small", SetupInstallState.NotSupported).Unobserved(), new InstallManager());
        editor.WaitForAssertion(() => Assert.Equal(2, Cards(editor).Count));

        AngleSharp.Dom.IElement card = editor.Find(".inference-install-card");
        string text = Squash(card.TextContent);
        Assert.Contains("Not supported", text, StringComparison.Ordinal);
        Assert.Contains(InstallSettings.UnsupportedReason, text, StringComparison.Ordinal);
        Assert.Equal(["Delete download"], card.QuerySelectorAll("button").Select(button => button.TextContent.Trim()));

        Assert.Equal(["small 465 MB download Not supported", "tiny 465 MB download Not downloaded"], Cards(editor).Select(pack => Squash(pack.TextContent)));
        foreach (AngleSharp.Dom.IElement root in new[] { card, Cards(editor)[0] })
        {
            AngleSharp.Dom.IElement chip = root.QuerySelector(".inference-install-chip")!;
            Assert.Empty(chip.QuerySelectorAll("svg"));
        }
        Assert.Empty(editor.FindAll(".inference-model-pack-card[aria-pressed=\"true\"]"));
    }

    [Fact]
    public void ClickingDownloadRunsTheDownloadActionThroughTheHost()
    {
        var settings = new InstallSettings("small", SetupInstallState.NotDownloaded);
        var manager = new InstallManager();
        var host = new FakeHost();
        IRenderedComponent<InferenceSettingsEditor> editor = RenderInstallEditor(settings, manager, host);

        editor.Find(".inference-install-card button").Click();

        editor.WaitForAssertion(() => Assert.Equal(["download"], manager.Runs));
        Assert.Equal(1, host.ExecutionCount);
    }

    [Fact]
    public async Task PackCardsShowSizeAndStateAndFollowAStatusChange()
    {
        var manager = new InstallManager();
        IRenderedComponent<InferenceSettingsEditor> editor = RenderInstallEditor(new InstallSettings("small", SetupInstallState.Installed), manager);
        editor.WaitForAssertion(() => Assert.Equal(2, Cards(editor).Count));

        // The cards replace the list.
        Assert.Empty(Field(editor, "model").FindComponents<Combobox>());
        Assert.Equal(["small 465 MB download Installed", "tiny 465 MB download Not downloaded"], Cards(editor).Select(card => Squash(card.TextContent)));

        manager.Next = SetupInstallState.NotDownloaded;
        await editor.InvokeAsync(manager.RaiseStatusChanged);

        editor.WaitForAssertion(() => Assert.EndsWith("Not downloaded", Squash(Cards(editor)[0].TextContent), StringComparison.Ordinal));
        Assert.Equal(1, manager.RefreshCount);
    }

    [Fact]
    public void AModelThatIsNotInstalledIsNeverTheShownSelection()
    {
        IRenderedComponent<InferenceSettingsEditor> editor = RenderInstallEditor(new InstallSettings("small", SetupInstallState.FilesMissing), new InstallManager());
        editor.WaitForAssertion(() => Assert.Equal(2, Cards(editor).Count));

        Assert.Empty(editor.FindAll(".inference-model-pack-card[aria-pressed=\"true\"]"));
        Assert.Contains("small", editor.Find(".inference-install-card").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstalledSelectionIsTheSelectedCardAndAClickChoosesAnother()
    {
        var settings = new InstallSettings("small", SetupInstallState.Installed);
        var host = new FakeHost();
        IRenderedComponent<InferenceSettingsEditor> editor = RenderInstallEditor(settings, new InstallManager(), host);
        editor.WaitForAssertion(() => Assert.Equal(2, Cards(editor).Count));
        Assert.Equal("small", Assert.Single(editor.FindAll(".inference-model-pack-card[aria-pressed=\"true\"]")).GetAttribute("aria-label"));

        editor.Find(".inference-install-card button[aria-label=\"Show the model folder\"]").Click();
        editor.WaitForAssertion(() => Assert.Equal(["/models"], host.Revealed));

        editor.Find(".inference-model-pack-card[aria-label=\"tiny\"]").Click();
        editor.WaitForAssertion(() => Assert.Equal("tiny", settings.Model));
    }

    private IRenderedComponent<InferenceSettingsEditor> RenderInstallEditor(InstallSettings settings, InstallManager manager, FakeHost? host = null) =>
        Render<InferenceSettingsEditor>(parameters => parameters
            .Add(value => value.Settings, settings).Add(value => value.Manager, manager).Add(value => value.Host, host ?? new FakeHost()));

    private static IRenderedComponent<SetupFieldInput> Field(IRenderedComponent<InferenceSettingsEditor> editor, string id) =>
        editor.FindComponents<SetupFieldInput>().Single(field => field.Instance.Field.Description.Id == id);

    // The model pack cards the model field shows.
    private static IReadOnlyList<AngleSharp.Dom.IElement> Cards(IRenderedComponent<InferenceSettingsEditor> editor) =>
        Field(editor, "model").FindAll(".inference-model-pack-card");

    private static string Squash(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private IRenderedComponent<InferenceSettingsEditor> RenderEditor(FakeSettings settings, FakeManager manager, FakeHost? host = null) =>
        Render<InferenceSettingsEditor>(parameters => parameters
            .Add(value => value.Settings, settings).Add(value => value.Manager, manager).Add(value => value.Host, host ?? new FakeHost()));

    private static AngleSharp.Dom.IElement SaveButton(IRenderedComponent<InferenceSettingsEditor> editor) =>
        editor.FindAll("button").Single(button => button.TextContent.Contains("Save settings", StringComparison.Ordinal));

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
    }
}
