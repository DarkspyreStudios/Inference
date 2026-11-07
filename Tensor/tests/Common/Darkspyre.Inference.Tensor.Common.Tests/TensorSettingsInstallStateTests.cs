using System.Security.Cryptography;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorSettingsInstallStateTests : IDisposable
{
    private const string Store = "Family";
    private static readonly InferenceSetupDefinition Setup = new(
        new("test.tensor", "Test Tensor", InferenceImplementationKind.Tensor, InferenceTask.TextGeneration), "Test engine.");
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"darkspyre-install-state-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    [Fact]
    public async Task ChoicesAndTheCardFollowEachModelsInstallState()
    {
        TensorModelPackDefinition installed = Definition("test/installed", "installed weights");
        TensorModelPackDefinition small = Definition("test/large", "large weights");
        TensorModelPackDefinition large = small with { Artifacts = [small.Artifacts[0] with { Length = 487_601_967 }] };
        TensorModelPackDefinition missing = Definition("test/missing", "missing weights");
        var list = new TestList(new TensorModelListing("/models", [
            new(installed, SetupInstallState.Installed, null, null),
            new(large, SetupInstallState.NotDownloaded, null, null),
            new(missing, SetupInstallState.FilesMissing, "model.gguf", null)], null));
        using var packs = new TensorModelPackManager(new([]), Path.Combine(_root, Store));
        var manager = new TensorSettingsManager(Setup, new NoSave(), list, packs);
        TensorSettings settings = await manager.OpenAsync(NewEntry());

        SetupChoicePage choices = await manager.QueryChoicesAsync(settings, "model", new());
        Assert.Equal(
            [
                (installed.Key.ToString(), SetupInstallState.Installed, installed.Artifacts[0].Length),
                (large.Key.ToString(), SetupInstallState.NotDownloaded, 487_601_967L),
                (missing.Key.ToString(), SetupInstallState.NotDownloaded, missing.Artifacts[0].Length)
            ],
            choices.Choices.Select(choice => (choice.Value, choice.Install!.State, choice.Install.Size)));
        SetupStatus status = settings.Status;
        Assert.All(new[] { status.Configured, status.Authenticated, status.Installed, status.Verified, status.Loaded, status.Ready },
            observation => Assert.False(observation.IsApplicable));
        Assert.DoesNotContain(settings.Actions, action => action.Id is "status" or "install");
        Assert.Null(InstallField(settings));

        Select(settings, installed);
        Assert.True(ModelField(settings).ShowValue);
        SetupInstallStatus card = InstallField(settings)!.Status;
        Assert.Equal((SetupInstallState.Installed, "test/installed", "/models", 1, "test-only"),
            (card.State, card.Name, card.Location, card.FileCount, card.License));
        Assert.Equal([("remove", "Delete download")], card.Actions.Select(action => (action.Id, action.DisplayName)));

        Select(settings, large);
        Assert.False(ModelField(settings).ShowValue);
        card = InstallField(settings)!.Status;
        Assert.Equal(SetupInstallState.NotDownloaded, card.State);
        Assert.Equal(["Download (465 MB)"], card.Actions.Select(action => action.DisplayName));
        Assert.Equal("download", card.Actions[0].Id);

        Select(settings, missing);
        Assert.False(ModelField(settings).ShowValue);
        card = InstallField(settings)!.Status;
        Assert.Equal((SetupInstallState.FilesMissing, "model.gguf"), (card.State, card.MissingFile));
        Assert.Equal([("download", "Download again"), ("remove", "Delete download")],
            card.Actions.Select(action => (action.Id, action.DisplayName)));
    }

    private static SetupConfigurationIdentity NewEntry() => new(SetupChangeIntent.Create, Guid.NewGuid().ToString("N"), null);

    private static void Select(TensorSettings settings, TensorModelPackDefinition model) =>
        Assert.Equal(SetupEditState.Applied, ModelField(settings).SetValue(model.Key.ToString()).State);

    private static IEnumerable<SetupField> Fields(TensorSettings settings) =>
        settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields);

    private static SetupChoiceField ModelField(TensorSettings settings) =>
        Fields(settings).OfType<SetupChoiceField>().Single(field => field.Description.Id == "model");

    private static SetupInstallStatusField? InstallField(TensorSettings settings) =>
        Fields(settings).OfType<SetupInstallStatusField>().SingleOrDefault();

    private async Task<TensorModelPackDefinition> InstallAsync(string id, string content)
    {
        TensorModelPackDefinition definition = Definition(id, content);
        Directory.CreateDirectory(Path.Combine(_root, "sources"));
        await File.WriteAllTextAsync(definition.Artifacts[0].Sources[0].LocalPath, content);
        using var packs = new TensorModelPackManager(new([definition]), Path.Combine(_root, Store));
        await packs.InstallAsync(definition.Key);
        return definition;
    }

    private TensorModelPackDefinition Definition(string id, string content)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        string source = Path.Combine(_root, "sources", id.Replace('/', '-') + ".gguf");
        return new()
        {
            Key = new(id, "revision"),
            Capability = TensorCapability.Llm,
            RuntimeArchitecture = "test",
            License = "test-only",
            SupportedBackends = new HashSet<TensorBackendKind> { TensorBackendKind.Cpu },
            Artifacts = [new() { Role = "model", RelativePath = "model.gguf", Length = bytes.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Sources = [new Uri(new Uri(Path.GetFullPath(source)).AbsoluteUri)] }]
        };
    }

    private string StorePath(string assetName) => Path.Combine(_root, Store, assetName);

    private sealed class TestList : ITensorModelList
    {
        private readonly Func<Task<TensorModelListing>>? _read;

        public TestList(TensorModelListing listing) => Listing = listing;

        public TestList(Func<Task<TensorModelListing>> read)
        {
            _read = read;
            Listing = new("/models", [], null);
        }

        public TensorModelListing Listing { get; set; }

        public event EventHandler? Changed;

        public Task<TensorModelListing> ReadAsync(CancellationToken cancellationToken = default) =>
            _read?.Invoke() ?? Task.FromResult(Listing);

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class NoSave : ISetupCommitTarget<TensorSetupChanges>
    {
        public Task<SetupCommitResult> CommitAsync(TensorSetupChanges changes, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SetupCommitResult(SetupCommitState.Invalid));
    }
}
