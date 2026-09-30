using GpuArbiter.Backends;

namespace GpuArbiter.Gpu;

// The lock owns admission and counters. Only the hosted loop calls backend mutation APIs.
public sealed class GpuCoordinator(ILlamaRouterClient llama, IComfyClient comfy, IVramProbe vram,
    ArbiterOptions options, TimeProvider clock, ILogger<GpuCoordinator> logger) : BackgroundService
{
    private readonly object _gate = new();
    private readonly List<Waiter> _waiters = [];
    private GpuTarget? _holder;
    private ComfyState _comfy = new(false, 0, 0);
    private int _llmInFlight;
    private int _promptInFlight;
    private int? _vramUsed;
    private string _phase = "idle";
    private bool _ready;
    private bool _holderUsable;
    private bool _switching;
    private long _generation;
    private CancellationTokenSource _routerStopped = new();

    public GpuView Snapshot()
    {
        lock (_gate)
            return new(_holder?.ToString() ?? "none", _switching ? "switching" : (_llmInFlight > 0 || ComfyBusy) ? "busy" : "idle",
                _switching ? _phase : null, _llmInFlight, _comfy, _waiters.Select(w => new WaiterView(w.Target.ToString(), w.Kind,
                    (clock.GetUtcNow() - w.Since).TotalSeconds)).ToArray(), _vramUsed);
    }

    public async Task<GpuView> SnapshotAsync(CancellationToken token)
    {
        var used = await vram.ReadAsync(token).ConfigureAwait(false);
        lock (_gate) _vramUsed = used;
        return Snapshot();
    }

    public string WaitingReason()
    {
        lock (_gate)
        {
            if (!_ready) return "starting llama-server";
            if (_switching) return _phase;
            if (ComfyBusy) return $"ComfyUI running {_comfy.Running} job, {_comfy.Pending} pending";
            return $"{_llmInFlight} LLM requests in flight";
        }
    }

    public async Task<GpuLease> AcquireAsync(GpuTarget target, CancellationToken cancellationToken)
    {
        Waiter waiter;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_switching && (target.Model is null || _ready) && _holder == target && _holderUsable &&
                !_waiters.Any(w => w.Target != target || w.Kind == "unload"))
                return Lease(target);
            waiter = new(target, "request", clock.GetUtcNow());
            _waiters.Add(waiter);
        }
        using var registration = cancellationToken.Register(() => Cancel(waiter, cancellationToken));
        return (await waiter.Completion.Task.ConfigureAwait(false))!;
    }

    public async Task UnloadAsync(string model, CancellationToken cancellationToken)
    {
        Waiter waiter;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            waiter = new(GpuTarget.Llm(model), "unload", clock.GetUtcNow());
            _waiters.Add(waiter);
        }
        using var registration = cancellationToken.Register(() => Cancel(waiter, cancellationToken));
        await waiter.Completion.Task.ConfigureAwait(false);
    }

    private void Cancel(Waiter waiter, CancellationToken token)
    {
        lock (_gate)
        {
            if (_waiters.Remove(waiter))
            {
                waiter.Completion.TrySetCanceled(token);
                logger.LogInformation("Canceled waiter for {Target}", waiter.Target);
            }
        }
    }

    public void RouterReady()
    {
        lock (_gate) _ready = true;
    }

    public void RouterExited(int? exitCode)
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            _ready = false;
            _generation++;
            previous = _routerStopped;
            _routerStopped = new();
            if (_holder?.Model is not null) { _holder = null; _holderUsable = false; }
            _llmInFlight = 0;
        }
        previous.Cancel();
        previous.Dispose();
        logger.LogWarning("Router exited with code {ExitCode}", exitCode);
    }

    public void ObserveComfy(ComfyState state)
    {
        lock (_gate)
        {
            _comfy = state;
            if (!state.Busy)
            {
                _waiters.RemoveAll(w => w.Kind == "watcher");
                return;
            }
            if (_holder is null && !_switching) { _holder = GpuTarget.Comfy; _holderUsable = true; }
            else if (_holder != GpuTarget.Comfy && !_waiters.Any(w => w.Kind == "watcher"))
                _waiters.Add(new(GpuTarget.Comfy, "watcher", clock.GetUtcNow()));
        }
    }

    private bool ComfyBusy => _comfy.Busy || _promptInFlight > 0;

    private GpuLease Lease(GpuTarget target)
    {
        var generation = _generation;
        if (target.Model is null) _promptInFlight++;
        else _llmInFlight++;
        logger.LogInformation("Admitted {Target}", target);
        return new(() =>
        {
            lock (_gate)
            {
                if (target.Model is null) _promptInFlight--;
                else if (generation == _generation) _llmInFlight--;
            }
        }, target.Model is null ? CancellationToken.None : _routerStopped.Token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await StepAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(50), clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (_gate)
            {
                foreach (var waiter in _waiters) waiter.Completion.TrySetCanceled(stoppingToken);
                _waiters.Clear();
            }
        }
    }

    // Tests advance one loop iteration; production calls this only from ExecuteAsync.
    internal async Task StepAsync(CancellationToken cancellationToken)
    {
        Waiter head;
        long generation;
        lock (_gate)
        {
            if (_switching || _waiters.Count == 0) return;
            head = _waiters[0];
            if (!_ready && (head.Target.Model is not null || head.Kind == "unload")) return;
            if (_llmInFlight > 0 && (head.Target != _holder || head.Kind == "unload")) return;
            if (_holder == GpuTarget.Comfy && ComfyBusy && head.Target != _holder) return;
            // A direct Comfy submission also prevents LLM acquisition when our holder is stale.
            if (head.Target.Model is not null && ComfyBusy) return;
            _switching = true;
            generation = _generation;
        }
        try
        {
            if (head.Kind == "unload")
            {
                await UnloadModelAsync(head.Target.Model!, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_holder == head.Target) _holder = null;
                    _waiters.Remove(head);
                    head.Completion.TrySetResult(null);
                }
                return;
            }
            bool same;
            lock (_gate) same = _holder == head.Target && _holderUsable;
            if (!same)
            {
                SetPhase("switching: evicting models");
                foreach (var model in _ready ? await llama.ModelsAsync(cancellationToken).ConfigureAwait(false) : [])
                    if (model.Id != head.Target.Model && model.Status != "unloaded")
                        await UnloadModelAsync(model.Id, cancellationToken).ConfigureAwait(false);
                if (head.Target.Model is not null)
                {
                    var state = await comfy.QueueAsync(cancellationToken).ConfigureAwait(false);
                    ObserveComfy(state);
                    if (state.Busy) return;
                    if (state.Reachable)
                    {
                        SetPhase("switching: freeing ComfyUI");
                        await comfy.FreeAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                // A target already loaded still needs all other users evicted, but its own VRAM
                // cannot meet an unloaded threshold. Owned models normally reach here unloaded.
                var models = _ready ? await llama.ModelsAsync(cancellationToken).ConfigureAwait(false) : [];
                var targetLoaded = head.Target.Model is not null && models.Any(m => m.Id == head.Target.Model && m.Status == "loaded");
                if (!targetLoaded) await ConfirmReleaseAsync(cancellationToken).ConfigureAwait(false);
                if (head.Target.Model is not null && !targetLoaded)
                {
                    SetPhase($"switching: loading {head.Target.Model}");
                    try
                    {
                        await llama.LoadAsync(head.Target.Model, cancellationToken).ConfigureAwait(false);
                        await WaitModelAsync(head.Target.Model, "loaded", options.Llama.LoadTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        lock (_gate) _holder = null;
                        throw;
                    }
                }
            }
            lock (_gate)
            {
                if (head.Target.Model is not null && (generation != _generation || !_ready)) throw new GpuException("llama-server restarted during handoff", 502);
                _holder = head.Target;
                _holderUsable = true;
                // The first target wins the handoff; its queued requests share the acquired holder.
                foreach (var waiter in _waiters.TakeWhile(w => w.Kind != "unload").Where(w => w.Target == head.Target).ToArray())
                {
                    _waiters.Remove(waiter);
                    waiter.Completion.TrySetResult(waiter.Kind == "watcher" ? null : Lease(waiter.Target));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var failure = error as GpuException ?? new GpuException("GPU backend operation failed");
            logger.LogWarning("Handoff failed for {Target}: {Reason}", head.Target, failure.Message);
            lock (_gate)
            {
                foreach (var waiter in _waiters.Where(w => w.Target == head.Target).ToArray())
                {
                    _waiters.Remove(waiter);
                    waiter.Completion.TrySetException(failure);
                    logger.LogInformation("Failed waiter for {Target}: {Reason}", waiter.Target, failure.Message);
                }
            }
        }
        finally
        {
            lock (_gate) { _switching = false; _phase = "idle"; }
        }
    }

    private void SetPhase(string phase)
    {
        lock (_gate) _phase = phase;
        logger.LogInformation("GPU {Phase}", phase);
    }

    private async Task UnloadModelAsync(string model, CancellationToken token)
    {
        SetPhase($"switching: unloading {model}");
        lock (_gate) { if (_holder?.Model == model) _holderUsable = false; }
        await llama.UnloadAsync(model, token).ConfigureAwait(false);
        await WaitModelAsync(model, "unloaded", options.Vram.ReleaseTimeoutSeconds, token).ConfigureAwait(false);
    }

    private async Task WaitModelAsync(string model, string status, double timeout, CancellationToken token)
    {
        var deadline = clock.GetUtcNow().AddSeconds(timeout);
        int? lastExitCode = null;
        do
        {
            var state = (await llama.ModelsAsync(token).ConfigureAwait(false)).FirstOrDefault(m => m.Id == model);
            if (state is null) throw new GpuException("Unknown model");
            lastExitCode = state.ExitCode;
            if (status == "loaded" && state.Failed)
                throw new GpuException($"Model load failed; router exit code: {state.ExitCode?.ToString() ?? "unavailable"}");
            if (state.Status == status) return;
            await Task.Delay(TimeSpan.FromMilliseconds(500), clock, token).ConfigureAwait(false);
        } while (clock.GetUtcNow() < deadline);
        throw new GpuException(status == "loaded"
            ? $"Model load timed out; router exit code: {lastExitCode?.ToString() ?? "unavailable"}"
            : $"Timed out waiting for model to become {status}");
    }

    private async Task ConfirmReleaseAsync(CancellationToken token)
    {
        SetPhase("switching: waiting for VRAM release");
        var deadline = clock.GetUtcNow().AddSeconds(options.Vram.ReleaseTimeoutSeconds);
        int used;
        do
        {
            used = await vram.ReadAsync(token).ConfigureAwait(false);
            lock (_gate) _vramUsed = used;
            logger.LogInformation("VRAM used: {UsedMiB} MiB", used);
            if (used <= options.Vram.ReleasedMaxUsedMiB) return;
            await Task.Delay(TimeSpan.FromMilliseconds(500), clock, token).ConfigureAwait(false);
        } while (clock.GetUtcNow() < deadline);
        throw new GpuException($"GPU was not released: {used} MiB in use");
    }

    public override void Dispose()
    {
        base.Dispose();
        _routerStopped.Dispose();
    }

    private sealed class Waiter(GpuTarget target, string kind, DateTimeOffset since)
    {
        public GpuTarget Target { get; } = target;
        public string Kind { get; } = kind;
        public DateTimeOffset Since { get; } = since;
        public TaskCompletionSource<GpuLease?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
