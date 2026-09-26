using Bunit;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.UI.Settings;
using Lumeo;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Darkspyre.Inference.UI.Settings.Tests;

public sealed class SetupEditorRegressionTests : BunitContext
{
    public SetupEditorRegressionTests() { Services.AddLumeo(); JSInterop.Mode = JSRuntimeMode.Loose; }

    [Fact]
    public async Task InvalidUrlSurvivesRefreshAndAnotherEditAndNeverCommitsOldValue()
    {
        var (settings, manager) = New();
        IRenderedComponent<InferenceSettingsEditor> editor = Editor(settings, manager);
        Field(editor, "url").Find("input").Input("not a url");
        await editor.InvokeAsync(manager.RaiseStatusChanged);
        Field(editor, "name").Find("input").Input("changed");
        editor.WaitForAssertion(() => Assert.Equal("not a url", Field(editor, "url").Find("input").GetAttribute("value")));
        Assert.True(editor.Instance.State.HasUnsavedChanges);
        Assert.True(editor.Instance.State.HasInputErrors);
        Assert.False(editor.Instance.State.CanSave);
        SetupCommitResult result = await editor.InvokeAsync(editor.Instance.SaveAsync);
        Assert.Equal(SetupCommitState.Invalid, result.State);
        Assert.Equal(0, manager.Target.Calls);
        WritePreview(editor.Markup, "invalid");
        Field(editor, "url").Find("input").Input("https://changed.test/");
        Assert.Equal(SetupCommitState.Committed, (await editor.InvokeAsync(editor.Instance.SaveAsync)).State);
        Assert.Equal(new Uri("https://changed.test/"), manager.Target.Saved!.Endpoint);
        Assert.False(editor.Instance.State.HasUnsavedChanges);
    }

    [Theory]
    [InlineData("count", "-")]
    [InlineData("count", "1.5")]
    [InlineData("count", "9223372036854775808")]
    [InlineData("amount", "bad")]
    [InlineData("amount", "11")]
    [InlineData("amount", "0.12345678901234567890123456789")]
    public async Task InvalidNumericInputCannotSavePreviousValue(string field, string text)
    {
        var (settings, manager) = New();
        IRenderedComponent<InferenceSettingsEditor> editor = Editor(settings, manager);
        Field(editor, field).Find("input").Input(text);
        Assert.Equal(text, Field(editor, field).Find("input").GetAttribute("value"));
        Assert.Equal(SetupCommitState.Invalid, (await editor.InvokeAsync(editor.Instance.SaveAsync)).State);
        Assert.Equal(0, manager.Target.Calls);
    }

    [Fact]
    public async Task ErrorsArePerFieldAndNextCannotDiscardThem()
    {
        var (settings, manager) = New();
        IRenderedComponent<InferenceSettingsEditor> editor = Editor(settings, manager);
        Field(editor, "url").Find("input").Input("bad");
        Field(editor, "count").Find("input").Input("-");
        Click(editor, "Next");
        Assert.Contains("First step", editor.Markup, StringComparison.Ordinal);
        Field(editor, "url").Find("input").Input("https://original.test/");
        SetupCommitResult result = await editor.InvokeAsync(editor.Instance.SaveAsync);
        Assert.Equal(SetupCommitState.Invalid, result.State);
        Assert.Contains(result.Issues!, issue => issue.FieldId == "count");
        Assert.DoesNotContain(result.Issues!, issue => issue.FieldId == "url");
    }

    [Fact]
    public async Task BackNavigationRetainsInvalidInputAndSaveReturnsToItsStep()
    {
        var (settings, manager) = New();
        IRenderedComponent<InferenceSettingsEditor> editor = Editor(settings, manager);
        Click(editor, "Next");
        Field(editor, "second-url").Find("input").Input("unfinished");
        Click(editor, "Back");
        Assert.True(editor.Instance.State.HasPendingInput);
        Assert.Equal(SetupCommitState.Invalid, (await editor.InvokeAsync(editor.Instance.SaveAsync)).State);
        Assert.Equal("unfinished", Field(editor, "second-url").Find("input").GetAttribute("value"));
        Assert.Equal(0, manager.Target.Calls);
    }

