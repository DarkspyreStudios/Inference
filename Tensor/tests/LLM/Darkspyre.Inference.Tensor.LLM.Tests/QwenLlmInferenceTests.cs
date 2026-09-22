using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.LLM.Tests;

public sealed class QwenLlmInferenceTests
{
    [Fact]
    public async Task CapabilitiesDescribeTheImplementedPortableLane()
    {
        await using var inference = new QwenLlmInference(new MissingPackManager());

        LlmCapabilities capabilities = await inference.GetCapabilitiesAsync(
            TensorLlmModels.Qwen3FourBillionQ4Km.Key.ModelId);

        Assert.True(capabilities.SupportsStreaming);
        Assert.True(capabilities.SupportsJsonSchema);
        Assert.False(capabilities.SupportsToolUse);
        Assert.False(capabilities.SupportsPromptImages);
    }

    [Fact]
    public async Task UnsupportedToolsFailBeforeModelMaterialization()
    {
        await using var inference = new QwenLlmInference(new MissingPackManager());
        var request = new LlmInferenceRequest
        {
            ModelId = TensorLlmModels.Qwen3FourBillionQ4Km.Key.ModelId,
            Messages = [new LlmMessage("user", "hello")],
            Tools =
            [
                new LlmToolDefinition
                {
                    Name = "test",
                    InputSchema = System.Text.Json.JsonElement.Parse("{}")
                }
            ]
        };

        InferenceException exception = await Assert.ThrowsAsync<InferenceException>(async () =>
        {
            await foreach (LlmStreamEvent _ in inference.StreamAsync(request))
            {
            }
        });

        Assert.Equal(InferenceFailureCode.UnsupportedFeature, exception.Code);
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
