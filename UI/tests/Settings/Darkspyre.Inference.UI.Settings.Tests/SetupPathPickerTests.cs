using Bunit;
using Darkspyre.Inference.Common.Setup;
using Lumeo;
using Xunit;

namespace Darkspyre.Inference.UI.Settings.Tests;

public sealed class SetupPathPickerTests : BunitContext
{
    public SetupPathPickerTests() { Services.AddLumeo(); JSInterop.Mode = JSRuntimeMode.Loose; }

    [Theory]
    [InlineData(SetupPathSelectionKind.File)]
    [InlineData(SetupPathSelectionKind.Folder)]
    public async Task HostPickerAppliesTheSelectedPathThroughTheBoundField(SetupPathSelectionKind kind)
    {
        string? saved = null;
        var host = new PickerHost { Result = Task.FromResult<string?>("/models/selected") };
        var field = new SetupTextField(new("path", "Location"), "/models/old", string.Empty,
            value => { saved = value; return SetupEditResult.Applied; }, pathSelection: kind);
        var component = Render<SetupFieldInput>(parameters => parameters.Add(item => item.Field, field).Add(item => item.Host, host));
        await component.Find("button").ClickAsync(new());
        Assert.Equal(kind, host.Kind);
        Assert.Equal("/models/old", host.Current);
        Assert.Equal("/models/selected", saved);
    }

    [Fact]
    public async Task CancelledPickerDoesNotEditAndLateResultCannotOverwriteNewField()
    {
        int calls = 0;
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new PickerHost { Result = completion.Task };
        var field = new SetupTextField(new("path", "Location"), string.Empty, string.Empty,
            _ => { calls++; return SetupEditResult.Applied; }, pathSelection: SetupPathSelectionKind.File);
        var component = Render<SetupFieldInput>(parameters => parameters.Add(item => item.Field, field).Add(item => item.Host, host));
        Task click = component.Find("button").ClickAsync(new());
        component.Render(parameters => parameters.Add(item => item.EditVersion, 1));
        completion.SetResult("/obsolete");
        await click;
        Assert.Equal(0, calls);
        host.Result = Task.FromResult<string?>(null);
        await component.Find("button").ClickAsync(new());
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ExecutableSelectionUsesTheSamePickerWithoutRemovingManualCommandNames()
    {
        string? saved = null;
        var host = new PickerHost { Result = Task.FromResult<string?>("/bin/provider") };
        var field = new SetupExecutableField(new("executable", "Executable"), new("provider"), new(string.Empty),
            value => { saved = value.Value; return SetupEditResult.Applied; });
        var component = Render<SetupFieldInput>(parameters => parameters.Add(item => item.Field, field).Add(item => item.Host, host));
        component.Find("input").Input("provider-on-path");
        Assert.Equal("provider-on-path", saved);
        await component.Find("button").ClickAsync(new());
        Assert.Equal("/bin/provider", saved);
    }

    private sealed class PickerHost : IInferenceSettingsInteractionHost, IInferenceSettingsPathPicker
    {
        public Task<string?> Result { get; set; } = Task.FromResult<string?>(null);
        public SetupPathSelectionKind? Kind { get; private set; }
        public string? Current { get; private set; }
        public ValueTask<string?> SelectPathAsync(SetupPathSelectionKind kind, string currentValue, CancellationToken cancellationToken = default)
        { Kind = kind; Current = currentValue; return new(Result); }
        public ValueTask<bool> AuthorizeAsync(string operation, SetupSideEffect sideEffects, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        public Task<SetupActionResult> ExecuteAsync(IInferenceSettingsManager manager, InferenceSettings settings, SetupActionDescriptor action,
            IProgress<SetupProgress> progress, CancellationToken cancellationToken = default) => manager.RunAsync(settings, action.Id, progress, cancellationToken);
        public ValueTask OpenBrowserAsync(Uri uri, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
