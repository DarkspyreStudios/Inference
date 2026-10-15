using System.Security.Cryptography;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Persist;

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
            new(installed, SetupInstallState.Installed, null),
            new(large, SetupInstallState.NotDownloaded, null),
            new(missing, SetupInstallState.FilesMissing, "model.gguf")], null));
        using var packs = new TensorModelPackManager(new([]), Path.Combine(_root, Store));
        var manager = new TensorSettingsManager(Setup, new NoSave(), list, packs);
        TensorSettings settings = await manager.OpenAsync(NewEntry());

        SetupChoicePage choices = await manager.QueryChoicesAsync(settings, "model", new());
        Assert.Equal(
            [
                (installed.Key.ToString(), true, installed.Artifacts[0].Length),
                (large.Key.ToString(), false, 487_601_967L),
                (missing.Key.ToString(), false, missing.Artifacts[0].Length)
            ],
            choices.Choices.Select(choice => (choice.Value, choice.Install!.IsInstalled, choice.Install.Size)));
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

    [Fact]
    public async Task AChangedListRefreshesTheCardAndKeepsTheSelection()
    {
        TensorModelPackDefinition model = Definition("test/model", "model weights");
        var list = new TestList(new TensorModelListing("/models", [new(model, SetupInstallState.Installed, null)], null));
        using var packs = new TensorModelPackManager(new([]), Path.Combine(_root, Store));
        var manager = new TensorSettingsManager(Setup, new NoSave(), list, packs);
        TensorSettings settings = await manager.OpenAsync(NewEntry());
        Select(settings, model);
        int raised = 0;
        manager.StatusChanged += (_, _) => raised++;

        list.Listing = new("/other", [new(model, SetupInstallState.NotDownloaded, null)], null);
        list.Raise();
        await manager.RefreshStatusAsync(settings);

        Assert.Equal(1, raised);
        Assert.Equal(model.Key.ToString(), ModelField(settings).Value);
        Assert.Equal((SetupInstallState.NotDownloaded, "/other"),
            (InstallField(settings)!.Status.State, InstallField(settings)!.Status.Location));
        Assert.False(ModelField(settings).ShowValue);
    }

    [Fact]
    public async Task RemoveDeletesThePackAndTheCardShowsNotDownloaded()
    {
        TensorModelPackDefinition model = await InstallAsync("test/model", "model weights");
        File.Delete(StorePath(TensorModelPackManager.ArtifactName(model.Key, "model.gguf")));
        using var packs = new TensorModelPackManager(new([model]), Path.Combine(_root, Store));
        var manager = new TensorSettingsManager(Setup, new NoSave(), StoreList([model], saved: null), packs);
        TensorSettings settings = await manager.OpenAsync(NewEntry());
        Select(settings, model);
        Assert.Equal(SetupInstallState.FilesMissing, InstallField(settings)!.Status.State);

        SetupActionResult result = await manager.RunAsync(settings, "remove");

        Assert.Equal(SetupActionState.Completed, result.State);
        Assert.False(File.Exists(StorePath(TensorModelPackManager.ManifestName(model.Key))));
        Assert.Equal(SetupInstallState.NotDownloaded, InstallField(settings)!.Status.State);
    }

    [Fact]
    public async Task AnUnreportedSavedModelShowsOnlyInTheCard()
    {
        TensorModelPackDefinition builtIn = Definition("test/built-in", "built-in weights");
        TensorModelPackDefinition saved = await InstallAsync("test/imported", "imported weights");
        File.Delete(StorePath(TensorModelPackManager.ArtifactName(saved.Key, "model.gguf")));
        using var packs = new TensorModelPackManager(new([]), Path.Combine(_root, Store));
        var manager = new TensorSettingsManager(Setup, new NoSave(), StoreList([builtIn], saved), packs);
        TensorSettings settings = await manager.OpenAsync(new(SetupChangeIntent.Edit, "entry", "1"),
            new(Setup.Implementation.Id, saved.Key, TensorBackendKind.Auto));

        SetupChoicePage choices = await manager.QueryChoicesAsync(settings, "model", new());
        Assert.Equal([builtIn.Key.ToString()], choices.Choices.Select(choice => choice.Value));
        Assert.Equal(saved.Key, settings.Model);
        Assert.False(ModelField(settings).ShowValue);
        SetupInstallStatus card = InstallField(settings)!.Status;
        Assert.Equal((SetupInstallState.FilesMissing, "model.gguf"), (card.State, card.MissingFile));
        Assert.Equal(["Download again", "Delete download"], card.Actions.Select(action => action.DisplayName));

        SetupActionResult result = await manager.RunAsync(settings, "remove");

        Assert.Equal(SetupActionState.Completed, result.State);
        Assert.False(File.Exists(StorePath(TensorModelPackManager.ManifestName(saved.Key))));
        Assert.False(File.Exists(StorePath(TensorModelPackManager.DescriptionName(saved.Key))));
        Assert.Equal(SetupInstallState.NotDownloaded, InstallField(settings)!.Status.State);
    }

    /// <summary>An installed pack offers Delete download, which a loaded model blocks until it is unloaded.</summary>
    [Fact]
    public async Task InstalledPackOffersDeleteDownloadOnlyWhenUnloaded()
    {
        TensorModelPackDefinition model = Definition("test/model", "model weights");
        var list = new TestList(new TensorModelListing("/models", [new(model, SetupInstallState.Installed, null)], null));
        using var packs = new TensorModelPackManager(new([]), Path.Combine(_root, Store));
        var manager = new TensorSettingsManager(Setup, new NoSave(), list, packs);
        TensorSettings settings = await manager.OpenAsync(NewEntry());
        Select(settings, model);
        SetupActionDescriptor Delete() => Assert.Single(InstallField(settings)!.Status.Actions, action => action.Id == "remove");
        Assert.True(Delete().IsEnabled);

        settings.InstanceObservation = new(model.Key, TensorBackendKind.Cpu, TensorEngineState.Loaded, DateTimeOffset.UtcNow);

        Assert.Equal((false, "Unload the model first."), (Delete().IsEnabled, Delete().UnavailableReason));
        Assert.Equal(SetupActionState.Unavailable, (await manager.RunAsync(settings, "remove")).State);

        settings.InstanceObservation = new(null, TensorBackendKind.Cpu, TensorEngineState.Unloaded, DateTimeOffset.UtcNow);
        Assert.True(Delete().IsEnabled);
    }

    /// <summary>The backend shows by name, the actions read plainly, and every custom location field sits in one advanced section.</summary>
    [Fact]
    public async Task BackendNamesActionLabelsAndTheCustomLocationSection()
    {
        TensorModelPackDefinition model = Definition("test/model", "model weights") with
        { SupportedBackends = new HashSet<TensorBackendKind> { TensorBackendKind.GgmlCpu, TensorBackendKind.GgmlMetal } };
        var list = new TestList(new TensorModelListing("/models", [new(model, SetupInstallState.Installed, null)], null));
        using var packs = new TensorModelPackManager(new([]), Path.Combine(_root, Store));
        var manager = new TensorSettingsManager(Setup, new NoSave(), list, packs, resolver: new NoResolver());
        TensorSettings settings = await manager.OpenAsync(NewEntry());
        Select(settings, model);

        Assert.Equal("Runs on", Fields(settings).Single(field => field.Description.Id == "backend").Description.DisplayName);
        SetupChoicePage backends = await manager.QueryChoicesAsync(settings, "backend", new());
        Assert.Equal(["Automatic", "CPU", "Apple GPU"], backends.Choices.Select(choice => choice.DisplayName));
        Assert.Equal(("Check files", "Load now"),
            (settings.Actions.Single(action => action.Id == "verify").DisplayName, settings.Actions.Single(action => action.Id == "load").DisplayName));
        SetupSection elsewhere = settings.Steps[0].Sections.Single(section => section.Title == "Use a model from somewhere else");
        Assert.True(elsewhere.IsAdvanced);
        Assert.Contains(elsewhere.Fields, field => field.Description.Id == "location-kind");
        Assert.Contains(elsewhere.Fields, field => field.Description.Id == "compatibility");
        Assert.Equal(["model", "model-definition", "elsewhere"], settings.Steps[0].Sections.Select(section => section.Id));
        string contents = Assert.IsType<SetupTextField>(Fields(settings).Single(field => field.Description.Id == "pack-details")).Value;
        Assert.Contains("13 B;", contents, StringComparison.Ordinal);
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

    // The listing the host builds: the family's report of the store, then the files check.
    private TestList StoreList(IReadOnlyList<TensorModelPackDefinition> builtIns, TensorModelPackDefinition? saved) => new(async () =>
    {
        var modelStore = new OsUserPersist(_root);
        IReadOnlyList<TensorModelPackDefinition> installed =
            await TensorInstalledPacks.ReportAsync(modelStore, Store, builtIns, _ => true, formatCheck: null);
        return await TensorInstalledPacks.ListAsync(modelStore, "/models", Store, builtIns, installed, saved);
    });

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

    private sealed class NoResolver : ITensorModelResolver
    {
        public ITensorModelSource? Source => null;
        public IReadOnlyList<TensorArtifactRequirement> Requirements => [];
        public string? AdditionalArtifactRolePrefix => null;
        public Task<TensorSourceResult<TensorModelCandidate>> DiscoverAsync(TensorModelSelection selection, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Opening settings resolves nothing.");
        public Task<TensorModelResolution> ResolveCandidateAsync(TensorModelCandidate candidate, TensorBackendKind backend,
            ITensorArtifactReader reader, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Opening settings resolves nothing.");
        public Task<TensorModelEvaluation> EvaluateAsync(TensorModelCandidate candidate, TensorBackendKind backend, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Opening settings resolves nothing.");
        public Task<TensorModelResolution> ResolveAsync(TensorModelSelection selection, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Opening settings resolves nothing.");
    }

    private sealed class NoSave : ISetupCommitTarget<TensorSetupChanges>
    {
        public Task<SetupCommitResult> CommitAsync(TensorSetupChanges changes, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SetupCommitResult(SetupCommitState.Invalid));
    }
}
