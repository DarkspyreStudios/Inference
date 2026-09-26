using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorSetupTests
{
    private static readonly TensorModelPackDefinition Pack = new()
    {
        Key = new("test-model", "revision"),
        Capability = TensorCapability.Image,
        RuntimeArchitecture = "fixture",
        License = "test",
        SupportedBackends = new HashSet<TensorBackendKind> { TensorBackendKind.Cpu },
        Artifacts = [new() { Role = "model", RelativePath = "model.bin", Length = 1, Sha256 = new('a', 64) }]
    };
    private static readonly InferenceSetupDefinition Definition = new(
        new("tensor-test", "Test", InferenceImplementationKind.Tensor, InferenceTask.ImageGeneration), "Test");

    [Fact]
    public async Task SavingASelectionDoesNotInstallOrLoad()
    {
        var manager = new Packs();
        var target = new Target();
        await using var session = new TensorSetupSession(Definition, new(SetupChangeIntent.Create, "id", null), target, [Pack], manager);
        SetupChoiceField model = Assert.IsType<SetupChoiceField>(session.Snapshot.Steps[0].Sections[0].Fields[0]);
        Assert.Equal(SetupEditState.Applied, model.SetValue(Pack.Key.ToString()).State);
        Assert.Equal(SetupCommitState.Committed, (await session.CommitAsync()).State);
        Assert.Equal(Pack.Key, target.Changes!.Configuration.Model);
        Assert.Equal(0, manager.Calls);
    }

    [Fact]
    public async Task InstalledDoesNotMeanLoadedOrReadyWithoutALiveInstance()
    {
        var manager = new Packs();
        await using TensorSetupSession session = Edit(manager);
        Assert.Equal(SetupActionState.Completed, (await Action(session, SetupActionPurpose.CheckReadiness).ExecuteAsync()).State);
        Assert.True(session.Snapshot.Status.Installed.Value);
        Assert.Null(session.Snapshot.Status.Verified.Value);
        Assert.Null(session.Snapshot.Status.Loaded.Value);
        Assert.Null(session.Snapshot.Status.Ready.Value);
    }

    [Fact]
    public async Task PrecancelledUnloadStillReachesTheEngineAsUrgentTeardown()
    {
        var runtime = new Runtime { Observation = new(Pack.Key, TensorBackendKind.Cpu, false, DateTimeOffset.UtcNow) };
        await using TensorSetupSession session = Edit(new Packs(), runtime);
        SetupActionResult result = await Action(session, SetupActionPurpose.Unload).ExecuteAsync(cancellationToken: new(true));
        Assert.Equal(SetupActionState.Completed, result.State);
        Assert.True(runtime.Urgent);
        Assert.False(session.Snapshot.Status.Loaded.Value);
    }

    [Fact]
    public async Task HostModelChangesAreReadFromTheRuntimeNotASecondSetupCache()
    {
        var runtime = new Runtime();
        await using TensorSetupSession session = Edit(new Packs(), runtime);
        Assert.False(session.Snapshot.Status.Ready.Value);
        await runtime.LoadAsync(Pack.Key);
        Assert.True(session.Snapshot.Status.Ready.Value);
        await runtime.UnloadAsync();
        Assert.False(session.Snapshot.Status.Ready.Value);
        Assert.Equal(0, runtime.StopCalls);
        await session.DisposeAsync();
        Assert.Equal(0, runtime.StopCalls); // The session does not own the host's engine.
    }

    [Fact]
    public async Task ExplicitStopUpdatesReadinessAndDisablesFurtherLifecycleActions()
    {
        var runtime = new Runtime();
        await using TensorSetupSession session = Edit(new Packs(), runtime);
        Assert.Equal(SetupActionState.Completed, (await Action(session, SetupActionPurpose.Stop).ExecuteAsync()).State);
        Assert.Equal(1, runtime.StopCalls);
        Assert.False(Action(session, SetupActionPurpose.Load).Descriptor.IsEnabled);
        Assert.False(session.Snapshot.Status.Ready.Value);
    }

    private static TensorSetupSession Edit(Packs packs, Runtime? runtime = null) =>
        new(Definition, new(SetupChangeIntent.Edit, "id", "v1"), new Target(), [Pack], packs,
            new(Definition.Implementation.Id, Pack.Key, TensorBackendKind.Cpu), runtime);

    [Fact]
    public async Task DirtyTracksBackendRevertsAndNotReadinessChecks()
    {
        await using TensorSetupSession session = Edit(new Packs());
        Assert.False(session.Snapshot.IsDirty);
        await Action(session, SetupActionPurpose.CheckReadiness).ExecuteAsync();
        Assert.False(session.Snapshot.IsDirty);
        SetupChoiceField Backend() => Assert.Single(session.Snapshot.Steps[0].Sections[0].Fields.OfType<SetupChoiceField>(), field => field.Description.Id == "backend");
        Assert.Equal(SetupChoiceCapabilities.Bounded, Backend().Capabilities);
        Backend().SetValue(nameof(TensorBackendKind.Auto));
        Assert.True(session.Snapshot.IsDirty);
        Backend().SetValue(nameof(TensorBackendKind.Cpu));
        Assert.False(session.Snapshot.IsDirty);
    }
    private static ISetupAction Action(TensorSetupSession session, SetupActionPurpose purpose) =>
        Assert.Single(session.Snapshot.Actions, action => action.Descriptor.Purpose == purpose);

    private sealed class Target : ISetupCommitTarget<TensorSetupChanges>
    {
        public TensorSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(TensorSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "v2")); }
    }

    private sealed class Packs : ITensorModelPackManager
    {
        public Task<TensorModelImportResult> ImportAsync(TensorModelImportRequest request, IProgress<TensorModelInstallProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TensorModelPackDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TensorModelPackDefinition>>([Pack]);
        public Task<TensorModelPackDefinition> RegisterAsync(TensorModelPackDefinition definition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public int Calls { get; private set; }
        public Task<TensorModelPackDefinition> GetRequiredDefinitionAsync(TensorModelKey key, CancellationToken cancellationToken = default) => Task.FromResult(Pack);
        public Task<TensorModelPackStatus> GetStatusAsync(TensorModelKey key, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new TensorModelPackStatus(key, TensorModelPackState.Installed)); }
        public Task InstallAsync(TensorModelKey key, IProgress<TensorModelInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        { Calls++; return Task.CompletedTask; }
        public Task<TensorModelPackStatus> VerifyAsync(TensorModelKey key, CancellationToken cancellationToken = default) => GetStatusAsync(key, cancellationToken);
        public Task<TensorModelPackLease> AcquireAsync(TensorModelKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(TensorModelKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Runtime : ITensorModelInstance
    {
        public TensorModelObservation Observation { get; set; } = new(null, null, false, DateTimeOffset.UtcNow);
        public TensorBackendKind RequestedBackend => TensorBackendKind.Cpu;
        public bool Urgent { get; private set; }
        public int StopCalls { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(TensorModelKey model, CancellationToken cancellationToken = default)
        { Observation = new(model, RequestedBackend, false, DateTimeOffset.UtcNow); return Task.CompletedTask; }
        public Task UnloadAsync(CancellationToken cancellationToken = default)
        { Urgent = cancellationToken.IsCancellationRequested; Observation = new(null, null, false, DateTimeOffset.UtcNow); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default)
        { StopCalls++; Observation = new(null, null, true, DateTimeOffset.UtcNow); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
