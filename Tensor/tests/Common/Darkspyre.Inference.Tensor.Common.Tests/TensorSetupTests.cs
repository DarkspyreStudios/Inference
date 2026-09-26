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
        var packs = new Packs();
        var target = new Target();
        var manager = new TensorSettingsManager(Definition, target, [Pack], packs);
        TensorSettings settings = await manager.OpenAsync(new(SetupChangeIntent.Create, "id", null));
        SetupChoiceField model = Assert.IsType<SetupChoiceField>(settings.Steps[0].Sections[0].Fields[0]);
        Assert.Equal(SetupEditState.Applied, model.SetValue(Pack.Key.ToString()).State);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Equal(Pack.Key, target.Changes!.Configuration.Model);
        Assert.Equal(0, packs.Calls);
    }

    [Fact]
    public async Task InstalledDoesNotMeanLoadedOrReadyWithoutALiveInstance()
    {
        var (manager, settings) = await EditAsync(new Packs());
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, Action(settings, SetupActionPurpose.CheckReadiness).Id)).State);
        Assert.True(settings.Status.Installed.Value);
        Assert.Null(settings.Status.Verified.Value);
        Assert.Null(settings.Status.Loaded.Value);
        Assert.Null(settings.Status.Ready.Value);
    }

    [Fact]
    public async Task PrecancelledUnloadStillReachesTheEngineAsUrgentTeardown()
    {
        var runtime = new Runtime { Observation = new(Pack.Key, TensorBackendKind.Cpu, TensorEngineState.Loaded, DateTimeOffset.UtcNow) };
        var (manager, settings) = await EditAsync(new Packs(), runtime);
        SetupActionResult result = await manager.RunAsync(settings, Action(settings, SetupActionPurpose.Unload).Id, cancellationToken: new(true));
        Assert.Equal(SetupActionState.Completed, result.State);
        Assert.True(runtime.Urgent);
        Assert.False(settings.Status.Loaded.Value);
    }

    [Fact]
    public async Task HostModelChangesReachTheSettingsThroughStatusChanged()
    {
        var runtime = new Runtime();
        var (manager, settings) = await EditAsync(new Packs(), runtime);
        Assert.False(settings.Status.Ready.Value);
        int changes = 0;
        void OnChanged(object? sender, EventArgs args) => changes++;
        manager.StatusChanged += OnChanged;
        Assert.Equal(1, runtime.Listeners);
        await runtime.LoadAsync(Pack.Key);
        Assert.Equal(1, changes);
        await manager.RefreshStatusAsync(settings);
        Assert.True(settings.Status.Ready.Value);
        await runtime.UnloadAsync();
        await manager.RefreshStatusAsync(settings);
        Assert.False(settings.Status.Ready.Value);
        manager.StatusChanged -= OnChanged;
        Assert.Equal(0, runtime.Listeners);
        Assert.Equal(0, runtime.StopCalls); // The manager does not own the host's engine.
    }

    [Fact]
    public async Task ExplicitStopUpdatesReadinessAndDisablesFurtherLifecycleActions()
    {
        var runtime = new Runtime();
        var (manager, settings) = await EditAsync(new Packs(), runtime);
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, Action(settings, SetupActionPurpose.Stop).Id)).State);
        Assert.Equal(1, runtime.StopCalls);
        Assert.False(Action(settings, SetupActionPurpose.Load).IsEnabled);
        Assert.False(settings.Status.Ready.Value);
    }

    private static async Task<(TensorSettingsManager Manager, TensorSettings Settings)> EditAsync(Packs packs, Runtime? runtime = null)
    {
        var manager = new TensorSettingsManager(Definition, new Target(), [Pack], packs, runtime);
        return (manager, await manager.OpenAsync(new(SetupChangeIntent.Edit, "id", "v1"),
            new(Definition.Implementation.Id, Pack.Key, TensorBackendKind.Cpu)));
    }

    [Fact]
    public async Task DirtyTracksBackendRevertsAndNotReadinessChecks()
    {
        var (manager, settings) = await EditAsync(new Packs());
        Assert.False(settings.HasUnsavedChanges);
        await manager.RunAsync(settings, Action(settings, SetupActionPurpose.CheckReadiness).Id);
        Assert.False(settings.HasUnsavedChanges);
        SetupChoiceField Backend() => Assert.Single(settings.Steps[0].Sections[0].Fields.OfType<SetupChoiceField>(), field => field.Description.Id == "backend");
        Assert.Equal(SetupChoiceCapabilities.Bounded, Backend().Capabilities);
        Backend().SetValue(nameof(TensorBackendKind.Auto));
        Assert.True(settings.HasUnsavedChanges);
        Backend().SetValue(nameof(TensorBackendKind.Cpu));
        Assert.False(settings.HasUnsavedChanges);
    }
    private static SetupActionDescriptor Action(TensorSettings settings, SetupActionPurpose purpose) =>
        Assert.Single(settings.Actions, action => action.Purpose == purpose);

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
        private EventHandler<TensorEngineStatus>? _engineStatusChanged;
        public event EventHandler<TensorEngineStatus>? EngineStatusChanged { add => _engineStatusChanged += value; remove => _engineStatusChanged -= value; }
        public int Listeners => _engineStatusChanged?.GetInvocationList().Length ?? 0;
        public TensorModelObservation Observation { get; set; } = new(null, null, TensorEngineState.Unloaded, DateTimeOffset.UtcNow);
        public TensorBackendKind RequestedBackend => TensorBackendKind.Cpu;
        public bool Urgent { get; private set; }
        public int StopCalls { get; private set; }
        public Task<TensorModelObservation> GetObservationAsync(CancellationToken cancellationToken = default) => Task.FromResult(Observation);
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(TensorModelKey model, CancellationToken cancellationToken = default)
        { Report(new(model, RequestedBackend, TensorEngineState.Loaded, DateTimeOffset.UtcNow)); return Task.CompletedTask; }
        public Task UnloadAsync(CancellationToken cancellationToken = default)
        { Urgent = cancellationToken.IsCancellationRequested; Report(new(null, null, TensorEngineState.Unloaded, DateTimeOffset.UtcNow)); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default)
        { StopCalls++; Report(new(null, null, TensorEngineState.Stopped, DateTimeOffset.UtcNow)); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void Report(TensorModelObservation observation)
        {
            Observation = observation;
            _engineStatusChanged?.Invoke(this, new(observation.Model, observation.Backend, observation.State, observation.ObservedAt));
        }
    }
}
