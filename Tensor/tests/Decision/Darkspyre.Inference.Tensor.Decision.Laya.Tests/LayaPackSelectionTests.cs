using System.Net;
using System.Security.Cryptography;
using System.Text;
using Darkspyre.Inference.Common;
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
        using var packs = new TensorModelPackManager(new TensorModelCatalog([pack]), _root, http);
        var loader = new Loader();
        await using var engine = new LayaDecisionInference(packs, loader, TensorBackendKind.Cpu);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => engine.LoadAndWaitAsync(pack.Key));
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, error.Code);
        Assert.Empty(loader.Loaded);
    }

    private static TensorModelPackDefinition Pack(string id, string revision) => new()
    {
        Key = new(id, revision),
        Capability = TensorCapability.Decision,
        RuntimeArchitecture = TensorDecisionModels.LayaRuntimeArchitecture,
        License = "test-only",
        SupportedBackends = new HashSet<TensorBackendKind> { TensorBackendKind.Cpu },
        Artifacts = ArtifactRoles.Select(role =>
        {
            var uri = new Uri($"https://example.test/{id}/{revision}/{role}");
            byte[] content = ArtifactContent(uri);
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
    private static byte[] ArtifactContent(Uri uri) => Encoding.UTF8.GetBytes(
        uri.AbsolutePath.EndsWith("/agent-config", StringComparison.Ordinal) ? AgentConfiguration : uri.AbsolutePath);
    private sealed class ArtifactHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ArtifactContent(request.RequestUri!)) });
    }
    private sealed class Loader : ILayaModelLoader
    {
        public List<TensorModelKey> Loaded { get; } = [];
        public Task<LayaModelResources> LoadAsync(TensorModelPackLease lease, TensorBackendKind backend,
            LayaAgentConfiguration configuration, CancellationToken cancellationToken)
        {
            Loaded.Add(lease.Key);
            throw new InvalidOperationException("A pack without its agent configuration must not load.");
        }
    }
}
