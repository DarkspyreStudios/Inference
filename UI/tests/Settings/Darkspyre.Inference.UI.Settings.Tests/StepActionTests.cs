using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Xunit;

namespace Darkspyre.Inference.UI.Settings.Tests;

/// <summary>The actions each step shows, the installed-tool checklist and the test that follows a model choice.</summary>
public sealed class StepActionTests
{
    [Fact]
    public Task StepShowsOnlyItsOwnActions() => EditorSession.RunAsync(() =>
    {
        InferenceSettingsEditor editor = Show(new TwoStepSettings(), new Manager());

        Assert.Equal(["Sign in", "Check"], ActionButtons(editor));
        EditorSession.Click(EditorSession.Button(editor, "Next"));
        Assert.Equal(["Test model"], ActionButtons(editor));
        return Task.CompletedTask;
    });

    [Fact]
    public Task SettingsWithNoStepActionsShowAllActions() => EditorSession.RunAsync(() =>
    {
        InferenceSettingsEditor editor = Show(new TwoStepSettings { NameActions = false }, new Manager());

        Assert.Equal(["Sign in", "Check", "Test model"], ActionButtons(editor));
        EditorSession.Click(EditorSession.Button(editor, "Next"));
        Assert.Equal(["Sign in", "Check", "Test model"], ActionButtons(editor));
        return Task.CompletedTask;
    });

    [Fact]
    public Task CliStepChecksTheToolWhenShown() => EditorSession.RunAsync(async () =>
    {
        var manager = new Manager();
        InferenceSettingsEditor editor = Show(new TwoStepSettings { UsesTool = true }, manager);

        await EditorSession.WaitForAsync(() => manager.Runs.SequenceEqual(["check"]));
        StackPanel checklist = Assert.Single(EditorSession.All<StackPanel>(editor, "inference-settings-checklist"));
        Assert.Contains("✓ Installed: tool", EditorSession.TextOf(checklist), StringComparison.Ordinal);
        // The checklist offers the sign-in; the tail keeps only the check.
        Assert.Equal(["Check"], ActionButtons(editor));

        EditorSession.Click(EditorSession.Button(checklist, "Sign in"));

        await EditorSession.WaitForAsync(() => manager.Runs.SequenceEqual(["check", "sign-in", "check"]));
        Assert.Contains("✓ Signed in", EditorSession.TextOf(editor), StringComparison.Ordinal);
    });

    [Fact]
    public Task ChoosingAModelRunsTheTest() => EditorSession.RunAsync(async () =>
    {
        var manager = new Manager();
        InferenceSettingsEditor editor = Show(new TwoStepSettings(), manager);
        EditorSession.Click(EditorSession.Button(editor, "Next"));
        AutoCompleteBox models = Assert.Single(EditorSession.All<AutoCompleteBox>(EditorSession.Field(editor, "model")));
        await EditorSession.WaitForAsync(() => models.ItemsSource is IEnumerable<SetupChoice> loaded && loaded.Any());

        models.SelectedItem = ((IEnumerable<SetupChoice>)models.ItemsSource!).Single();

        await EditorSession.WaitForAsync(() => manager.Runs.SequenceEqual(["test"]));
        string text = EditorSession.TextOf(editor);
        Assert.Contains("Test reply received in 1.4 s", text, StringComparison.Ordinal);
        Assert.Contains("Model A answered \"OK\"", text, StringComparison.Ordinal);
        EditorSession.Click(EditorSession.Button(editor, "Test again"));
        await EditorSession.WaitForAsync(() => manager.Runs.SequenceEqual(["test", "test"]));
    });

