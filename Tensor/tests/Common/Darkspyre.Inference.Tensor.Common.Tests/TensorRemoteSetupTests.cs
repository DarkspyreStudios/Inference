using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorRemoteSetupTests
{
    private static readonly TensorModelPackDefinition Pack = new()
    {
        Key = new("remote-model", "immutable-revision:fingerprint"),
        Capability = TensorCapability.Image,
        RuntimeArchitecture = "fixture",
        License = "test",
        SupportedBackends = new HashSet<TensorBackendKind> { TensorBackendKind.Cpu, TensorBackendKind.Cuda },
        Artifacts = [new() { Role = "model", RelativePath = "model.bin", Length = 17, Sha256 = new('a', 64) }]
    };

    [Fact]
    public async Task RemoteChoicesResolveAndSaveCarryTheManifestWithoutRegistrationDownloadOrLoad()
    {
        var resolver = new Resolver();
        var packs = new Packs();
        var target = new Target();
        var (manager, settings) = await OpenAsync(resolver, packs, target);
        Assert.Equal(0, resolver.SourceImpl.Calls);
        SetupChoiceField repository = Choice(settings, "repository");
        Assert.Equal(SetupSideEffect.Network, repository.ChoiceSideEffects);
        Assert.True(repository.AllowsManualEntry);
        Assert.Equal("owner/model", (await manager.QueryChoicesAsync(settings, "repository", new())).Choices.Single().Value);
        Select(settings, "repository", "owner/model");
        Assert.Equal("commit", (await manager.QueryChoicesAsync(settings, "source-revision", new())).Choices.Single().Value);
        Select(settings, "source-revision", "commit");
        Assert.Equal("model.bin", (await manager.QueryChoicesAsync(settings, "variant", new())).Choices.Single().Value);
        Select(settings, "variant", "model.bin");
        Assert.False(settings.Validate().IsValid);
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, "resolve")).State);
        Assert.Equal(new TensorModelLocation.RepositoryArtifact("test", new("owner/model", "commit", "model.bin")), resolver.Selection!.Location);
        Assert.True(settings.Validate().IsValid);
        Assert.Contains("17 bytes", Text(settings, "pack-details").Value, StringComparison.Ordinal);
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, "status")).State);
        Assert.False(settings.Status.Installed.Value);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Equal(Pack.Key, target.Changes!.Definition.Key);
        Assert.Equal(Pack.Key, target.Changes.Configuration.Model);
        Assert.Equal(0, packs.Calls);
    }

    [Fact]
    public async Task ExplicitInstallRegistersResolvedDefinitionBeforeInstallingAndVerifying()
    {
        var resolver = new Resolver();
        var packs = new Packs();
        var (manager, settings) = await OpenAsync(resolver, packs, new Target());
        await Resolve(manager, settings);
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, "install")).State);
        Assert.Equal(["register", "install", "verify"], packs.Operations);
        Assert.True(settings.Status.Installed.Value);
        Assert.True(settings.Status.Verified.Value);
        Assert.Null(settings.Status.Ready.Value);
    }

    [Theory]
    [InlineData(TensorCompatibilityState.Unsupported)]
    [InlineData(TensorCompatibilityState.Unverified)]
    public async Task UnresolvedCandidatesCannotBeSavedOrInstalled(TensorCompatibilityState state)
    {
        var resolver = new Resolver { State = state };
        var packs = new Packs();
        var (manager, settings) = await OpenAsync(resolver, packs, new Target());
        SelectCandidate(settings);
        Assert.Equal(SetupActionState.Failed, (await manager.RunAsync(settings, "resolve")).State);
        Assert.Equal(SetupCommitState.Invalid, (await manager.SaveAsync(settings)).State);
        Assert.False(Action(settings, "install").IsEnabled);
        Assert.Contains(state.ToString(), Text(settings, "compatibility").Value, StringComparison.Ordinal);
        Assert.Equal(0, packs.Calls);
    }

    [Fact]
    public async Task BackendAndSourceEditsInvalidateCompatibilityAndDownstreamSelections()
    {
        var resolver = new Resolver();
        var (manager, settings) = await OpenAsync(resolver, new Packs(), new Target());
        await Resolve(manager, settings);
        Select(settings, "backend", "Cuda");
        Assert.False(settings.Validate().IsValid);
        Assert.Equal("Not evaluated.", Text(settings, "compatibility").Value);
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, "resolve")).State);
        Assert.Equal(TensorBackendKind.Cuda, resolver.Selection!.Backend);
        Select(settings, "repository", "owner/another");
        Assert.Equal(string.Empty, Choice(settings, "source-revision").Value);
        Assert.Equal(string.Empty, Choice(settings, "variant").Value);
        Assert.False(Action(settings, "resolve").IsEnabled);
        Assert.False(Action(settings, "install").IsEnabled);
    }

    [Fact]
    public async Task QueryCompletionAfterEditDoesNotSelectAnything()
    {
        var response = new TaskCompletionSource<TensorSourceResult<TensorSourcePage<TensorSourceModel>>>();
        var resolver = new Resolver();
        resolver.SourceImpl.Search = (_, _) => response.Task;
        var (manager, settings) = await OpenAsync(resolver, new Packs(), new Target());
        Task<SetupChoicePage> pending = manager.QueryChoicesAsync(settings, "repository", new());
        Select(settings, "repository", "owner/changed");
        response.SetResult(new(TensorSourceState.Available, new([new("owner/old", "commit", "test", false, false)])));
        Assert.Equal("owner/old", Assert.Single((await pending).Choices).Value);
        Assert.Equal("owner/changed", Choice(settings, "repository").Value);
        Assert.Equal(string.Empty, Choice(settings, "model").Value);
    }

    [Fact]
    public async Task GatedSourceFailureIsProjectedWithoutPretendingTheListIsEmptySuccess()
    {
        var resolver = new Resolver();
        resolver.SourceImpl.Search = (_, _) => Task.FromResult(new TensorSourceResult<TensorSourcePage<TensorSourceModel>>(
            TensorSourceState.Gated, Issues: [new("source-gated", "Accept the license with the model host.")]));
        var (manager, settings) = await OpenAsync(resolver, new Packs(), new Target());
        SetupChoicePage page = await manager.QueryChoicesAsync(settings, "repository", new());
        Assert.Equal(SetupChoiceState.Failed, page.State);
        Assert.Equal("source-gated", Assert.Single(page.Issues!).Code);
    }

    [Fact]
    public async Task CancelledResolutionDoesNotPublishOrPermitInstall()
    {
        var resolver = new Resolver();
        using var cancellation = new CancellationTokenSource();
        resolver.BeforeResolve = token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); };
        var (manager, settings) = await OpenAsync(resolver, new Packs(), new Target());
        SelectCandidate(settings);
        Assert.Equal(SetupActionState.Cancelled, (await manager.RunAsync(settings, "resolve", cancellationToken: cancellation.Token)).State);
        Assert.False(Action(settings, "install").IsEnabled);
    }

    [Fact]
    public async Task BackendProbeIsExplicitReadOnlyAndInvalidatedByAnEdit()
    {
        int nativeCalls = 0;
        ITensorBackendProbe probe = TensorBackendProbes.CpuCuda(() => { nativeCalls++; return false; });
        var (manager, settings) = await OpenAsync(new Resolver(), new Packs(), new Target(), probe);
        Select(settings, "model", Pack.Key.ToString());
        Select(settings, "backend", "Cuda");
        Assert.Equal(0, nativeCalls);
        Assert.Equal(SetupActionState.Failed, (await manager.RunAsync(settings, "backend-check")).State);
        Assert.Equal(1, nativeCalls);
        Assert.Contains("unavailable", Text(settings, "backend-availability").Value, StringComparison.Ordinal);
        Assert.Equal(SetupEditState.Invalid, Text(settings, "backend-availability").SetValue("available").State);
        Select(settings, "backend", "Cpu");
        Assert.Contains("Unknown", Text(settings, "backend-availability").Value, StringComparison.Ordinal);
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, "backend-check")).State);
        Assert.Equal(1, nativeCalls);
    }

    private static async Task<(TensorSettingsManager Manager, TensorSettings Settings)> OpenAsync(Resolver resolver, Packs packs, Target target,
        ITensorBackendProbe? probe = null)
    {
        var manager = new TensorSettingsManager(new(new("test", "Test", InferenceImplementationKind.Tensor, InferenceTask.ImageGeneration), "Test"),
            target, [Pack], packs, backendProbe: probe, resolver: resolver);
        return (manager, await manager.OpenAsync(new(SetupChangeIntent.Create, "id", null)));
    }

    private static SetupChoiceField Choice(TensorSettings settings, string id) => Assert.IsType<SetupChoiceField>(Field(settings, id));
    private static SetupTextField Text(TensorSettings settings, string id) => Assert.IsType<SetupTextField>(Field(settings, id));
    private static SetupField Field(TensorSettings settings, string id) => settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields).Single(field => field.Description.Id == id);
    private static void Select(TensorSettings settings, string id, string value) => Assert.Equal(SetupEditState.Applied, Choice(settings, id).SetValue(value).State);
    private static SetupActionDescriptor Action(TensorSettings settings, string id) => settings.Actions.Single(action => action.Id == id);
    private static void SelectCandidate(TensorSettings settings)
    { Select(settings, "repository", "owner/model"); Select(settings, "source-revision", "commit"); Select(settings, "variant", "model.bin"); }
    private static async Task Resolve(TensorSettingsManager manager, TensorSettings settings)
    { SelectCandidate(settings); Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, "resolve")).State); }

    private sealed class Target : ISetupCommitTarget<TensorSetupChanges>
    {
        public TensorSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(TensorSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "v1")); }
    }

    private sealed class Packs : ITensorModelPackManager
    {
        public Task<TensorModelImportResult> ImportAsync(TensorModelImportRequest request, IProgress<TensorModelInstallProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        private readonly TensorModelCatalog _catalog = new([]);
        public List<string> Operations { get; } = [];
        public int Calls => Operations.Count;
        public Task<IReadOnlyList<TensorModelPackDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TensorModelPackDefinition>>(_catalog.Definitions);
        public Task<TensorModelPackDefinition> RegisterAsync(TensorModelPackDefinition definition, CancellationToken cancellationToken = default) { Operations.Add("register"); return Task.FromResult(_catalog.Register(definition)); }
        public Task<TensorModelPackDefinition> GetRequiredDefinitionAsync(TensorModelKey key, CancellationToken cancellationToken = default) => Task.FromResult(_catalog.GetRequired(key));
        public Task<TensorModelPackStatus> GetStatusAsync(TensorModelKey key, CancellationToken cancellationToken = default)
        { Operations.Add("status"); return Task.FromResult(new TensorModelPackStatus(key, TensorModelPackState.Installed)); }
        public Task InstallAsync(TensorModelKey key, IProgress<TensorModelInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        { Assert.Equal(key, _catalog.GetRequired(key).Key); Operations.Add("install"); return Task.CompletedTask; }
        public Task<TensorModelPackStatus> VerifyAsync(TensorModelKey key, CancellationToken cancellationToken = default)
        { Operations.Add("verify"); return Task.FromResult(new TensorModelPackStatus(key, TensorModelPackState.Installed)); }
        public Task<TensorModelPackLease> AcquireAsync(TensorModelKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(TensorModelKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Resolver : ITensorModelResolver
    {
        public IReadOnlyList<TensorArtifactRequirement> Requirements => [new("model", "*.bin")];
        public string? AdditionalArtifactRolePrefix => null;
        public Task<TensorSourceResult<TensorModelCandidate>> DiscoverAsync(TensorModelSelection selection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TensorModelResolution> ResolveCandidateAsync(TensorModelCandidate candidate, TensorBackendKind backend, ITensorArtifactReader reader, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Source SourceImpl { get; } = new();
        public ITensorModelSource Source => SourceImpl;
        public TensorCompatibilityState State { get; init; } = TensorCompatibilityState.Supported;
        public TensorModelSelection? Selection { get; private set; }
        public Action<CancellationToken>? BeforeResolve { get; set; }
        public Task<TensorModelEvaluation> EvaluateAsync(TensorModelCandidate candidate, TensorBackendKind backend, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TensorModelResolution> ResolveAsync(TensorModelSelection selection, CancellationToken cancellationToken = default)
        { BeforeResolve?.Invoke(cancellationToken); Selection = selection; return Task.FromResult(new TensorModelResolution(new(State, [], []), Pack)); }
    }

    private sealed class Source : ITensorModelSource
    {
        public string Id => "test";
        public int Calls { get; private set; }
        public Func<TensorSourceQuery, CancellationToken, Task<TensorSourceResult<TensorSourcePage<TensorSourceModel>>>>? Search { get; set; }
        public Task<TensorSourceResult<TensorSourcePage<TensorSourceModel>>> SearchAsync(TensorSourceQuery query, CancellationToken cancellationToken = default)
        { Calls++; return Search?.Invoke(query, cancellationToken) ?? Task.FromResult(new TensorSourceResult<TensorSourcePage<TensorSourceModel>>(TensorSourceState.Available, new([new("owner/model", "commit", "test", false, false)]))); }
        public Task<TensorSourceResult<TensorSourcePage<TensorSourceRevision>>> ListRevisionsAsync(TensorRepositoryQuery query, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new TensorSourceResult<TensorSourcePage<TensorSourceRevision>>(TensorSourceState.Available, new([new("commit", null)]))); }
        public Task<TensorSourceResult<TensorSourcePage<TensorSourceArtifact>>> ListVariantsAsync(TensorVariantQuery query, CancellationToken cancellationToken = default)
        {
            Calls++; return Task.FromResult(new TensorSourceResult<TensorSourcePage<TensorSourceArtifact>>(TensorSourceState.Available,
                new([new("model.bin", 17, new('a', 64), new(new("https://example.test"), query.Repository, query.Revision, "model.bin", "test"), new("https://example.test/model.bin"))])));
        }
        public Task<TensorSourceResult<TensorModelCandidate>> GetCandidateAsync(TensorCandidateSelection selection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TensorSourceResult<TensorMetadataContent>> ReadMetadataAsync(TensorSourceArtifact artifact, TensorMetadataRead read, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
