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

    [Theory]
    [InlineData("success")]
    [InlineData("http-failure")]
    [InlineData("read-failure")]
    [InlineData("cancel")]
    public async Task ReferenceUploadPreservesBorrowedStreamWithoutRewindOrFlush(string outcome)
    {
        using var cancellation = new CancellationTokenSource();
        using var content = new BorrowedInputStream(outcome == "read-failure");
        byte[]? payload = null;
        var handler = new StubHandler(async request =>
        {
            payload = await request.Content!.ReadAsByteArrayAsync(cancellation.Token);
            if (outcome == "cancel")
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            return outcome == "http-failure" ? NotFound() : JsonResponse("{\"name\":\"actual-upload.png\",\"subfolder\":\"\",\"type\":\"input\"}");
        });
        Task upload = CreateTransport(handler).UploadReferenceAsync(Connection(), Reference(), content, cancellationToken: cancellation.Token);
        if (outcome == "success")
            await upload;
        else
            await Assert.ThrowsAnyAsync<Exception>(() => upload);
        Assert.Equal(0, content.Disposals);
        Assert.True(content.CanRead);
        Assert.Equal(0, content.Seeks);
        Assert.Equal(0, content.Flushes);
        if (outcome != "read-failure")
        {
            Assert.NotNull(payload);
            Assert.DoesNotContain((byte)3, payload);
            Assert.Contains((byte)4, payload);
            Assert.Contains((byte)5, payload);
            Assert.Contains((byte)6, payload);
        }
    }

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

        string? uploadName = null, prompt = null;
        var handler = new StubHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/upload/image", StringComparison.Ordinal))
            {
                uploadName = ((MultipartFormDataContent)request.Content!).Single(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "image")
                    .Headers.ContentDisposition!.FileName!.Trim('"');
                return JsonResponse($"{{\"name\":\"{uploadName}\",\"subfolder\":\"\",\"type\":\"input\"}}");
            }
            prompt = await request.Content!.ReadAsStringAsync();
            return JsonResponse("{\"prompt_id\":\"p-1\"}");
        });
        ComfyUiImageTransport restarted = CreateTransport(handler);
        using var content = new MemoryStream([1, 2, 3]);
        await restarted.UploadReferenceAsync(Connection(), Reference(), content);

        Assert.Equal("p-1", await restarted.SubmitAsync(Connection(), stored));
        const string expected = "darkspyre-reference-7ec55b60b1f54e71b2deaa8accfd2ef0.png";
        Assert.Equal(expected, uploadName);
        Assert.Contains($"\"image\":\"{expected}\"", prompt, StringComparison.Ordinal);

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
                         "2":{"class_type":"Prompt","inputs":{"text":"${positivePrompt}","width":"${width}","height":"${height}","seed":"${seed}"}}}}
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

    private sealed class BorrowedInputStream(bool refuseRead) : Stream
    {
        private readonly MemoryStream _inner = new([3, 4, 5, 6]) { Position = 1 };
        public int Disposals { get; private set; }
        public int Seeks { get; private set; }
        public int Flushes { get; private set; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set { Seeks++; throw new NotSupportedException(); } }
        public override int Read(byte[] buffer, int offset, int count)
            => refuseRead ? throw new IOException("Controlled input read refusal.") : _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => refuseRead ? ValueTask.FromException<int>(new IOException("Controlled input read refusal.")) : _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) { Seeks++; throw new NotSupportedException(); }
        public override void Flush() { Flushes++; throw new NotSupportedException(); }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Disposals++; _inner.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
