using Amazon.Bedrock.Model;
using Amazon.BedrockRuntime.Model;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed partial class BedrockLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementFencesLateConverseResultsBeforeLinkedCancellationPropagates(bool streaming)
    {
        using var caller = new CancellationTokenSource();
        using var body = new TrackedStream();
        var runtime = new RuntimeClient { BlockCalls = true, IgnoreCancellation = true, Body = body };
        var factory = Factory(_ => runtime);
        using BedrockClientFactory.Lease<Amazon.BedrockRuntime.AmazonBedrockRuntimeClient> anchor = factory.Runtime(Aws);
        var invoker = new BedrockConverseInvoker(factory);
        Task? operation = null;
        Task? retirement = null;
        IBedrockEventStream? escaped = null;
        using var stop = new HeldCancellation();
        async Task CallAsync()
        {
            if (streaming) escaped = await invoker.ConverseStreamAsync(Aws, new(), caller.Token);
            else _ = await invoker.ConverseAsync(Aws, new(), caller.Token);
        }
        try
        {
            operation = CallAsync();
            await runtime.Started.Task.WaitAsync(Bound);
            stop.Register(anchor.Token);
            retirement = factory.DisposeAsync().AsTask();
            await stop.Started.Task.WaitAsync(Bound);
            Assert.False(runtime.OperationToken.IsCancellationRequested);
            Assert.False(retirement.IsCompleted);
            runtime.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(Bound));
            Assert.Null(escaped);
            if (streaming) Assert.True(body.Disposed);
        }
        finally
        {
            retirement ??= factory.DisposeAsync().AsTask();
            stop.Release.TrySetResult();
            runtime.Release.TrySetResult();
            try { if (operation is not null) await IgnoreCancellationAsync(operation); }
            finally
            {
                try { if (escaped is not null) await escaped.DisposeAsync(); }
                finally { anchor.Dispose(); await retirement.WaitAsync(Bound); }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementFencesLateSdkReadsBeforeLinkedCancellationPropagates(bool agentCore)
    {
        using var caller = new CancellationTokenSource();
        using var body = new IgnoringReadStream();
        var runtime = new RuntimeClient { Body = body };
        var agent = new AgentClient { Body = body };
        var factory = new BedrockClientFactory(_ => runtime, _ => throw new NotSupportedException(), _ => agent);
        using BedrockClientFactory.Lease<Amazon.BedrockRuntime.AmazonBedrockRuntimeClient> anchor = factory.Runtime(Aws);
        using var stop = new HeldCancellation();
        Task? read = null;
        Task? retirement = null;
        IBedrockEventStream? stream = null;
        async Task ReadAsync()
        {
            if (agentCore)
            {
                await foreach (var _ in new BedrockAgentCoreInvoker(factory).InvokeAsync(Aws, new(), caller.Token)) { }
            }
            else
            {
                stream = await new BedrockConverseInvoker(factory).ConverseStreamAsync(Aws, new(), caller.Token);
                await foreach (var _ in stream.ReadAsync(caller.Token)) { }
            }
        }
        try
        {
            read = ReadAsync();
            await body.Started.Task.WaitAsync(Bound);
            stop.Register(anchor.Token);
            retirement = factory.DisposeAsync().AsTask();
            await stop.Started.Task.WaitAsync(Bound);
            Assert.False(body.Token.IsCancellationRequested);
            Assert.False(retirement.IsCompleted);
            body.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Bound));
        }
        finally
        {
            retirement ??= factory.DisposeAsync().AsTask();
            stop.Release.TrySetResult();
            body.Release.TrySetResult();
            try { if (read is not null) await IgnoreCancellationAsync(read); }
            finally
            {
                try { if (stream is not null) await stream.DisposeAsync(); }
                finally
                {
                    anchor.Dispose();
                    await retirement.WaitAsync(Bound);
                    if (!agentCore) agent.Dispose();
                }
            }
        }
    }

    [Fact]
    public async Task LoggingRetirementFencesLateSuccessBeforeLinkedCancellationPropagates()
    {
        using var caller = new CancellationTokenSource();
        var guard = new BedrockLoggingGuard();
        var firstStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new HeldCancellation();
        Task? first = null;
        Task? second = null;
        Task? retirement = null;
        try
        {
            first = guard.EnsureLoggingDisabledAsync("anchor", Aws.Region!, async token =>
            { firstStarted.TrySetResult(token); await release.Task; return null; });
            CancellationToken owner = await firstStarted.Task.WaitAsync(Bound);
            second = guard.EnsureLoggingDisabledAsync("pending", Aws.Region!, async token =>
            { secondStarted.TrySetResult(token); await release.Task; return null; }, caller.Token);
            CancellationToken linked = await secondStarted.Task.WaitAsync(Bound);
            stop.Register(owner);
            retirement = guard.DisposeAsync().AsTask();
            await stop.Started.Task.WaitAsync(Bound);
            Assert.False(linked.IsCancellationRequested);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(Bound));
            Assert.False(retirement.IsCompleted);
        }
        finally
        {
            retirement ??= guard.DisposeAsync().AsTask();
            stop.Release.TrySetResult();
            release.TrySetResult();
            try { if (first is not null) await IgnoreCancellationAsync(first); }
            finally
            {
                try { if (second is not null) await IgnoreCancellationAsync(second); }
                finally { await retirement.WaitAsync(Bound); }
            }
        }
    }

    [Fact]
    public async Task CatalogRetirementFencesTheNextLookupBeforeLinkedCancellationPropagates()
    {
        using var caller = new CancellationTokenSource();
        var control = new ControlClient { BlockListing = true };
        var factory = new BedrockClientFactory(_ => throw new NotSupportedException(), _ => control, _ => throw new NotSupportedException());
        using var anchor = factory.Control(Aws);
        await using var guard = new BedrockLoggingGuard();
        var client = new BedrockLlmClient(factory, guard, new StubConverseInvoker());
        using var stop = new HeldCancellation();
        Task? listing = null;
        Task? retirement = null;
        try
        {
            listing = client.ListModelsAsync(new()
            {
                Version = "v1", Transport = LlmTransport.AwsBedrockConverse,
                Endpoint = "aws://bedrock", Credential = new LlmCredential { Aws = Aws }.Serialize()
            }, caller.Token);
            await control.Started.Task.WaitAsync(Bound);
            stop.Register(anchor.Token);
            retirement = factory.DisposeAsync().AsTask();
            await stop.Started.Task.WaitAsync(Bound);
            Assert.False(control.Token.IsCancellationRequested);
            control.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing.WaitAsync(Bound));
            Assert.Equal(0, control.FoundationLookups);
        }
        finally
        {
            retirement ??= factory.DisposeAsync().AsTask();
            stop.Release.TrySetResult();
            control.Release.TrySetResult();
            try { if (listing is not null) await IgnoreCancellationAsync(listing); }
            finally { anchor.Dispose(); await retirement.WaitAsync(Bound); }
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try { await task.WaitAsync(Bound); }
        catch (OperationCanceledException) { }
    }

    private sealed class HeldCancellation : IDisposable
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;
        internal void Register(CancellationToken token) => _registration = token.Register(() =>
        { Started.TrySetResult(); Release.Task.GetAwaiter().GetResult(); });
        public void Dispose() { Release.TrySetResult(); _registration.Dispose(); }
    }

    private sealed class IgnoringReadStream : TrackedStream
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token { get; private set; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WaitAsync(cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(WaitAsync(cancellationToken));
        private async Task<int> WaitAsync(CancellationToken token)
        { Token = token; Started.TrySetResult(); await Release.Task; return 0; }
    }
}
