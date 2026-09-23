using Darkspyre.Inference.Tensor.Common;
using Microsoft.ML.OnnxRuntime;

namespace Darkspyre.Inference.Tensor.Decision.Laya.Tests;

public sealed class LayaOnnxModelTests
{
    private const int FixtureLength = 16;

    [Fact]
    public void ForwardPadsTheSequenceAndReturnsOnlyTheRealOptions()
    {
        using LayaOnnxModel model = Create("laya-fixture.onnx");
        var sequence = new LayaSequence([7, 8, 9], [1, 2], ["a", "b"], 2);

        LayaModelOutput output = model.Forward(sequence, CancellationToken.None);

        Assert.Equal(4, model.MaximumOptions);
        Assert.Equal([1f, 2f], output.Logits);
        // The fixture's act_logits are [count of real tokens, question type].
        Assert.Equal(LayaOnnxModel.Softmax([3f, 2f])[0], output.ActionProbability);
    }

    [Fact]
    public void CreateRejectsAModelWhoseLengthDiffersFromTheAgentConfiguration()
    {
        Assert.Throws<InvalidDataException>(() => Create("laya-fixture.onnx", FixtureLength * 2));
    }

    [Theory]
    [InlineData("laya-fixture-no-metadata.onnx")]
    [InlineData("laya-fixture-dynamic.onnx")]
    public void CreateRejectsMissingMetadataAndNonStaticShapes(string fixture)
    {
        Assert.Throws<InvalidDataException>(() => Create(fixture));
    }

    [Fact]
    public void ForwardRejectsSequencesThatDoNotFitTheGraph()
    {
        using LayaOnnxModel model = Create("laya-fixture.onnx");
        int[] tooManyTokens = Enumerable.Repeat(1, FixtureLength + 1).ToArray();

        Assert.Throws<ArgumentException>(() => model.Forward(new([1, 2], [0, 1, 1, 1, 1], ["a", "b", "c", "d", "e"], 0), CancellationToken.None));
        Assert.Throws<ArgumentException>(() => model.Forward(new(tooManyTokens, [0], ["a"], 0), CancellationToken.None));
        Assert.Throws<ArgumentException>(() => model.Forward(new([1], [], [], 0), CancellationToken.None));
    }

    [Fact]
    public void ACancelledRunSurfacesCancellation()
    {
        using LayaOnnxModel model = Create("laya-fixture.onnx");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => model.Forward(new([1, 2], [1], ["a"], 0), cancellation.Token));
    }

    [Fact]
    public void ProviderSelectionAcceptsOnlyCoreMlAndCpu()
    {
        using (LayaModelLoader.CreateOptions(TensorBackendKind.Cpu)) { }
        if (OperatingSystem.IsMacOS())
        {
            using (LayaModelLoader.CreateOptions(TensorBackendKind.CoreMl)) { }
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => LayaModelLoader.CreateOptions(TensorBackendKind.Cuda));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayaModelLoader.CreateOptions(TensorBackendKind.GgmlMetal));
    }

    [Fact]
    public async Task ProbeReportsCpuAndResolvesAutomaticSelection()
    {
        var probe = new LayaBackendProbe();

        Assert.True((await probe.CheckAsync(TensorBackendKind.Cpu)).IsAvailable);
        Assert.False((await probe.CheckAsync(TensorBackendKind.Cuda)).IsAvailable);
        Assert.Equal(OperatingSystem.IsMacOS() ? TensorBackendKind.CoreMl : TensorBackendKind.Cpu,
            LayaDecisionInference.ResolveBackend(TensorBackendKind.Auto));
        if (OperatingSystem.IsMacOS())
        {
            Assert.True((await probe.CheckAsync(TensorBackendKind.Auto)).IsAvailable);
        }
    }

    private static LayaOnnxModel Create(string fixture, int configuredLength = FixtureLength)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        using var options = new SessionOptions();
        return LayaOnnxModel.Create(bytes, options, configuredLength);
    }
}
