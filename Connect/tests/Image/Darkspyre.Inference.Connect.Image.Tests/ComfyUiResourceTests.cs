using System.Net;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.Image.Tests;

public sealed partial class ComfyUiImageTransportTests
{
    private static readonly TimeSpan ResourceBound = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualComfyCompositeKeepsAdmittedScopeAndCommitsOriginalBeforeUpload(bool binary)
    {
        var headers = new Dictionary<string, string> { ["X-Account"] = "original" };
        var resources = new ImageTestResources();
        var logical = new InferenceResourceReference("workspace", "original");
        resources.Committed[logical] = (new(logical, "session", null, null, 3), [1, 2, 3]);
        resources.ReadProvider = (reference, _) =>
        {
            headers["X-Account"] = "changed";
            var committed = resources.Committed[reference];
            var reader = new ImageTestResources.Reader(committed.Resource, new MemoryStream(committed.Data, writable: false));
            resources.Readers.Add(reader);
            return Task.FromResult<IInferenceResourceReadHandle>(reader);
        };
        bool uploaded = false;
        var handler = new StubHandler(async request =>
        {
            Assert.Equal("original", request.Headers.GetValues("X-Account").Single());
            if (request.RequestUri!.AbsolutePath == "/upload/image")
            {
                if (binary) Assert.Single(resources.Writers);
                Assert.Contains((byte)1, await request.Content!.ReadAsByteArrayAsync());
                uploaded = true;
                return JsonResponse("{\"name\":\"actual.png\",\"subfolder\":\"staged\",\"type\":\"input\"}");
            }
            if (request.RequestUri.AbsolutePath == "/prompt")
            {
                Assert.True(uploaded);
                using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("staged/actual.png", body.RootElement.GetProperty("prompt").GetProperty("1").GetProperty("inputs").GetProperty("reference").GetString());
                return JsonResponse("{\"prompt_id\":\"job\"}");
            }
            if (request.RequestUri.AbsolutePath == "/history/job")
                return JsonResponse("{\"job\":{\"outputs\":{\"10\":{\"images\":[{\"filename\":\"output.png\"}]}}}}");
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent([9, 8]) { Headers = { ContentType = new("image/webp") } } };
        });
        var transport = CreateTransport(handler);
        var inference = new ImageProviderService([transport]).Bind(Connection() with { AdditionalHeaders = headers },
            Workflow(true), resources: resources, pollInterval: TimeSpan.Zero);
        try
        {
            var result = await inference.GenerateAsync(new()
            {
                ModelId = "scene", Prompt = "draw",
                References = binary ? [new BinaryMediaSource(null, new byte[] { 1, 2, 3 })] : [new ResourceMediaSource(logical)]
            });
            var image = Assert.Single(result.Images);
            Assert.Equal("image/webp", image.ContentType);
            Assert.Equal(new byte[] { 9, 8 }, image.Data.ToArray());
            Assert.Equal("image/webp", image.Resource!.ContentType);
            Assert.Equal(binary ? 2 : 1, resources.Writers.Count);
            Assert.All(resources.Readers, reader => Assert.Equal(1, reader.Disposals));
            Assert.Equal(0, resources.Disposals);
            Assert.Equal(4, handler.Headers.Count);
        }
        finally { await ((IAsyncDisposable)inference).DisposeAsync(); }
    }

    [Fact]
    public async Task StandaloneResourceResolutionFreezesHeaderSelectionBeforeIgnoredRead()
    {
        var headers = new Dictionary<string, string> { ["X-Account"] = "original" };
        ImageConnection connection = Connection() with { AdditionalHeaders = headers };
        var release = new TaskCompletionSource<IInferenceResourceReadHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new InferenceResourceReference("workspace", "source");
        var resources = new ImageTestResources { ReadProvider = (_, _) => { entered.SetResult(); return release.Task; } };
        var handler = new StubHandler(request =>
        {
            Assert.Equal("original", request.Headers.GetValues("X-Account").Single());
            return JsonResponse("{\"name\":\"native.png\",\"subfolder\":\"\",\"type\":\"input\"}");
        });
        var transport = CreateTransport(handler);
        Task<ImagePreparedReference> pending = transport.ResolveReferenceAsync(connection, new ResourceMediaSource(resource), resources: resources);
        await entered.Task.WaitAsync(ResourceBound);
        headers["X-Account"] = "changed";
        var reader = new ImageTestResources.Reader(new(resource, "session", null, null, 1), new MemoryStream([1]));
        release.SetResult(reader);
        ImagePreparedReference receipt = await pending.WaitAsync(ResourceBound);
        Assert.Equal(resource, receipt.Resource);
        Assert.Equal(1, reader.Disposals);
        var prepared = transport.Prepare(Request(Workflow(true)) with { References = [receipt] });
        await Assert.ThrowsAsync<InferenceException>(() => transport.SubmitAsync(connection, prepared));
        Assert.Single(handler.Headers);
        Assert.Equal(0, resources.Disposals);
    }

    [Fact]
    public async Task RetiringCompositeDrainsIgnoredReaderAndNeverUploadsOrRewindsItsStream()
    {
        var release = new TaskCompletionSource<IInferenceResourceReadHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logical = new InferenceResourceReference("workspace", "source");
        var resources = new ImageTestResources { ReadProvider = (_, _) => { entered.SetResult(); return release.Task; } };
        var handler = new StubHandler((Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new InvalidOperationException("HTTP must not be entered.")));
        var inference = new ImageProviderService([CreateTransport(handler)]).Bind(Connection(), Workflow(true), resources: resources);
        Task pending = inference.GenerateAsync(new() { ModelId = "scene", Prompt = "draw", References = [new ResourceMediaSource(logical)] });
        await entered.Task.WaitAsync(ResourceBound);
        Task retirement = ((IAsyncDisposable)inference).DisposeAsync().AsTask();
        Assert.False(retirement.IsCompleted);
        var reader = new ImageTestResources.Reader(new(logical, "session", null, null, 1), new MemoryStream([1]));
        release.SetResult(reader);
        InferenceResourceOperationCanceledException error = await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => pending.WaitAsync(ResourceBound));
        await retirement.WaitAsync(ResourceBound);
        Assert.Equal(InferenceResourceOperationPhase.OpenRead, error.Failure.Phase);
        Assert.Equal(logical, error.Failure.Reference);
        Assert.Equal(1, reader.Disposals);
        Assert.Empty(handler.Headers);
        Assert.Equal(0, resources.Disposals);
    }
}
