using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Decision;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Decision.Laya.Tests;

public sealed class LayaPackSelectionTests : IDisposable
{
    private static readonly string[] ArtifactRoles = ["model", "tokenizer", "agent-config"];
    private const string AgentConfiguration = """
        {"max_len":384,"head_max_len":128,"temperature":[2,3,4],"temperature_by_options":{"noul:2":1.006}}
        """;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"darkspyre-laya-selection-{Guid.NewGuid():N}");

    [Fact]
    public async Task MissingAgentConfigurationFailsCompatibilityBeforeLoading()
    {
        TensorModelPackDefinition pack = Pack("host/custom", "1");
        pack = pack with { Artifacts = pack.Artifacts.Where(artifact => artifact.Role != "agent-config").ToArray() };
        using var http = new HttpClient(new ArtifactHandler());
        using var packs = Manager(http, pack);
        var loader = new Loader();
        await using var engine = new LayaDecisionInference(packs, loader, TensorBackendKind.Cpu);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => engine.LoadAndWaitAsync(pack.Key));
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, error.Code);
        Assert.Empty(loader.Loaded);
    }

    private TensorModelPackManager Manager(HttpClient http, params TensorModelPackDefinition[] definitions) =>
        new(new TensorModelCatalog(definitions), _root, http);
    private static SetupActionDescriptor Action(TensorSettings setup, SetupActionPurpose purpose) =>
        Assert.Single(setup.Actions, action => action.Purpose == purpose);
    private static TensorModelPackDefinition Pack(string id, string revision, string configuration = AgentConfiguration) => new()
    {
        Key = new(id, revision),
        Capability = TensorCapability.Decision,
        RuntimeArchitecture = TensorDecisionModels.LayaRuntimeArchitecture,
        License = "test-only",
        SupportedBackends = new HashSet<TensorBackendKind> { TensorBackendKind.Cpu },
        Artifacts = ArtifactRoles.Select(role =>
        {
            var uri = new Uri($"https://example.test/{id}/{revision}/{role}");
            byte[] content = ArtifactContent(uri, configuration);
            return new TensorModelArtifactDefinition
            {
                Role = role,
                RelativePath = role + ".bin",
                Length = content.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
                Sources = [uri]
            };
        }).ToArray()
    };
    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
    private static byte[] ArtifactContent(Uri uri, string configuration) => Encoding.UTF8.GetBytes(
        uri.AbsolutePath.EndsWith("/agent-config", StringComparison.Ordinal) ? configuration : uri.AbsolutePath);
    private sealed class ArtifactHandler(string configuration = AgentConfiguration) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ArtifactContent(request.RequestUri!, configuration)) });
    }
    private sealed class Loader : ILayaModelLoader
    {
        public List<TensorModelKey> Loaded { get; } = [];
        public List<Model> Models { get; } = [];
        public Exception? Failure { get; set; }
        public Action? AfterLoad { get; init; }
        public Action? Forward { get; init; }
        public async Task<LayaModelResources> LoadAsync(TensorModelPackLease lease, TensorBackendKind backend,
            LayaAgentConfiguration configuration, CancellationToken cancellationToken)
        {
            Loaded.Add(lease.Key);
            Assert.Equal(TensorBackendKind.Cpu, backend);
            Assert.Equal($"/{lease.Key.ModelId}/{lease.Key.Revision}/model",
                await File.ReadAllTextAsync(lease.GetRequiredArtifact("model"), cancellationToken));
            if (Failure is not null) { throw Failure; }
            var model = new Model(Forward);
            Models.Add(model);
            AfterLoad?.Invoke();
            return new(new Tokenizer(), model);
        }
    }
    private sealed class Model(Action? forward) : ILayaDecisionModel
    {
        public bool Disposed { get; private set; }
        public List<LayaSequence> Sequences { get; } = [];
        public int MaximumOptions => 32;
        public LayaModelOutput Forward(LayaSequence sequence, CancellationToken cancellationToken)
        { Assert.False(Disposed); Sequences.Add(sequence); forward?.Invoke(); Assert.False(Disposed); return new([0, 2], .75f); }
        public void Dispose() => Disposed = true;
    }
    private sealed class Tokenizer : ILayaTokenizer
    {
        public IReadOnlyList<int> Encode(string text) => Enumerable.Repeat(1, text.Length).ToArray();
    }
}
