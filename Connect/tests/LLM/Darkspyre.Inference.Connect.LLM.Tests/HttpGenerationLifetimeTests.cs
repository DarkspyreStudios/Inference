using System.Net;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HttpGenerationLifetimeTests
{
    private const string Pipeline = "OpenAiResponsesLlmClient";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SynchronousOwnerFenceRejectsLateHeadersWhileLinkedCancellationIsStillBlocked()
    {
        var handler = new HeldHandler(); await using ServiceProvider root = CreateRoot(handler);
        SdkHttpGenerationPool pool = root.GetRequiredService<SdkHttpGenerationPool>(); using HttpClient client = pool.CreateClient(Pipeline);
        Task<HttpResponseMessage> sending = client.GetAsync("https://controlled.invalid/", HttpCompletionOption.ResponseHeadersRead);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? retirement = null;
        CancellationTokenRegistration blocker = default;
        try
        {
            await handler.Entered.Task.WaitAsync(Bound);
            var lifetime = (LlmOperationLifetime)typeof(SdkHttpGenerationPool).GetField("_lifetime", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(pool)!;
            var stop = (CancellationTokenSource)typeof(LlmOperationLifetime).GetField("_stop", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(lifetime)!;
            blocker = stop.Token.Register(() => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); });
            retirement = pool.DisposeAsync().AsTask(); await entered.Task.WaitAsync(Bound);
            Assert.False(handler.Token.IsCancellationRequested);
            handler.Release.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
            Assert.Equal(1, handler.Content.Disposals); Assert.False(retirement.IsCompleted);
            release.TrySetResult(); await retirement.WaitAsync(Bound);
        }
        finally
        {
            retirement ??= pool.DisposeAsync().AsTask(); handler.Release.TrySetResult(); release.TrySetResult();
            await ObserveAsync(sending); await ObserveAsync(retirement);
            blocker.Dispose();
        }
    }

    [Fact]
    public async Task IgnoringSendDrainsAndLateResponseIsDisposedBeforeSharedRetirementCompletes()
    {
        var handler = new HeldHandler();
        await using ServiceProvider root = CreateRoot(handler);
        SdkHttpGenerationPool pool = root.GetRequiredService<SdkHttpGenerationPool>();
        using HttpClient client = pool.CreateClient(Pipeline);
        Task<HttpResponseMessage> sending = client.GetAsync("https://controlled.invalid/", HttpCompletionOption.ResponseHeadersRead);
        Task? retirement = null;
        try
        {
            await handler.Entered.Task.WaitAsync(Bound);
            retirement = pool.DisposeAsync().AsTask();
            Assert.Same(retirement, pool.DisposeAsync().AsTask());
            Assert.False(retirement.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => pool.CreateClient(Pipeline));
            handler.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
            await retirement.WaitAsync(Bound);
            Assert.Equal(1, handler.Content.Disposals); Assert.Equal(1, handler.Disposals);
        }
        finally
        {
            retirement ??= pool.DisposeAsync().AsTask(); handler.Release.TrySetResult();
            await ObserveAsync(sending); await ObserveAsync(retirement);
        }
    }

    [Fact]
    public async Task IgnoringBodyReadKeepsHeadersLeaseAndCannotPublishLateBytes()
    {
        var stream = new HeldStream(); var handler = new ImmediateHandler(new StreamContent(stream));
        await using ServiceProvider root = CreateRoot(handler);
        SdkHttpGenerationPool pool = root.GetRequiredService<SdkHttpGenerationPool>();
        using HttpClient client = pool.CreateClient(Pipeline);
        using HttpResponseMessage response = await client.GetAsync("https://controlled.invalid/", HttpCompletionOption.ResponseHeadersRead);
        Stream body = await response.Content.ReadAsStreamAsync();
        byte[] buffer = new byte[4]; Task<int> reading = body.ReadAsync(buffer).AsTask(); Task? retirement = null;
        try
        {
            await stream.Entered.Task.WaitAsync(Bound);
            retirement = pool.DisposeAsync().AsTask(); Assert.False(retirement.IsCompleted);
            stream.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
            Assert.False(retirement.IsCompleted);
            response.Dispose(); await retirement.WaitAsync(Bound);
            Assert.Equal(1, handler.Disposals);
        }
        finally
        {
            retirement ??= pool.DisposeAsync().AsTask(); stream.Release.TrySetResult();
            await ObserveAsync(reading); response.Dispose(); await ObserveAsync(retirement);
        }
    }

    [Fact]
    public async Task AsyncStreamCleanupKeepsResponseLeaseUntilActualCleanupSettles()
    {
        var stream = new HeldStream(); var handler = new ImmediateHandler(new StreamContent(stream));
        await using ServiceProvider root = CreateRoot(handler);
        SdkHttpGenerationPool pool = root.GetRequiredService<SdkHttpGenerationPool>();
        using HttpClient client = pool.CreateClient(Pipeline);
        using HttpResponseMessage response = await client.GetAsync("https://controlled.invalid/", HttpCompletionOption.ResponseHeadersRead);
        Stream body = await response.Content.ReadAsStreamAsync();
        Task cleanup = body.DisposeAsync().AsTask(); Task? retirement = null;
        try
        {
            await stream.CleanupEntered.Task.WaitAsync(Bound);
            response.Dispose(); retirement = pool.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted); Assert.Equal(0, handler.Disposals);
            stream.CleanupRelease.TrySetResult(); await cleanup.WaitAsync(Bound); await retirement.WaitAsync(Bound);
            Assert.Equal(1, stream.AsyncDisposals); Assert.Equal(1, handler.Disposals);
        }
        finally
        {
            retirement ??= pool.DisposeAsync().AsTask(); stream.CleanupRelease.TrySetResult();
            await ObserveAsync(cleanup); response.Dispose(); await ObserveAsync(retirement);
        }
    }

    [Fact]
    public async Task FailedAsyncStreamCleanupRetainsOwnerWithoutDependentSyncRetry()
    {
        var error = new InvalidOperationException("controlled stream cleanup failure");
        var stream = new FailedCleanupStream(error); var content = new StreamContent(stream);
        var handler = new ImmediateHandler(content); ServiceProvider root = CreateRoot(handler);
        SdkHttpGenerationPool pool = root.GetRequiredService<SdkHttpGenerationPool>();
        using HttpClient client = pool.CreateClient(Pipeline);
        using HttpResponseMessage response = await client.GetAsync("https://controlled.invalid/", HttpCompletionOption.ResponseHeadersRead);
        Stream body = await response.Content.ReadAsStreamAsync();
        try
        {
            Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => body.DisposeAsync().AsTask()));
            Assert.Equal(1, stream.AsyncDisposals); Assert.Equal(0, stream.SyncDisposals);
            response.Dispose(); Assert.Equal(0, stream.SyncDisposals);
            Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => pool.DisposeAsync().AsTask().WaitAsync(Bound));
            Assert.Same(error, failure); Assert.Equal(0, handler.Disposals);
            Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => pool.DisposeAsync().AsTask()));
        }
        finally { response.Dispose(); await ObserveAsync(root.DisposeAsync().AsTask()); }
    }

    [Fact]
    public async Task ThrowingCancellationCallbackStillDrainsAndEveryDisposerReceivesSameFailure()
    {
        var callbackFailure = new InvalidOperationException("controlled callback failure");
        var handler = new HeldHandler(callbackFailure);
        ServiceProvider root = CreateRoot(handler);
        SdkHttpGenerationPool pool = root.GetRequiredService<SdkHttpGenerationPool>();
        using HttpClient client = pool.CreateClient(Pipeline);
        Task<HttpResponseMessage> sending = client.GetAsync("https://controlled.invalid/", HttpCompletionOption.ResponseHeadersRead);
        Task? retirement = null;
        try
        {
            await handler.Entered.Task.WaitAsync(Bound);
            retirement = pool.DisposeAsync().AsTask(); await handler.CallbackEntered.Task.WaitAsync(Bound);
            Assert.False(retirement.IsCompleted); handler.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
            Exception first = await Assert.ThrowsAnyAsync<Exception>(() => retirement.WaitAsync(Bound));
            Assert.Contains(callbackFailure, Assert.IsType<AggregateException>(first).Flatten().InnerExceptions);
            Exception repeat = await Assert.ThrowsAnyAsync<Exception>(() => pool.DisposeAsync().AsTask()); Assert.Same(first, repeat);
            Assert.Equal(1, handler.Disposals); Assert.Equal(1, handler.Content.Disposals);
        }
        finally
        {
            retirement ??= pool.DisposeAsync().AsTask(); handler.Release.TrySetResult();
            await ObserveAsync(sending); await ObserveAsync(retirement); await ObserveAsync(root.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task IndependentCleanupFailuresArePreservedAndFailedHandlerKeepsItsScope()
    {
        var one = new InvalidOperationException("one"); var two = new InvalidOperationException("two");
        var handlerOne = new ImmediateHandler(new ByteArrayContent([]), one);
        var handlerTwo = new ImmediateHandler(new ByteArrayContent([]), two);
        var services = new ServiceCollection(); services.AddLlmConnect(); services.AddScoped<ScopeMarker>();
        ScopeMarker? marker = null;
        services.AddHttpClient(Pipeline).ConfigurePrimaryHttpMessageHandler(provider => { marker = provider.GetRequiredService<ScopeMarker>(); return handlerOne; });
        services.AddHttpClient("AnthropicMessagesLlmClient").ConfigurePrimaryHttpMessageHandler(() => handlerTwo);
        ServiceProvider root = services.BuildServiceProvider(); SdkHttpGenerationPool pool = root.GetRequiredService<SdkHttpGenerationPool>();
        using HttpClient clientOne = pool.CreateClient(Pipeline); using HttpClient clientTwo = pool.CreateClient("AnthropicMessagesLlmClient");
        try
        {
            Task retirement = pool.DisposeAsync().AsTask();
            Exception first = await Assert.ThrowsAnyAsync<Exception>(() => retirement.WaitAsync(Bound));
            Assert.Equal([one, two], Assert.IsType<AggregateException>(first).Flatten().InnerExceptions.OrderBy(error => error.Message).ToArray());
            Assert.Same(first, await Assert.ThrowsAnyAsync<Exception>(() => pool.DisposeAsync().AsTask()));
            Assert.Equal(1, handlerOne.Disposals); Assert.Equal(1, handlerTwo.Disposals); Assert.Equal(0, marker!.Disposals);
        }
        finally { await ObserveAsync(root.DisposeAsync().AsTask()); }
    }

    [Fact]
    public async Task RotationRetainsIssuedClientAndResponseWhileNewClientsUseNewEntry()
    {
        var clock = new ControlledClock(); var handlers = new List<ImmediateHandler>();
        var services = new ServiceCollection(); services.AddSingleton<TimeProvider>(clock); services.AddLlmConnect();
        services.AddHttpClient(Pipeline).SetHandlerLifetime(TimeSpan.FromSeconds(2)).ConfigurePrimaryHttpMessageHandler(() =>
        { var handler = new ImmediateHandler(new ByteArrayContent([1, 2])); handlers.Add(handler); return handler; });
        await using ServiceProvider root = services.BuildServiceProvider(); SdkHttpGenerationPool pool = root.GetRequiredService<SdkHttpGenerationPool>();
        using HttpClient first = pool.CreateClient(Pipeline);
        using HttpResponseMessage response = await first.GetAsync("https://controlled.invalid/", HttpCompletionOption.ResponseHeadersRead);
        clock.Fire();
        using HttpClient second = pool.CreateClient(Pipeline);
        Assert.Equal(2, handlers.Count); Assert.Equal(0, handlers[0].Disposals);
        first.Dispose(); Assert.Equal(0, handlers[0].Disposals);
        response.Dispose(); await handlers[0].Disposed.Task.WaitAsync(Bound);
        Assert.Equal(1, handlers[0].Disposals); Assert.Equal(0, handlers[1].Disposals);
        await pool.DisposeAsync(); Assert.Equal(1, handlers[1].Disposals); Assert.All(clock.Timers, timer => Assert.Equal(1, timer.Disposals));
    }

    private static ServiceProvider CreateRoot(HttpMessageHandler handler)
    {
        var services = new ServiceCollection(); services.AddLlmConnect();
        services.AddHttpClient(Pipeline).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }
    private static async Task ObserveAsync(Task task)
    {
        try { await task.WaitAsync(Bound); }
        catch (Exception error) when (task.IsCompleted && error is not TimeoutException) { }
    }
    private sealed class ScopeMarker : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
    private sealed class TrackedContent : ByteArrayContent
    {
        public TrackedContent() : base([7]) { }
        public int Disposals { get; private set; }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
    private sealed class HeldHandler(Exception? callbackFailure = null) : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CallbackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TrackedContent Content { get; } = new();
        public CancellationToken Token { get; private set; }
        public int Disposals { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            { CallbackEntered.TrySetResult(); if (callbackFailure is not null) throw callbackFailure; });
            Entered.TrySetResult(); await Release.Task; return new(HttpStatusCode.OK) { Content = Content };
        }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
    private sealed class ImmediateHandler(HttpContent content, Exception? failure = null) : HttpMessageHandler
    {
        public int Disposals { get; private set; }
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        protected override void Dispose(bool disposing)
        { if (disposing) { Disposals++; Disposed.TrySetResult(); if (failure is not null) throw failure; } base.Dispose(disposing); }
    }
    private sealed class HeldStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AsyncDisposals { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Release.Task; buffer.Span[0] = 7; return 1; }
        public override async ValueTask DisposeAsync()
        { AsyncDisposals++; CleanupEntered.TrySetResult(); await CleanupRelease.Task; await base.DisposeAsync(); }
    }
    private sealed class FailedCleanupStream(Exception failure) : MemoryStream
    {
        public int AsyncDisposals { get; private set; }
        public int SyncDisposals { get; private set; }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2215", Justification = "This controlled fixture refuses actual cleanup and detects a forbidden dependent synchronous retry.")]
        public override ValueTask DisposeAsync() { AsyncDisposals++; return ValueTask.FromException(failure); }
        protected override void Dispose(bool disposing) { if (disposing) SyncDisposals++; base.Dispose(disposing); }
    }
    private sealed class ControlledClock : TimeProvider
    {
        public List<ControlledTimer> Timers { get; } = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { Assert.Equal(Timeout.InfiniteTimeSpan, period); var timer = new ControlledTimer(callback, state); Timers.Add(timer); return timer; }
        public void Fire() { foreach (ControlledTimer timer in Timers.ToArray()) timer.Fire(); }
    }
    private sealed class ControlledTimer(TimerCallback callback, object? state) : ITimer
    {
        public int Disposals { get; private set; }
        public void Fire() { if (Disposals == 0) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Disposals++;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
