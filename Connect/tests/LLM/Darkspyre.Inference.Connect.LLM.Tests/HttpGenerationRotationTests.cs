using System.Runtime.CompilerServices;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HttpGenerationRotationTests
{
    private const string Pipeline = "OpenAiResponsesLlmClient";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ActualScopedFacadeDropReleasesTypedWrapperAndExpiredChainWhileRootStaysAlive()
    {
        var clock = new Clock(); var factory = new CapturingTypedFactory(); var source = new BorrowedSource();
        var services = new ServiceCollection(); services.AddSingleton<TimeProvider>(clock); services.AddSingleton<ILlmConnectionSource>(source);
        services.AddSingleton<ITypedHttpClientFactory<OpenAiResponsesLlmClient>>(factory); services.AddLlmConnect();
        var handler = new Handler(); services.AddHttpClient(Pipeline).SetHandlerLifetime(TimeSpan.FromSeconds(2)).ConfigurePrimaryHttpMessageHandler(() => handler);
        await using ServiceProvider root = services.BuildServiceProvider();
        WeakReference[] roots = DropActualScopedFacade(root, factory);
        Collect(roots);
        Assert.All(roots, reference => Assert.False(reference.IsAlive, "The retained SDK pool must not strongly own a dropped scoped typed consumer."));
        clock.Advance(TimeSpan.FromSeconds(2)); clock.Advance(TimeSpan.FromSeconds(10));
        await handler.Disposed.Task.WaitAsync(Bound); Assert.Equal(1, handler.Disposals);
        await root.DisposeAsync(); Assert.Equal(0, source.Disposals); GC.KeepAlive(root);
    }

    [Fact]
    public async Task CollectedWrapperCannotReleaseStillLiveResponseAndCleanupTimerStopsAfterLastLease()
    {
        var clock = new Clock(); var services = new ServiceCollection(); services.AddSingleton<TimeProvider>(clock); services.AddLlmConnect();
        var handlers = new List<Handler>(); services.AddHttpClient(Pipeline).SetHandlerLifetime(TimeSpan.FromSeconds(2)).ConfigurePrimaryHttpMessageHandler(() =>
        { var handler = new Handler(); handlers.Add(handler); return handler; });
        await using ServiceProvider root = services.BuildServiceProvider(); SdkHttpGenerationPool pool = root.GetRequiredService<SdkHttpGenerationPool>();
        (HttpResponseMessage response, WeakReference wrapper) = await DropWrapperAfterHeadersAsync(pool);
        using (response)
        {
            Collect([wrapper]); Assert.False(wrapper.IsAlive);
            clock.Advance(TimeSpan.FromSeconds(2));
            Timer sweep = Assert.Single(clock.Timers, timer => timer.Period == TimeSpan.FromSeconds(10));
            Assert.Equal(TimeSpan.FromSeconds(10), sweep.Interval);
            clock.Advance(TimeSpan.FromSeconds(10)); Assert.Equal(0, handlers[0].Disposals);
            using HttpClient next = pool.CreateClient(Pipeline); Assert.Equal(2, handlers.Count);
            response.Dispose(); await handlers[0].Disposed.Task.WaitAsync(Bound);
            await sweep.Disposed.Task.WaitAsync(Bound); Assert.Equal(1, sweep.Disposals);
        }
        await pool.DisposeAsync(); Assert.Equal(1, handlers[1].Disposals); Assert.All(clock.Timers, timer => Assert.Equal(1, timer.Disposals));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] DropActualScopedFacade(ServiceProvider root, CapturingTypedFactory factory)
    {
        using IServiceScope scope = root.CreateScope();
        LlmProviderService facade = scope.ServiceProvider.GetRequiredService<LlmProviderService>();
        Assert.NotEmpty(facade.SupportedProviders); Assert.Equal(2, factory.Calls);
        return [new(facade), .. factory.Clients];
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(HttpResponseMessage Response, WeakReference Wrapper)> DropWrapperAfterHeadersAsync(SdkHttpGenerationPool pool)
    {
        HttpClient client = pool.CreateClient(Pipeline);
        HttpResponseMessage response = await client.GetAsync("https://controlled.invalid/", HttpCompletionOption.ResponseHeadersRead);
        return (response, new(client));
    }
    private static void Collect(WeakReference[] references)
    {
        for (int attempt = 0; references.Any(reference => reference.IsAlive) && attempt < 30; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
    }
    private sealed class CapturingTypedFactory : ITypedHttpClientFactory<OpenAiResponsesLlmClient>
    {
        public int Calls { get; private set; }
        public List<WeakReference> Clients { get; } = [];
        public OpenAiResponsesLlmClient CreateClient(HttpClient httpClient) { Calls++; Clients.Add(new(httpClient)); return new(httpClient); }
    }
    private sealed class BorrowedSource : ILlmConnectionSource, IDisposable
    {
        public int Disposals { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => Task.FromResult<LlmConnection?>(null);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() => Disposals++;
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Disposals { get; private set; }
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("actual") });
        protected override void Dispose(bool disposing) { if (disposing) { Disposals++; Disposed.TrySetResult(); } base.Dispose(disposing); }
    }
    private sealed class Clock : TimeProvider
    {
        public TimeSpan Elapsed { get; private set; }
        public List<Timer> Timers { get; } = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(callback, state, Elapsed + dueTime, dueTime, period); Timers.Add(timer); return timer; }
        public void Advance(TimeSpan elapsed)
        {
            Elapsed += elapsed;
            foreach (Timer timer in Timers.ToArray()) timer.Fire(Elapsed);
        }
    }
    private sealed class Timer(TimerCallback callback, object? state, TimeSpan due, TimeSpan interval, TimeSpan period) : ITimer
    {
        private TimeSpan _due = due;
        public TimeSpan Interval => interval;
        public TimeSpan Period => period;
        public int Disposals { get; private set; }
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Fire(TimeSpan elapsed)
        {
            if (Disposals != 0 || _due == Timeout.InfiniteTimeSpan || elapsed < _due) return;
            _due = period == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : elapsed + period; callback(state);
        }
        public bool Change(TimeSpan dueTime, TimeSpan nextPeriod) => throw new NotSupportedException();
        public void Dispose() { Disposals++; Disposed.TrySetResult(); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
