using Bunit;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Lumeo;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Darkspyre.Inference.UI.Settings.Blazor.Tests;

/// <summary>The actions each step shows, the installed-tool checklist and the test that follows a model choice.</summary>
public sealed class StepActionTests : BunitContext
{
    public StepActionTests()
    {
        Services.AddLumeo();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void StepShowsOnlyItsOwnActions()
    {
        IRenderedComponent<InferenceSettingsEditor> editor = Show(new TwoStepSettings(), new Manager());

        Assert.Equal(["Sign in", "Check"], ActionButtons(editor));
        Click(editor, "Next");
        Assert.Equal(["Test model"], ActionButtons(editor));
    }

    [Fact]
    public void SettingsWithNoStepActionsShowAllActions()
    {
        IRenderedComponent<InferenceSettingsEditor> editor = Show(new TwoStepSettings { NameActions = false }, new Manager());

        Assert.Equal(["Sign in", "Check", "Test model"], ActionButtons(editor));
        Click(editor, "Next");
        Assert.Equal(["Sign in", "Check", "Test model"], ActionButtons(editor));
    }

    [Fact]
    public void CliStepChecksTheToolWhenShown()
    {
        var manager = new Manager();
        IRenderedComponent<InferenceSettingsEditor> editor = Show(new TwoStepSettings { UsesTool = true }, manager);

        editor.WaitForAssertion(() => Assert.Equal(["check"], manager.Runs));
        editor.WaitForAssertion(() => Assert.Contains("✓ Installed: tool", editor.Find(".inference-settings-editor__checklist").TextContent, StringComparison.Ordinal));
        // The checklist offers the sign-in; the tail keeps only the check.
        Assert.Equal(["Check"], ActionButtons(editor));

        editor.Find(".inference-settings-editor__checklist").QuerySelectorAll("button").Single(button => button.TextContent.Trim() == "Sign in").Click();

        editor.WaitForAssertion(() => Assert.Equal(["check", "sign-in", "check"], manager.Runs));
        editor.WaitForAssertion(() => Assert.Contains("✓ Signed in", editor.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChoosingAModelRunsTheTest()
    {
        var manager = new Manager();
        IRenderedComponent<InferenceSettingsEditor> editor = Show(new TwoStepSettings(), manager);
        Click(editor, "Next");

        await ChooseModelAsync(editor);

        editor.WaitForAssertion(() => Assert.Equal(["test"], manager.Runs));
        editor.WaitForAssertion(() => Assert.Contains("Test reply received in 1.4 s", editor.Markup, StringComparison.Ordinal));
        Assert.Contains("Model A answered \"OK\"", editor.Find(".inference-settings-editor__result").TextContent, StringComparison.Ordinal);
        Click(editor, "Test again");
        editor.WaitForAssertion(() => Assert.Equal(["test", "test"], manager.Runs));
    }

    [Fact]
    public async Task ConnectionTabsShowOneStepPerTab()
    {
        RenderFragment workers = builder => builder.AddMarkupContent(0, "<span class=\"workers\">Background workers at once</span>");
        IRenderedComponent<ConnectionTabs> tabs = Render<ConnectionTabs>(parameters => parameters
            .Add(value => value.Settings, new TwoStepSettings()).Add(value => value.Manager, new Manager()).Add(value => value.Host, new Host())
            .Add(value => value.MoreOptionsContent, workers));
        Assert.Equal(["Connection", "Model"], tabs.FindComponents<TabsTrigger>().Select(trigger => trigger.Instance.Value));

        Assert.Equal("Sign in", Assert.Single(tabs.FindAll(".inference-settings-editor__step h3")).TextContent);
        Assert.Contains("About this connection", tabs.Markup, StringComparison.Ordinal);
        // The host's content closes the step's own "More options" section.
        AngleSharp.Dom.IElement more = Assert.Single(tabs.FindAll("details"), details => details.QuerySelector("summary")!.TextContent == "More options");
        Assert.Equal("workers", more.QuerySelector(".inference-settings-editor__fields")!.LastElementChild!.ClassName);
        Assert.DoesNotContain(tabs.FindAll("button"), button => button.TextContent.Trim() is "Next" or "Back" or "Save settings");

        await tabs.InvokeAsync(() => tabs.FindComponent<Tabs>().Instance.ActiveValueChanged.InvokeAsync("Model"));

        tabs.WaitForAssertion(() => Assert.Equal("Model", Assert.Single(tabs.FindAll(".inference-settings-editor__step h3")).TextContent));
        Assert.DoesNotContain(tabs.FindAll("details summary"), summary => summary.TextContent == "More options");
        Assert.Equal(["Test model"], tabs.FindAll(".inference-settings-editor__actions button").Select(button => button.TextContent.Trim()));
    }

    [Fact]
    public void AHostStripReplacesTheStepListAndFollowsTheStep()
    {
        List<int> shown = [];
        IRenderedComponent<InferenceSettingsEditor> editor = Render<InferenceSettingsEditor>(parameters => parameters
            .Add(value => value.Settings, new TwoStepSettings()).Add(value => value.Manager, new Manager()).Add(value => value.Host, new Host())
            .Add(value => value.ShowStepList, false).Add(value => value.StepChanged, (int index) => shown.Add(index)));

        Assert.Empty(editor.FindAll(".inference-settings-editor__steps"));
        Assert.Equal("Sign in", Assert.Single(editor.FindAll(".inference-settings-editor__step h3")).TextContent);
        Click(editor, "Next");

        Assert.Equal(1, editor.Instance.CurrentStepIndex);
        Assert.Equal([1], shown);
        Assert.Equal("Model", Assert.Single(editor.FindAll(".inference-settings-editor__step h3")).TextContent);
        Click(editor, "Back");
        Assert.Equal([1, 0], shown);
    }

    [Fact]
    public async Task ConnectionTabsReportTheEditorsState()
    {
        List<SetupEditorState> states = [];
        IRenderedComponent<ConnectionTabs> tabs = Render<ConnectionTabs>(parameters => parameters
            .Add(value => value.Settings, new TwoStepSettings()).Add(value => value.Manager, new Manager()).Add(value => value.Host, new Host())
            .Add(value => value.StateChanged, (SetupEditorState state) => states.Add(state)));
        Assert.False(tabs.Instance.State!.HasUnsavedChanges);
        await tabs.InvokeAsync(() => tabs.FindComponent<Tabs>().Instance.ActiveValueChanged.InvokeAsync("Model"));

        IRenderedComponent<SetupFieldInput> model = tabs.FindComponents<SetupFieldInput>().Single(field => field.Instance.Field.Description.Id == "model");
        model.WaitForAssertion(() => Assert.Single(model.FindComponents<Combobox>()));
        await model.InvokeAsync(() => model.FindComponent<Combobox>().Instance.ValueChanged.InvokeAsync("a"));

        tabs.WaitForAssertion(() => Assert.True(tabs.Instance.State!.HasUnsavedChanges && tabs.Instance.State.CanSave));
        Assert.Contains(states, state => state.HasUnsavedChanges);
    }

    [Fact]
    public void AnActionTheHostCannotRunShowsWhy()
    {
        IRenderedComponent<InferenceSettingsEditor> editor = Render<InferenceSettingsEditor>(parameters => parameters
            .Add(value => value.Settings, new TwoStepSettings()).Add(value => value.Manager, new Manager())
            .Add(value => value.Host, new Host { ExecuteFailure = new IOException("The tool is locked.") }).Add(value => value.ShowActionEffects, false));

        Click(editor, "Check");

        editor.WaitForAssertion(() => Assert.Contains("The host could not run the action (IOException): The tool is locked.",
            editor.Markup, StringComparison.Ordinal));
    }

    private IRenderedComponent<InferenceSettingsEditor> Show(TwoStepSettings settings, Manager manager) =>
        Render<InferenceSettingsEditor>(parameters => parameters
            .Add(value => value.Settings, settings).Add(value => value.Manager, manager).Add(value => value.Host, new Host())
            .Add(value => value.ShowActionEffects, false));

    private static async Task ChooseModelAsync(IRenderedComponent<InferenceSettingsEditor> editor)
    {
        IRenderedComponent<SetupFieldInput> model = editor.FindComponents<SetupFieldInput>().Single(field => field.Instance.Field.Description.Id == "model");
        model.WaitForAssertion(() => Assert.Single(model.FindComponents<Combobox>()));
        await model.InvokeAsync(() => model.FindComponent<Combobox>().Instance.ValueChanged.InvokeAsync("a"));
    }

    private static void Click(IRenderedComponent<InferenceSettingsEditor> editor, string label) =>
        editor.FindAll("button").Single(button => button.TextContent.Trim() == label).Click();

    private static string[] ActionButtons(IRenderedComponent<InferenceSettingsEditor> editor) =>
        [.. editor.FindAll(".inference-settings-editor__actions button").Select(button => button.TextContent.Trim())];

    // A connection step that signs in and checks, and a model step that tests, like an LLM provider's settings.
    private sealed class TwoStepSettings : InferenceSettings<string>
    {
        private string _model = string.Empty;
        private string _tool = string.Empty;

        public TwoStepSettings() : base(
            new(new("tool", "Tool provider", InferenceImplementationKind.Connect, InferenceTask.TextGeneration), "A provider with a tool"),
            new(SetupChangeIntent.Create, "entry", null))
        { }

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
        /// <summary>The exception every action run throws, or null to run the action.</summary>
        public Exception? ExecuteFailure { get; init; }

        public ValueTask<bool> AuthorizeAsync(string operation, SetupSideEffect sideEffects, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
        public Task<SetupActionResult> ExecuteAsync(IInferenceSettingsManager manager, InferenceSettings settings, SetupActionDescriptor action,
            IProgress<SetupProgress> progress, CancellationToken cancellationToken = default) =>
            ExecuteFailure is { } failure ? Task.FromException<SetupActionResult>(failure) : manager.RunAsync(settings, action.Id, progress, cancellationToken);
        public ValueTask OpenBrowserAsync(Uri uri, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public Task RevealFolderAsync(string folder, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
