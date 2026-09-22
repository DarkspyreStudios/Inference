using CommonImage = Darkspyre.Inference.Common.Image;

namespace Darkspyre.Inference.Connect.Image.Tests;

public sealed class PortableInferenceAdapterTests
{
    [Fact]
    public async Task BoundInferenceMapsTypedRequestResultAndProvenance()
    {
        var transport = new RecordingTransport();
        var service = new ImageProviderService([transport]);
        CommonImage.IImageInference inference = service.Bind(
            new ImageConnection
            {
                Transport = ImageTransport.ComfyUi,
                Endpoint = "http://provider.test"
            },
            new ImageWorkflow
            {
                Id = "workflow-a",
                Label = "Workflow A",
                Version = "7",
                Transport = ImageTransport.ComfyUi,
                DefaultWidth = 512,
                DefaultHeight = 768,
                OutputContentType = "image/png"
            },
            TimeSpan.Zero);

        CommonImage.ImageGenerationResult result = await inference.GenerateAsync(
            new CommonImage.ImageGenerationRequest
            {
                ModelId = "workflow-a",
                Prompt = "a lighthouse",
                NegativePrompt = "fog",
                Seed = 42
            });

        Assert.Equal("a lighthouse", transport.Prepared?.PositivePrompt);
        Assert.Equal("fog", transport.Prepared?.NegativePrompt);
        Assert.Equal(42, transport.Prepared?.Seed);
        Assert.Equal("workflow-a", result.Provenance.ModelId);
        Assert.Equal("7", result.Provenance.ModelRevision);
        Assert.Equal("darkspyre.connect.image", result.Provenance.ImplementationId);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(result.Images).Data.ToArray());
    }

    private sealed class RecordingTransport : IImageTransport
    {
        private int _pollCount;

        public ImageTransportPreparationRequest? Prepared { get; private set; }

        public ImageTransportDescriptor Descriptor { get; } = new(
            ImageTransport.ComfyUi,
            "Test transport",
            "Test transport",
            new ImageTransportCapabilities(false, false, false));

        public PreparedImageTransportRequest Prepare(ImageTransportPreparationRequest request)
        {
            Prepared = request;
            return new PreparedImageTransportRequest("{}");
        }

        public Task<string> SubmitAsync(
            ImageConnection connection,
            PreparedImageTransportRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult("job-1");

        public Task<ImageTransportJobSnapshot> GetJobAsync(
            ImageConnection connection,
            string providerJobId,
            ImageWorkflow workflow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_pollCount++ == 0
                ? new ImageTransportJobSnapshot(ImageTransportJobState.Generating)
                : new ImageTransportJobSnapshot(
                    ImageTransportJobState.Completed,
                    new ImageTransportOutput("result.png", "{}")));

        public Task<byte[]> DownloadAsync(
            ImageConnection connection,
            ImageTransportOutput output,
            CancellationToken cancellationToken = default) => Task.FromResult(new byte[] { 1, 2, 3 });

        public Task<ImageProviderHealth> TestAsync(
            ImageConnection connection,
            ImageWorkflow? workflow = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IImageTransportProgressConnection?> TryConnectProgressAsync(
            ImageConnection connection,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UploadReferenceAsync(
            ImageConnection connection,
            ImageReference reference,
            Stream content,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> TryCancelAsync(
            ImageConnection connection,
            string providerJobId,
            bool interruptRunning,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