    [Fact]
    public Task ConnectionTabsShowOneStepPerTab() => EditorSession.RunAsync(() =>
    {
        TextBlock workers = new() { Text = "Background workers at once" };
        ConnectionTabs tabs = new(new TwoStepSettings(), new Manager(), new Host()) { MoreOptionsContent = workers };
        EditorSession.Show(tabs);
        TabStrip strip = Assert.Single(EditorSession.All<TabStrip>(tabs));
        Assert.Equal(["Connection", "Model"], (IEnumerable<string>)strip.ItemsSource!);

        Assert.Equal("Sign in", Assert.Single(EditorSession.All<TextBlock>(tabs, "h3")).Text);
        Assert.Contains("About this connection", EditorSession.TextOf(tabs), StringComparison.Ordinal);
        // The host's content closes the step's own "More options" section.
        Expander more = Assert.Single(EditorSession.All<Expander>(tabs), expander => expander.Header as string == "More options");
        Assert.Same(workers, ((StackPanel)more.Content!).Children[^1]);
        Assert.DoesNotContain(EditorSession.All<Button>(tabs), button => button.Content as string is "Next" or "Back" or "Save settings");

        strip.SelectedIndex = 1;
        EditorSession.Settle();

        Assert.Equal("Model", Assert.Single(EditorSession.All<TextBlock>(tabs, "h3")).Text);
        Assert.DoesNotContain(EditorSession.All<Expander>(tabs), expander => expander.Header as string == "More options");
        Assert.Equal(["Test model"], EditorSession.All<StackPanel>(tabs, "inference-settings-actions")
            .SelectMany(panel => EditorSession.All<Button>(panel)).Select(button => (string)button.Content!));
        return Task.CompletedTask;
    });

