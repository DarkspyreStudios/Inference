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
        await using var session = Session(resolver, packs, target);
        Assert.Equal(0, resolver.SourceImpl.Calls);
        SetupChoiceField repository = Choice(session, "repository");
        Assert.Equal(SetupSideEffect.Network, repository.ChoiceSideEffects);
        Assert.True(repository.AllowsManualEntry);
        Assert.Equal("owner/model", (await repository.GetChoicesAsync(new(session.Snapshot.Revision))).Choices.Single().Value);
        Select(session, "repository", "owner/model");
        Assert.Equal("commit", (await Choice(session, "source-revision").GetChoicesAsync(new(session.Snapshot.Revision))).Choices.Single().Value);
        Select(session, "source-revision", "commit");
        Assert.Equal("model.bin", (await Choice(session, "variant").GetChoicesAsync(new(session.Snapshot.Revision))).Choices.Single().Value);
        Select(session, "variant", "model.bin");
        Assert.False((await session.ValidateAsync()).IsValid);
        Assert.Equal(SetupActionState.Completed, (await Action(session, "resolve").ExecuteAsync()).State);
        Assert.Equal(new TensorModelLocation.RepositoryArtifact("test", new("owner/model", "commit", "model.bin")), resolver.Selection!.Location);
        Assert.True((await session.ValidateAsync()).IsValid);
        Assert.Contains("17 bytes", Text(session, "pack-details").Value, StringComparison.Ordinal);
        Assert.Equal(SetupActionState.Completed, (await Action(session, "status").ExecuteAsync()).State);
        Assert.False(session.Snapshot.Status.Installed.Value);
        Assert.Equal(SetupCommitState.Committed, (await session.CommitAsync()).State);
        Assert.Equal(Pack.Key, target.Changes!.Definition.Key);
        Assert.Equal(Pack.Key, target.Changes.Configuration.Model);
        Assert.Equal(0, packs.Calls);
    }

    [Fact]
    public async Task ExplicitInstallRegistersResolvedDefinitionBeforeInstallingAndVerifying()
    {
        var resolver = new Resolver();
        var packs = new Packs();
        await using var session = Session(resolver, packs, new Target());
        await Resolve(session);
        Assert.Equal(SetupActionState.Completed, (await Action(session, "install").ExecuteAsync()).State);
        Assert.Equal(["register", "install", "verify"], packs.Operations);
        Assert.True(session.Snapshot.Status.Installed.Value);
        Assert.True(session.Snapshot.Status.Verified.Value);
        Assert.Null(session.Snapshot.Status.Ready.Value);
    }

    [Theory]
    [InlineData(TensorCompatibilityState.Unsupported)]
    [InlineData(TensorCompatibilityState.Unverified)]
    public async Task UnresolvedCandidatesCannotBeSavedOrInstalled(TensorCompatibilityState state)
    {
        var resolver = new Resolver { State = state };
        var packs = new Packs();
        await using var session = Session(resolver, packs, new Target());
        SelectCandidate(session);
        Assert.Equal(SetupActionState.Failed, (await Action(session, "resolve").ExecuteAsync()).State);
        Assert.Equal(SetupCommitState.Invalid, (await session.CommitAsync()).State);
        Assert.False(Action(session, "install").Descriptor.IsEnabled);
        Assert.Contains(state.ToString(), Text(session, "compatibility").Value, StringComparison.Ordinal);
        Assert.Equal(0, packs.Calls);
    }

    [Fact]
    public async Task BackendAndSourceEditsInvalidateCompatibilityAndDownstreamSelections()
    {
        var resolver = new Resolver();
        await using var session = Session(resolver, new Packs(), new Target());
        await Resolve(session);
        Select(session, "backend", "Cuda");
        Assert.False((await session.ValidateAsync()).IsValid);
        Assert.Equal("Not evaluated.", Text(session, "compatibility").Value);
        Assert.Equal(SetupActionState.Completed, (await Action(session, "resolve").ExecuteAsync()).State);
        Assert.Equal(TensorBackendKind.Cuda, resolver.Selection!.Backend);
        Select(session, "repository", "owner/another");
        Assert.Equal(string.Empty, Choice(session, "source-revision").Value);
        Assert.Equal(string.Empty, Choice(session, "variant").Value);
        Assert.False(Action(session, "resolve").Descriptor.IsEnabled);
        Assert.False(Action(session, "install").Descriptor.IsEnabled);
    }

    [Fact]
    public async Task QueryCompletionAfterEditIsStaleAndDoesNotSelectAnything()
    {
        var response = new TaskCompletionSource<TensorSourceResult<TensorSourcePage<TensorSourceModel>>>();
        var resolver = new Resolver();
        resolver.SourceImpl.Search = (_, _) => response.Task;
        await using var session = Session(resolver, new Packs(), new Target());
        Task<SetupChoicePage> pending = Choice(session, "repository").GetChoicesAsync(new(session.Snapshot.Revision));
        Select(session, "repository", "owner/changed");
        response.SetResult(new(TensorSourceState.Available, new([new("owner/old", "commit", "test", false, false)])));
        Assert.Equal(SetupChoiceState.Stale, (await pending).State);
        Assert.Equal("owner/changed", Choice(session, "repository").Value);
        Assert.Equal(string.Empty, Choice(session, "model").Value);
    }

    [Fact]
    public async Task GatedSourceFailureIsProjectedWithoutPretendingTheListIsEmptySuccess()
    {
        var resolver = new Resolver();
        resolver.SourceImpl.Search = (_, _) => Task.FromResult(new TensorSourceResult<TensorSourcePage<TensorSourceModel>>(
            TensorSourceState.Gated, Issues: [new("source-gated", "Accept the license with the model host.")]));
        await using var session = Session(resolver, new Packs(), new Target());
        SetupChoicePage page = await Choice(session, "repository").GetChoicesAsync(new(session.Snapshot.Revision));
        Assert.Equal(SetupChoiceState.Failed, page.State);
        Assert.Equal("source-gated", Assert.Single(page.Issues!).Code);
    }

    [Fact]
    public async Task CancelledResolutionDoesNotPublishOrPermitInstall()
    {
        var resolver = new Resolver();
        using var cancellation = new CancellationTokenSource();
        resolver.BeforeResolve = token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); };
        await using var session = Session(resolver, new Packs(), new Target());
        SelectCandidate(session);
        Assert.Equal(SetupActionState.Cancelled, (await Action(session, "resolve").ExecuteAsync(cancellationToken: cancellation.Token)).State);
        Assert.False(Action(session, "install").Descriptor.IsEnabled);
    }

    [Fact]
    public async Task BackendProbeIsExplicitReadOnlyAndInvalidatedByAnEdit()
    {
        int nativeCalls = 0;
        ITensorBackendProbe probe = TensorBackendProbes.CpuCuda(() => { nativeCalls++; return false; });
        await using var session = Session(new Resolver(), new Packs(), new Target(), probe);
        Select(session, "model", Pack.Key.ToString());
        Select(session, "backend", "Cuda");
        Assert.Equal(0, nativeCalls);
        Assert.Equal(SetupActionState.Failed, (await Action(session, "backend-check").ExecuteAsync()).State);
        Assert.Equal(1, nativeCalls);
        Assert.Contains("unavailable", Text(session, "backend-availability").Value, StringComparison.Ordinal);
        Assert.Equal(SetupEditState.Invalid, Text(session, "backend-availability").SetValue("available").State);
        Select(session, "backend", "Cpu");
        Assert.Contains("Unknown", Text(session, "backend-availability").Value, StringComparison.Ordinal);
        Assert.Equal(SetupActionState.Completed, (await Action(session, "backend-check").ExecuteAsync()).State);
        Assert.Equal(1, nativeCalls);
    }

    private static TensorSetupSession Session(Resolver resolver, Packs packs, Target target, ITensorBackendProbe? probe = null) =>
        new(new(new("test", "Test", InferenceImplementationKind.Tensor, InferenceTask.ImageGeneration), "Test"),
            new(SetupChangeIntent.Create, "id", null), target, [Pack], packs, backendProbe: probe, resolver: resolver);

    private static SetupChoiceField Choice(TensorSetupSession session, string id) => Assert.IsType<SetupChoiceField>(Field(session, id));
    private static SetupTextField Text(TensorSetupSession session, string id) => Assert.IsType<SetupTextField>(Field(session, id));
    private static SetupField Field(TensorSetupSession session, string id) => session.Snapshot.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields).Single(field => field.Description.Id == id);
    private static void Select(TensorSetupSession session, string id, string value) => Assert.Equal(SetupEditState.Applied, Choice(session, id).SetValue(value).State);
    private static ISetupAction Action(TensorSetupSession session, string id) => session.Snapshot.Actions.Single(action => action.Descriptor.Id == id);
    private static void SelectCandidate(TensorSetupSession session)
    { Select(session, "repository", "owner/model"); Select(session, "source-revision", "commit"); Select(session, "variant", "model.bin"); }
    private static async Task Resolve(TensorSetupSession session)
    { SelectCandidate(session); Assert.Equal(SetupActionState.Completed, (await Action(session, "resolve").ExecuteAsync()).State); }

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
