using System.Net;
using System.Text;
using Amazon;
using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Amazon.BedrockAgentCore;
using Amazon.BedrockAgentCore.Model;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Amazon.Runtime.Identity;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockLifetimeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly LlmAwsCredentials Aws = new()
    {
        Region = "us-east-1",
        AccessKeyId = "fixture-key",
        SecretAccessKey = "fixture-secret",
        SessionToken = "fixture-session"
    };

    [Fact]
    public async Task ConcurrentAcquisitionCreatesOnlyOneClient()
    {
        int created = 0;
        await using var factory = Factory(_ =>
        {
            Interlocked.Increment(ref created);
            return new RuntimeClient();
        });
        Task<AmazonBedrockRuntimeClient>[] tasks = Enumerable.Range(0, 24).Select(_ => Task.Run(() =>
        {
            using BedrockClientFactory.Lease<AmazonBedrockRuntimeClient> lease = factory.Runtime(Aws);
            return lease.Client;
        })).ToArray();
        AmazonBedrockRuntimeClient[] clients = await Task.WhenAll(tasks).WaitAsync(Bound);

        Assert.Equal(1, created);
        Assert.All(clients, client => Assert.Same(clients[0], client));
        await factory.DisposeAsync();
        Assert.Equal(1, ((RuntimeClient)clients[0]).Disposals);
    }

    [Theory]
    [InlineData("region")]
    [InlineData("profile")]
    [InlineData("key")]
    [InlineData("secret")]
    [InlineData("session")]
    public async Task EveryCredentialFieldSeparatesClientAndLoggingScopes(string field)
    {
        LlmAwsCredentials changed = field switch
        {
            "region" => Aws with { Region = "us-west-2" },
            "profile" => Aws with { Profile = "another-profile" },
            "key" => Aws with { AccessKeyId = "another-key" },
            "secret" => Aws with { SecretAccessKey = "another-secret" },
            _ => Aws with { SessionToken = "another-session" }
        };
        await using var factory = Factory(_ => new RuntimeClient());
        using BedrockClientFactory.Lease<AmazonBedrockRuntimeClient> first = factory.Runtime(Aws);
        using BedrockClientFactory.Lease<AmazonBedrockRuntimeClient> second = factory.Runtime(changed);

        Assert.NotSame(first.Client, second.Client);
        Assert.NotEqual(BedrockClientFactory.ScopeKey(Aws), BedrockClientFactory.ScopeKey(changed));
        string key = BedrockClientFactory.ScopeKey(Aws);
        Assert.Equal(64, key.Length);
        Assert.DoesNotContain(Aws.AccessKeyId!, key);
        Assert.DoesNotContain(Aws.SecretAccessKey!, key);
        Assert.DoesNotContain(Aws.SessionToken!, key);
    }

    [Fact]
    public void StructuredScopeEncodingPreservesBoundariesAndCredentialCase()
    {
        var first = Aws with { Profile = "a|b", AccessKeyId = "c" };
        var second = Aws with { Profile = "a", AccessKeyId = "b|c" };
        Assert.NotEqual(BedrockClientFactory.ScopeKey(first), BedrockClientFactory.ScopeKey(second));
        Assert.NotEqual(BedrockClientFactory.ScopeKey(Aws), BedrockClientFactory.ScopeKey(Aws with { SecretAccessKey = "FIXTURE-SECRET" }));
    }

    [Fact]
    public async Task AllClientFamiliesStayAliveUntilTheirLeasesLeave()
    {
        var runtime = new RuntimeClient();
        var control = new ControlClient();
        var agent = new AgentClient();
        await using var factory = new BedrockClientFactory(_ => runtime, _ => control, _ => agent);
        using BedrockClientFactory.Lease<AmazonBedrockRuntimeClient> r = factory.Runtime(Aws);
        using BedrockClientFactory.Lease<AmazonBedrockClient> c = factory.Control(Aws);
        using BedrockClientFactory.Lease<AmazonBedrockAgentCoreClient> a = factory.AgentCore(Aws);
        Task retirement = factory.DisposeAsync().AsTask();

        Assert.Same(retirement, factory.DisposeAsync().AsTask());
        Assert.True(r.Token.IsCancellationRequested);
        Assert.True(c.Token.IsCancellationRequested);
        Assert.True(a.Token.IsCancellationRequested);
        Assert.Equal(0, runtime.Disposals + control.Disposals + agent.Disposals);
        Assert.Throws<ObjectDisposedException>(() => factory.Runtime(Aws));
        Assert.Throws<ObjectDisposedException>(() => factory.Control(Aws));
        Assert.Throws<ObjectDisposedException>(() => factory.AgentCore(Aws));
        r.Dispose();
        c.Dispose();
        Assert.False(retirement.IsCompleted);
        a.Dispose();

        await retirement.WaitAsync(Bound);
        Assert.Equal(1, runtime.Disposals);
        Assert.Equal(1, control.Disposals);
        Assert.Equal(1, agent.Disposals);
        Assert.Throws<ObjectDisposedException>(() => r.Client);
    }

    [Fact]
    public async Task AResourceSharedByCacheEntriesIsDisposedOnlyOnce()
    {
        var runtime = new RuntimeClient();
        await using var factory = Factory(_ => runtime);
        factory.Runtime(Aws).Dispose();
        factory.Runtime(Aws with { Region = "us-west-2" }).Dispose();

        await factory.DisposeAsync();

        Assert.Equal(1, runtime.Disposals);
    }

    [Fact]
    public async Task FailedCreationIsRemovedAndCanBeRetried()
    {
        int attempts = 0;
        await using var factory = Factory(_ => ++attempts == 1
            ? throw new InvalidOperationException("controlled creation failure") : new RuntimeClient());
        Assert.Throws<InvalidOperationException>(() => factory.Runtime(Aws));
        using BedrockClientFactory.Lease<AmazonBedrockRuntimeClient> lease = factory.Runtime(Aws);
        Assert.Equal(2, attempts);
        Assert.IsType<RuntimeClient>(lease.Client);
    }

    [Fact]
    public async Task OneDisposalFailureDoesNotSkipOtherClients()
    {
        var runtime = new RuntimeClient { FailDisposal = true };
        var control = new ControlClient();
        var agent = new AgentClient();
        var factory = new BedrockClientFactory(_ => runtime, _ => control, _ => agent);
        factory.Runtime(Aws).Dispose();
        factory.Control(Aws).Dispose();
        factory.AgentCore(Aws).Dispose();

        Task retirement = factory.DisposeAsync().AsTask();

        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => retirement.WaitAsync(Bound));
        Assert.IsType<InvalidOperationException>(Assert.Single(error.InnerExceptions));
        Assert.Equal(1, runtime.Disposals);
        Assert.Equal(1, control.Disposals);
        Assert.Equal(1, agent.Disposals);
        Assert.Same(retirement, factory.DisposeAsync().AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonStreamingCallCarriesOwnerCancellationAndRejectsAnIgnoringLateResult(bool ignore)
    {
        var runtime = new RuntimeClient { BlockCalls = true, IgnoreCancellation = ignore };
        await using var factory = Factory(_ => runtime);
        var invoker = new BedrockConverseInvoker(factory);
        Task<ConverseResponse> call = invoker.ConverseAsync(Aws, new(), CancellationToken.None);
        try
        {
            await runtime.Started.Task.WaitAsync(Bound);
            Task retirement = factory.DisposeAsync().AsTask();
            Assert.True(runtime.OperationToken.IsCancellationRequested);
            if (ignore)
            {
                await runtime.Stopped.Task.WaitAsync(Bound);
                Assert.False(retirement.IsCompleted);
                Assert.Equal(0, runtime.Disposals);
            }
            runtime.Release.TrySetResult();

            await retirement.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            Assert.Equal(1, runtime.Disposals);
        }
        finally { runtime.Release.TrySetResult(); }
    }

    [Fact]
    public async Task StreamOpeningThatReturnsAfterRetirementDisposesItsResponse()
    {
        using var body = new TrackedStream();
        var runtime = new RuntimeClient { BlockCalls = true, IgnoreCancellation = true, Body = body };
        await using var factory = Factory(_ => runtime);
        var invoker = new BedrockConverseInvoker(factory);
        Task<IBedrockEventStream> call = invoker.ConverseStreamAsync(Aws, new(), CancellationToken.None);
        try
        {
            await runtime.Started.Task.WaitAsync(Bound);
            Task retirement = factory.DisposeAsync().AsTask();
            await runtime.Stopped.Task.WaitAsync(Bound);
            runtime.Release.TrySetResult();

            await retirement.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            Assert.True(body.Disposed);
            Assert.Equal(1, runtime.Disposals);
        }
        finally { runtime.Release.TrySetResult(); }
    }

    [Fact]
    public async Task ResponseDisposalFailureRemainsVisibleWhenItsFactoryRetires()
    {
        var body = new TrackedStream { FailDisposal = true };
        var runtime = new RuntimeClient { Body = body };
        var factory = Factory(_ => runtime);
        IBedrockEventStream stream = await new BedrockConverseInvoker(factory).ConverseStreamAsync(Aws, new(), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.DisposeAsync().AsTask());

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => factory.DisposeAsync().AsTask().WaitAsync(Bound));

        Assert.IsType<InvalidOperationException>(Assert.Single(failure.InnerExceptions));
        Assert.True(body.Disposed);
        Assert.Equal(1, runtime.Disposals);
    }

    [Fact]
    public async Task OpenStreamOwnsItsFactoryLeaseUntilResponseDisposal()
    {
        using var body = new TrackedStream();
        var runtime = new RuntimeClient { Body = body };
        await using var factory = Factory(_ => runtime);
        var invoker = new BedrockConverseInvoker(factory);
        await using IBedrockEventStream stream = await invoker.ConverseStreamAsync(Aws, new(), CancellationToken.None);
        Task retirement = factory.DisposeAsync().AsTask();
        Assert.False(retirement.IsCompleted);
        Assert.False(body.Disposed);
        Assert.Equal(0, runtime.Disposals);

        await stream.DisposeAsync();
        await retirement.WaitAsync(Bound);

        Assert.True(body.Disposed);
        Assert.Equal(1, runtime.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await foreach (var _ in stream.ReadAsync(CancellationToken.None)) { }
        });
    }

    [Fact]
    public async Task ControlRetirementRejectsLateProfilesWithoutStartingFoundationLookup()
    {
        var control = new ControlClient { BlockListing = true };
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(), _ => control, _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var client = new BedrockLlmClient(factory, guard, new StubConverseInvoker());
        Task<IReadOnlyList<LlmModel>> listing = client.ListModelsAsync(new()
        {
            Version = "v1",
            Transport = LlmTransport.AwsBedrockConverse,
            Endpoint = "aws://bedrock",
            Credential = new LlmCredential { Aws = Aws }.Serialize()
        });
        try
        {
            await control.Started.Task.WaitAsync(Bound);
            Task retirement = factory.DisposeAsync().AsTask();
            Assert.True(control.Token.IsCancellationRequested);
            Assert.False(retirement.IsCompleted);
            control.Release.TrySetResult();

            await retirement.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing);
            Assert.Equal(0, control.FoundationLookups);
            Assert.Equal(1, control.Disposals);
        }
        finally { control.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentCoreInvokerCancelsItsOpeningAndDisposesLateResponses(bool ignore)
    {
        using var body = new TrackedStream();
        var agent = new AgentClient { BlockCalls = true, IgnoreCancellation = ignore, Body = body };
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(), _ => throw new NotSupportedException(), _ => agent);
        var invoker = new BedrockAgentCoreInvoker(factory);
        async Task ReadAsync()
        {
            await foreach (var _ in invoker.InvokeAsync(Aws, new(), CancellationToken.None)) { }
        }
        Task invocation = ReadAsync();
        try
        {
            await agent.Started.Task.WaitAsync(Bound);
            Task retirement = factory.DisposeAsync().AsTask();
            Assert.True(agent.Token.IsCancellationRequested);
            agent.Release.TrySetResult();

            await retirement.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
            if (ignore) Assert.True(body.Disposed);
            Assert.Equal(1, agent.Disposals);
        }
        finally { agent.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementDuringSdkEventReadingSurfacesCancellationAndDisposesTheResponse(bool agentCore)
    {
        using var body = new BlockingStream();
        var runtime = new RuntimeClient { Body = body };
        var agent = new AgentClient { Body = body };
        if (agentCore) runtime.Dispose();
        else agent.Dispose();
        await using var factory = new BedrockClientFactory(_ => runtime, _ => throw new NotSupportedException(), _ => agent);
        async Task ReadAsync()
        {
            if (agentCore)
            {
                await foreach (var _ in new BedrockAgentCoreInvoker(factory).InvokeAsync(Aws, new(), CancellationToken.None)) { }
            }
            else
            {
                await using IBedrockEventStream stream = await new BedrockConverseInvoker(factory).ConverseStreamAsync(Aws, new(), CancellationToken.None);
                await foreach (var _ in stream.ReadAsync(CancellationToken.None)) { }
            }
        }
        Task invocation = ReadAsync();
        try
        {
            await body.Started.Task.WaitAsync(Bound);
            await factory.DisposeAsync().AsTask().WaitAsync(Bound);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
            Assert.True(body.Disposed);
            Assert.Equal(1, agentCore ? agent.Disposals : runtime.Disposals);
        }
        finally { body.Release.TrySetResult(); }
    }

    [Fact]
    public async Task LoggingGuardRetirementDrainsAnIgnoringLookupAndFencesCachedScopes()
    {
        await using var guard = new BedrockLoggingGuard();
        await guard.EnsureLoggingDisabledAsync("cached", Aws.Region!, _ => Task.FromResult<LoggingConfig?>(null));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task lookup = guard.EnsureLoggingDisabledAsync("pending", Aws.Region!, async token =>
        {
            using CancellationTokenRegistration registration = token.Register(() => stopped.TrySetResult());
            started.TrySetResult();
            await release.Task;
            return null;
        });
        try
        {
            await started.Task.WaitAsync(Bound);
            Task retirement = guard.DisposeAsync().AsTask();
            await stopped.Task.WaitAsync(Bound);
            Assert.False(retirement.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => guard.EnsureLoggingDisabledAsync("cached", Aws.Region!, _ => throw new InvalidOperationException()));
            release.TrySetResult();

            await retirement.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ProductionClientsUsePrivateIdentityResolutionAndSdkLocalHttpCaching()
    {
        await using var first = new BedrockClientFactory();
        await using var second = new BedrockClientFactory();
        using var r = first.Runtime(Aws);
        using var c = first.Control(Aws);
        using var a = first.AgentCore(Aws);
        using var other = second.Runtime(Aws);
        IClientConfig config = r.Client.Config;
        Assert.IsType<BedrockHttpClientFactory>(config.HttpClientFactory);
        Assert.True(config.HttpClientFactory.UseSDKHttpClientCaching(config));
        Assert.False(config.HttpClientFactory.DisposeHttpClientsAfterUse(config));
        Assert.Null(config.HttpClientFactory.GetConfigUniqueString(config));
        Assert.Equal(Timeout.InfiniteTimeSpan, config.Timeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, a.Client.Config.Timeout);
        Assert.NotEqual(Timeout.InfiniteTimeSpan, c.Client.Config.Timeout);
        IIdentityResolver resolver = config.IdentityResolverConfiguration.GetIdentityResolver<AWSCredentials>();
        Assert.Same(resolver, c.Client.Config.IdentityResolverConfiguration.GetIdentityResolver<AWSCredentials>());
        Assert.NotSame(resolver, other.Client.Config.IdentityResolverConfiguration.GetIdentityResolver<AWSCredentials>());
        using HttpClient http = config.HttpClientFactory.CreateHttpClient(config);
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
    }

    [Fact]
    public async Task SdkPipelineDisposalReleasesItsClientLocalHttpCache()
    {
        var httpFactory = new ProbeHttpFactory();
        var runtime = new AmazonBedrockRuntimeClient(new BasicAWSCredentials("fixture-key", "fixture-secret"),
            new AmazonBedrockRuntimeConfig { RegionEndpoint = RegionEndpoint.USEast1, HttpClientFactory = httpFactory });
        await using var owner = Factory(_ => runtime);
        var invoker = new BedrockConverseInvoker(owner);

        await invoker.ConverseAsync(Aws, new() { ModelId = "fixture-model", Messages = [] }, CancellationToken.None);
        Assert.NotNull(httpFactory.Client);
        Assert.False(httpFactory.Handler.Disposed);
        await owner.DisposeAsync();

        Assert.True(httpFactory.Handler.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => httpFactory.Client!.GetAsync("https://provider.test/"));
    }

    private static BedrockClientFactory Factory(Func<LlmAwsCredentials, AmazonBedrockRuntimeClient> runtime) =>
        new(runtime, _ => throw new NotSupportedException(), _ => throw new NotSupportedException());

    private sealed class RuntimeClient : AmazonBedrockRuntimeClient
    {
        internal bool BlockCalls { get; init; }
        internal bool IgnoreCancellation { get; init; }
        internal bool FailDisposal { get; init; }
        internal TrackedStream? Body { get; init; }
        internal int Disposals { get; private set; }
        internal CancellationToken OperationToken { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal RuntimeClient() : base(new BasicAWSCredentials("fixture-key", "fixture-secret"), RegionEndpoint.USEast1) { }
        public override async Task<ConverseResponse> ConverseAsync(ConverseRequest request, CancellationToken cancellationToken = default)
        {
            await WaitAsync(cancellationToken);
            return new();
        }
        public override async Task<ConverseStreamResponse> ConverseStreamAsync(ConverseStreamRequest request, CancellationToken cancellationToken = default)
        {
            await WaitAsync(cancellationToken);
            return new() { Stream = new ConverseStreamOutput(Body ?? new MemoryStream()) };
        }
        private async Task WaitAsync(CancellationToken token)
        {
            OperationToken = token;
            using CancellationTokenRegistration registration = token.Register(() => Stopped.TrySetResult());
            Started.TrySetResult();
            if (BlockCalls)
            {
                if (IgnoreCancellation) await Release.Task;
                else await Release.Task.WaitAsync(token);
            }
        }
        protected override void Dispose(bool disposing)
        {
            Disposals++;
            base.Dispose(disposing);
            if (FailDisposal) throw new InvalidOperationException("controlled SDK disposal failure");
        }
    }

    private sealed class ControlClient() : AmazonBedrockClient(new BasicAWSCredentials("fixture-key", "fixture-secret"), RegionEndpoint.USEast1)
    {
        internal bool BlockListing { get; init; }
        internal int Disposals { get; private set; }
        internal int FoundationLookups { get; private set; }
        internal CancellationToken Token { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<ListInferenceProfilesResponse> ListInferenceProfilesAsync(ListInferenceProfilesRequest request, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            Started.TrySetResult();
            if (BlockListing) await Release.Task;
            return new() { InferenceProfileSummaries = [] };
        }
        public override Task<ListFoundationModelsResponse> ListFoundationModelsAsync(ListFoundationModelsRequest request, CancellationToken cancellationToken = default)
        {
            FoundationLookups++;
            return Task.FromResult(new ListFoundationModelsResponse { ModelSummaries = [] });
        }
        protected override void Dispose(bool disposing) { Disposals++; base.Dispose(disposing); }
    }

    private sealed class AgentClient() : AmazonBedrockAgentCoreClient(new BasicAWSCredentials("fixture-key", "fixture-secret"), RegionEndpoint.USEast1)
    {
        internal bool BlockCalls { get; init; }
        internal bool IgnoreCancellation { get; init; }
        internal TrackedStream? Body { get; init; }
        internal int Disposals { get; private set; }
        internal CancellationToken Token { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<InvokeHarnessResponse> InvokeHarnessAsync(InvokeHarnessRequest request, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            Started.TrySetResult();
            if (BlockCalls)
            {
                if (IgnoreCancellation) await Release.Task;
                else await Release.Task.WaitAsync(cancellationToken);
            }
            return new() { Stream = new InvokeHarnessStreamOutput(Body ?? new MemoryStream()) };
        }
        protected override void Dispose(bool disposing) { Disposals++; base.Dispose(disposing); }
    }

    private class TrackedStream : MemoryStream
    {
        internal bool FailDisposal { get; init; }
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
            if (FailDisposal) throw new InvalidOperationException("controlled response disposal failure");
        }
    }

    private sealed class BlockingStream : TrackedStream
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WaitAsync(cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(WaitAsync(cancellationToken));
        private async Task<int> WaitAsync(CancellationToken token)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            return 0;
        }
    }

    private sealed class ProbeHttpFactory : HttpClientFactory
    {
        private readonly BedrockHttpClientFactory _policy = new();
        internal ProbeHandler Handler { get; } = new();
        internal HttpClient? Client { get; private set; }
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => Client = new(Handler);
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => _policy.UseSDKHttpClientCaching(clientConfig);
        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => _policy.DisposeHttpClientsAfterUse(clientConfig);
        public override string? GetConfigUniqueString(IClientConfig clientConfig) => _policy.GetConfigUniqueString(clientConfig);
    }

    private sealed class ProbeHandler : HttpMessageHandler
    {
        internal bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"output\":{\"message\":{\"role\":\"assistant\",\"content\":[{\"text\":\"ok\"}]}},\"stopReason\":\"end_turn\"}", Encoding.UTF8, "application/json")
            });
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
