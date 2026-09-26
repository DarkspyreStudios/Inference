using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Decision;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Decision.Laya.Tests;

public sealed class LayaDecisionInferenceTests
{
    [Fact]
    public async Task ValidRequestNeverLoadsAHiddenModel()
    {
        await using var inference = new LayaDecisionInference(new MissingPackManager());
        await inference.StartAndWaitAsync();
        var request = new DecisionInferenceRequest
        {
            ModelId = Pack.Key.ModelId,
            State = JsonElement.Parse("{}"),
            Questions = [new BooleanDecisionQuestion { Id = "ready", Instructions = "Is the system ready?" }]
        };

        InferenceException exception = await Assert.ThrowsAsync<InferenceException>(
            () => inference.DecideAsync(request));

        Assert.Equal(InferenceFailureCode.ModelUnavailable, exception.Code);
        await inference.StopAndWaitAsync();
    }

    [Fact]
    public async Task CapabilitiesDescribeAllTypedDecisionKinds()
    {
        await using var inference = new LayaDecisionInference(new MissingPackManager());

        DecisionCapabilities capabilities = await inference.GetCapabilitiesAsync(
            Pack.Key.ModelId);

        Assert.Equal(3, capabilities.SupportedQuestionKinds.Count);
        Assert.Contains(DecisionQuestionKind.Choice, capabilities.SupportedQuestionKinds);
        Assert.Contains(DecisionQuestionKind.Score, capabilities.SupportedQuestionKinds);
        Assert.Contains(DecisionQuestionKind.Boolean, capabilities.SupportedQuestionKinds);
        Assert.Null(capabilities.MaximumInputTokens);
        Assert.True(capabilities.SupportsCalibratedConfidence);
        Assert.True(capabilities.SupportsActionProbability);
    }

    [Fact]
    public async Task InvalidQuestionFailsBeforeModelMaterialization()
    {
        await using var inference = new LayaDecisionInference(new MissingPackManager());
        var request = new DecisionInferenceRequest
        {
            ModelId = Pack.Key.ModelId,
            State = JsonElement.Parse("{}"),
            Questions =
            [
                new ScoreDecisionQuestion
                {
                    Id = "quality",
                    Instructions = "Score the quality.",
                    Levels = [new DecisionScoreLevel { Id = "only", Description = "Only level" }]
                }
            ]
        };

        InferenceException exception = await Assert.ThrowsAsync<InferenceException>(
            () => inference.DecideAsync(request));

        Assert.Equal(InferenceFailureCode.InvalidRequest, exception.Code);
    }

    [Fact]
    public async Task MissingPackIsReportedThroughThePortableFailureContract()
    {
        await using var inference = new LayaDecisionInference(new MissingPackManager());
        var request = new DecisionInferenceRequest
        {
            ModelId = Pack.Key.ModelId,
            State = JsonElement.Parse("{\"status\":\"ready\"}"),
            Questions =
            [
                new BooleanDecisionQuestion
                {
                    Id = "ready",
                    Instructions = "Is the system ready?"
                }
            ]
        };

        InferenceException exception = await Assert.ThrowsAsync<InferenceException>(
            () => inference.LoadAndWaitAsync(Pack.Key));

        Assert.Equal(InferenceFailureCode.ModelUnavailable, exception.Code);
    }

    private static readonly TensorModelPackDefinition Pack = new()
    {
        Key = new("host/laya", "1"),
        Capability = TensorCapability.Decision,
        RuntimeArchitecture = TensorDecisionModels.LayaRuntimeArchitecture,
        License = "test-only",
        SupportedBackends = new HashSet<TensorBackendKind> { TensorBackendKind.CoreMl, TensorBackendKind.Cpu },
        Artifacts = new[] { "model", "tokenizer", "agent-config" }.Select(static role => new TensorModelArtifactDefinition
        {
            Role = role,
            RelativePath = role + ".bin",
            Length = 1,
            Sha256 = new string('0', 64),
            Sources = [new Uri($"https://example.test/{role}")]
        }).ToArray()
    };

    private sealed class MissingPackManager : ITensorModelPackManager
    {
        public Task<TensorModelImportResult> ImportAsync(TensorModelImportRequest request, IProgress<TensorModelInstallProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TensorModelPackDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TensorModelPackDefinition>>([Pack]);
        public Task<TensorModelPackDefinition> RegisterAsync(TensorModelPackDefinition definition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TensorModelPackDefinition> GetRequiredDefinitionAsync(TensorModelKey key, CancellationToken cancellationToken = default) => Task.FromResult(Pack);

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
