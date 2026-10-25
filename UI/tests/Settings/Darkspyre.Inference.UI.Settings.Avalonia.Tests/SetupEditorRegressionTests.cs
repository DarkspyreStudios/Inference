using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.UI.Settings;
using Xunit;

namespace Darkspyre.Inference.UI.Settings.Avalonia.Tests;

public sealed class SetupEditorRegressionTests
{
    [Fact]
    public Task ExecutableDefaultAppearsAsPlaceholderWithoutApplyingAnEdit() => EditorSession.RunAsync(() =>
    {
        var (settings, manager) = New();
        int edits = 0;
        var field = new SetupExecutableField(new("executable", "Executable"), new(string.Empty), new("claude"), _ =>
        {
            edits++;
            return SetupEditResult.Applied;
        });
        SetupFieldInput input = new(field, settings, manager, new Host(), new SetupEditorInputState(), _ => Task.CompletedTask);
        EditorSession.Show(input);

        TextBox box = EditorSession.All<TextBox>(input).First();
        Assert.Equal("claude", box.PlaceholderText);
        Assert.Empty(box.Text ?? string.Empty);
        Assert.Empty(field.Value.Value);
        Assert.Equal(0, edits);
        return Task.CompletedTask;
    });

    [Fact]
    public Task InvalidUrlSurvivesRefreshAndAnotherEditAndNeverCommitsOldValue() => EditorSession.RunAsync(async () =>
    {
        var (settings, manager) = New();
        InferenceSettingsEditor editor = Editor(settings, manager);
        EditorSession.Type(EditorSession.Field(editor, "url"), "not a url");
        manager.RaiseStatusChanged();
        EditorSession.Type(EditorSession.Field(editor, "name"), "changed");
        await EditorSession.WaitForAsync(() => EditorSession.TextIn(EditorSession.Field(editor, "url")) == "not a url");
        Assert.True(editor.State.HasUnsavedChanges);
        Assert.True(editor.State.HasInputErrors);
        Assert.False(editor.State.CanSave);
        Assert.Equal(SetupCommitState.Invalid, (await editor.SaveAsync()).State);
        Assert.Equal(0, manager.Target.Calls);
        EditorSession.Type(EditorSession.Field(editor, "url"), "https://changed.test/");
        Assert.Equal(SetupCommitState.Committed, (await editor.SaveAsync()).State);
        Assert.Equal(new Uri("https://changed.test/"), manager.Target.Saved!.Endpoint);
        Assert.False(editor.State.HasUnsavedChanges);
    });

    [Fact]
    public Task HostStateIncludesUnappliedSecretsAndRevertsWithoutRevisionHeuristics() => EditorSession.RunAsync(async () =>
    {
        var (settings, manager) = New();
        var states = new List<SetupEditorState>();
        InferenceSettingsEditor editor = Editor(settings, manager, states);
        manager.RaiseStatusChanged();
        EditorSession.Settle();
        Assert.False(editor.State.HasUnsavedChanges);
        EditorSession.Type(EditorSession.Field(editor, "name"), "new");
        Assert.True(editor.State.HasUnsavedChanges);
        EditorSession.Type(EditorSession.Field(editor, "name"), string.Empty);
        Assert.False(editor.State.HasUnsavedChanges);
        EditorSession.Type(EditorSession.Field(editor, "secret"), "never silently discard");
        Assert.True(editor.State.HasPendingInput);
        Assert.Equal(SetupCommitState.Invalid, (await editor.SaveAsync()).State);
        EditorSession.Click(EditorSession.Button(EditorSession.Field(editor, "secret"), "Replace secret"));
        Assert.False(editor.State.HasPendingInput);
        Assert.True(editor.State.HasUnsavedChanges);
        EditorSession.Click(EditorSession.Button(EditorSession.Field(editor, "secret"), "Keep"));
        Assert.False(editor.State.HasUnsavedChanges);
        Assert.Contains(states, state => state.HasPendingInput && state.HasUnsavedChanges);
    });

