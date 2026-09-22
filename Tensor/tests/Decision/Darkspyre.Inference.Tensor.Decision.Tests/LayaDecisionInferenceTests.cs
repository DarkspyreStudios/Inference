using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Decision;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Decision.Tests;

public sealed class LayaDecisionInferenceTests
{
    [Fact]
    public async Task CapabilitiesDescribeAllTypedDecisionKinds()
    {
        await using var inference = new LayaDecisionInference(new MissingPackManager());

        DecisionCapabilities capabilities = await inference.GetCapabilitiesAsync(
            TensorDecisionModels.LayaTypedDecisions.Key.ModelId);

        Assert.Equal(3, capabilities.SupportedQuestionKinds.Count);
        Assert.Contains(DecisionQuestionKind.Choice, capabilities.SupportedQuestionKinds);
        Assert.Contains(DecisionQuestionKind.Score, capabilities.SupportedQuestionKinds);
        Assert.Contains(DecisionQuestionKind.Boolean, capabilities.SupportedQuestionKinds);
        Assert.Equal(1_024, capabilities.MaximumInputTokens);
        Assert.True(capabilities.SupportsCalibratedConfidence);
        Assert.True(capabilities.SupportsActionProbability);
    }

    [Fact]
    public async Task InvalidQuestionFailsBeforeModelMaterialization()
    {
        await using var inference = new LayaDecisionInference(new MissingPackManager());
        var request = new DecisionInferenceRequest
        {
            ModelId = TensorDecisionModels.LayaTypedDecisions.Key.ModelId,
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
            ModelId = TensorDecisionModels.LayaTypedDecisions.Key.ModelId,
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
            () => inference.DecideAsync(request));

        Assert.Equal(InferenceFailureCode.ModelUnavailable, exception.Code);
    }

    [Fact]
    public void ModelPackPinsEveryArtifact()
    {
        TensorModelPackDefinition pack = TensorDecisionModels.LayaTypedDecisions;

        Assert.Equal(TensorCapability.Decision, pack.Capability);
        Assert.Equal(5, pack.Artifacts.Count);
        Assert.All(pack.Artifacts, artifact =>
        {
            Assert.True(artifact.Length > 0);
            Assert.Equal(64, artifact.Sha256.Length);
            Assert.Single(artifact.Sources);
            Assert.Contains(pack.Key.Revision, artifact.Sources[0].AbsoluteUri, StringComparison.Ordinal);
        });
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
