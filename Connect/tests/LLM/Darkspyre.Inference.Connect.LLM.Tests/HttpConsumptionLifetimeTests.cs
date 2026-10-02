using System.Reflection;
using System.Text;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HttpConsumptionLifetimeTests
{
    private const string Name = "OpenAiResponsesLlmClient";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RawCachedBclContentRemainsExplicitlyOutsideTransparentInterception()
    {
        await using ServiceProvider root = CreateRoot("actual"); SdkHttpGenerationPool pool = root.GetRequiredService<SdkHttpGenerationPool>();
        using HttpClient client = pool.CreateClient(Name); using HttpResponseMessage response = await client.GetAsync("https://controlled.invalid/");
        Task retirement = pool.DisposeAsync().AsTask();
        try
        {
            Assert.Equal("actual", await response.Content.ReadAsStringAsync());
            Assert.Equal("actual"u8.ToArray(), await response.Content.ReadAsByteArrayAsync());
            using var destination = new MemoryStream(); await response.Content.CopyToAsync(destination); Assert.Equal("actual"u8.ToArray(), destination.ToArray());
            Stream raw = await response.Content.ReadAsStreamAsync(); Assert.IsType<MemoryStream>(raw); Assert.Equal('a', raw.ReadByte());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SdkHttpConsumption.ReadAsStringAsync(response.Content));
        }
        finally { response.Dispose(); await retirement.WaitAsync(Bound); }
    }

    [Fact]
    public async Task BufferedStreamAcquiredBeforeRetirementStillFencesLaterReads()
    {
        await using ServiceProvider root = CreateRoot("actual"); SdkHttpGenerationPool pool = root.GetRequiredService<SdkHttpGenerationPool>();
        using HttpClient client = pool.CreateClient(Name); using HttpResponseMessage response = await client.GetAsync("https://controlled.invalid/");
        await using Stream stream = await SdkHttpConsumption.ReadAsStreamAsync(response.Content); Assert.IsType<SdkHttpBodyStream>(stream);
        Task retirement = pool.DisposeAsync().AsTask();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadAsync(new byte[1]).AsTask()); }
        finally { await stream.DisposeAsync(); response.Dispose(); await retirement.WaitAsync(Bound); }
    }

    [Fact]
    public async Task FinalMappedPublicationRemainsAdmittedAfterResponseCleanup()
    {
        await using ServiceProvider root = CreateRoot("actual"); SdkHttpGenerationPool pool = root.GetRequiredService<SdkHttpGenerationPool>();
        using HttpClient client = pool.CreateClient(Name);
        using var operation = SdkHttpConsumption.Enter(client, default);
        using HttpResponseMessage response = await client.GetAsync("https://controlled.invalid/");
        string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, operation.Token); response.Dispose();
        Task retirement = pool.DisposeAsync().AsTask();
        try { Assert.False(retirement.IsCompleted); Assert.ThrowsAny<OperationCanceledException>(() => operation.Publish(body)); }
        finally { operation.Dispose(); await retirement.WaitAsync(Bound); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedCopyDrainsIgnoringBorrowedDestinationAndPreservesFailureOrActualBytes(bool fail)
    {
        await using ServiceProvider root = CreateRoot("actual"); SdkHttpGenerationPool pool = root.GetRequiredService<SdkHttpGenerationPool>();
        using HttpClient client = pool.CreateClient(Name); using HttpResponseMessage response = await client.GetAsync("https://controlled.invalid/");
        var failure = new IOException("controlled copy failure"); var destination = new IgnoringDestination(fail ? failure : null);
        Task copy = SdkHttpConsumption.CopyToAsync(response.Content, destination);
        Task? retirement = null;
        try
        {
            await destination.Entered.Task.WaitAsync(Bound); retirement = pool.DisposeAsync().AsTask(); response.Dispose();
            Assert.False(retirement.IsCompleted); Assert.Equal(0, destination.Disposals);
            destination.Release.TrySetResult();
            if (fail) Assert.Same(failure, (await Assert.ThrowsAsync<HttpRequestException>(() => copy.WaitAsync(Bound))).InnerException);
            else { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy.WaitAsync(Bound)); Assert.Equal("actual"u8.ToArray(), destination.ToArray()); }
            await retirement.WaitAsync(Bound); Assert.Equal(0, destination.Disposals);
        }
        finally
        {
            destination.Release.TrySetResult(); try { await copy.WaitAsync(Bound); } catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
            response.Dispose(); if (retirement is not null) await retirement.WaitAsync(Bound); destination.Dispose();
        }
    }

    [Fact]
    public async Task ActualResponsesBufferedSseCannotPublishAfterSynchronousOwnerFence()
    {
        const string body = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"first\"}\n\n"
            + "data: {\"type\":\"response.output_text.delta\",\"delta\":\"second\"}\n\n";
        await using ServiceProvider root = CreateRoot(body); SdkHttpGenerationPool pool = root.GetRequiredService<SdkHttpGenerationPool>();
        var adapter = root.GetRequiredService<OpenAiResponsesLlmClient>();
        await using IAsyncEnumerator<LlmStreamEvent> iterator = adapter.StreamAsync(new LlmConnection { Version = "controlled", Endpoint = "https://controlled.invalid", Transport = LlmTransport.OpenAiResponses },
            new LlmChatRequest { ModelId = "gpt-4.1", Messages = [new("user", "actual")] }).GetAsyncEnumerator();
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? retirement = null;
        CancellationTokenRegistration blocker = default;
        try
        {
            Assert.True(await iterator.MoveNextAsync()); Assert.Equal("first", Assert.IsType<LlmTextDeltaEvent>(iterator.Current).Delta);
            blocker = OwnedStop(pool).Token.Register(() => { entered.TrySetResult(); release.Wait(Bound); });
            retirement = pool.DisposeAsync().AsTask(); await entered.Task.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => iterator.MoveNextAsync().AsTask().WaitAsync(Bound));
        }
        finally { release.Set(); blocker.Dispose(); try { await iterator.DisposeAsync(); } finally { if (retirement is not null) await retirement.WaitAsync(Bound); } }
    }

    [Fact]
    public async Task BorrowedClientAndContentKeepOrdinaryBodyAndDisposalMeaning()
    {
        using var client = new HttpClient(new Handler("actual"));
        using var operation = SdkHttpConsumption.Enter(client, default);
        using HttpResponseMessage response = await client.GetAsync("https://controlled.invalid/");
        Assert.Equal("actual", await SdkHttpConsumption.ReadAsStringAsync(response.Content));
        Assert.Equal("actual"u8.ToArray(), await SdkHttpConsumption.ReadAsByteArrayAsync(response.Content));
        await using Stream stream = await SdkHttpConsumption.ReadAsStreamAsync(response.Content); Assert.IsType<MemoryStream>(stream);
        operation.Dispose(); Assert.Equal('a', stream.ReadByte()); Assert.Equal("actual", operation.Publish("actual"));
    }

    private static ServiceProvider CreateRoot(string body)
    {
        var services = new ServiceCollection(); services.AddLlmConnect();
        services.AddHttpClient(Name).ConfigurePrimaryHttpMessageHandler(() => new Handler(body)); return services.BuildServiceProvider();
    }
    private static CancellationTokenSource OwnedStop(SdkHttpGenerationPool pool)
    {
        object lifetime = typeof(SdkHttpGenerationPool).GetField("_lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!;
        return (CancellationTokenSource)typeof(LlmOperationLifetime).GetField("_stop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lifetime)!;
    }
    private sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
    }
    private sealed class IgnoringDestination(Exception? failure) : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposals { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Release.Task; if (failure is not null) throw failure; Write(buffer.Span); }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
}