    [Fact]
    public Task CallbackIsRetainedOnlyForItsAttemptAndDoesNotEnterFieldValues() => EditorSession.RunAsync(async () =>
    {
        var (settings, manager) = New();
        settings.StartCallback();
        InferenceSettingsEditor editor = Editor(settings, manager);
        string id = ((SetupBrowserAuthorization)settings.Interaction!).Callback.Description.Id;
        EditorSession.Type(EditorSession.Field(editor, id), "code");
        manager.RaiseStatusChanged();
        EditorSession.Settle();
        Assert.Equal("code", EditorSession.TextIn(EditorSession.Field(editor, id)));
        Assert.Empty(((SetupBrowserAuthorization)settings.Interaction!).Callback.Value);
        EditorSession.Type(EditorSession.Field(editor, id), "code#state");
        EditorSession.Click(EditorSession.Button(editor, "Continue"));
        await EditorSession.WaitForAsync(() => manager.CompletedCallback == "code#state");
        settings.StartCallback();
        manager.RaiseStatusChanged();
        string next = ((SetupBrowserAuthorization)settings.Interaction!).Callback.Description.Id;
        Assert.NotEqual(id, next);
        await EditorSession.WaitForAsync(() => EditorSession.All<SetupFieldInput>(editor).Any(field => field.Field.Description.Id == next));
        Assert.Empty(EditorSession.TextIn(EditorSession.Field(editor, next)) ?? string.Empty);
    });

    [Fact]
    public Task DependentEditStartsNewChoiceRequestAndDiscardsOldCompletion() => EditorSession.RunAsync(async () =>
    {
        var first = new TaskCompletionSource<SetupChoicePage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<SetupChoicePage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new TestSettings { ChoiceCapabilities = SetupChoiceCapabilities.Searchable };
        var manager = new TestManager(new Target());
        manager.Loader = (_, _) => manager.ChoiceCalls == 1 ? first.Task : second.Task;
        try
        {
            InferenceSettingsEditor editor = Editor(settings, manager);
            Assert.Equal(1, manager.ChoiceCalls);
            EditorSession.Type(EditorSession.Field(editor, "name"), "new repository");
            await EditorSession.WaitForAsync(() => manager.ChoiceCalls == 2);
            Assert.True(manager.ChoiceTokens[0].IsCancellationRequested);
            second.SetResult(new([new("new", "New variant")]));
            await EditorSession.WaitForAsync(() => !EditorSession.TextOf(EditorSession.Field(editor, "choice")).Contains("Loading choices", StringComparison.Ordinal));
            Assert.Equal(["new"], ChoiceValues(EditorSession.Field(editor, "choice")));
            first.SetResult(new([], State: SetupChoiceState.Failed, Issues: [new("old", "Obsolete failure")]));
            EditorSession.Settle();
            await Task.Delay(50);
            EditorSession.Settle();
            Assert.DoesNotContain("Obsolete failure", EditorSession.TextOf(EditorSession.Field(editor, "choice")), StringComparison.Ordinal);
            Assert.Equal(["new"], ChoiceValues(EditorSession.Field(editor, "choice")));
        }
        finally
        {
            first.TrySetResult(new([]));
            second.TrySetResult(new([]));
        }
    });

    [Fact]
    public Task BoundedChoicesUseSelectWithoutSearchOrRefresh() => EditorSession.RunAsync(() =>
    {
        var (settings, manager) = New();
        InferenceSettingsEditor editor = Editor(settings, manager);
        SetupFieldInput choice = EditorSession.Field(editor, "choice");
        Assert.Single(EditorSession.All<ComboBox>(choice));
        Assert.Empty(EditorSession.All<AutoCompleteBox>(choice));
        Assert.Empty(EditorSession.All<DockPanel>(choice, "inference-settings-choice-search"));
        Assert.DoesNotContain(EditorSession.All<Button>(choice), button => button.Content as string == "Refresh choices");
        Assert.Equal("Count", global::Avalonia.Automation.AutomationProperties.GetName(EditorSession.All<TextBox>(EditorSession.Field(editor, "count")).First()));
        Assert.Equal("Amount", global::Avalonia.Automation.AutomationProperties.GetName(EditorSession.All<TextBox>(EditorSession.Field(editor, "amount")).First()));
        return Task.CompletedTask;
    });

    [Fact]
    public Task UnknownChoiceSupportKeepsRefreshAndPagingAvailable() => EditorSession.RunAsync(async () =>
    {
        var settings = new TestSettings { ChoiceCapabilities = new() };
        var manager = new TestManager(new Target())
        {
            Loader = (_, _) => Task.FromResult(new SetupChoicePage([new("first", "First")], "next"))
        };
        InferenceSettingsEditor editor = Editor(settings, manager);
        await EditorSession.WaitForAsync(() => SearchShown(EditorSession.Field(editor, "choice")));
        SetupFieldInput choice = EditorSession.Field(editor, "choice");
        Assert.NotNull(EditorSession.Button(choice, "Refresh choices"));
        Assert.NotNull(EditorSession.Button(choice, "Load more"));
        Assert.DoesNotContain("incomplete bounded list", EditorSession.TextOf(choice), StringComparison.Ordinal);
    });

