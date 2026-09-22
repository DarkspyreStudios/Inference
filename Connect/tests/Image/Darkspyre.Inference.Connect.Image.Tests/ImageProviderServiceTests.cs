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

        public PreparedImageTransportRequest Prepare(ImageTransportPreparationRequest request) =>
            throw new NotSupportedException();

        public Task<ImageProviderHealth> TestAsync(
            ImageConnection connection,
            ImageWorkflow? workflow = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IImageTransportProgressConnection?> TryConnectProgressAsync(
            ImageConnection connection,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UploadReferenceAsync(
            ImageConnection connection,
            ImageReference reference,
            Stream content,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> SubmitAsync(
            ImageConnection connection,
            PreparedImageTransportRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ImageTransportJobSnapshot> GetJobAsync(
            ImageConnection connection,
            string providerJobId,
            ImageWorkflow workflow,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<byte[]> DownloadAsync(
            ImageConnection connection,
            ImageTransportOutput output,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryCancelAsync(
            ImageConnection connection,
            string providerJobId,
            bool interruptRunning,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
