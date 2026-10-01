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
    [InlineData("unchanged")]
    [InlineData("headers")]
    [InlineData("connection")]
    [InlineData("transport")]
    public async Task NativeReceiptRequiresUnchangedObservedOwnerBeforeSubmit(string change)
    {
        var headers = new Dictionary<string, string> { ["X-Account"] = "one" };
        ImageConnection connection = Connection() with { AdditionalHeaders = headers };
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath == "/upload/image"
            ? JsonResponse("{\"name\":\"server.png\",\"subfolder\":\"actual\",\"type\":\"input\"}")
            : JsonResponse("{\"prompt_id\":\"job\"}"));
        ComfyUiImageTransport transport = CreateTransport(handler);
        using var input = new MemoryStream([1, 2, 3]);
        ImagePreparedReference receipt = await transport.UploadReferenceAsync(connection, Reference(), input);
        Assert.Equal("server.png", receipt.Metadata.FileName);
        PreparedImageTransportRequest prepared = transport.Prepare(Request(Workflow(true)) with { References = [receipt] });
        prepared = JsonSerializer.Deserialize(JsonSerializer.Serialize(prepared, ConnectImageJson.PreparedImageTransportRequest),
            ConnectImageJson.PreparedImageTransportRequest)!;
        Assert.Single(handler.Headers);
        if (change == "headers") headers["X-Account"] = "two";
        if (change == "connection") connection = connection with { };
        if (change == "transport") transport = CreateTransport(handler);
        if (change == "unchanged")
        {
            Assert.Equal("job", await transport.SubmitAsync(connection, prepared));
            Assert.Equal(2, handler.Headers.Count);
        }
        else
        {
            InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => transport.SubmitAsync(connection, prepared));
            Assert.Equal(InferenceFailureCode.UnsupportedFeature, error.Code);
            Assert.Single(handler.Headers);
        }
    }

    [Fact]
    public async Task DynamicAuthorizationAndUnsupportedSourcesRefuseBeforeCallbacksOrHttp()
    {
        var authority = new RefusingAuthorization();
        var handler = new StubHandler((Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new InvalidOperationException("HTTP must not be entered.")));
        ComfyUiImageTransport transport = CreateTransport(handler);
        ImageConnection dynamic = Connection() with { Authorization = authority };
        using var input = new MemoryStream([1]);
        await Assert.ThrowsAsync<InferenceException>(() => transport.UploadReferenceAsync(dynamic, Reference(), input));
        await Assert.ThrowsAsync<InferenceException>(() => transport.GetWorkflowsAsync(Connection()));
        await Assert.ThrowsAsync<InferenceException>(() => transport.ResolveReferenceAsync(Connection(), new UrlMediaSource(new("https://unread.invalid/image"))));
        Assert.Equal(0, authority.Calls);
        Assert.Empty(handler.Headers);
    }

    [Fact]
    public async Task EveryOutputAndObservedDownloadMimeSurviveWithoutWorkflowGuessing()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath == "/history/job"
            ? JsonResponse("{\"job\":{\"outputs\":{\"10\":{\"images\":[{\"filename\":\"first.png\"},{\"filename\":\"second.png\"}]}}}}")
            : new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent([7]) { Headers = { ContentType = new("image/webp") } } });
        ComfyUiImageTransport transport = CreateTransport(handler);
        ImageConnection connection = Connection();
        ImageTransportJobSnapshot snapshot = await transport.GetJobAsync(connection, "job", Workflow());
        Assert.Equal("first.png", snapshot.Outputs![0].FileName);
        Assert.Equal("second.png", snapshot.Outputs[1].FileName);
        Assert.Equal(2, snapshot.Outputs.Count);
        foreach (ImageTransportOutput output in snapshot.Outputs)
        {
            Assert.Null(output.ContentType);
            ImageTransportDownload download = await transport.DownloadAsync(connection, output);
            Assert.Equal("image/webp", download.ContentType);
            Assert.Equal(new byte[] { 7 }, download.Data.ToArray());
        }
    }

    [Fact]
    public void PreparationCopiesReceiptStateWithoutCallbacksAndRejectsUnqualifiedControls()
    {
        var handler = new StubHandler((Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new InvalidOperationException("Prepare must not perform HTTP.")));
        ComfyUiImageTransport transport = CreateTransport(handler);
        using JsonDocument state = JsonDocument.Parse("{\"name\":\"native.png\",\"subfolder\":\"\",\"type\":\"input\"}");
        var receipt = new ImagePreparedReference(Reference(), new("owner", "identity"), state.RootElement);
        var supplied = new List<ImagePreparedReference> { receipt };
        ImageTransportPreparationRequest request = Request(Workflow(true)) with { References = supplied };
        supplied.Clear();
        state.Dispose();
        Assert.Contains("native.png", transport.Prepare(request).ProviderRequest, StringComparison.Ordinal);
        Assert.Empty(handler.Headers);
        Assert.Throws<InferenceException>(() => transport.Prepare(request with { Count = 2 }));
        Assert.Throws<InferenceException>(() => transport.Prepare(request with { Steps = 3 }));
    }

    private sealed class RefusingAuthorization : IProviderAuthorization
    {
        public int Calls { get; private set; }
        public ValueTask<ProviderAuthorizationHeader> GetAsync(CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Authorization must not be acquired."); }
    }

    [Fact]
    public void PrepareRendersWorkflowAndProviderNeutralReference()
    {
        ComfyUiImageTransport transport = CreateTransport(new StubHandler(_ => NotFound()));
        ImageReference reference = Reference();

        PreparedImageTransportRequest prepared = transport.Prepare(new ImageTransportPreparationRequest
        {
            Workflow = Workflow(requiresReference: true),
            PositivePrompt = "A rainy neon street.",
            NegativePrompt = "captions",
            Width = 1_216,
            Height = 832,
            Seed = 42,
            CacheKey = "cache-key",
            Label = "Neon scene",
            References = [new(reference, new("owner", "identity"), Json("{\"name\":\"actual.png\",\"subfolder\":\"folder\",\"type\":\"input\"}"))]
        });

        using JsonDocument document = JsonDocument.Parse(prepared.ProviderRequest);
        JsonElement inputs = document.RootElement.GetProperty("Workflow").GetProperty("1").GetProperty("inputs");
        Assert.Equal("A rainy neon street.", inputs.GetProperty("positive").GetString());
        Assert.Equal("captions", inputs.GetProperty("negative").GetString());
        Assert.Equal(1_216, inputs.GetProperty("width").GetInt32());
        Assert.Equal(832, inputs.GetProperty("height").GetInt32());
        Assert.Equal(42, inputs.GetProperty("seed").GetInt64());
        Assert.Equal(20, inputs.GetProperty("steps").GetInt32());
        Assert.Equal("folder/actual.png", inputs.GetProperty("reference").GetString());
        Assert.Equal("cache-key-42", inputs.GetProperty("prefix").GetString());
    }

    [Fact]
    public void PrepareRejectsMissingRequiredReferenceAndUnresolvedPlaceholders()
    {
        ComfyUiImageTransport transport = CreateTransport(new StubHandler(_ => NotFound()));
        ImageTransportPreparationRequest request = Request(Workflow(requiresReference: true));

        InvalidOperationException missing = Assert.Throws<InvalidOperationException>(() => transport.Prepare(request));
        Assert.Contains("requires a reference image", missing.Message, StringComparison.Ordinal);

        ImageWorkflow invalid = Workflow() with
        {
            TransportConfiguration = Json("""
                {
                  "outputNodeId": "10",
                  "filenamePrefixTemplate": "${cacheKey}",
                  "defaultParameters": {},
                  "workflow": { "1": { "class_type": "KSampler", "inputs": { "positive": "${positivePrompt}" } } }
                }
                """)
        };
        InvalidOperationException placeholder = Assert.Throws<InvalidOperationException>(() =>
            transport.Prepare(Request(invalid)));
        Assert.Contains("${width}", placeholder.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthChecksReachabilityAndRequiredWorkflowNodes()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/system_stats" => JsonResponse("{\"system\":{}}"),
            "/object_info" => JsonResponse("{\"KSampler\":{}}"),
            _ => NotFound()
        });
        ComfyUiImageTransport transport = CreateTransport(handler);

        ImageProviderHealth health = await transport.TestAsync(Connection(), Workflow());

        Assert.True(health.IsHealthy);
        Assert.Contains("supports every node", health.Message, StringComparison.Ordinal);
        Assert.Contains("KSampler", health.Diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("system", health.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitStatusDownloadAndCancellationPreserveOpaqueAdapterState()
    {
        var paths = new List<string>();
        var bodies = new List<string>();
        var handler = new StubHandler(async request =>
        {
            paths.Add(request.RequestUri!.PathAndQuery);
            if (request.Content is not null)
            {
                bodies.Add(await request.Content.ReadAsStringAsync());
            }

            return request.RequestUri.AbsolutePath switch
            {
                "/prompt" => JsonResponse("{\"prompt_id\":\"job-1\"}"),
                "/history/job-1" => JsonResponse(
                    "{\"job-1\":{\"outputs\":{\"10\":{\"images\":[{\"filename\":\"result.png\",\"subfolder\":\"generated\",\"type\":\"output\"}]}}}}"),
                "/view" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                },
                "/queue" or "/interrupt" => JsonResponse("{}"),
                _ => NotFound()
            };
        });
        ComfyUiImageTransport transport = CreateTransport(handler);
        ImageConnection connection = Connection() with
        {
            AdditionalHeaders = new Dictionary<string, string> { ["X-Test-Connection"] = "present" }
        };
        PreparedImageTransportRequest prepared = transport.Prepare(Request(Workflow()));

        string jobId = await transport.SubmitAsync(connection, prepared);
        ImageTransportJobSnapshot snapshot = await transport.GetJobAsync(connection, jobId, Workflow());
        ImageTransportDownload download = await transport.DownloadAsync(connection, Assert.Single(snapshot.Outputs!));
        bool cancelled = await transport.TryCancelAsync(connection, jobId, interruptRunning: true);

        Assert.Equal("job-1", jobId);
        Assert.Equal(ImageTransportJobState.Completed, snapshot.State);
        Assert.Equal("result.png", Assert.Single(snapshot.Outputs!).FileName);
        Assert.Equal([1, 2, 3], download.Data.ToArray());
        Assert.Null(download.ContentType);
        Assert.True(cancelled);
        Assert.Contains("/view?filename=result.png&subfolder=generated&type=output", paths);
        Assert.Contains("/queue", paths);
        Assert.Contains("/interrupt", paths);
        Assert.Contains(bodies, body => body.Contains("\"client_id\"", StringComparison.Ordinal));
        Assert.All(handler.Headers, headers => Assert.Equal("present", headers["X-Test-Connection"]));
        Assert.DoesNotContain("outputs", snapshot.Diagnostics!.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReferenceUploadUsesDeterministicNameAndContentType()
    {
        string? body = null;
        string? contentType = null;
        var handler = new StubHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            contentType = request.Content.Headers.ContentType?.MediaType;
            return JsonResponse("{\"name\":\"actual-upload.png\",\"subfolder\":\"\",\"type\":\"input\"}");
        });
        ComfyUiImageTransport transport = CreateTransport(handler);
        ImageReference reference = Reference();
        await using var content = new MemoryStream([4, 5, 6]);

        await transport.UploadReferenceAsync(Connection(), reference, content);

        Assert.Equal("multipart/form-data", contentType);
        Assert.Contains($"darkspyre-reference-{reference.Id:N}.png", body, StringComparison.Ordinal);
        Assert.Contains("image/png", body, StringComparison.Ordinal);
        Assert.Contains("true", body, StringComparison.Ordinal);
    }

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

    [Theory]
    [InlineData("{\"type\":\"executing\",\"data\":{\"prompt_id\":\"job\",\"node\":\"1\"}}", "Executing")]
    [InlineData("{\"type\":\"executing\",\"data\":{\"prompt_id\":\"job\",\"node\":null}}", "Finished")]
    [InlineData("{\"type\":\"execution_success\",\"data\":{\"prompt_id\":\"job\"}}", "Finished")]
    [InlineData("{\"type\":\"execution_error\",\"data\":{\"prompt_id\":\"job\",\"exception_message\":\"bad node\"}}", "Failed")]
    [InlineData("not-json", "None")]
    public void WebSocketFramesMapToProviderNeutralProgress(string json, string expected)
    {
        ComfySignal signal = ComfyUiImageTransport.InterpretFrame(json);

        Assert.Equal(expected, signal.Kind.ToString());
    }

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

    private static ImageTransportPreparationRequest Request(ImageWorkflow workflow) => new()
    {
        Workflow = workflow,
        PositivePrompt = "A city at night",
        Width = 1_024,
        Height = 1_024,
        Seed = 7,
        CacheKey = "cache-key"
    };

    private static ImageWorkflow Workflow(bool requiresReference = false) => new()
    {
        Id = "scene",
        Label = "Scene",
        Transport = ImageTransport.ComfyUi,
        RequiresReferenceImage = requiresReference,
        TransportConfiguration = Json("""
            {
              "outputNodeId": "10",
              "filenamePrefixTemplate": "${cacheKey}-${seed}",
              "defaultParameters": { "steps": 20 },
              "workflow": {
                "1": {
                  "class_type": "KSampler",
                  "inputs": {
                    "positive": "${positivePrompt}",
                    "negative": "${negativePrompt}",
                    "width": "${width}",
                    "height": "${height}",
                    "seed": "${seed}",
                    "steps": "${steps}",
                    "reference": "${referenceImageName}",
                    "prefix": "${filenamePrefix}"
                  }
                }
              }
            }
            """)
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
