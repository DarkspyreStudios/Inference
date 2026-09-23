using System.Net;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class QwenLocationTests
{
    [Theory]
    [InlineData("file", false)]
    [InlineData("folder", false)]
    [InlineData("url", false)]
    [InlineData("file", true)]
    [InlineData("folder", true)]
    [InlineData("url", true)]
    public async Task LocationsImportCompleteSingleAndSplitModelsThroughTheSamePolicy(string kind, bool split)
    {
        string root = Directory.CreateTempSubdirectory("qwen-location-tests-").FullName;
        try
        {
            var files = split ? new Dictionary<string, byte[]>
            {
                ["model-00001-of-00002.gguf"] = QwenCompatibilityTests.Fixture("valid", 0),
                ["model-00002-of-00002.gguf"] = QwenCompatibilityTests.Fixture("valid", 1)
            } : new Dictionary<string, byte[]> { ["model.gguf"] = QwenCompatibilityTests.Fixture("valid") };
            foreach ((string name, byte[] content) in files) await File.WriteAllBytesAsync(Path.Combine(root, name), content);
            using var http = new HttpClient(new Handler(files));
            var resolver = new QwenModelResolver(reader: new TensorArtifactReader(httpClient: http));
            string primary = files.Keys.First();
            TensorModelLocation Location(string name) => kind == "url"
                ? new TensorModelLocation.RemoteFile(new Uri("https://models.test/" + name))
                : new TensorModelLocation.LocalFile(Path.Combine(root, name));
            var selection = new TensorModelSelection(kind == "folder" ? new TensorModelLocation.LocalFolder(root, primary) : Location(primary),
                TensorBackendKind.Cpu, "test", Companions: kind != "folder" && split
                    ? [new("model-shard-2", Location(files.Keys.Last()))] : null);
            using var manager = new TensorModelPackManager(new([]), new TensorDirectFilePersistStore(Path.Combine(root, "store")), http);
            TensorModelImportResult result = await manager.ImportAsync(new(selection, resolver));
            Assert.Equal(TensorCompatibilityState.Supported, result.Resolution.Evaluation.State);
            Assert.Equal(files.Count, result.Resolution.Definition!.Artifacts.Count);
            Assert.Equal(TensorModelPackState.Installed, (await manager.VerifyAsync(result.Resolution.Definition.Key)).State);
            Assert.All(result.Resolution.Definition.Artifacts, artifact => Assert.Null(artifact.Provenance!.Repository));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Handler(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(files[request.RequestUri!.Segments.Last()]) });
    }
}
