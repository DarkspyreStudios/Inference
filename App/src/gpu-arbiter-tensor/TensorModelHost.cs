using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Registry;
using GpuArbiter.Backends;
using GpuArbiter.Gpu;

namespace GpuArbiter.Tensor;

public sealed record HostedModel(string Alias, TensorModelPackDefinition Pack, ITensorModelInstance Instance);

public interface ITensorModelHost : IModelHost
{
    IReadOnlyList<HostedModel> Models { get; }
    HostedModel Get(string alias);
}

public sealed class TensorModelHost(TensorOptions options, TensorEngineRegistry registry, IServiceProvider services)
    : IHostedService, ITensorModelHost, IAsyncDisposable
{
    internal static JsonSerializerOptions ManifestJson { get; } = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    private readonly List<HostedModel> _models = [];
    private readonly Dictionary<string, TensorModelPackManager> _packs = new(StringComparer.Ordinal);
    public IReadOnlyList<HostedModel> Models => _models.AsReadOnly();

    public HostedModel Get(string alias) => _models.FirstOrDefault(model => model.Alias == alias)
        ?? throw new ApiException(404, "Unknown model", "model");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var configuration in options.Models)
            {
                if (!registry.TryGetProvider(configuration.ImplementationId, out var provider))
                    throw new InvalidOperationException("Configured Tensor engine is not registered.");
                await using var stream = File.OpenRead(configuration.Manifest);
                var definition = await JsonSerializer.DeserializeAsync<TensorModelPackDefinition>(stream, ManifestJson, cancellationToken)
                    ?? throw new InvalidOperationException("Empty model manifest.");
                if (configuration.Backend != TensorBackendKind.Auto && !definition.SupportedBackends.Contains(configuration.Backend))
                    throw new InvalidOperationException("Configured backend is not supported by the pack.");
                if (!_packs.TryGetValue(provider.Definition.ModelStoreKey, out var packs))
                {
                    packs = new TensorModelPackManager(new TensorModelCatalog([]), Path.Combine(options.Store, provider.Definition.ModelStoreKey));
                    _packs.Add(provider.Definition.ModelStoreKey, packs);
                }
                await packs.RegisterAsync(definition, cancellationToken);
                if (!(await provider.GetOfferedModelsAsync(packs, cancellationToken)).Any(pack => pack.Key == definition.Key))
                    throw new InvalidOperationException("The model does not belong to the configured engine.");
                if ((await packs.GetStatusAsync(definition.Key, cancellationToken)).State != TensorModelPackState.Installed)
                    throw new InvalidOperationException("Configured pack is not installed. Prepare it before starting the service.");
                var instance = provider.CreateInstance(packs, configuration.Backend);
                _models.Add(new(configuration.Alias, definition, instance));
                await AwaitStateAsync(instance, () => instance.StartAsync(cancellationToken), TensorEngineState.Started, cancellationToken);
            }
            services.GetRequiredService<GpuCoordinator>().ModelHostReady();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<ModelState>> ModelsAsync(CancellationToken cancellationToken)
    {
        var states = new List<ModelState>();
        foreach (var model in _models)
        {
            var observation = await model.Instance.GetObservationAsync(cancellationToken);
            var state = observation.State switch
            {
                TensorEngineState.Loaded => "loaded",
                TensorEngineState.Loading => "loading",
                TensorEngineState.Unloading or TensorEngineState.Stopping => "unloading",
                _ => "unloaded"
            };
            states.Add(new(model.Alias, state, observation.State == TensorEngineState.Failed, null));
        }
        return states;
    }

    public async Task LoadAsync(string model, CancellationToken cancellationToken)
    {
        var entry = Get(model);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.ModelLoadTimeoutSeconds));
        try
        {
            await AwaitStateAsync(entry.Instance, () => entry.Instance.LoadAsync(entry.Pack.Key, timeout.Token), TensorEngineState.Loaded, timeout.Token);
        }
        catch
        {
            // A timed-out native load can still finish. Drain it before another target acquires the GPU.
            await AwaitStateAsync(entry.Instance, () => entry.Instance.UnloadAsync(CancellationToken.None), TensorEngineState.Unloaded, CancellationToken.None);
            throw;
        }
    }

    public async Task UnloadAsync(string model, CancellationToken cancellationToken)
    {
        var entry = Get(model);
        await AwaitStateAsync(entry.Instance, () => entry.Instance.UnloadAsync(cancellationToken), TensorEngineState.Unloaded, cancellationToken);
    }

    internal static async Task AwaitStateAsync(ITensorModelInstance instance, Func<Task> accept, TensorEngineState target, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, TensorEngineStatus status)
        {
            if (status.State == target) completion.TrySetResult();
            else if (status.State == TensorEngineState.Failed) completion.TrySetException(new GpuException("Tensor engine lifecycle failed"));
        }
        instance.EngineStatusChanged += Changed;
        try
        {
            await accept();
            var observation = await instance.GetObservationAsync(token);
            if (observation.State == target) completion.TrySetResult();
            else if (observation.State == TensorEngineState.Failed) completion.TrySetException(new GpuException("Tensor engine lifecycle failed"));
            await completion.Task.WaitAsync(token);
        }
        finally { instance.EngineStatusChanged -= Changed; }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        services.GetRequiredService<GpuCoordinator>().ModelHostExited(null);
        foreach (var model in _models)
            await AwaitStateAsync(model.Instance, () => model.Instance.StopAsync(cancellationToken), TensorEngineState.Stopped, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var model in _models) await model.Instance.DisposeAsync();
        _models.Clear();
        foreach (var packs in _packs.Values) packs.Dispose();
        _packs.Clear();
    }
}
