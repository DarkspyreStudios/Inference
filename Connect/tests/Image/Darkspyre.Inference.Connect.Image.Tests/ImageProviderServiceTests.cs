using System.Text.Json;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.Image.Tests;

public sealed class ImageProviderServiceTests
{
    [Fact]
    public void ConstructorRejectsDuplicateTransportKinds()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            new ImageProviderService(
            [
                new StubImageTransport(ImageTransport.ComfyUi, "First"),
                new StubImageTransport(ImageTransport.ComfyUi, "Second")
            ]));

        Assert.Contains("Duplicate image transports", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceDiscoversAndResolvesRegisteredTransport()
    {
        var transport = new StubImageTransport(ImageTransport.ComfyUi, "ComfyUI");
        var service = new ImageProviderService([transport]);

        Assert.Same(transport, service.GetRequiredTransport(ImageTransport.ComfyUi));
        Assert.Equal("ComfyUI", Assert.Single(service.AvailableTransports).DisplayName);
    }

    [Fact]
    public void RegistrationAddsTheBuiltInComfyUiTransportOnce()
    {
        var services = new ServiceCollection();
        services.AddImageConnect();
        services.AddImageConnect();
        using ServiceProvider provider = services.BuildServiceProvider();

        ImageProviderService service = provider.GetRequiredService<ImageProviderService>();

        ImageTransportDescriptor descriptor = Assert.Single(service.AvailableTransports);
        Assert.Equal(ImageTransport.ComfyUi, descriptor.Transport);
        Assert.Same(
            service.GetRequiredTransport(ImageTransport.ComfyUi),
            service.GetRequiredTransport(ImageTransport.ComfyUi));
    }

    private sealed class StubImageTransport(ImageTransport transport, string name) : IImageTransport
    {
        public ImageTransportDescriptor Descriptor { get; } = new(
            transport,
            name,
            "Test transport",
            new ImageTransportCapabilities(false, false, false));


        public Task<IReadOnlyList<ImageWorkflow>> GetWorkflowsAsync(ImageConnection connection, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ImageCapabilities> GetCapabilitiesAsync(ImageConnection connection, ImageWorkflow workflow, string? modelId = null, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ImageCapabilities { SupportedSizes = [new(workflow.DefaultWidth, workflow.DefaultHeight)], SupportsNegativePrompt = true, SupportsSeed = true });
        public Task<ImagePreparedReference> ResolveReferenceAsync(ImageConnection connection, InferenceMediaSource source, XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public PreparedImageTransportRequest Prepare(ImageTransportPreparationRequest request) =>
            throw new NotSupportedException();

        public Task<ImageProviderHealth> TestAsync(
            ImageConnection connection,
            ImageWorkflow? workflow = null,
            XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IImageTransportProgressConnection?> TryConnectProgressAsync(
            ImageConnection connection,
            XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ImagePreparedReference> UploadReferenceAsync(
            ImageConnection connection,
            ImageReference reference,
            Stream content,
            XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> SubmitAsync(
            ImageConnection connection,
            PreparedImageTransportRequest request,
            XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ImageTransportJobSnapshot> GetJobAsync(
            ImageConnection connection,
            string providerJobId,
            ImageWorkflow workflow,
            XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ImageTransportDownload> DownloadAsync(
            ImageConnection connection,
            ImageTransportOutput output,
            XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryCancelAsync(
            ImageConnection connection,
            string providerJobId,
            bool interruptRunning,
            XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
