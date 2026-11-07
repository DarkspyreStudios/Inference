using Avalonia.Controls;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.UI.Settings;
using Xunit;

namespace Darkspyre.Inference.UI.Settings.Avalonia.Tests;

public sealed class InferenceSettingsEditorTests
{
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

    private static InferenceSettingsEditor ShowInstallEditor(InstallSettings settings, InstallManager manager, FakeHost? host = null)
    {
        InferenceSettingsEditor editor = new(settings, manager, host ?? new FakeHost());
        EditorSession.Show(editor);
        return editor;
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

    private sealed class FakeHost : IAvaloniaSettingsInteractionHost
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
        public Task CopyToClipboardAsync(global::Avalonia.Visual source, string text) => Task.CompletedTask;
    }
}
