using GpuArbiter.Backends;
using GpuArbiter.Gpu;

namespace GpuArbiter.Splash;

public sealed class SplashModelHost(SplashOptions options, ISplashProcessFactory processes,
    ISplashControl control, TimeProvider clock, ILogger<SplashModelHost> logger) : IModelHost, IHostedService
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Dictionary<string, ModelState> _states = options.Models.ToDictionary(m => m.Id, m => new ModelState(m.Id, "unloaded", false, null), StringComparer.Ordinal);
    private ISplashProcess? _process;
    private string? _model;
    private bool _stopping;
    private Task _monitor = Task.CompletedTask;
    public event Action? Available;
    public event Action<int?>? UnexpectedExit;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Available?.Invoke();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ModelState>> ModelsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult<IReadOnlyList<ModelState>>(_states.Values.ToArray());
    }

    public bool GroupsReleased
    {
        get { lock (_gate) return _process is null || !_process.GroupAlive; }
    }

    public async Task<SplashObservation> ObservationAsync(CancellationToken token)
    {
        ISplashProcess? process;
        lock (_gate) process = _process;
        if (process is null || process.Exit.IsCompleted) return SplashObservation.Unavailable;
        var observation = await control.ReadAsync(token).ConfigureAwait(false);
        lock (_gate)
            return _process == process && observation.ServerPid == process.Pid ? observation : SplashObservation.Unavailable;
    }

    public async Task LoadAsync(string model, CancellationToken token)
    {
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_stopping) throw new GpuException("Splash host is stopping");
            var configuration = options.Models.FirstOrDefault(m => m.Id == model) ?? throw new GpuException("Unknown model", 404);
            if (_process is not null) throw new GpuException("An owned Splash instance must unload before loading another model");
            SetState(model, "loading");
            try
            {
                var process = processes.Start(options, configuration);
                lock (_gate) { _process = process; _model = model; }
                logger.LogInformation("Started Splash model {Model}, owned process {Pid}", model, process.Pid);
                _monitor = MonitorAsync(process, model);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.ModelLoadTimeoutSeconds));
                while (true)
                {
                    if (process.Exit.IsCompleted) throw new GpuException("Splash server exited during load");
                    var observation = await ObservationAsync(timeout.Token).ConfigureAwait(false);
                    if (observation.Ready && observation.MetalHealthy && !observation.Stale && await control.ReadyAsync(timeout.Token).ConfigureAwait(false))
                    {
                        SetState(model, "loaded");
                        return;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(250), clock, timeout.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                int? exitCode;
                lock (_gate) exitCode = _process?.Exit.IsCompletedSuccessfully == true ? _process.Exit.Result : null;
                logger.LogWarning("Splash model {Model} load failed; server exit code {ExitCode}", model, exitCode);
                try { await StopOwnedAsync().ConfigureAwait(false); }
                finally
                {
                    bool released;
                    lock (_gate) released = _process is null;
                    SetState(model, released ? "unloaded" : "failed", failed: true, exitCode: exitCode);
                }
                if (token.IsCancellationRequested) throw;
                throw new GpuException($"Splash model load failed or timed out; server exit code: {exitCode?.ToString() ?? "unavailable"}");
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task UnloadAsync(string model, CancellationToken token)
    {
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_states.ContainsKey(model)) throw new GpuException("Unknown model", 404);
            if (_model != model) return;
            SetState(model, "unloading");
            await StopOwnedAsync().ConfigureAwait(false);
            SetState(model, "unloaded");
        }
        finally { _lifecycle.Release(); }
    }

    public async Task ConfirmCompletionAsync(CancellationToken hostStopped)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.CleanupTimeoutSeconds));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, hostStopped);
        try
        {
            while (true)
            {
                if (GroupsReleased) return;
                var observation = await ObservationAsync(cancellation.Token).ConfigureAwait(false);
                if (observation.Idle) return;
                await Task.Delay(TimeSpan.FromMilliseconds(50), clock, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (hostStopped.IsCancellationRequested) { return; }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_process is null) return;
                UnexpectedExit?.Invoke(null);
                var model = _model!;
                await StopOwnedAsync().ConfigureAwait(false);
                SetState(model, "unloaded", failed: true);
                if (!_stopping) Available?.Invoke();
            }
            finally { _lifecycle.Release(); }
        }
    }

    private async Task MonitorAsync(ISplashProcess process, string model)
    {
        var exitCode = await process.Exit.ConfigureAwait(false);
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_process != process) return;
            UnexpectedExit?.Invoke(exitCode);
            logger.LogWarning("Splash model {Model} exited with code {ExitCode}", model, exitCode);
            await StopOwnedAsync().ConfigureAwait(false);
            SetState(model, "unloaded", true, exitCode);
            if (!_stopping) Available?.Invoke();
        }
        catch (Exception)
        {
            SetState(model, "failed", true, exitCode);
            logger.LogError("Owned Splash cleanup failed; admission remains unavailable");
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StopOwnedAsync()
    {
        ISplashProcess? process;
        lock (_gate) process = _process;
        if (process is null) return;
        try { await process.StopAsync(TimeSpan.FromSeconds(options.StopTimeoutSeconds), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error)
        {
            logger.LogError("Splash process cleanup failed for {Pid}; failure type {FailureType}", process.Pid, error.GetType().Name);
            throw new GpuException($"Splash process cleanup failed ({error.GetType().Name})");
        }
        if (process.GroupAlive) throw new GpuException("Owned Splash native process remains alive");
        await process.DisposeAsync().ConfigureAwait(false);
        lock (_gate) { _process = null; _model = null; }
        logger.LogInformation("Confirmed Splash process-group exit for {Pid}", process.Pid);
    }

    private void SetState(string model, string status, bool failed = false, int? exitCode = null)
    {
        lock (_gate) _states[model] = new(model, status, failed, exitCode);
        logger.LogInformation("Splash model {Model}: {Status}", model, status);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            UnexpectedExit?.Invoke(null);
            var model = _model;
            await StopOwnedAsync().ConfigureAwait(false);
            if (model is not null) SetState(model, "unloaded");
        }
        finally { _lifecycle.Release(); }
        await _monitor.ConfigureAwait(false);
    }
}

public sealed class MacReleaseGuard(SplashModelHost host) : IGpuReleaseGuard
{
    public Task<GpuReleaseObservation> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new GpuReleaseObservation(host.GroupsReleased, "owned-process-termination", null));
    }
}