    [Fact]
    public async Task HostStateIncludesUnappliedSecretsAndRevertsWithoutRevisionHeuristics()
    {
        var (settings, manager) = New();
        var states = new List<SetupEditorState>();
        IRenderedComponent<InferenceSettingsEditor> editor = Editor(settings, manager, states);
        await editor.InvokeAsync(manager.RaiseStatusChanged);
        Assert.False(editor.Instance.State.HasUnsavedChanges);
        Field(editor, "name").Find("input").Input("new");
        Assert.True(editor.Instance.State.HasUnsavedChanges);
        Field(editor, "name").Find("input").Input(string.Empty);
        Assert.False(editor.Instance.State.HasUnsavedChanges);
        Field(editor, "secret").Find("input").Input("never silently discard");
        Assert.True(editor.Instance.State.HasPendingInput);
        Assert.Equal(SetupCommitState.Invalid, (await editor.InvokeAsync(editor.Instance.SaveAsync)).State);
        Click(Field(editor, "secret"), "Replace secret");
        Assert.False(editor.Instance.State.HasPendingInput);
        Assert.True(editor.Instance.State.HasUnsavedChanges);
        Click(Field(editor, "secret"), "Keep");
        Assert.False(editor.Instance.State.HasUnsavedChanges);
        Assert.Contains(states, state => state.HasPendingInput && state.HasUnsavedChanges);
    }

    [Fact]
    public async Task UncertainCommitRemainsAnExplicitHostState()
    {
        var (settings, manager) = New(new Target { Result = new(SetupCommitState.Uncertain) });
        IRenderedComponent<InferenceSettingsEditor> editor = Editor(settings, manager);
        Assert.Equal(SetupCommitState.Uncertain, (await editor.InvokeAsync(editor.Instance.SaveAsync)).State);
        Assert.True(editor.Instance.State.CommitOutcomeUncertain);
        Assert.True(editor.Instance.State.HasUnsavedChanges);
        Assert.False(editor.Instance.State.CanSave);
    }

    [Fact]
    public async Task InteractionInstructionsAndRecheckComeFromTheSettings()
    {
        var (settings, manager) = New();
        settings.ShowPrerequisite();
        IRenderedComponent<InferenceSettingsEditor> editor = Editor(settings, manager);
        Assert.Contains("Authenticate the external CLI", editor.Markup, StringComparison.Ordinal);
        await editor.InvokeAsync(manager.RaiseStatusChanged);
        Click(editor, "Check again");
        editor.WaitForAssertion(() => Assert.Null(settings.Interaction));
        Assert.Equal(1, manager.Rechecks);
    }

