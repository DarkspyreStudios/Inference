using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class QwenLlmInferenceTests
{
    [Fact]
    public async Task ProviderControlsRefuseBeforeModelOrNativeEntry()
    {
        await using var inference = new QwenLlmInference(new MissingPackManager());
        var failure = await Assert.ThrowsAsync<InferenceException>(async () =>
        {
            await foreach (var unused in inference.StreamAsync(new()
            {
                ModelId = TensorLlmModels.Qwen3FourBillionQ4Km.Key.ModelId,
                Messages = [new("user", "hello")],
                ProviderControls = new UnsupportedControls()
            })) { }
        });
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, failure.Code);
    }

    private sealed record UnsupportedControls : LlmProviderControls;

    [Fact]
    public async Task ExplicitCacheRefusesBeforeModelOrNativeEntry()
    {
        await using var inference = new QwenLlmInference(new MissingPackManager());
        var request = new LlmInferenceRequest
        {
            ModelId = TensorLlmModels.Qwen3FourBillionQ4Km.Key.ModelId,
            Messages = [new("user", "hello")],
            ExplicitCache = new("cachedContents/cache", "model", "scope"),
            PromptCache = new() { Retention = LlmPromptCacheRetention.Extended }
        };
        var failure = await Assert.ThrowsAsync<InferenceException>(async () =>
        { await foreach (var unused in inference.StreamAsync(request)) { } });
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, failure.Code);
        Assert.False((await inference.GetCapabilitiesAsync(request.ModelId)).SupportsExplicitCache);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostedRequestsRefuseBeforeModelOrRuntimeMaterialization(bool limitOnly)
    {
        await using var inference = new QwenLlmInference(new MissingPackManager());
        var request = new LlmInferenceRequest
        {
            ModelId = TensorLlmModels.Qwen3FourBillionQ4Km.Key.ModelId,
            Messages = [new LlmMessage("user", "hello")],
            HostedTools = limitOnly ? [] : [new LlmCodeExecutionTool()],
            MaxHostedTurns = limitOnly ? 1 : null
        };
        var failure = await Assert.ThrowsAsync<InferenceException>(async () =>
        { await foreach (var unused in inference.StreamAsync(request)) { } });
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, failure.Code);
        Assert.Empty((await inference.GetCapabilitiesAsync(request.ModelId)).HostedTools);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidRequestNeverLoadsAHiddenModel(bool cacheHint)
    {
        await using var inference = new QwenLlmInference(new MissingPackManager());
        await inference.StartAndWaitAsync();
        var request = new LlmInferenceRequest
        {
            ModelId = TensorLlmModels.Qwen3FourBillionQ4Km.Key.ModelId,
            Messages = [new LlmMessage("user", "hello")],
            PromptCache = cacheHint ? new() { Scope = LlmPromptCacheScope.Conversation, Retention = LlmPromptCacheRetention.Extended } : null
        };

        InferenceException exception = await Assert.ThrowsAsync<InferenceException>(async () =>
        {
            await foreach (LlmStreamEvent _ in inference.StreamAsync(request))
            {
            }
        });

        Assert.Equal(InferenceFailureCode.ModelUnavailable, exception.Code);
        await inference.StopAndWaitAsync();
    }

    [Fact]
    public async Task CapabilitiesDescribeTheImplementedPortableLane()
    {
        await using var inference = new QwenLlmInference(new MissingPackManager());

        LlmCapabilities capabilities = await inference.GetCapabilitiesAsync(
            TensorLlmModels.Qwen3FourBillionQ4Km.Key.ModelId);

        Assert.True(capabilities.SupportsStreaming);
        Assert.Equal(LlmOutputLimitEnforcement.EngineEnforced, capabilities.OutputLimitEnforcement);
        Assert.True(capabilities.SupportsJsonSchema);
        Assert.True(capabilities.SupportsToolUse);
        Assert.False(capabilities.SupportsPromptImages);
        Assert.Equal([LlmReasoningLevel.None, LlmReasoningLevel.High], capabilities.ReasoningLevels.Order());
        Assert.True(capabilities.SupportsReasoning);
        Assert.False(capabilities.PromptCache.Automatic);
        Assert.False(capabilities.PromptCache.AcceptsHints);
        Assert.Empty(capabilities.PromptCache.Retentions);
    }

    [Fact]
    public async Task ReasoningOtherThanNoneOrHighFailsBeforeModelMaterialization()
    {
        await using var inference = new QwenLlmInference(new MissingPackManager());
        var request = new LlmInferenceRequest
        {
            ModelId = TensorLlmModels.Qwen3FourBillionQ4Km.Key.ModelId,
            Messages = [new LlmMessage("user", "hello")],
            ReasoningLevel = LlmReasoningLevel.Low
        };

        InferenceException exception = await Assert.ThrowsAsync<InferenceException>(async () =>
        {
            await foreach (LlmStreamEvent _ in inference.StreamAsync(request))
            {
            }
        });

        Assert.Equal(InferenceFailureCode.InvalidRequest, exception.Code);
    }

    [Fact]
    public async Task ToolChoiceFailsBeforeModelMaterialization()
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
            ],
            AllowParallelToolCalls = false
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
        public Task<TensorModelImportResult> ImportAsync(TensorModelImportRequest request, IProgress<TensorModelInstallProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TensorModelPackDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TensorModelPackDefinition>>([TensorLlmModels.Qwen3FourBillionQ4Km]);
        public Task<TensorModelPackDefinition> RegisterAsync(TensorModelPackDefinition definition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TensorModelPackDefinition> GetRequiredDefinitionAsync(TensorModelKey key, CancellationToken cancellationToken = default) => Task.FromResult(TensorLlmModels.Qwen3FourBillionQ4Km);

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
