using System.Net;
using System.Security.Cryptography;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorModelPackManagerTests : IDisposable
{
    private readonly string _packDirectory = Path.Combine(
        Path.GetTempPath(),
        $"darkspyre-tensor-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_packDirectory))
        {
            Directory.Delete(_packDirectory, recursive: true);
        }
    }

    private static TensorModelPackDefinition Definition(byte[] weights) =>
        new()
        {
            Key = new TensorModelKey("test/model", "revision-1"),
            Capability = TensorCapability.Llm,
            RuntimeArchitecture = "test",
            License = "test-only",
            SupportedBackends = new HashSet<TensorBackendKind> { TensorBackendKind.Cpu },
            Artifacts =
            [
                new TensorModelArtifactDefinition
                {
                    Role = "weights",
                    RelativePath = "model.gguf",
                    Length = weights.Length,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(weights)),
                    Sources = [new Uri("http://127.0.0.1/model.gguf")]
                }
            ]
        };

    [Fact]
    public async Task DownloadsStageInsideThePackDirectoryAndLeaveNothingBehind()
    {
        byte[] weights = "weights"u8.ToArray();
        TensorModelPackDefinition definition = Definition(weights);
        string staging = Path.Combine(_packDirectory, ".tensor-staging");
        var observed = new List<string>();
        using var http = new HttpClient(new StagingProbeHandler(weights, () => observed.AddRange(Directory.GetFiles(staging))));
        using var manager = new TensorModelPackManager(new([definition]), _packDirectory, http);

        await manager.InstallAsync(definition.Key);

        // While the body streams, its file is in the pack directory's staging folder.
        Assert.Single(observed);
        Assert.Empty(Directory.GetFiles(staging));
        Assert.Equal(TensorModelPackState.Installed, (await manager.VerifyAsync(definition.Key)).State);
    }

    // Calls back when the body is first read, which is while the download writes its staging file.
    private sealed class StagingProbeHandler(byte[] content, Action probe) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ProbeStream(content, probe)),
                RequestMessage = request
            });
    }

    private sealed class ProbeStream(byte[] content, Action probe) : MemoryStream(content)
    {
        private bool _probed;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_probed && Position > 0) { _probed = true; probe(); }
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

}