    [Fact]
    public async Task CallbackIsRetainedOnlyForItsAttemptAndDoesNotEnterFieldValues()
    {
        var (settings, manager) = New();
        settings.StartCallback();
        IRenderedComponent<InferenceSettingsEditor> editor = Editor(settings, manager);
        string id = ((SetupBrowserAuthorization)settings.Interaction!).Callback.Description.Id;
        Field(editor, id).Find("input").Input("code");
        await editor.InvokeAsync(manager.RaiseStatusChanged);
        Assert.Equal("code", Field(editor, id).Find("input").GetAttribute("value"));
        Assert.Empty(((SetupBrowserAuthorization)settings.Interaction!).Callback.Value);
        Field(editor, id).Find("input").Input("code#state");
        Click(editor, "Continue");
        editor.WaitForAssertion(() => Assert.Equal("code#state", manager.CompletedCallback));
        settings.StartCallback();
        editor.Render();
        string next = ((SetupBrowserAuthorization)settings.Interaction!).Callback.Description.Id;
        Assert.NotEqual(id, next);
        Assert.Empty(Field(editor, next).Find("input").GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public void ReplacedSettingsCannotRetainOldInvalidInput()
    {
        var (first, manager) = New();
        var second = new TestSettings();
        IRenderedComponent<InferenceSettingsEditor> editor = Editor(first, manager);
        Field(editor, "url").Find("input").Input("old invalid input");
        editor.Render(parameters => parameters.Add(value => value.Settings, second));
        Assert.False(editor.Instance.State.HasUnsavedChanges);
        Assert.Equal("https://original.test/", Field(editor, "url").Find("input").GetAttribute("value"));
    }

    [Fact]
    public async Task DependentEditStartsNewChoiceRequestAndDiscardsOldCompletion()
    {
        var first = new TaskCompletionSource<SetupChoicePage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<SetupChoicePage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new TestSettings { ChoiceCapabilities = SetupChoiceCapabilities.Searchable };
        var manager = new TestManager(new Target());
        manager.Loader = (_, _) => manager.ChoiceCalls == 1 ? first.Task : second.Task;
        try
        {
            IRenderedComponent<InferenceSettingsEditor> editor = Editor(settings, manager);
            Field(editor, "name").Find("input").Input("new repository");
            editor.WaitForAssertion(() => Assert.Equal(2, manager.ChoiceCalls));
            Assert.True(manager.ChoiceTokens[0].IsCancellationRequested);
            second.SetResult(new([new("new", "New variant")]));
            IRenderedComponent<SetupFieldInput> choices = Field(editor, "choice");
            choices.WaitForAssertion(() => Assert.DoesNotContain("Loading choices", choices.Markup, StringComparison.Ordinal));
            await choices.InvokeAsync(() => choices.FindComponent<Combobox>().Instance.OpenChanged.InvokeAsync(true));
            choices.WaitForAssertion(() => Assert.Contains(choices.FindComponents<ComboboxItem>(), item => item.Instance.Value == "new"));
            first.SetResult(new([], State: SetupChoiceState.Failed, Issues: [new("old", "Obsolete failure")]));
            await editor.InvokeAsync(() => Task.CompletedTask);
            choices.WaitForAssertion(() => Assert.DoesNotContain("Obsolete failure", choices.Markup, StringComparison.Ordinal));
            Assert.Contains(Field(editor, "choice").FindComponents<ComboboxItem>(), item => item.Instance.Value == "new");
        }
        finally
        {
            first.TrySetResult(new([]));
            second.TrySetResult(new([]));
        }
    }

    [Fact]
    public void BoundedChoicesUseSelectWithoutSearchOrRefresh()
    {
        var (settings, manager) = New();
        IRenderedComponent<InferenceSettingsEditor> editor = Editor(settings, manager);
        IRenderedComponent<SetupFieldInput> choice = Field(editor, "choice");
        Assert.Single(choice.FindComponents<Select>());
        Assert.Empty(choice.FindComponents<Combobox>());
        Assert.DoesNotContain("Search choices", choice.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Refresh choices", choice.Markup, StringComparison.Ordinal);
        Assert.Equal("Count", Field(editor, "count").Find("input").GetAttribute("aria-label"));
        Assert.Equal("Amount", Field(editor, "amount").Find("input").GetAttribute("aria-label"));
        WritePreview(editor.Markup, "valid");
    }

    [Fact]
    public void MultiStepSettingsNameTheCurrentStepOnceInTheStepList()
    {
        var (settings, manager) = New();
        IRenderedComponent<InferenceSettingsEditor> editor = Editor(settings, manager);
        Assert.Empty(editor.FindAll(".inference-settings-editor__step h3"));
        Assert.Equal(["First step", "Second step"], editor.FindAll(".inference-settings-editor__steps li").Select(item => item.TextContent));
        Assert.Equal("First step", editor.Find(".inference-settings-editor__steps li[aria-current=step]").TextContent);
        Click(editor, "Next");
        Assert.Equal("Second step", editor.Find(".inference-settings-editor__steps li[aria-current=step]").TextContent);
        Assert.Empty(editor.FindAll(".inference-settings-editor__step h3"));
    }

    [Fact]
    public void DeviceCodeCanBeCopiedAndReportsWhenTheClipboardRefuses()
    {
        var (settings, manager) = New();
        IRenderedComponent<InferenceSettingsEditor> editor = Editor(settings, manager);
        settings.StartDevice("ABCD-1234");
        editor.Render();
        editor.WaitForAssertion(() => Assert.Equal("ABCD-1234", editor.Find(".inference-settings-editor__user-code").TextContent));
        Click(editor, "Copy code");
        editor.WaitForAssertion(() => Assert.Contains("Code copied.", editor.Markup, StringComparison.Ordinal));
        Assert.Equal(["ABCD-1234"], JSInterop.VerifyInvoke("navigator.clipboard.writeText").Arguments);

        JSInterop.SetupVoid("navigator.clipboard.writeText", "WXYZ-9876").SetException(new Microsoft.JSInterop.JSException("denied"));
        settings.StartDevice("WXYZ-9876");
        editor.Render();
        editor.WaitForAssertion(() => Assert.DoesNotContain("Code copied.", editor.Markup, StringComparison.Ordinal));
        Click(editor, "Copy code");
        editor.WaitForAssertion(() => Assert.Contains("The code could not be copied. Select it and copy it instead.", editor.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void ProviderSearchAppearsOnlyWhileMoreChoicesRemain()
    {
        var searchable = new TestSettings { ChoiceCapabilities = SetupChoiceCapabilities.Searchable };
        IRenderedComponent<SetupFieldInput> choice = Field(Editor(searchable, new TestManager(new Target())), "choice");
        choice.WaitForAssertion(() => Assert.Single(choice.FindComponents<Combobox>()));
        Assert.DoesNotContain("Search choices", choice.Markup, StringComparison.Ordinal);

        var paged = new TestManager(new Target()) { Loader = (_, _) => Task.FromResult(new SetupChoicePage([new("a", "A")], "1")) };
        IRenderedComponent<SetupFieldInput> more = Field(Editor(new TestSettings { ChoiceCapabilities = SetupChoiceCapabilities.Searchable }, paged), "choice");
        more.WaitForAssertion(() => Assert.Contains("Search choices", more.Markup, StringComparison.Ordinal));
        Assert.Contains("Load more", more.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChoicesSharingADisplayNameShowTheirValues()
    {
        var manager = new TestManager(new Target())
        {
            Loader = (_, _) => Task.FromResult(new SetupChoicePage(
                [new("a", "a"), new("model-v1", "Model"), new("model-v1:300k", "Model")])),
        };
        IRenderedComponent<SetupFieldInput> choice = Field(Editor(new TestSettings { ChoiceCapabilities = SetupChoiceCapabilities.Searchable }, manager), "choice");
        choice.WaitForAssertion(() => Assert.Single(choice.FindComponents<Combobox>()));
        await choice.InvokeAsync(() => choice.FindComponent<Combobox>().Instance.OpenChanged.InvokeAsync(true));
        choice.WaitForAssertion(() => Assert.Equal(3, choice.FindComponents<ComboboxItem>().Count));
        IReadOnlyList<IRenderedComponent<ComboboxItem>> items = choice.FindComponents<ComboboxItem>();
        Assert.Empty(items.Single(item => item.Instance.Value == "a").FindAll(".inference-settings-editor__choice-value"));
        Assert.Equal("model-v1:300k", items.Single(item => item.Instance.Value == "model-v1:300k")
            .Find(".inference-settings-editor__choice-value").TextContent);
        Assert.Equal("Model model-v1", items.Single(item => item.Instance.Value == "model-v1").Instance.SearchValue);
    }

    [Fact]
    public void NumericKeyboardBoundsAndDisabledStateAreAccessible()
    {
        SetupNumberEdit<decimal>? lastEdit = null;
        IRenderedComponent<SetupNumberInput<decimal>> number = Render<SetupNumberInput<decimal>>(parameters => parameters
            .Add(value => value.Text, "1.5").Add(value => value.Minimum, 0m).Add(value => value.Maximum, 2m)
            .Add(value => value.Step, 0.1m).Add(value => value.Label, "Speed")
            .Add(value => value.Edited, edit => lastEdit = edit));
        var input = number.Find("input");
        Assert.Equal("spinbutton", input.GetAttribute("role"));
        Assert.Equal("0", input.GetAttribute("aria-valuemin"));
        Assert.Equal("2", input.GetAttribute("aria-valuemax"));
        Assert.Equal("1.5", input.GetAttribute("aria-valuenow"));
        input.KeyDown("ArrowUp");
        Assert.Equal(1.6m, lastEdit!.Value);
        number.Render(parameters => parameters.Add(value => value.Text, "2"));
        Assert.True(number.FindAll("button")[1].HasAttribute("disabled"));
        number.Find("input").KeyDown("ArrowDown");
        Assert.Equal(1.9m, lastEdit.Value);
        lastEdit = null;
        number.Render(parameters => parameters.Add(value => value.Disabled, true));
        Assert.True(number.Find("input").HasAttribute("disabled"));
        Assert.All(number.FindAll("button"), button => Assert.True(button.HasAttribute("disabled")));
        number.Find("input").KeyDown("ArrowDown");
        Assert.Null(lastEdit);
    }

    [Fact]
    public void NumericControlsPreserveExactTypesAndOnlyClampExplicitSteps()
    {
        SetupNumberEdit<long>? whole = null;
        IRenderedComponent<SetupNumberInput<long>> integer = Render<SetupNumberInput<long>>(parameters => parameters
            .Add(value => value.Text, "9223372036854775806").Add(value => value.Step, 2L)
            .Add(value => value.Edited, edit => whole = edit));
        integer.Find("input").Input("9007199254740993");
        Assert.Equal(9007199254740993L, whole!.Value);
        integer.FindAll("button")[1].Click();
        Assert.Equal(long.MaxValue, whole.Value);
        SetupNumberEdit<decimal>? fraction = null;
        IRenderedComponent<SetupNumberInput<decimal>> number = Render<SetupNumberInput<decimal>>(parameters => parameters
            .Add(value => value.Minimum, 0m).Add(value => value.Maximum, 10m)
            .Add(value => value.Edited, edit => fraction = edit));
        number.Find("input").Input("1.1234567890123456789012345678");
        Assert.Equal(1.1234567890123456789012345678m, fraction!.Value);
        number.Find("input").Input("11");
        Assert.Equal(11m, fraction.Value);
        Assert.NotNull(fraction.Error);
        number.Find("input").Input(string.Empty);
        Assert.Null(fraction.Value);
        Assert.Null(fraction.Error);
    }

    private static (TestSettings Settings, TestManager Manager) New(Target? target = null) =>
        (new TestSettings(), new TestManager(target ?? new Target()));
    private IRenderedComponent<InferenceSettingsEditor> Editor(TestSettings settings, TestManager manager, List<SetupEditorState>? states = null) =>
        Render<InferenceSettingsEditor>(parameters => parameters.Add(value => value.Settings, settings).Add(value => value.Manager, manager)
            .Add(value => value.Host, new Host()).Add(value => value.StateChanged, state => states?.Add(state)));
    private static IRenderedComponent<SetupFieldInput> Field(IRenderedComponent<InferenceSettingsEditor> editor, string id) =>
        Assert.Single(editor.FindComponents<SetupFieldInput>(), component => component.Instance.Field.Description.Id == id);
    private static void Click<T>(IRenderedComponent<T> fragment, string label) where T : IComponent =>
        Assert.Single(fragment.FindAll("button"), button => button.TextContent.Trim() == label).Click();

    // Optional generated evidence for browser layout review; test assertions always run.
    // Serve beside the pinned Lumeo CSS and this library's generated scoped CSS bundle.
    private static void WritePreview(string markup, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("INFERENCE_UI_PREVIEW_DIRECTORY");
        if (string.IsNullOrEmpty(directory)) { return; }
        Directory.CreateDirectory(directory);
        foreach (bool darkFrame in new[] { false, true })
        {
            File.WriteAllText(Path.Combine(directory, name + (darkFrame ? "-dark-framed" : string.Empty) + ".html"), $$"""
            <!doctype html><html lang="en" class="{{(darkFrame ? "dark framed" : string.Empty)}}"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Settings renderer — {{name}}</title>
            <link rel="stylesheet" href="lumeo.css"><link rel="stylesheet" href="lumeo-utilities.css">
            <link rel="stylesheet" href="settings.css">
            <style>
            body { margin: 0; padding: 1rem; font: 14px system-ui; color: var(--color-foreground); background: var(--color-background); }
            main { max-width: 46rem; margin: auto; }
            .framed main { padding: 1rem; border: 1px solid var(--color-border); border-radius: var(--radius); background: var(--color-card); }
            </style></head><body><main>{{markup}}</main></body></html>
            """);
        }
    }

    private sealed record Changes(Uri? Endpoint, long? Count, decimal? Amount);
    private sealed class Target : ISetupCommitTarget<Changes>
    {
        public int Calls { get; private set; }
        public Changes? Saved { get; private set; }
        public SetupCommitResult Result { get; init; } = new(SetupCommitState.Committed);
        public Task<SetupCommitResult> CommitAsync(Changes changes, CancellationToken cancellationToken = default)
        { Calls++; Saved = changes; return Task.FromResult(Result); }
    }

    private sealed class TestSettings() : InferenceSettings<Changes>(
        new(new("test", "Test", InferenceImplementationKind.Connect, InferenceTask.TextGeneration), "Test editor"),
        new(SetupChangeIntent.Create, "entry", null))
    {
        private Uri? _endpoint = new("https://original.test/");
        private Uri? _secondEndpoint = new("https://second.test/");
        private long? _count = 2;
        private decimal? _amount = 1.5m;
        private string _name = string.Empty, _choice = "a";
        private SetupSecretEditKind _secret;
        public string Callback { get; private set; } = string.Empty;
        public SetupChoiceCapabilities ChoiceCapabilities { get; init; } = SetupChoiceCapabilities.Bounded;
        public void ShowPrerequisite() => Interaction = new SetupExternalPrerequisite("Authenticate the external CLI",
            new("recheck", "Check again", SetupActionPurpose.CheckPrerequisites, SetupSideEffect.None));
        public void StartCallback()
        {
            Callback = string.Empty;
            Interaction = new SetupBrowserAuthorization(new("https://auth.test/"),
                new(new(Guid.NewGuid().ToString("N"), "Callback"), string.Empty, string.Empty, value => Edit(() => Callback = value)),
                new("continue", "Continue", SetupActionPurpose.ContinueAuthentication, SetupSideEffect.None),
                new("cancel", "Cancel", SetupActionPurpose.CancelAuthentication, SetupSideEffect.None));
        }
        public void StartDevice(string code) => Interaction = new SetupDeviceAuthorization(new("https://device.test/"), code, null,
            new("continue", "Continue", SetupActionPurpose.ContinueAuthentication, SetupSideEffect.Network),
            new("cancel", "Cancel", SetupActionPurpose.CancelAuthentication, SetupSideEffect.None));
        public void EndInteraction()
        {
            Callback = string.Empty;
            Interaction = null;
        }
        protected override bool IsDraftDirty => _endpoint != new Uri("https://original.test/") || _secondEndpoint != new Uri("https://second.test/")
            || _count != 2 || _amount != 1.5m || _name.Length != 0 || _choice != "a" || _secret != SetupSecretEditKind.Keep || Callback.Length != 0;
        protected override IReadOnlyList<SetupStep> CreateSteps() =>
        [new("first", "First step", "Edit", [new("fields", "Fields",
        [
            new SetupUriField(new("url", "URL"), _endpoint, null, value => Edit(() => _endpoint = value)),
            new SetupTextField(new("name", "Name"), _name, string.Empty, value => Edit(() => _name = value)),
            new SetupIntegerField(new("count", "Count"), _count, 2, value => Edit(() => _count = value)),
            new SetupDecimalField(new("amount", "Amount"), _amount, 1.5m, value => Edit(() => _amount = value), 0, 10, 0.1m),
            new SetupSecretField(new("secret", "Secret"), true, _secret, value => Edit(() => _secret = value.Kind)),
            new SetupChoiceField(new("choice", "Choice"), _choice, "a", value => Edit(() => _choice = value), false,
                capabilities: ChoiceCapabilities)
        ])]), new("second", "Second step", "Edit", [new("second-fields", "Fields",
            [new SetupUriField(new("second-url", "Second URL"), _secondEndpoint, null, value => Edit(() => _secondEndpoint = value))])])];
        protected override IReadOnlyList<SetupActionDescriptor> CreateActions() => [];
        protected override SetupValidation ValidateDraft() => SetupValidation.Valid;
        protected override Changes CreateChanges() => new(_endpoint, _count, _amount);
    }

    private sealed class TestManager(Target target) : InferenceSettingsManager<TestSettings, Changes>(target), IInferenceSettingsStatusSource
    {
        public Target Target { get; } = target;
        public int Rechecks { get; private set; }
        public string? CompletedCallback { get; private set; }
        public int ChoiceCalls { get; private set; }
        public List<CancellationToken> ChoiceTokens { get; } = [];
        public Func<SetupChoiceQuery, CancellationToken, Task<SetupChoicePage>> Loader { get; set; } =
            (_, _) => Task.FromResult(new SetupChoicePage([new("a", "A"), new("b", "B")]));
        public event EventHandler? StatusChanged;
        public void RaiseStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);
        public Task RefreshStatusAsync(InferenceSettings settings, CancellationToken cancellationToken = default)
        {
            settings.Status = settings.Status with { Verified = SetupObservation.Known(true) };
            return Task.CompletedTask;
        }

        protected override Task<SetupActionResult> RunCoreAsync(TestSettings settings, SetupActionDescriptor action,
            IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
        {
            switch (action.Id)
            {
                case "recheck": Rechecks++; break;
                case "continue" when settings.Interaction is SetupBrowserAuthorization: CompletedCallback = settings.Callback; break;
                case "continue": return Task.FromResult(SetupActionResult.Completed);
            }
            settings.EndInteraction();
            return Task.FromResult(SetupActionResult.Completed);
        }

        protected override Task<SetupChoicePage> QueryProviderChoicesAsync(TestSettings settings, string fieldId,
            SetupChoiceQuery query, CancellationToken cancellationToken)
        {
            ChoiceCalls++;
            ChoiceTokens.Add(cancellationToken);
            return Loader(query, cancellationToken);
        }
    }

    private sealed class Host : IInferenceSettingsInteractionHost
    {
        public ValueTask<bool> AuthorizeAsync(string operation, SetupSideEffect sideEffects, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        public Task<SetupActionResult> ExecuteAsync(IInferenceSettingsManager manager, InferenceSettings settings, SetupActionDescriptor action,
            IProgress<SetupProgress> progress, CancellationToken cancellationToken = default) =>
            manager.RunAsync(settings, action.Id, progress, cancellationToken);
        public ValueTask OpenBrowserAsync(Uri uri, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
