using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal sealed class SdkHttpGenerationPool : IHttpClientFactory, IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly Dictionary<string, SdkHttpPipelineEntry> _active = new(StringComparer.Ordinal);
    private readonly HashSet<SdkHttpPipelineEntry> _entries = [];
    private readonly HashSet<SdkHttpClientRegistration> _clients = [];
    private readonly List<(ITimer Timer, Task Cleanup)> _cleanupTimers = [];
    private ITimer? _cleanupTimer;
    private bool _releaseStarted;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(10);
    private IServiceProvider? _services;
    private IOptionsMonitor<HttpClientFactoryOptions>? _options;
    private IHttpMessageHandlerBuilderFilter[]? _filters;
    private SdkHttpConfiguration? _configuration;
    private TimeProvider? _clock;
    private readonly InferenceHttpCreationMode _mode;

    public SdkHttpGenerationPool(IServiceProvider services, SdkHttpConfiguration configuration, TimeProvider clock)
    {
        _services = services; _configuration = configuration.Snapshot(); _mode = configuration.Mode; _clock = clock;
        if (_mode == InferenceHttpCreationMode.SdkGenerationOwned)
        {
            _options = services.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();
            _filters = services.GetServices<IHttpMessageHandlerBuilderFilter>().ToArray();
        }
    }

    public HttpClient CreateClient(string name)
    {
        SdkHttpRegistration.ValidateName(name);
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
        if (_mode == InferenceHttpCreationMode.BorrowHostFactory) return _services!.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        SdkHttpPipelineEntry entry;
        lock (_gate)
        {
            if (!_active.TryGetValue(name, out entry!) || entry.Expired)
            {
                entry = new(this);
                SdkHttpPipelineEntry created = entry;
                entry.Initialization = new(() => { BuildEntry(name, created); return true; });
                _active[name] = entry;
                _entries.Add(entry);
            }
            entry.Holders++;
        }
        SdkHttpClientLease? client = null;
        try
        {
            _ = entry.Initialization!.Value;
            var registration = new SdkHttpClientRegistration(entry);
            client = new(this, registration);
            registration.Client = new(client);
            lock (_gate) _clients.Add(registration);
            foreach (Action<HttpClient> action in _options!.Get(name).HttpClientActions) action(client);
            Fence(operation.Token);
            entry.StartTimer(_clock!);
            return client;
        }
        catch (Exception original)
        {
            if (client is null)
            {
                lock (_gate)
                {
                    if (_active.TryGetValue(name, out SdkHttpPipelineEntry? current) && ReferenceEquals(current, entry)) _active.Remove(name);
                    entry.Expired = true; entry.Holders--; StartUnusedRetirement(entry);
                }
                throw;
            }
            try { client.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            throw;
        }
    }

    private void BuildEntry(string name, SdkHttpPipelineEntry entry)
    {
        HttpClientFactoryOptions options = _options!.Get(name);
        IServiceScope? scope = options.SuppressHandlerScope ? null : _services!.CreateScope();
        entry.Scope = scope;
        entry.Lifetime = options.HandlerLifetime;
        var builder = new SdkHttpMessageHandlerBuilder(name, scope?.ServiceProvider ?? _services!, options.HandlerLifetime, _configuration!, this, entry);
        try
        {
            Action<HttpMessageHandlerBuilder> configure = handler =>
            {
                foreach (Action<HttpMessageHandlerBuilder> action in options.HttpMessageHandlerBuilderActions) action(handler);
                ((SdkHttpMessageHandlerBuilder)handler).BuildLoggers();
            };
            for (int index = _filters!.Length - 1; index >= 0; index--) configure = _filters[index].Configure(configure);
            configure(builder);
            entry.Handler = builder.Build();
            entry.Invoker = new(entry.Handler, disposeHandler: false);
        }
        catch (Exception original)
        {
            var errors = new List<Exception> { original };
            var components = new HashSet<HttpMessageHandler>(builder.AdditionalHandlers);
            if (builder.CreatedPrimary is { } primary) components.Add(primary);
            var children = new HashSet<HttpMessageHandler>(components.OfType<DelegatingHandler>()
                .Select(handler => handler.InnerHandler).OfType<HttpMessageHandler>());
            foreach (HttpMessageHandler handler in components.Where(handler => !children.Contains(handler)))
                try { handler.Dispose(); } catch (Exception cleanup) { errors.Add(cleanup); entry.Failures.Add((handler, cleanup)); }
            if (entry.Failures.Count == 0)
                try { entry.DisposeScopeAsync().GetAwaiter().GetResult(); } catch (Exception cleanup) { errors.Add(cleanup); entry.Failures.Add((scope!, cleanup)); }
            ThrowFailures(errors); throw;
        }
    }

    internal LlmOperationLifetime.Lease EnterSend(SdkHttpPipelineEntry entry, CancellationToken cancellationToken)
    {
        LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        lock (_gate) entry.Holders++;
        return operation;
    }
    internal LlmOperationLifetime.Lease? EnterConsumption(CancellationToken token) =>
        _mode == InferenceHttpCreationMode.SdkGenerationOwned ? _lifetime.Enter(token) : null;
    internal LlmOperationLifetime.Lease EnterBody(SdkHttpPipelineEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { return EnterSend(entry, cancellationToken); }
        catch (ObjectDisposedException) { throw new OperationCanceledException("The SDK HTTP generation is retiring.", cancellationToken); }
    }
    internal void Fence(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { _lifetime.ThrowIfStopping(); }
        catch (ObjectDisposedException) { throw new OperationCanceledException("The SDK HTTP generation is retiring.", cancellationToken); }
    }
    internal void ReleaseUse(SdkHttpPipelineEntry entry, LlmOperationLifetime.Lease operation)
    {
        lock (_gate) { entry.Holders--; StartUnusedRetirement(entry); }
        operation.Dispose();
    }
    internal void ReleaseClient(SdkHttpClientLease client, SdkHttpClientRegistration registration, Exception? failure)
    {
        lock (_gate)
        {
            if (!_clients.Remove(registration)) return;
            SdkHttpPipelineEntry entry = registration.Entry;
            entry.Holders--;
            if (failure is not null) entry.Failures.Add((client, failure));
            StartUnusedRetirement(entry);
        }
    }
    internal void RecordFailure(SdkHttpPipelineEntry entry, object resource, Exception failure)
    { lock (_gate) entry.Failures.Add((resource, failure)); }
    internal void Expire(SdkHttpPipelineEntry entry)
    { lock (_gate) { entry.Expired = true; ReconcileClients(); StartUnusedRetirement(entry); UpdateCleanupTimer(); } }
    private void ReconcileClients()
    {
        foreach (SdkHttpClientRegistration registration in _clients.ToArray())
        {
            if (registration.Client!.TryGetTarget(out _)) continue;
            _clients.Remove(registration);
            registration.Entry.Holders--;
            StartUnusedRetirement(registration.Entry);
        }
    }
    private void StartUnusedRetirement(SdkHttpPipelineEntry entry)
    {
        if (entry.Expired && entry.Holders == 0) entry.Cleanup ??= Task.Run(() => CleanupEntryAsync(entry));
    }
    private async Task CleanupEntryAsync(SdkHttpPipelineEntry entry)
    {
        try { await entry.CleanupAsync().ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                if (entry.Released)
                {
                    _entries.Remove(entry);
                    foreach (string name in _active.Where(pair => ReferenceEquals(pair.Value, entry)).Select(pair => pair.Key).ToArray()) _active.Remove(name);
                }
                UpdateCleanupTimer();
            }
        }
    }
    private void UpdateCleanupTimer()
    {
        bool pending = !_releaseStarted && _entries.Any(entry => entry.Expired && entry.Cleanup?.IsCompleted != true);
        if (pending && _cleanupTimer is null)
            _cleanupTimer = _clock!.CreateTimer(_ => SweepClients(), null, CleanupInterval, CleanupInterval);
        else if (!pending && _cleanupTimer is { } timer)
        {
            _cleanupTimer = null;
            _cleanupTimers.Add((timer, Task.Run(async () => await timer.DisposeAsync().ConfigureAwait(false))));
        }
        _cleanupTimers.RemoveAll(item => item.Cleanup.IsCompletedSuccessfully);
    }
    private void SweepClients()
    {
        LlmOperationLifetime.Lease operation;
        try { operation = _lifetime.Enter(default); }
        catch (ObjectDisposedException) { return; }
        using (operation)
            lock (_gate) { ReconcileClients(); UpdateCleanupTimer(); }
    }

    private async Task ReleaseAsync()
    {
        SdkHttpClientLease[] clients; SdkHttpPipelineEntry[] entries;
        Task[] timers;
        lock (_gate)
        {
            _releaseStarted = true; UpdateCleanupTimer(); ReconcileClients();
            clients = _clients.Select(registration => registration.Client!.TryGetTarget(out SdkHttpClientLease? client) ? client : null).OfType<SdkHttpClientLease>().ToArray();
            entries = _entries.ToArray(); timers = _cleanupTimers.Select(item => item.Cleanup).ToArray(); _active.Clear();
        }
        var errors = new List<Exception>();
        foreach (Task timer in timers)
            try { await timer.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        foreach (SdkHttpClientLease client in clients)
            try { client.Dispose(); } catch (Exception) { /* The client records its exact failure on its owned entry. */ }
        foreach (SdkHttpPipelineEntry entry in entries)
        {
            Task cleanup; lock (_gate) cleanup = entry.Cleanup ??= Task.Run(() => CleanupEntryAsync(entry));
            try { await cleanup.ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
        }
        lock (_gate)
        {
            _clients.Clear(); _cleanupTimers.RemoveAll(item => item.Cleanup.IsCompletedSuccessfully); _entries.RemoveWhere(entry => entry.Released);
            _services = null; _options = null; _filters = null; _clock = null;
            _configuration!.Detach(); _configuration = null;
        }
        ThrowFailures(errors);
    }
    internal static void ThrowFailures(IReadOnlyList<Exception> failures)
    {
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }
    public void Dispose() => _lifetime.RetireAsync(ReleaseAsync).GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(_lifetime.RetireAsync(ReleaseAsync));
}

internal sealed class SdkHttpPipelineEntry(SdkHttpGenerationPool owner)
{
    private readonly Lock _timerGate = new();
    private ITimer? _timer;
    internal Lazy<bool>? Initialization { get; set; }
    internal IServiceScope? Scope { get; set; }
    internal TimeSpan Lifetime { get; set; }
    internal HttpMessageHandler? Handler { get; set; }
    internal HttpMessageInvoker? Invoker { get; set; }
    internal int Holders { get; set; }
    internal bool Expired { get; set; }
    internal bool Released { get; private set; }
    internal Task? Cleanup { get; set; }
    internal List<(object Resource, Exception Error)> Failures { get; } = [];
    internal void StartTimer(TimeProvider clock)
    {
        lock (_timerGate)
        {
            if (Lifetime != Timeout.InfiniteTimeSpan && _timer is null)
                _timer = clock.CreateTimer(_ => owner.Expire(this), null, Lifetime, Timeout.InfiniteTimeSpan);
        }
    }
    internal async Task DisposeScopeAsync()
    {
        if (Scope is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync().ConfigureAwait(false);
        else Scope?.Dispose();
        Scope = null;
    }
    internal async Task CleanupAsync()
    {
        var errors = Failures.Select(failure => failure.Error).ToList();
        if (_timer is not null)
            try { await _timer.DisposeAsync().ConfigureAwait(false); _timer = null; }
            catch (Exception error) { errors.Add(error); }
        if (Failures.Count == 0)
        {
            bool handlerReleased = false;
            try { Handler?.Dispose(); Handler = null; Invoker?.Dispose(); Invoker = null; handlerReleased = true; }
            catch (Exception error) { errors.Add(error); }
            if (handlerReleased)
                try { await DisposeScopeAsync().ConfigureAwait(false); }
                catch (Exception error) { errors.Add(error); }
        }
        Released = errors.Count == 0;
        Initialization = null;
        SdkHttpGenerationPool.ThrowFailures(errors);
    }
}

internal sealed class SdkHttpClientRegistration(SdkHttpPipelineEntry entry)
{
    internal SdkHttpPipelineEntry Entry { get; } = entry;
    internal WeakReference<SdkHttpClientLease>? Client { get; set; }
}

internal sealed class SdkHttpClientLease(SdkHttpGenerationPool owner, SdkHttpClientRegistration registration)
    : HttpClient(new SdkHttpSendHandler(owner, registration.Entry), disposeHandler: true)
{
    internal SdkHttpGenerationPool GenerationOwner => owner;
    private int _disposed;
    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Exception? failure = null;
        try { base.Dispose(true); }
        catch (Exception error) { failure = error; throw; }
        finally { owner.ReleaseClient(this, registration, failure); }
    }
}

internal sealed class SdkHttpSendHandler(SdkHttpGenerationPool owner, SdkHttpPipelineEntry entry) : HttpMessageHandler
{
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LlmOperationLifetime.Lease operation = owner.EnterSend(entry, cancellationToken); bool transferred = false;
        try
        {
            HttpResponseMessage response = entry.Invoker!.Send(request, operation.Token);
            return Transfer(response, operation, ref transferred);
        }
        finally { if (!transferred) owner.ReleaseUse(entry, operation); }
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LlmOperationLifetime.Lease operation = owner.EnterSend(entry, cancellationToken); bool transferred = false;
        try
        {
            HttpResponseMessage response = await entry.Invoker!.SendAsync(request, operation.Token).ConfigureAwait(false);
            return Transfer(response, operation, ref transferred);
        }
        finally { if (!transferred) owner.ReleaseUse(entry, operation); }
    }
    private HttpResponseMessage Transfer(HttpResponseMessage response, LlmOperationLifetime.Lease operation, ref bool transferred)
    {
        try
        {
            owner.Fence(operation.Token);
            response.Content = new SdkHttpResponseLease(owner, entry, response.Content, operation);
            transferred = true; return response;
        }
        catch (Exception original)
        {
            try { response.Dispose(); }
            catch (Exception cleanup) { owner.RecordFailure(entry, response, cleanup); throw new AggregateException(original, cleanup); }
            throw;
        }
    }
}
