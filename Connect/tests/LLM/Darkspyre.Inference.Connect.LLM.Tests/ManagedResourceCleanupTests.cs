using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ManagedResourceCleanupTests
{
    [Fact]
    public void CachedCleanupFailureRetainsTheActualRefusedStreamAndResponseOnlyUntilReleased()
    {
        object?[] retained = CreateRetainedFailure();
        WeakReference[] roots = AssertRetainedThenRelease(retained);
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 30; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.All(roots, root => Assert.False(root.IsAlive));
        GC.KeepAlive(retained);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static object?[] CreateRetainedFailure() => [CreateRetainedFailureAsync().GetAwaiter().GetResult()];

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<RetainedFailure> CreateRetainedFailureAsync()
    {
        var resources = new ControlledSessionResources();
        await resources.RegisterAsync("source.txt", "text/plain", "source");
        using var destination = new LlmResourceMaterializationTests.ResourceDestination();
        var stream = new RefusedBodyStream(new OperationCanceledException("actual raw stream cleanup refusal"));
        using var handler = new StreamingDestination(destination, stream);
        using var http = new HttpClient(handler);
        var connection = LlmResourceMaterializationTests.Connection() with { CapabilityOverrides = new() { SupportsStreaming = true } };
        var client = new XaiResponsesLlmClient(http);
        var session = await ManagedAgentSession.CreateAsync(connection,
            new() { ModelId = "grok-4", CallerServices = new() { Resources = resources } }, [], client,
            (request, token) => client.StreamAsync(connection, request, token),
            model => ManagedResourceMaterializationTests.ManagedCapabilities(client, model));
        Exception failure;
        try
        {
            failure = await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => ManagedResourceMaterializationTests.Read(
                session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("inspect")] })));
            Assert.Same(failure, await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => session.DisposeAsync().AsTask()));
        }
        finally { await Drain(session.DisposeAsync().AsTask()); }
        Assert.NotNull(handler.Response);
        return new(session, failure, [new(stream), handler.Response]);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference[] AssertRetainedThenRelease(object?[] retained)
    {
        var actual = Assert.IsType<RetainedFailure>(retained[0]);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.All(actual.Roots, root => Assert.True(root.IsAlive));
        GC.KeepAlive(actual.Session);
        GC.KeepAlive(actual.Failure);
        retained[0] = null;
        return actual.Roots;
    }

    private sealed record RetainedFailure(ILlmAgentSession Session, Exception Failure, WeakReference[] Roots);

    [Fact]
    public async Task RefusedRawBodyStreamCleanupNeverRetriesThroughDependentResponseContent()
    {
        var resources = new ControlledSessionResources();
        await resources.RegisterAsync("source.txt", "text/plain", "source");
        var cleanup = new OperationCanceledException("actual raw body stream cleanup refusal");
        using var destination = new LlmResourceMaterializationTests.ResourceDestination();
        var stream = new RefusedBodyStream(cleanup);
        using var handler = new StreamingDestination(destination, stream);
        using var http = new HttpClient(handler);
        var connection = LlmResourceMaterializationTests.Connection() with { CapabilityOverrides = new() { SupportsStreaming = true } };
        var client = new XaiResponsesLlmClient(http);
        var session = await ManagedAgentSession.CreateAsync(connection,
            new() { ModelId = "grok-4", CallerServices = new() { Resources = resources } }, [], client,
            (request, token) => client.StreamAsync(connection, request, token),
            model => ManagedResourceMaterializationTests.ManagedCapabilities(client, model));
        Task? retirement = null;
        try
        {
            var failure = await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => ManagedResourceMaterializationTests.Read(
                session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("inspect")] })));
            Assert.Equal(InferenceResourceOperationPhase.Cleanup, failure.Failure.Phase);
            Assert.Same(cleanup, failure.InnerException);
            retirement = session.DisposeAsync().AsTask();
            Assert.Same(failure, await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => retirement));
            Assert.Equal(1, stream.AsyncCleanupAttempts);
            Assert.Equal(0, stream.SyncCleanupAttempts);
            Assert.Equal(1, resources.DisposedReads);
        }
        finally { retirement ??= session.DisposeAsync().AsTask(); await Drain(retirement); }
    }

    [Fact]
    public async Task HealthyHandleCleanupStartingRetirementCannotEraseFinalResourceCancellationFacts()
    {
        var resources = new ControlledSessionResources();
        await resources.RegisterAsync("source.txt", "text/plain", "source");
        using var destination = new LlmResourceMaterializationTests.ResourceDestination();
        using var http = new HttpClient(destination);
        var client = new XaiResponsesLlmClient(http);
        var session = await ManagedAgentSession.CreateAsync(LlmResourceMaterializationTests.Connection(),
            new() { ModelId = "grok-4", CallerServices = new() { Resources = resources } }, [], client,
            (request, token) => client.StreamAsync(LlmResourceMaterializationTests.Connection(), request, token),
            model => ManagedResourceMaterializationTests.ManagedCapabilities(client, model));
        Task? retirement = null;
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken actualReadToken = default;
        CancellationTokenRegistration registration = default;
        resources.BeforeRead = token => { actualReadToken = token; return Task.CompletedTask; };
        resources.BeforeReadCleanup = () =>
        {
            object run = typeof(ManagedAgentSession).GetField("_run", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(session)!;
            var owner = (CancellationTokenSource)run.GetType().GetProperty("Cancellation")!.GetValue(run)!;
            registration = owner.Token.Register(() => { callbackEntered.TrySetResult(); releaseCallback.Task.GetAwaiter().GetResult(); });
            retirement = session.DisposeAsync().AsTask();
            callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Assert.False(actualReadToken.IsCancellationRequested);
        };
        try
        {
            var failure = await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => ManagedResourceMaterializationTests.Read(
                session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("inspect")] })));
            Assert.Equal(InferenceResourceOperationPhase.Materialize, failure.Failure.Phase);
            Assert.NotNull(retirement);
            releaseCallback.TrySetResult();
            await retirement;
            Assert.Equal(1, resources.DisposedReads);
            Assert.Equal(1, destination.ResponseCleanupAttempts);
            Assert.Single(destination.Uploads);
        }
        finally
        {
            retirement ??= session.DisposeAsync().AsTask();
            releaseCallback.TrySetResult();
            await Drain(retirement);
            registration.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualResponseCleanupRefusalIsAnOwnedResourceFailure(bool malformedBody)
    {
        var resources = new ControlledSessionResources();
        await resources.RegisterAsync("source.txt", "text/plain", "source");
        var cleanup = new InvalidOperationException("actual response disposal refused");
        using var destination = new LlmResourceMaterializationTests.ResourceDestination { ResponseCleanupFailure = cleanup };
        if (malformedBody) destination.Responses.Enqueue("not-json");
        using var http = new HttpClient(destination);
        var client = new XaiResponsesLlmClient(http);
        var session = await ManagedAgentSession.CreateAsync(LlmResourceMaterializationTests.Connection(),
            new() { ModelId = "grok-4", CallerServices = new() { Resources = resources } }, [], client,
            (request, token) => client.StreamAsync(LlmResourceMaterializationTests.Connection(), request, token),
            model => ManagedResourceMaterializationTests.ManagedCapabilities(client, model));
        Task? retirement = null;
        try
        {
            Exception readerError = await Assert.ThrowsAnyAsync<Exception>(() => ManagedResourceMaterializationTests.Read(
                session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("inspect")] })));
            var carrier = Assert.Single(Leaves(readerError).OfType<InferenceResourceOperationException>(),
                failure => failure.Failure.Phase == (InferenceResourceOperationPhase)9);
            Assert.Same(cleanup, carrier.InnerException);
            Assert.Null(carrier.Failure.Reference);
            Assert.Equal(malformedBody ? 2 : 1, Leaves(readerError).Count());
            retirement = session.DisposeAsync().AsTask();
            Assert.Same(readerError, await Assert.ThrowsAnyAsync<Exception>(() => retirement));
            Assert.Equal(1, destination.ResponseCleanupAttempts);
            Assert.Equal(1, resources.DisposedReads);
        }
        finally { retirement ??= session.DisposeAsync().AsTask(); await Drain(retirement); }
    }

    [Fact]
    public async Task ExplicitFaultCompletionKeepsCleanupCancellationInFaultAccounting()
    {
        var original = new OperationCanceledException("actual cleanup refusal");
        Task scheduled = Task.Run(async () => { await Task.Yield(); throw original; });
        Assert.Same(original, await Assert.ThrowsAsync<OperationCanceledException>(() => scheduled));
        Assert.True(scheduled.IsCanceled);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetException(original);
        Assert.Same(original, await Assert.ThrowsAsync<OperationCanceledException>(() => completion.Task));
        Assert.True(completion.Task.IsFaulted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualHandleCleanupCancellationPreservesSameReaderAndRetirementFailure(bool cancelRead)
    {
        var resources = new ControlledSessionResources();
        InferenceResourceReference reference = await resources.RegisterAsync("source.txt", "text/plain", "source");
        var cleanup = new OperationCanceledException("actual handle cleanup refused");
        resources.ReadCleanupFailure = cleanup;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancelRead) resources.BeforeRead = async token =>
        {
            using var registration = token.Register(() => canceled.TrySetResult());
            entered.TrySetResult();
            await release.Task;
        };
        using var destination = new LlmResourceMaterializationTests.ResourceDestination();
        using var http = new HttpClient(destination);
        var client = new XaiResponsesLlmClient(http);
        var session = await ManagedAgentSession.CreateAsync(LlmResourceMaterializationTests.Connection(),
            new() { ModelId = "grok-4", CallerServices = new() { Resources = resources } }, [], client,
            (request, token) => client.StreamAsync(LlmResourceMaterializationTests.Connection(), request, token),
            model => ManagedResourceMaterializationTests.ManagedCapabilities(client, model));
        Task<List<LlmAgentEvent>> turn = ManagedResourceMaterializationTests.Read(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("inspect")] }));
        Task? retirement = null;
        try
        {
            if (cancelRead)
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                retirement = session.DisposeAsync().AsTask();
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(retirement.IsCompleted);
                release.TrySetResult();
            }
            Exception readerError = await Assert.ThrowsAnyAsync<Exception>(() => turn);
            InferenceResourceOperationCanceledException cleanupCarrier = Assert.Single(Leaves(readerError)
                .OfType<InferenceResourceOperationCanceledException>(), error => error.Failure.Phase == (InferenceResourceOperationPhase)9);
            Assert.Same(cleanup, cleanupCarrier.InnerException);
            Assert.Equal(reference, cleanupCarrier.Failure.Reference);
            Assert.Equal(cancelRead ? 2 : 1, Leaves(readerError).Count());
            retirement ??= session.DisposeAsync().AsTask();
            Assert.Same(readerError, await Assert.ThrowsAnyAsync<Exception>(() => retirement));
            Assert.Same(readerError, await Assert.ThrowsAnyAsync<Exception>(() => session.DisposeAsync().AsTask()));
            Assert.Equal(1, resources.ReadCleanupAttempts);
            Assert.Equal(0, resources.DisposedReads);
            Assert.Equal(0, resources.DisposeCalls);
        }
        finally
        {
            retirement ??= session.DisposeAsync().AsTask();
            release.TrySetResult();
            await Drain(turn);
            await Drain(retirement);
        }
    }

    private static IEnumerable<Exception> Leaves(Exception error) => error is AggregateException aggregate
        ? aggregate.InnerExceptions.SelectMany(Leaves) : [error];
    private static async Task Drain(Task task)
    { try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) when (task.IsCompleted) { } }

    private sealed class RefusedBodyStream(Exception failure) : MemoryStream(System.Text.Encoding.UTF8.GetBytes(
        "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"response-one\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"done\"}]}]}}\n\n"))
    {
        internal int AsyncCleanupAttempts { get; private set; }
        internal int SyncCleanupAttempts { get; private set; }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2215", Justification = "The controlled cleanup refusal deliberately leaves the resource unreleased.")]
        public override ValueTask DisposeAsync() { AsyncCleanupAttempts++; return ValueTask.FromException(failure); }
        protected override void Dispose(bool disposing) { if (disposing) SyncCleanupAttempts++; base.Dispose(disposing); }
    }
    private sealed class StreamingDestination(HttpMessageHandler destination, Stream body) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _destination = new(destination, disposeHandler: false);
        internal WeakReference? Response { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            HttpResponseMessage response = await _destination.SendAsync(request, token);
            if (request.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
            { response.Content.Dispose(); response.Content = new StreamContent(body); Response = new(response); }
            return response;
        }
        protected override void Dispose(bool disposing) { if (disposing) _destination.Dispose(); base.Dispose(disposing); }
    }
}
