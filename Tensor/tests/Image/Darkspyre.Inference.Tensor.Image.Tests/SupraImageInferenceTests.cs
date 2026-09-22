using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Image.Tests;

public sealed class SupraImageInferenceTests
{
    [Fact]
    public async Task CapabilitiesDescribeTheFixedSupraSurface()
    {
        await using var inference = new SupraImageInference(new MissingPackManager());

        ImageCapabilities capabilities = await inference.GetCapabilitiesAsync(
            TensorImageModels.Supra2Image.Key.ModelId);

        Assert.Equal([(256, 256)], capabilities.SupportedSizes);
        Assert.False(capabilities.SupportsNegativePrompt);
        Assert.True(capabilities.SupportsSeed);
        Assert.True(capabilities.SupportsSteps);
        Assert.True(capabilities.SupportsGuidanceScale);
        Assert.Equal(4, capabilities.MaximumImageCount);
    }

    [Theory]
    [InlineData(512, 256, null)]
    [InlineData(256, 256, "exclude text")]
    public async Task UnsupportedOptionsFailBeforeModelMaterialization(
        int width,
        int height,
        string? negativePrompt)
    {
        await using var inference = new SupraImageInference(new MissingPackManager());
        var request = new ImageGenerationRequest
        {
            ModelId = TensorImageModels.Supra2Image.Key.ModelId,
            Prompt = "a small glowing jellyfish",
            NegativePrompt = negativePrompt,
            Width = width,
            Height = height
        };

        InferenceException exception = await Assert.ThrowsAsync<InferenceException>(
            () => inference.GenerateAsync(request));

        Assert.Equal(InferenceFailureCode.UnsupportedFeature, exception.Code);
    }

    [Fact]
    public async Task MissingPackIsReportedThroughThePortableFailureContract()
    {
        await using var inference = new SupraImageInference(new MissingPackManager());
        var request = new ImageGenerationRequest
        {
            ModelId = TensorImageModels.Supra2Image.Key.ModelId,
            Prompt = "a small glowing jellyfish"
        };

        InferenceException exception = await Assert.ThrowsAsync<InferenceException>(
            () => inference.GenerateAsync(request));

        Assert.Equal(InferenceFailureCode.ModelUnavailable, exception.Code);
    }

    [Fact]
    public void ModelPackPinsEveryArtifactAndCompanionRevision()
    {
        TensorModelPackDefinition pack = TensorImageModels.Supra2Image;

        Assert.Equal(TensorCapability.Image, pack.Capability);
        Assert.Equal(4, pack.Artifacts.Count);
        Assert.Equal("Apache-2.0 AND MIT", pack.License);
        Assert.All(pack.Artifacts, artifact =>
        {
            Assert.True(artifact.Length > 0);
            Assert.Equal(64, artifact.Sha256.Length);
            Uri source = Assert.Single(artifact.Sources);
            Assert.DoesNotContain("/resolve/main/", source.AbsoluteUri, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void PatchLayoutRoundTripsChannelsFirstLatents()
    {
        float[] latent = Enumerable.Range(0, SupraDiT.LatentElementCount)
            .Select(static value => (float)value)
            .ToArray();

        float[] patches = SupraDiT.Patchify(latent);
        float[] restored = SupraDiT.Unpatchify(patches);

        Assert.Equal(latent, restored);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, 17)]
    [InlineData(-128, 15)]
    [InlineData(128, 31)]
    public void RelativePositionBucketsMatchT5BidirectionalLayout(int relativePosition, int expected)
    {
        Assert.Equal(expected, FlanT5Encoder.RelativePositionBucket(relativePosition));
    }

    private sealed class MissingPackManager : ITensorModelPackManager
    {
        public Task<TensorModelPackStatus> GetStatusAsync(
            TensorModelKey key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TensorModelPackStatus(key, TensorModelPackState.NotInstalled));

        public Task InstallAsync(
            TensorModelKey key,
            IProgress<TensorModelInstallProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TensorModelPackStatus> VerifyAsync(
            TensorModelKey key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TensorModelPackStatus(key, TensorModelPackState.NotInstalled));

        public Task<TensorModelPackLease> AcquireAsync(
            TensorModelKey key,
            CancellationToken cancellationToken = default) =>
            throw new TensorModelPackException(key, "not installed");

        public Task RemoveAsync(TensorModelKey key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
