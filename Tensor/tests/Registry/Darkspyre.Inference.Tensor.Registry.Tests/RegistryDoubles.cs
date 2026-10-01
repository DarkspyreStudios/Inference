using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.LLM;

namespace Darkspyre.Inference.Tensor.Registry.Tests;

/// <summary>An in-memory pack manager that records every call. Nothing reaches a file system or network.</summary>
internal sealed class RecordingPacks(params TensorModelPackDefinition[] registered) : ITensorModelPackManager
{
    public List<string> Calls { get; } = [];

    public Task<IReadOnlyList<TensorModelPackDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken = default)
    { Calls.Add("definitions"); return Task.FromResult<IReadOnlyList<TensorModelPackDefinition>>(registered); }

    public Task<TensorModelPackStatus> GetStatusAsync(TensorModelKey key, CancellationToken cancellationToken = default)
    { Calls.Add("status"); return Task.FromResult(new TensorModelPackStatus(key, TensorModelPackState.Installed)); }

    public Task<TensorModelPackDefinition> RegisterAsync(TensorModelPackDefinition definition, CancellationToken cancellationToken = default) => Unexpected<TensorModelPackDefinition>("register");
    public Task<TensorModelPackDefinition> GetRequiredDefinitionAsync(TensorModelKey key, CancellationToken cancellationToken = default) => Unexpected<TensorModelPackDefinition>("required");
    public Task InstallAsync(TensorModelKey key, IProgress<TensorModelInstallProgress>? progress = null, CancellationToken cancellationToken = default) => Unexpected<bool>("install");
    public Task<TensorModelImportResult> ImportAsync(TensorModelImportRequest request, IProgress<TensorModelInstallProgress>? progress = null, CancellationToken cancellationToken = default) => Unexpected<TensorModelImportResult>("import");
    public Task<TensorModelPackStatus> VerifyAsync(TensorModelKey key, CancellationToken cancellationToken = default) => Unexpected<TensorModelPackStatus>("verify");
    public Task<TensorModelPackLease> AcquireAsync(TensorModelKey key, CancellationToken cancellationToken = default) => Unexpected<TensorModelPackLease>("acquire");
    public Task RemoveAsync(TensorModelKey key, CancellationToken cancellationToken = default) => Unexpected<bool>("remove");

    private Task<T> Unexpected<T>(string call)
    {
        Calls.Add(call);
        throw new InvalidOperationException($"The test did not expect the pack manager to {call}.");
    }
}

internal sealed class RecordingTarget : ISetupCommitTarget<TensorSetupChanges>
{
    public TensorSetupChanges? Changes { get; private set; }

    public Task<SetupCommitResult> CommitAsync(TensorSetupChanges changes, CancellationToken cancellationToken = default)
    { Changes = changes; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "v1")); }
}

/// <summary>A test-composed TextGeneration engine registered beside the package's built-ins.</summary>
internal sealed class SyntheticLlmProvider : ITensorEngineProvider
{
    public static readonly TensorModelPackDefinition Pack = new()
    {
        Key = new("synthetic-llm", "r1"),
        Capability = TensorCapability.Llm,
        RuntimeArchitecture = "synthetic",
        License = "test",
        SupportedBackends = new HashSet<TensorBackendKind> { TensorBackendKind.Cpu },
        Artifacts = [new() { Role = "model", RelativePath = "model.bin", Length = 1, Sha256 = new('b', 64) }]
    };

    public TensorEngineDefinition Definition { get; } = new(new(
        new("test.tensor.synthetic-llm", "Synthetic LLM", InferenceImplementationKind.Tensor, InferenceTask.TextGeneration),
        "A deterministic test engine."), "Synthetic");

    public Task<IReadOnlyList<TensorModelPackDefinition>> GetOfferedModelsAsync(ITensorModelPackManager packs,
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TensorModelPackDefinition>>([Pack]);

    public ITensorModelInstance CreateInstance(ITensorModelPackManager packs, TensorBackendKind backend) =>
        new SyntheticLlm(Definition.Setup.Implementation, backend);

    public TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs,
        IReadOnlyList<TensorModelPackDefinition> offeredModels, ITensorModelInstance? instance = null,
        ITensorModelSource? source = null, ITensorArtifactReader? reader = null, ITensorBackendProbe? backendProbe = null) =>
        new(Definition.Setup, target, offeredModels, packs, instance, backendProbe);
}

/// <summary>A provider whose definition is set by the test, for collision checks.</summary>
internal sealed class StubProvider(TensorEngineDefinition? definition) : ITensorEngineProvider
{
    public TensorEngineDefinition Definition { get; } = definition!;

    public Task<IReadOnlyList<TensorModelPackDefinition>> GetOfferedModelsAsync(ITensorModelPackManager packs,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public ITensorModelInstance CreateInstance(ITensorModelPackManager packs, TensorBackendKind backend) => throw new NotSupportedException();

    public TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs,
        IReadOnlyList<TensorModelPackDefinition> offeredModels, ITensorModelInstance? instance = null,
        ITensorModelSource? source = null, ITensorArtifactReader? reader = null, ITensorBackendProbe? backendProbe = null) => throw new NotSupportedException();

    public static StubProvider Create(string id, string storeKey, InferenceTask task = InferenceTask.TextGeneration, int displayOrder = 0) =>
        new(new(new(new(id, id, InferenceImplementationKind.Tensor, task), "Stub", displayOrder), storeKey));
}

/// <summary>An LLM instance that reports each accepted lifecycle request synchronously.</summary>
internal sealed class SyntheticLlm(InferenceImplementationDescriptor descriptor, TensorBackendKind backend) : ITensorLlmInference
{
    private TensorModelObservation _observation = new(null, null, TensorEngineState.Unloaded, DateTimeOffset.UnixEpoch);

    public event EventHandler<TensorEngineStatus>? EngineStatusChanged;

    public InferenceImplementationDescriptor Descriptor => descriptor;
    public TensorBackendKind RequestedBackend => backend;
    public bool Disposed { get; private set; }

    public Task<TensorModelObservation> GetObservationAsync(CancellationToken cancellationToken = default) => Task.FromResult(_observation);
    public Task StartAsync(CancellationToken cancellationToken = default) => Report(null, TensorEngineState.Started);
    public Task LoadAsync(TensorModelKey model, CancellationToken cancellationToken = default) => Report(model, TensorEngineState.Loaded);
    public Task UnloadAsync(CancellationToken cancellationToken = default) => Report(null, TensorEngineState.Unloaded);
    public Task StopAsync(CancellationToken cancellationToken = default) => Report(null, TensorEngineState.Stopped);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Disposed = true;
    }

    public Task<LlmCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<LlmModelInfo>> RefreshModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmInferenceRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    private Task Report(TensorModelKey? model, TensorEngineState state)
    {
        _observation = new(model, model is null ? null : backend, state, DateTimeOffset.UnixEpoch);
        EngineStatusChanged?.Invoke(this, new(_observation.Model, _observation.Backend, state, _observation.ObservedAt));
        return Task.CompletedTask;
    }
}

/// <summary>A lifecycle-only instance that implements no capability interface.</summary>
internal sealed class BareInstance : ITensorModelInstance
{
    public event EventHandler<TensorEngineStatus>? EngineStatusChanged { add { } remove { } }
    public TensorBackendKind RequestedBackend => TensorBackendKind.Cpu;
    public Task<TensorModelObservation> GetObservationAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task LoadAsync(TensorModelKey model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task UnloadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