    [Fact]
    public Task MultiStepSettingsNameTheCurrentStepOnceInTheStepList() => EditorSession.RunAsync(() =>
    {
        var (settings, manager) = New();
        InferenceSettingsEditor editor = Editor(settings, manager);
        Assert.Empty(EditorSession.All<TextBlock>(editor, "h3"));
        Assert.Equal(["1. First step", "2. Second step"],
            EditorSession.All<WrapPanel>(editor, "inference-settings-steps").Single().Children.OfType<TextBlock>().Select(item => item.Text));
        Assert.Equal("1. First step", Assert.Single(EditorSession.All<TextBlock>(editor, "current")).Text);
        EditorSession.Click(EditorSession.Button(editor, "Next"));
        Assert.Equal("2. Second step", Assert.Single(EditorSession.All<TextBlock>(editor, "current")).Text);
        Assert.Empty(EditorSession.All<TextBlock>(editor, "h3"));
        return Task.CompletedTask;
    });

    [Fact]
    public Task DeviceCodeCanBeCopied() => EditorSession.RunAsync(async () =>
    {
        var (settings, manager) = New();
        var host = new Host();
        InferenceSettingsEditor editor = Editor(settings, manager, host: host);
        settings.StartDevice("ABCD-1234");
        manager.RaiseStatusChanged();
        await EditorSession.WaitForAsync(() => host.Copies.Count == 1);
        Assert.Equal("ABCD-1234", EditorSession.All<SelectableTextBlock>(editor, "inference-settings-user-code").Single().Text);
        EditorSession.Click(EditorSession.Button(editor, "Copy code"));
        await EditorSession.WaitForAsync(() => host.Copies.Count == 2);
        Assert.Equal(("ABCD-1234", "Sign-in code copied to clipboard."), host.Copies[1]);
    });

    /// <summary>A device code is copied through the host the first time it is shown, not again on a redraw, and a new
    /// code is copied once more.</summary>
    [Fact]
    public Task DeviceCodeIsCopiedOnceWhenShown() => EditorSession.RunAsync(async () =>
    {
        var (settings, manager) = New();
        var host = new Host();
        InferenceSettingsEditor editor = Editor(settings, manager, host: host);
        settings.StartDevice("ABCD-1234");
        manager.RaiseStatusChanged();
        await EditorSession.WaitForAsync(() => host.Copies.Count == 1);
        manager.RaiseStatusChanged();
        EditorSession.Settle();
        Assert.Single(host.Copies);
        settings.StartDevice("WXYZ-5678");
        manager.RaiseStatusChanged();
        await EditorSession.WaitForAsync(() => host.Copies.Count == 2);

        Assert.Equal("WXYZ-5678", EditorSession.All<SelectableTextBlock>(editor, "inference-settings-user-code").Single().Text);
        Assert.Equal(["ABCD-1234", "WXYZ-5678"], host.Copies.Select(copy => copy.Text));
        Assert.All(host.Copies, copy => Assert.Equal("Sign-in code copied to clipboard.", copy.Copied));
    });

    [Fact]
    public Task ProviderSearchAppearsOnlyWhileMoreChoicesRemain() => EditorSession.RunAsync(async () =>
    {
        var searchable = new TestSettings { ChoiceCapabilities = SetupChoiceCapabilities.Searchable };
        InferenceSettingsEditor first = Editor(searchable, new TestManager(new Target()));
        SetupFieldInput choice = EditorSession.Field(first, "choice");
        Assert.Single(EditorSession.All<AutoCompleteBox>(choice));
        Assert.False(SearchShown(choice));

        var paged = new TestManager(new Target()) { Loader = (_, _) => Task.FromResult(new SetupChoicePage([new("a", "A")], "1")) };
        InferenceSettingsEditor second = Editor(new TestSettings { ChoiceCapabilities = SetupChoiceCapabilities.Searchable }, paged);
        await EditorSession.WaitForAsync(() => SearchShown(EditorSession.Field(second, "choice")));
        Assert.NotNull(EditorSession.Button(EditorSession.Field(second, "choice"), "Load more"));
    });

