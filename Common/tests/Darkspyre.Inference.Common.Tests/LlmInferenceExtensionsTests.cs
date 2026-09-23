using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Common.Tests;

public sealed class LlmInferenceExtensionsTests
{
    [Fact]
    public async Task CompleteAsyncReturnsTheSingleCompletion()
    {
        var inference = new TestInference();
        LlmInferenceResult result = await inference.CompleteAsync(
            new LlmInferenceRequest { ModelId = "test" });

        Assert.Equal("complete", result.Text);
    }

    private sealed class TestInference : ILlmInference
    {
        public InferenceImplementationDescriptor Descriptor { get; } =
            new("test", "Test", InferenceImplementationKind.Tensor, InferenceTask.TextGeneration);

        public Task<LlmCapabilities> GetCapabilitiesAsync(
            string? modelId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmCapabilities { SupportsStreaming = true });

        public Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmModelInfo>>([]);

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            LlmInferenceRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new LlmTextDeltaEvent("complete");
            yield return new LlmCompletionEvent(new LlmInferenceResult
            {
                Text = "complete",
                Status = LlmCompletionStatus.Completed,
                Provenance = new InferenceProvenance
                {
                    ImplementationId = "test",
                    ModelId = request.ModelId
                }
            });
            await Task.CompletedTask;
        }
    }
}
