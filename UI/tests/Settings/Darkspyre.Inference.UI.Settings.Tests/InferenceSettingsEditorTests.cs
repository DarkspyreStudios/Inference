using Bunit;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.UI.Settings;
using Lumeo;
using Xunit;

namespace Darkspyre.Inference.UI.Settings.Tests;

public sealed class InferenceSettingsEditorTests : BunitContext
{
    public InferenceSettingsEditorTests()
    {
        Services.AddLumeo();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void RendersProviderStatusWithoutAHostConnectionFlag()
    {
        IRenderedComponent<InferenceSettingsEditor> editor = RenderEditor(new FakeSettings(), new FakeManager());

        Assert.Contains("Example provider", editor.Markup, StringComparison.Ordinal);
        Assert.Contains("Configured: Yes", editor.Markup, StringComparison.Ordinal);
        Assert.Contains("Ready: Unknown", editor.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"dialog\"", editor.Markup, StringComparison.Ordinal);
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
        Assert.Contains("Configured: Yes", framed.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveCallsTheManagerOnceAndReportsTheResult()
    {
        var manager = new FakeManager();
        SetupCommitResult? reported = null;
        IRenderedComponent<InferenceSettingsEditor> editor = Render<InferenceSettingsEditor>(parameters => parameters
            .Add(value => value.Settings, new FakeSettings()).Add(value => value.Manager, manager)
            .Add(value => value.Host, new FakeHost()).Add(value => value.CommitCompleted, result => reported = result));

        SetupCommitResult result = await editor.InvokeAsync(() => editor.Instance.SaveAsync());

        Assert.Equal(SetupCommitState.Committed, result.State);
        Assert.Equal(SetupCommitState.Committed, reported?.State);
        Assert.Equal(1, manager.Target.Calls);
        Assert.False(editor.Instance.State.CanSave);
    }

    [Fact]
    public void SaveIsEnabledOnlyWhenRequiredValuesAndSignInAreSatisfied()
    {
        Assert.True(SaveButton(RenderEditor(new FakeSettings(), new FakeManager())).HasAttribute("disabled") is false);
        Assert.True(SaveButton(RenderEditor(new FakeSettings { Url = string.Empty }, new FakeManager())).HasAttribute("disabled"));
        Assert.True(SaveButton(RenderEditor(new FakeSettings { SignedIn = false }, new FakeManager())).HasAttribute("disabled"));
    }

    [Fact]
    public void CheckConnectionRunsThroughTheHostAndManager()
    {
        var manager = new FakeManager();
        var host = new FakeHost();
        IRenderedComponent<InferenceSettingsEditor> editor = RenderEditor(new FakeSettings(), manager, host);

        editor.FindAll("button").Single(button => button.TextContent.Contains("Check connection", StringComparison.Ordinal)).Click();

        editor.WaitForAssertion(() => Assert.Contains("Ready: Yes", editor.Markup, StringComparison.Ordinal));
        Assert.Equal(1, host.AuthorizationCount);
        Assert.Equal(1, host.ExecutionCount);
        Assert.Equal(0, manager.Target.Calls);
    }

    [Fact]
    public void StatusChangeRefreshesTheRenderedStatus()
    {
        var manager = new FakeStatusManager();
        IRenderedComponent<InferenceSettingsEditor> editor = RenderEditor(new FakeSettings(), manager);
        Assert.Contains("Ready: Unknown", editor.Markup, StringComparison.Ordinal);

        manager.RaiseStatusChanged();

        editor.WaitForAssertion(() => Assert.Contains("Ready: Yes", editor.Markup, StringComparison.Ordinal));
        Assert.Equal(1, manager.RefreshCount);
    }

    [Fact]
    public async Task DisposingTheEditorStopsListeningForStatus()
    {
        var manager = new FakeStatusManager();
        RenderEditor(new FakeSettings(), manager);
        Assert.Equal(1, manager.ListenerCount);

        await DisposeComponentsAsync();

        Assert.Equal(0, manager.ListenerCount);
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
    }
}