    [Fact]
    public Task ChoicesSharingADisplayNameShowTheirValues() => EditorSession.RunAsync(async () =>
    {
        var manager = new TestManager(new Target())
        {
            Loader = (_, _) => Task.FromResult(new SetupChoicePage(
                [new("a", "a"), new("model-v1", "Model"), new("model-v1:300k", "Model")])),
        };
        InferenceSettingsEditor editor = Editor(new TestSettings { ChoiceCapabilities = SetupChoiceCapabilities.Searchable }, manager);
        await EditorSession.WaitForAsync(() => ChoiceValues(EditorSession.Field(editor, "choice")).Length == 3);
        AutoCompleteBox box = Assert.Single(EditorSession.All<AutoCompleteBox>(EditorSession.Field(editor, "choice")));
        SetupChoice[] items = [.. (IEnumerable<SetupChoice>)box.ItemsSource!];
        Control Drawn(string value) => box.ItemTemplate!.Build(items.Single(item => item.Value == value))!;
        Assert.Empty(EditorSession.All<TextBlock>(Drawn("a"), "inference-settings-choice-value"));
        Assert.Equal("model-v1:300k", EditorSession.All<TextBlock>(Drawn("model-v1:300k"), "inference-settings-choice-value").Single().Text);
        // The value is searchable alongside the shared display name.
        Assert.True(box.ItemFilter!("model-v1:300k", items.Single(item => item.Value == "model-v1:300k")));
        Assert.False(box.ItemFilter!("model-v1:300k", items.Single(item => item.Value == "model-v1")));
    });

    [Fact]
    public Task IntegerControlsPreserveExactValuesAndOnlyClampExplicitSteps() => EditorSession.RunAsync(() =>
    {
        SetupNumberEdit<long>? whole = null;
        SetupNumberInput<long> integer = new("Count", null, null, 2L, false);
        integer.Edited += (_, edit) => whole = edit;
        EditorSession.Show(integer);
        integer.Show("9223372036854775806", false, false);
        TextBox box = EditorSession.All<TextBox>(integer).Single();
        box.Text = "9007199254740993";
        EditorSession.Settle();
        Assert.Equal(9007199254740993L, whole!.Value);
        box.Text = "9223372036854775806";
        EditorSession.Settle();
        EditorSession.Click(EditorSession.Button(integer, "Increase Count"));
        Assert.Equal(long.MaxValue, whole.Value);
        return Task.CompletedTask;
    });

    private static (TestSettings Settings, TestManager Manager) New(Target? target = null) =>
        (new TestSettings(), new TestManager(target ?? new Target()));

    private static InferenceSettingsEditor Editor(TestSettings settings, TestManager manager, List<SetupEditorState>? states = null, Host? host = null)
    {
        InferenceSettingsEditor editor = new(settings, manager, host ?? new Host());
        editor.StateChanged += (_, state) => states?.Add(state);
        EditorSession.Show(editor);
        return editor;
    }

    private static string[] ChoiceValues(SetupFieldInput field) =>
        [.. (Assert.Single(EditorSession.All<AutoCompleteBox>(field)).ItemsSource as IEnumerable<SetupChoice> ?? []).Select(choice => choice.Value)];

    // The provider search row shows only while more choices remain or a search is typed.
    private static bool SearchShown(SetupFieldInput field) =>
        EditorSession.All<DockPanel>(field, "inference-settings-choice-search").SingleOrDefault()?.IsVisible == true;

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

    private sealed class Host : IAvaloniaSettingsInteractionHost
    {
        public ValueTask<bool> AuthorizeAsync(string operation, SetupSideEffect sideEffects, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        public Task<SetupActionResult> ExecuteAsync(IInferenceSettingsManager manager, InferenceSettings settings, SetupActionDescriptor action,
            IProgress<SetupProgress> progress, CancellationToken cancellationToken = default) =>
            manager.RunAsync(settings, action.Id, progress, cancellationToken);
        public ValueTask OpenBrowserAsync(Uri uri, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public Task RevealFolderAsync(string folder, CancellationToken cancellationToken) => Task.CompletedTask;
        public List<(string Text, string Copied)> Copies { get; } = [];
        public Task CopyToClipboardAsync(global::Avalonia.Visual source, string text, string copied)
        {
            Copies.Add((text, copied));
            return Task.CompletedTask;
        }
    }
}
