using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class EmbeddingLifetimeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RetirementCancelsEveryActiveRequestAndFencesNewWork()
    {
        using var source = new Source();
        using var client = new Client();
        await using var facade = new LlmEmbeddingProviderService(source, [client]);
        Task<LlmEmbeddingResult> first = facade.EmbedAsync("connection", Request());
        Task<LlmEmbeddingResult> second = facade.EmbedAsync("connection", Request());
        await client.Started.Task.WaitAsync(Bound);

        await facade.DisposeAsync().AsTask().WaitAsync(Bound);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(2, client.Tokens.Count);
        Assert.All(client.Tokens, token => Assert.True(token.IsCancellationRequested));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.EmbedAsync("connection", Request()));
        Assert.Equal(2, source.Reads);
        Assert.False(source.Disposed);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task RetirementWaitsForIgnoringProviderAndRejectsItsLateResult()
    {
        using var source = new Source();
        using var client = new Client { IgnoreCancellation = true };
        await using var facade = new LlmEmbeddingProviderService(source, [client]);
        Task<LlmEmbeddingResult> request = facade.EmbedAsync("connection", Request());
        try
        {
            await client.Started.Task.WaitAsync(Bound);
            Task retirement = facade.DisposeAsync().AsTask();
            await client.Stopped.Task.WaitAsync(Bound);
            Assert.Same(retirement, facade.DisposeAsync().AsTask());
            Assert.False(retirement.IsCompleted);
            client.Release.TrySetResult();

            await retirement.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.False(client.Disposed);
        }
        finally { client.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementDuringConnectionResolutionNeverStartsProviderWork(bool ignoreCancellation)
    {
        using var source = new Source { BlockResolution = true, IgnoreCancellation = ignoreCancellation };
        using var client = new Client();
        await using var facade = new LlmEmbeddingProviderService(source, [client]);
        Task<LlmEmbeddingResult> request = facade.EmbedAsync("connection", Request());
        try
        {
            await source.Started.Task.WaitAsync(Bound);
            Task retirement = facade.DisposeAsync().AsTask();
            await source.Stopped.Task.WaitAsync(Bound);
            if (ignoreCancellation) Assert.False(retirement.IsCompleted);
            source.Release.TrySetResult();

            await retirement.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.Empty(client.Tokens);
            Assert.False(source.Disposed);
        }
        finally { source.Release.TrySetResult(); }
    }

    [Fact]
    public async Task CallerCancellationDoesNotRetireOtherRequestsOrTheFacade()
    {
        using var source = new Source();
        using var client = new Client();
        await using var facade = new LlmEmbeddingProviderService(source, [client]);
        using var caller = new CancellationTokenSource();
        Task<LlmEmbeddingResult> abandoned = facade.EmbedAsync("connection", Request(), caller.Token);
        Task<LlmEmbeddingResult> remaining = facade.EmbedAsync("connection", Request());
        try
        {
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
            Assert.False(remaining.IsCompleted);
            Assert.Equal(1, client.Tokens.Count(token => token.IsCancellationRequested));
            client.Release.TrySetResult();

            Assert.Single((await remaining.WaitAsync(Bound)).Vectors);
            Assert.Single((await facade.EmbedAsync("connection", Request())).Vectors);
        }
        finally { client.Release.TrySetResult(); }
    }

    [Fact]
    public async Task ProviderFailureReleasesTheOperationWithoutFailingRetirement()
    {
        using var source = new Source();
        using var client = new Client { Fail = true };
        await using var facade = new LlmEmbeddingProviderService(source, [client]);
        client.Release.TrySetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.EmbedAsync("connection", Request()));
        await facade.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task AsyncScopeDisposalCancelsAndDrainsActiveEmbeddingWork()
    {
        using var source = new Source();
        using var client = new Client();
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConnectionSource>(source);
        services.AddSingleton<ILlmEmbeddingClient>(client);
        services.AddScoped<LlmEmbeddingProviderService>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        AsyncServiceScope scope = provider.CreateAsyncScope();
        LlmEmbeddingProviderService facade = scope.ServiceProvider.GetRequiredService<LlmEmbeddingProviderService>();
        Task<LlmEmbeddingResult> request = facade.EmbedAsync("connection", Request());

        await scope.DisposeAsync().AsTask().WaitAsync(Bound);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(Assert.Single(client.Tokens).IsCancellationRequested);
        Assert.False(source.Disposed);
        Assert.False(client.Disposed);
    }

    [Fact]
    public void ScopeDisposalRetiresFacadeWithoutDisposingBorrowedInstances()
    {
        using var source = new Source();
        using var client = new Client();
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConnectionSource>(source);
        services.AddSingleton<ILlmEmbeddingClient>(client);
        services.AddScoped<LlmEmbeddingProviderService>();
        using ServiceProvider provider = services.BuildServiceProvider();
        LlmEmbeddingProviderService facade;
        using (IServiceScope scope = provider.CreateScope())
        {
            facade = scope.ServiceProvider.GetRequiredService<LlmEmbeddingProviderService>();
        }

        Assert.Throws<ObjectDisposedException>(() => facade.EmbedAsync("connection", Request()).GetAwaiter().GetResult());
        Assert.Equal(0, source.Reads);
        Assert.False(source.Disposed);
        Assert.False(client.Disposed);
        facade.Dispose();
    }

    private static LlmEmbeddingRequest Request() => new() { ModelId = "embedding-model", Inputs = ["input"] };

    private sealed class Source : ILlmConnectionSource, IDisposable
    {
        internal bool BlockResolution { get; init; }
        internal bool IgnoreCancellation { get; init; }
        internal bool Disposed { get; private set; }
        internal int Reads { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Reads++;
            using CancellationTokenRegistration registration = cancellationToken.Register(() => Stopped.TrySetResult());
            Started.TrySetResult();
            if (BlockResolution)
            {
                if (IgnoreCancellation) await Release.Task;
                else await Release.Task.WaitAsync(cancellationToken);
            }
            return new() { Version = "v1", Transport = LlmTransport.OpenAiResponses, Endpoint = "https://provider.test/v1" };
        }

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Dispose() => Disposed = true;
    }

    private sealed class Client : ILlmEmbeddingClient, IDisposable
    {
        internal bool IgnoreCancellation { get; init; }
        internal bool Fail { get; init; }
        internal bool Disposed { get; private set; }
        internal ConcurrentQueue<CancellationToken> Tokens { get; } = new();
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Supports(LlmTransport transport) => transport == LlmTransport.OpenAiResponses;

        public async Task<LlmEmbeddingResult> EmbedAsync(LlmConnection connection, LlmEmbeddingRequest request,
            CancellationToken cancellationToken = default)
        {
            Tokens.Enqueue(cancellationToken);
            using CancellationTokenRegistration registration = cancellationToken.Register(() => Stopped.TrySetResult());
            Started.TrySetResult();
            if (IgnoreCancellation) await Release.Task;
            else await Release.Task.WaitAsync(cancellationToken);
            if (Fail) throw new InvalidOperationException("controlled embedding failure");
            return new() { Vectors = [new float[] { 1 }], Diagnostics = new(200, null, null) };
        }

        public void Dispose() => Disposed = true;
    }
}
