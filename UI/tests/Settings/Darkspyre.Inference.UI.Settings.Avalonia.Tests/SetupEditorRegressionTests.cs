using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.UI.Settings;
using Xunit;

namespace Darkspyre.Inference.UI.Settings.Avalonia.Tests;

public sealed class SetupEditorRegressionTests
{
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
        Assert.All(host.Copies, copy => Assert.IsNotType<global::Avalonia.Controls.Button>(copy.Source));
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

    private static (TestSettings Settings, TestManager Manager) New(Target? target = null) =>
        (new TestSettings(), new TestManager(target ?? new Target()));

    private static InferenceSettingsEditor Editor(TestSettings settings, TestManager manager, List<SetupEditorState>? states = null, Host? host = null)
    {
        InferenceSettingsEditor editor = new(settings, manager, host ?? new Host());
        editor.StateChanged += (_, state) => states?.Add(state);
        EditorSession.Show(editor);
        return editor;
    }

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
        public List<(string Text, global::Avalonia.Visual Source)> Copies { get; } = [];
        public Task CopyToClipboardAsync(global::Avalonia.Visual source, string text)
        {
            Copies.Add((text, source));
            return Task.CompletedTask;
        }
    }
}
