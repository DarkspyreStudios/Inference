using Bunit;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.UI.Settings;
using Lumeo;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Darkspyre.Inference.UI.Settings.Blazor.Tests;

public sealed class SetupEditorRegressionTests : BunitContext
{
    public SetupEditorRegressionTests() { Services.AddLumeo(); JSInterop.Mode = JSRuntimeMode.Loose; }

    /// <summary>A device code is copied the first time it is shown, not again on a redraw; Copy code copies it again, and a
    /// clipboard that refuses says so.</summary>
    [Fact]
    public void DeviceCodeIsCopiedOnceWhenShownAndReportsWhenTheClipboardRefuses()
    {
        var (settings, manager) = New();
        IRenderedComponent<InferenceSettingsEditor> editor = Editor(settings, manager);
        settings.StartDevice("ABCD-1234");
        editor.Render();
        editor.WaitForAssertion(() => Assert.Contains("Sign-in code copied to clipboard.", editor.Markup, StringComparison.Ordinal));
        Assert.Equal("ABCD-1234", editor.Find(".inference-settings-editor__user-code").TextContent);
        editor.Render();
        Assert.Single(JSInterop.Invocations["navigator.clipboard.writeText"]);
        Click(editor, "Copy code");
        editor.WaitForAssertion(() => Assert.Equal(2, JSInterop.Invocations["navigator.clipboard.writeText"].Count));

        // The sign-in address shows as text and copies, for a browser that did not open.
        Assert.Equal("https://device.test/", editor.Find(".inference-settings-editor__auth-link").TextContent);
        Click(editor, "Copy link");
        editor.WaitForAssertion(() => Assert.Contains("Sign-in link copied to clipboard.", editor.Markup, StringComparison.Ordinal));

        JSInterop.SetupVoid("navigator.clipboard.writeText", "WXYZ-9876").SetException(new Microsoft.JSInterop.JSException("denied"));
        settings.StartDevice("WXYZ-9876");
        editor.Render();
        editor.WaitForAssertion(() => Assert.Contains("The code could not be copied. Select it and copy it instead.", editor.Markup, StringComparison.Ordinal));
        Assert.Equal(["ABCD-1234", "ABCD-1234", "https://device.test/", "WXYZ-9876"],
            JSInterop.Invocations["navigator.clipboard.writeText"].Select(call => (string)call.Arguments[0]!));
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

    private static (TestSettings Settings, TestManager Manager) New(Target? target = null) =>
        (new TestSettings(), new TestManager(target ?? new Target()));
    private IRenderedComponent<InferenceSettingsEditor> Editor(TestSettings settings, TestManager manager, List<SetupEditorState>? states = null) =>
        Render<InferenceSettingsEditor>(parameters => parameters.Add(value => value.Settings, settings).Add(value => value.Manager, manager)
            .Add(value => value.Host, new Host()).Add(value => value.StateChanged, state => states?.Add(state)));
    private static IRenderedComponent<SetupFieldInput> Field(IRenderedComponent<InferenceSettingsEditor> editor, string id) =>
        Assert.Single(editor.FindComponents<SetupFieldInput>(), component => component.Instance.Field.Description.Id == id);
    private static void Click<T>(IRenderedComponent<T> fragment, string label) where T : IComponent =>
        Assert.Single(fragment.FindAll("button"), button => button.TextContent.Trim() == label).Click();

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

    private sealed class Host : IInferenceSettingsInteractionHost
    {
        public ValueTask<bool> AuthorizeAsync(string operation, SetupSideEffect sideEffects, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        public Task<SetupActionResult> ExecuteAsync(IInferenceSettingsManager manager, InferenceSettings settings, SetupActionDescriptor action,
            IProgress<SetupProgress> progress, CancellationToken cancellationToken = default) =>
            manager.RunAsync(settings, action.Id, progress, cancellationToken);
        public ValueTask OpenBrowserAsync(Uri uri, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public Task RevealFolderAsync(string folder, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