    [Fact]
    public Task AHostStripReplacesTheStepListAndFollowsTheStep() => EditorSession.RunAsync(() =>
    {
        InferenceSettingsEditor editor = new(new TwoStepSettings(), new Manager(), new Host()) { ShowStepList = false };
        List<int> shown = [];
        editor.StepChanged += (_, index) => shown.Add(index);
        EditorSession.Show(editor);

        Assert.Empty(EditorSession.All<WrapPanel>(editor, "inference-settings-steps"));
        Assert.Equal("Sign in", Assert.Single(EditorSession.All<TextBlock>(editor, "h3")).Text);
        EditorSession.Click(EditorSession.Button(editor, "Next"));

        Assert.Equal(1, editor.CurrentStepIndex);
        Assert.Equal([1], shown);
        Assert.Equal("Model", Assert.Single(EditorSession.All<TextBlock>(editor, "h3")).Text);
        EditorSession.Click(EditorSession.Button(editor, "Back"));
        Assert.Equal([1, 0], shown);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ConnectionTabsReportTheEditorsState() => EditorSession.RunAsync(async () =>
    {
        ConnectionTabs tabs = new(new TwoStepSettings(), new Manager(), new Host());
        List<SetupEditorState> states = [];
        tabs.StateChanged += (_, state) => states.Add(state);
        EditorSession.Show(tabs);
        Assert.False(tabs.State.HasUnsavedChanges);
        Assert.Single(EditorSession.All<TabStrip>(tabs)).SelectedIndex = 1;
        EditorSession.Settle();
        AutoCompleteBox models = Assert.Single(EditorSession.All<AutoCompleteBox>(EditorSession.Field(tabs, "model")));
        await EditorSession.WaitForAsync(() => models.ItemsSource is IEnumerable<SetupChoice> loaded && loaded.Any());

        models.SelectedItem = ((IEnumerable<SetupChoice>)models.ItemsSource!).Single();

        await EditorSession.WaitForAsync(() => tabs.State.HasUnsavedChanges && tabs.State.CanSave);
        Assert.Contains(states, state => state.HasUnsavedChanges);
    });

    private static InferenceSettingsEditor Show(TwoStepSettings settings, Manager manager)
    {
        InferenceSettingsEditor editor = new(settings, manager, new Host()) { ShowActionEffects = false };
        EditorSession.Show(editor);
        return editor;
    }

    private static string[] ActionButtons(InferenceSettingsEditor editor) =>
        [.. EditorSession.All<StackPanel>(editor, "inference-settings-actions").SelectMany(panel => EditorSession.All<Button>(panel))
            .Select(button => (string)button.Content!)];

    // A connection step that signs in and checks, and a model step that tests, like an LLM provider's settings.
    private sealed class TwoStepSettings : InferenceSettings<string>
    {
        private string _model = string.Empty;
        private string _tool = string.Empty;

        public TwoStepSettings() : base(
            new(new("tool", "Tool provider", InferenceImplementationKind.Connect, InferenceTask.TextGeneration), "A provider with a tool"),
            new(SetupChangeIntent.Create, "entry", null)) { }

        public bool NameActions { get; init; } = true;

        public bool UsesTool
        {
            get => Status.Installed.IsApplicable;
            init => Status = Status with { Installed = value ? SetupObservation.Unknown : SetupObservation.NotApplicable };
        }

        public bool SignedIn { get; set; }

        protected override IReadOnlyList<SetupStep> CreateSteps() =>
        [
            new("connect", "Sign in", "How this connection is billed.",
                [new("more-options", "More options",
                    [new SetupExecutableField(new("executable", "Tool"), new(_tool), new("tool"), value => Edit(() => _tool = value.Value))],
                    IsAdvanced: true)])
            { ActionIds = NameActions ? ["sign-in", "check"] : [], Troubleshooting = "Run the tool in a terminal." },
            new("model", "Model", "Choose a model.",
                [new("model", "Model", [new SetupChoiceField(new("model", "Model"), _model, string.Empty, value => Edit(() => _model = value), true,
                    SetupSideEffect.None, SetupChoiceCapabilities.Searchable)])])
            { ActionIds = NameActions ? ["test"] : [] },
        ];

        protected override IReadOnlyList<SetupActionDescriptor> CreateActions() =>
        [
            new("sign-in", "Sign in", SetupActionPurpose.Authenticate, SetupSideEffect.BrowserInteraction),
            new("check", "Check", SetupActionPurpose.CheckPrerequisites, SetupSideEffect.ExternalCli),
            new("test", "Test model", SetupActionPurpose.Test, SetupSideEffect.PotentialBilling),
        ];

        protected override SetupValidation ValidateDraft() => SetupValidation.Valid;
        protected override string CreateChanges() => _model;
        protected override bool IsDraftDirty => _model.Length != 0;
    }

    private sealed class Manager() : InferenceSettingsManager<TwoStepSettings, string>(new Target())
    {
        public List<string> Runs { get; } = [];

        protected override Task<SetupActionResult> RunCoreAsync(TwoStepSettings settings, SetupActionDescriptor action,
            IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
        {
            Runs.Add(action.Id);
            switch (action.Id)
            {
                case "check":
                    settings.Status = settings.Status with
                    {
                        Installed = SetupObservation.Known(true),
                        Authenticated = SetupObservation.Known(settings.SignedIn)
                    };
                    return Task.FromResult(SetupActionResult.Completed);
                case "sign-in":
                    settings.SignedIn = true;
                    return Task.FromResult(SetupActionResult.Completed);
                default:
                    return Task.FromResult(new SetupActionResult(SetupActionState.Completed)
                    { Message = "Model A answered \"OK\"", Elapsed = TimeSpan.FromMilliseconds(1400) });
            }
        }

        protected override Task<SetupChoicePage> QueryProviderChoicesAsync(TwoStepSettings settings, string fieldId,
            SetupChoiceQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new SetupChoicePage([new("a", "Model A") { Tags = ["200K context"] }]));
    }

    private sealed class Target : ISetupCommitTarget<string>
    {
        public Task<SetupCommitResult> CommitAsync(string changes, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SetupCommitResult(SetupCommitState.Committed));
    }

    private sealed class Host : IInferenceSettingsInteractionHost
    {
        public ValueTask<bool> AuthorizeAsync(string operation, SetupSideEffect sideEffects, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
        public Task<SetupActionResult> ExecuteAsync(IInferenceSettingsManager manager, InferenceSettings settings, SetupActionDescriptor action,
            IProgress<SetupProgress> progress, CancellationToken cancellationToken = default) =>
            manager.RunAsync(settings, action.Id, progress, cancellationToken);
        public ValueTask OpenBrowserAsync(Uri uri, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public Task RevealFolderAsync(string folder, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
