using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using Amazon;
using Amazon.Bedrock;
using Amazon.BedrockAgentCore;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed partial class BedrockLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupFailureRetainsOnlyTheFailedOwnedResourceAndItsOriginalError(bool response)
    {
        FailedResourceEvidence evidence = ExerciseFailedOwnedResource(response);
        try
        {
            for (int attempt = 0; evidence.Healthy.IsAlive && attempt < 30; attempt++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
            Assert.True(evidence.Failed.IsAlive);
            Assert.False(evidence.Healthy.IsAlive);
            Assert.Same(evidence.Error, Record.Exception(() => evidence.Owner.Dispose()));
            Assert.Same(evidence.Error, Record.Exception(() => evidence.Owner.Dispose()));
        }
        finally { GC.KeepAlive(evidence.Owner); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FailedResourceEvidence ExerciseFailedOwnedResource(bool response) =>
        ExerciseFailedOwnedResourceAsync(response).GetAwaiter().GetResult();

    private static async Task<FailedResourceEvidence> ExerciseFailedOwnedResourceAsync(bool response)
    {
        var original = new InvalidOperationException("Original owned cleanup failure.");
        BedrockClientFactory owner;
        WeakReference failed; WeakReference healthy;
        if (response)
        {
            var sdkResponse = new ConverseStreamResponse { Stream = new ConverseStreamOutput(new IdentityFailureStream(original)) };
            var runtime = new TakenResponseClient(sdkResponse);
            owner = Factory(_ => runtime); failed = new(sdkResponse); healthy = new(runtime);
            IBedrockEventStream stream = await new BedrockConverseInvoker(owner).ConverseStreamAsync(Aws, new(), CancellationToken.None);
            Exception? first = await Record.ExceptionAsync(() => stream.DisposeAsync().AsTask());
            Assert.Same(original, first);
            Assert.Same(first, await Record.ExceptionAsync(() => stream.DisposeAsync().AsTask()));
        }
        else
        {
            var failing = new IdentityFailureClient(original);
            var successful = new RuntimeClient();
            owner = Factory(aws => aws.Region == Aws.Region ? failing : successful);
            owner.Runtime(Aws).Dispose(); owner.Runtime(Aws with { Region = "us-west-2" }).Dispose();
            failed = new(failing); healthy = new(successful);
        }
        Task retirement = owner.DisposeAsync().AsTask();
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => retirement);
        Assert.Same(original, Assert.Single(error.InnerExceptions));
        Assert.Same(retirement, owner.DisposeAsync().AsTask());
        return new(owner, failed, healthy, error);
    }

    private sealed record FailedResourceEvidence(BedrockClientFactory Owner, WeakReference Failed, WeakReference Healthy, AggregateException Error);
    private sealed class IdentityFailureClient(Exception error)
        : AmazonBedrockRuntimeClient(new BasicAWSCredentials("fixture-key", "fixture-secret"), RegionEndpoint.USEast1)
    {
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) throw error; }
    }

    [Fact]
    public void ActualPrivateFactoryAndSdkHttpPipelineReleaseTheirGeneration()
    {
        WeakReference[] roots = ExercisePrivateFactoryGeneration();
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 30; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        foreach (WeakReference root in roots) Assert.False(root.IsAlive, "A settled private Bedrock/SDK HTTP generation remains rooted.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExercisePrivateFactoryGeneration()
    {
        var generation = new PrivateSdkGeneration(Path.GetDirectoryName(typeof(BedrockLifetimeTests).Assembly.Location)!);
        var roots = new List<WeakReference> { new(generation) };
        try
        {
            Assembly[] originals = [typeof(BedrockLifetimeTests).Assembly, typeof(BedrockClientFactory).Assembly,
                typeof(LlmProviderService).Assembly, typeof(Common.LLM.LlmUsage).Assembly,
                typeof(AmazonBedrockRuntimeClient).Assembly, typeof(AmazonBedrockClient).Assembly,
                typeof(AmazonBedrockAgentCoreClient).Assembly, typeof(AWSCredentials).Assembly,
                typeof(Amazon.SecurityToken.AmazonSecurityTokenServiceClient).Assembly];
            foreach (Assembly original in originals)
            {
                Assembly actual = generation.LoadFromAssemblyPath(original.Location);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
                Assert.NotSame(original, actual);
                Assert.Equal(original.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                roots.Add(new(actual));
            }
            Assembly fixture = generation.Assemblies.Single(item => item.GetName().Name == typeof(BedrockLifetimeTests).Assembly.GetName().Name);
            Type type = fixture.GetType(typeof(BedrockLifetimeTests).FullName!, true)!;
            MethodInfo method = type.GetMethod(nameof(PrivateFactoryCallerAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.Same(fixture, method.DeclaringType!.Assembly);
            Assert.Equal(fixture.ManifestModule.ModuleVersionId, method.Module.ModuleVersionId);
            roots.Add(new(type)); roots.Add(new(method));
            var task = (Task<WeakReference[]>)method.Invoke(null, null)!;
            roots.AddRange(task.WaitAsync(Bound).GetAwaiter().GetResult());
            roots.Add(new(task));
        }
        finally { generation.Unload(); }
        return [.. roots];
    }

    private static async Task<WeakReference[]> PrivateFactoryCallerAsync()
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(BedrockClientFactory).Assembly)!;
        Assert.True(generation.IsCollectible);
        foreach (Type type in new[] { typeof(BedrockLifetimeTests), typeof(LlmProviderService), typeof(Common.LLM.LlmUsage),
            typeof(AmazonBedrockRuntimeClient), typeof(AmazonBedrockClient), typeof(AmazonBedrockAgentCoreClient), typeof(AWSCredentials) })
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(type.Assembly));
        var roots = new List<WeakReference>();
        var observations = new List<string>();
        var collaborator = new BedrockForeignCollaborator((name, mvid, value) =>
        {
            Assert.Equal(typeof(BedrockLifetimeTests).Assembly.ManifestModule.ModuleVersionId, mvid);
            observations.Add(name); roots.Add(new(value));
        });
        var owner = new BedrockClientFactory(collaborator.Runtime, collaborator.Control, collaborator.Agent);
        roots.Add(new(collaborator)); roots.Add(new(owner));
        try
        {
            using (var control = owner.Control(Aws)) Assert.Same(generation, AssemblyLoadContext.GetLoadContext(control.Client.GetType().Assembly));
            using (var agent = owner.AgentCore(Aws)) Assert.Same(generation, AssemblyLoadContext.GetLoadContext(agent.Client.GetType().Assembly));
            ConverseResponse response = await new BedrockConverseInvoker(owner).ConverseAsync(Aws,
                new() { ModelId = "fixture-model", Messages = [] }, CancellationToken.None);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(response.GetType().Assembly));
            Assert.Equal("ok", Assert.Single(response.Output.Message.Content).Text);
            roots.Add(new(response));
            await owner.DisposeAsync();
            Assert.Equal(1, observations.Count(name => name == "Send"));
            Assert.Equal(1, observations.Count(name => name == "HandlerDisposed"));
            Assert.DoesNotContain("BorrowerDisposed", observations);
        }
        finally { await owner.DisposeAsync(); }

        var production = new BedrockClientFactory();
        roots.Add(new(production));
        try
        {
            using var runtime = production.Runtime(Aws);
            using var control = production.Control(Aws);
            using var agent = production.AgentCore(Aws);
            foreach (IClientConfig config in new[] { runtime.Client.Config, control.Client.Config, agent.Client.Config })
            {
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(config.GetType().Assembly));
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(config.HttpClientFactory.GetType().Assembly));
                roots.Add(new(config)); roots.Add(new(config.HttpClientFactory));
                var resolver = config.IdentityResolverConfiguration.GetIdentityResolver<AWSCredentials>();
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(resolver.GetType().Assembly));
                roots.Add(new(resolver));
            }
        }
        finally { await production.DisposeAsync(); }
        return [.. roots];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedDefaultOwnerReleasesActualForeignCreatorsOrDisposedResponse(bool response)
    {
        ForeignEvidence evidence = ExerciseBorrowedGeneration(response);
        try
        {
            for (int attempt = 0; evidence.Roots.Any(root => root.IsAlive) && attempt < 30; attempt++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
            foreach (WeakReference root in evidence.Roots) Assert.False(root.IsAlive, "A drained foreign Bedrock collaborator remains rooted.");
        }
        finally { GC.KeepAlive(evidence.Owner); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ForeignEvidence ExerciseBorrowedGeneration(bool response)
    {
        var generation = new BorrowedGeneration();
        try
        {
            ForeignEvidence evidence = ExerciseBorrowedCallerAsync(generation, response).GetAwaiter().GetResult();
            return new([new(generation), .. evidence.Roots], evidence.Owner);
        }
        finally { generation.Unload(); }
    }

    private static async Task<ForeignEvidence> ExerciseBorrowedCallerAsync(AssemblyLoadContext generation, bool response)
    {
        Assembly fixture = generation.LoadFromAssemblyPath(typeof(BedrockLifetimeTests).Assembly.Location);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
        Assert.Equal(typeof(BedrockLifetimeTests).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
        Type type = fixture.GetType(typeof(BedrockForeignCollaborator).FullName!, true)!;
        Assert.Same(fixture, type.Assembly);
        var observations = new List<(string Name, Guid Mvid)>();
        var roots = new List<WeakReference> { new(fixture), new(type) };
        Action<string, Guid, object> observe = (name, mvid, value) =>
        { observations.Add((name, mvid)); roots.Add(new(value)); };
        var collaborator = Activator.CreateInstance(type, observe)!;
        roots.Add(new(collaborator));
        MethodInfo runtimeMethod = type.GetMethod(nameof(BedrockForeignCollaborator.Runtime))!;
        Assert.Equal(fixture.ManifestModule.ModuleVersionId, runtimeMethod.Module.ModuleVersionId);
        Assert.Equal(typeof(AmazonBedrockRuntimeClient), runtimeMethod.ReturnType);
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(runtimeMethod.ReturnType.Assembly));
        roots.Add(new(runtimeMethod));

        if (response)
        {
            MethodInfo responseMethod = type.GetMethod(nameof(BedrockForeignCollaborator.Response))!;
            roots.Add(new(responseMethod));
            var sdkResponse = (ConverseStreamResponse)responseMethod.Invoke(collaborator, null)!;
            Assert.Same(fixture, sdkResponse.GetType().Assembly);
            roots.Add(new(sdkResponse));
            var runtime = new TakenResponseClient(sdkResponse);
            var factory = Factory(_ => runtime);
            IBedrockEventStream? stream = null;
            try
            {
                stream = await new BedrockConverseInvoker(factory).ConverseStreamAsync(Aws, new(), CancellationToken.None);
                Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(stream.GetType().Assembly));
                Assert.Equal("foreign-request", stream.RequestId);
                await stream.DisposeAsync();
                await factory.DisposeAsync();
                Assert.Equal(1, runtime.Disposals);
                Assert.Equal(1, observations.Count(item => item.Name == "ResponseDisposed"));
                Assert.Equal("foreign-request", stream.RequestId);
                Assert.All(observations, item => Assert.Equal(fixture.ManifestModule.ModuleVersionId, item.Mvid));
                return new([.. roots], stream);
            }
            finally
            {
                try { if (stream is not null) await stream.DisposeAsync(); }
                finally { await factory.DisposeAsync(); }
            }
        }

        var runtimeCreator = runtimeMethod.CreateDelegate<Func<LlmAwsCredentials, AmazonBedrockRuntimeClient>>(collaborator);
        var controlCreator = type.GetMethod(nameof(BedrockForeignCollaborator.Control))!.CreateDelegate<Func<LlmAwsCredentials, AmazonBedrockClient>>(collaborator);
        var agentCreator = type.GetMethod(nameof(BedrockForeignCollaborator.Agent))!.CreateDelegate<Func<LlmAwsCredentials, AmazonBedrockAgentCoreClient>>(collaborator);
        roots.AddRange([new(runtimeCreator), new(controlCreator), new(agentCreator)]);
        var owner = new BedrockClientFactory(runtimeCreator, controlCreator, agentCreator);
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(owner.GetType().Assembly));
        try
        {
            using (var control = owner.Control(Aws)) { Assert.NotNull(control.Client); }
            using (var agent = owner.AgentCore(Aws)) { Assert.NotNull(agent.Client); }
            ConverseResponse result = await new BedrockConverseInvoker(owner).ConverseAsync(Aws,
                new() { ModelId = "fixture-model", Messages = [] }, CancellationToken.None);
            Assert.Equal("ok", Assert.Single(result.Output.Message.Content).Text);
            await owner.DisposeAsync();
            Assert.Contains(observations, item => item.Name == "Runtime");
            Assert.Contains(observations, item => item.Name == "Send");
            Assert.Equal(1, observations.Count(item => item.Name == "HandlerDisposed"));
            Assert.DoesNotContain(observations, item => item.Name == "BorrowerDisposed");
            Assert.All(observations, item => Assert.Equal(fixture.ManifestModule.ModuleVersionId, item.Mvid));
            return new([.. roots], owner);
        }
        finally { await owner.DisposeAsync(); }
    }

    private sealed record ForeignEvidence(WeakReference[] Roots, object Owner);
    private sealed class BorrowedGeneration() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()));
    }
    private sealed class PrivateSdkGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true
                && name.Name?.StartsWith("AWSSDK.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
    private sealed class TakenResponseClient(ConverseStreamResponse response)
        : AmazonBedrockRuntimeClient(new BasicAWSCredentials("fixture-key", "fixture-secret"), RegionEndpoint.USEast1)
    {
        private ConverseStreamResponse? _response = response;
        internal int Disposals { get; private set; }
        public override Task<ConverseStreamResponse> ConverseStreamAsync(ConverseStreamRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Interlocked.Exchange(ref _response, null) ?? throw new InvalidOperationException("Response already transferred."));
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
}

public sealed class BedrockForeignCollaborator(Action<string, Guid, object> observe) : IDisposable
{
    private void Record(string name, object value) => observe(name, typeof(BedrockForeignCollaborator).Assembly.ManifestModule.ModuleVersionId, value);
    public AmazonBedrockRuntimeClient Runtime(LlmAwsCredentials credentials)
    {
        Record("Runtime", this);
        var http = new ForeignHttpFactory(Record);
        var client = new AmazonBedrockRuntimeClient(new BasicAWSCredentials("fixture-key", "fixture-secret"),
            new AmazonBedrockRuntimeConfig { RegionEndpoint = RegionEndpoint.USEast1, HttpClientFactory = http });
        Record("SdkClient", client);
        Record("HttpFactory", http);
        return client;
    }
    public AmazonBedrockClient Control(LlmAwsCredentials credentials)
    {
        Record("Control", this);
        var client = new AmazonBedrockClient(new BasicAWSCredentials("fixture-key", "fixture-secret"), RegionEndpoint.USEast1);
        Record("ControlClient", client);
        return client;
    }
    public AmazonBedrockAgentCoreClient Agent(LlmAwsCredentials credentials)
    {
        Record("Agent", this);
        var client = new AmazonBedrockAgentCoreClient(new BasicAWSCredentials("fixture-key", "fixture-secret"), RegionEndpoint.USEast1);
        Record("AgentClient", client);
        return client;
    }
    public ConverseStreamResponse Response()
    {
        Record("Response", this);
        return new ForeignResponse(Record);
    }
    public void Dispose() => Record("BorrowerDisposed", this);

    private sealed class ForeignResponse : ConverseStreamResponse
    {
        private readonly Action<string, object> _record;
        public ForeignResponse(Action<string, object> record)
        {
            _record = record;
            ResponseMetadata = new() { RequestId = "foreign-request" };
            Stream = new ConverseStreamOutput(new MemoryStream());
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _record("ResponseDisposed", this);
        }
    }

    private sealed class ForeignHttpFactory(Action<string, object> record) : HttpClientFactory
    {
        private readonly BedrockHttpClientFactory _policy = new();
        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
        {
            var handler = new ForeignHandler(record);
            record("Handler", handler);
            var client = new HttpClient(handler);
            record("HttpClient", client);
            return client;
        }
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => _policy.UseSDKHttpClientCaching(clientConfig);
        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => _policy.DisposeHttpClientsAfterUse(clientConfig);
        public override string? GetConfigUniqueString(IClientConfig clientConfig) => _policy.GetConfigUniqueString(clientConfig);
    }
    private sealed class ForeignHandler(Action<string, object> record) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            record("Send", this);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"output\":{\"message\":{\"role\":\"assistant\",\"content\":[{\"text\":\"ok\"}]}},\"stopReason\":\"end_turn\"}", Encoding.UTF8, "application/json")
            });
        }
        protected override void Dispose(bool disposing) { if (disposing) record("HandlerDisposed", this); base.Dispose(disposing); }
    }
}
