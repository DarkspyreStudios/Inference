using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Connect.Image.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darkspyre.Inference.Connect.Image.Tests;

public sealed partial class ComfyUiImageTransportTests
{
    /// <summary>
    /// A request prepared with a planned reference names the file the later upload writes, still submits after a restart
    /// (a new transport and connection), and is refused by another endpoint.
    /// </summary>
    [Fact]
    public async Task APlannedReferenceNamesTheLaterUploadAndSurvivesARestartOnItsEndpointOnly()
    {
        ImagePreparedReference planned = CreateTransport(new StubHandler(_ => NotFound())).PlanReference(Connection(), Reference());
        PreparedImageTransportRequest stored = CreateTransport(new StubHandler(_ => NotFound())).Prepare(new()
        {
            Workflow = ReferenceWorkflow(),
            PositivePrompt = "draw",
            CacheKey = "job-1",
            References = [planned]
        });

        Assert.Equal(["Workflow", "ReferenceScopes"], Json(stored.ProviderRequest).EnumerateObject().Select(property => property.Name));
        string? uploadName = null, prompt = null;
        var handler = new StubHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/upload/image", StringComparison.Ordinal))
            {
                Assert.Equal("true", await ((MultipartFormDataContent)request.Content!).Single(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "overwrite").ReadAsStringAsync());
                uploadName = ((MultipartFormDataContent)request.Content!).Single(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "image")
                    .Headers.ContentDisposition!.FileName!.Trim('"');
                return JsonResponse($"{{\"name\":\"{uploadName}\",\"subfolder\":\"\",\"type\":\"input\"}}");
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/history/p-1", StringComparison.Ordinal))
                return JsonResponse("""{"p-1":{"outputs":{"9":{"images":[{"filename":"alpha.png","subfolder":"","type":"output"}]}},"status":{"completed":true}}}""");
            if (request.RequestUri.AbsolutePath.EndsWith("/view", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([7, 8, 9]) };
            prompt = await request.Content!.ReadAsStringAsync();
            Assert.Equal(["prompt", "client_id"], Json(prompt).EnumerateObject().Select(property => property.Name));
            return JsonResponse("{\"prompt_id\":\"p-1\"}");
        });
        ComfyUiImageTransport restarted = CreateTransport(handler);
        ImageConnection owner = Connection();
        using var content = new MemoryStream([1, 2, 3]);
        await restarted.UploadReferenceAsync(owner, Reference(), content);

        Assert.Equal("p-1", await restarted.SubmitAsync(owner, stored));
        const string expected = "darkspyre-reference-7ec55b60b1f54e71b2deaa8accfd2ef0.png";
        Assert.Equal(expected, uploadName);
        Assert.Contains($"\"image\":\"{expected}\"", prompt, StringComparison.Ordinal);
        ImageTransportJobSnapshot snapshot = await restarted.GetJobAsync(owner, "p-1", ReferenceWorkflow());
        Assert.Equal(ImageTransportJobState.Completed, snapshot.State);
        ImageTransportOutput output = Assert.Single(snapshot.Outputs!);
        Assert.Equal(new byte[] { 7, 8, 9 }, (await restarted.DownloadAsync(owner, output)).Data.ToArray());
        Assert.True(await restarted.ReleaseJobAsync(owner, ReferenceWorkflow(), "p-1"));

        InferenceException refused = await Assert.ThrowsAsync<InferenceException>(() =>
            restarted.SubmitAsync(Connection() with { Endpoint = "http://other.test:8188" }, stored));
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, refused.Code);
    }

    private static ImageWorkflow ReferenceWorkflow() => new()
    {
        Id = "edit",
        Label = "Edit",
        Transport = ImageTransport.ComfyUi,
        RequiresReferenceImage = true,
        TransportConfiguration = Json("""
            {"outputNodeId":"9","filenamePrefixTemplate":"${cacheKey}","defaultParameters":{},
             "workflow":{"1":{"class_type":"LoadImage","inputs":{"image":"${referenceImageName}"}},
                         "2":{"class_type":"Prompt","inputs":{"text":"${positivePrompt}","width":"${width}","height":"${height}","seed":"${seed}"}},
                         "9":{"class_type":"SaveImageWithAlpha","inputs":{"images":["2",0]}},
                         "10":{"class_type":"SaveImageWebsocket","inputs":{"images":["2",0]}}}}
            """)
    };

    private static ComfyUiImageTransport CreateTransport(HttpMessageHandler handler) =>
        new(new StubHttpClientFactory(handler), NullLogger<ComfyUiImageTransport>.Instance);

    private static ImageConnection Connection() => new()
    {
        Transport = ImageTransport.ComfyUi,
        Endpoint = "http://comfy.test:8188"
    };

    private static ImageReference Reference() => new()
    {
        Id = Guid.Parse("7ec55b60-b1f5-4e71-b2de-aa8accfd2ef0"),
        FileName = "reference.PNG",
        ContentType = "image/png"
    };

    private static JsonElement Json(string value) => JsonElement.Parse(value);

    private static HttpResponseMessage JsonResponse(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _response;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
            : this(request => Task.FromResult(response(request)))
        {
        }

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) => _response = response;

        public List<IReadOnlyDictionary<string, string>> Headers { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Headers.Add(request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase));
            return await _response(request);
        }
    }
}
