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
            using var runtime = new TakenResponseClient(sdkResponse);
            var factory = Factory(_ => runtime);
            IBedrockEventStream? stream = null;
            try
            {
                stream = await new BedrockConverseInvoker(factory).ConverseStreamAsync(Aws, new(), CancellationToken.None);
                Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(stream.GetType().Assembly));
                Assert.Equal("foreign-request", stream.RequestId);
                await stream.DisposeAsync();
                await factory.DisposeAsync();
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
    private sealed class TakenResponseClient(ConverseStreamResponse response)
        : AmazonBedrockRuntimeClient(new BasicAWSCredentials("fixture-key", "fixture-secret"), RegionEndpoint.USEast1)
    {
        private ConverseStreamResponse? _response = response;
        public override Task<ConverseStreamResponse> ConverseStreamAsync(ConverseStreamRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Interlocked.Exchange(ref _response, null) ?? throw new InvalidOperationException("Response already transferred."));
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
