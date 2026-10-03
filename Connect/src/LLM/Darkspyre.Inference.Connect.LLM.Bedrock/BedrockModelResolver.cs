using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.Logging;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

internal sealed class BedrockModelResolver
{
    public static async Task<string> ResolveAsync(
        BedrockClientFactory.Lease<AmazonBedrockClient> control,
        string region,
        string requestedModelId,
        ILogger<BedrockLlmClient> logger,
        LlmCredential credential,
        CancellationToken cancellationToken)
    {
        control.ThrowIfCanceled();
        if (LooksLikeInferenceProfile(requestedModelId))
        {
            return requestedModelId;
        }

        try
        {
            GetFoundationModelResponse details = await control.Client.GetFoundationModelAsync(
                new GetFoundationModelRequest { ModelIdentifier = requestedModelId },
                cancellationToken);
            control.ThrowIfCanceled();
            var types = details.ModelDetails?.InferenceTypesSupported ?? [];
            bool supportsOnDemand = types.Any(type => string.Equals(
                type,
                "ON_DEMAND",
                StringComparison.Ordinal));
            bool supportsProfile = types.Any(type => string.Equals(
                type,
                "INFERENCE_PROFILE",
                StringComparison.Ordinal));
            if (supportsOnDemand || !supportsProfile)
            {
                return requestedModelId;
            }

            string? profile = await FindProfileForModelAsync(
                control,
                region,
                requestedModelId,
                cancellationToken);
            control.ThrowIfCanceled();
            return profile ?? requestedModelId;
        }
        catch (AmazonBedrockException error)
        {
            _ = Warn(logger, credential, "ResolveModel", error);
            control.ThrowIfCanceled();
            return requestedModelId;
        }
    }

    public static async Task<IReadOnlyList<LlmModel>> ListAsync(
        BedrockClientFactory.Lease<AmazonBedrockClient> control,
        string region,
        ILogger<BedrockLlmClient> logger,
        LlmCredential credential,
        CancellationToken cancellationToken)
    {
        control.ThrowIfCanceled();
        var byId = new Dictionary<string, LlmModel>(StringComparer.Ordinal);
        var failures = new List<Exception>();
        bool profilesSucceeded = false;
        bool foundationsSucceeded = false;
        try
        {
            List<InferenceProfileSummary> profiles = await ListInferenceProfilesAsync(control, cancellationToken);
            profilesSucceeded = true;
            foreach (InferenceProfileSummary profile in profiles)
            {
                if (!string.IsNullOrEmpty(profile.InferenceProfileId))
                {
                    byId.TryAdd(
                        profile.InferenceProfileId,
                        new LlmModel(
                            profile.InferenceProfileId,
                            profile.InferenceProfileName ?? profile.InferenceProfileId,
                            region));
                }
            }
        }
        catch (AmazonBedrockException error)
        {
            failures.Add(Warn(logger, credential, "ListInferenceProfiles", error, failures));
            control.ThrowIfCanceled();
        }

        try
        {
            control.ThrowIfCanceled();
            ListFoundationModelsResponse models = await control.Client.ListFoundationModelsAsync(
                new ListFoundationModelsRequest(),
                cancellationToken);
            control.ThrowIfCanceled();
            foundationsSucceeded = true;
            foreach (FoundationModelSummary model in models.ModelSummaries ?? [])
            {
                if (!string.IsNullOrEmpty(model.ModelId) && !IsProvisionedOnly(model.InferenceTypesSupported))
                {
                    byId.TryAdd(
                        model.ModelId,
                        new LlmModel(model.ModelId, model.ModelName ?? model.ModelId, region));
                }
            }
        }
        catch (AmazonBedrockException error)
        {
            failures.Add(Warn(logger, credential, "ListFoundationModels", error, failures));
            control.ThrowIfCanceled();
        }

        control.ThrowIfCanceled();
        if (!profilesSucceeded && !foundationsSucceeded)
        {
            try { logger.LogError("Bedrock model listing failed after both catalog requests failed."); }
            catch (Exception error) { failures.Add(new LoggingEvidence(error)); }
            throw new AggregateException("Bedrock model listing failed.", failures);
        }

        return [.. byId.Values.OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)];
    }

    private static async Task<string?> FindProfileForModelAsync(
        BedrockClientFactory.Lease<AmazonBedrockClient> control,
        string region,
        string requestedModelId,
        CancellationToken cancellationToken)
    {
        var matches = new List<string>();
        foreach (InferenceProfileSummary profile in await ListInferenceProfilesAsync(
                     control,
                     cancellationToken))
        {
            if (string.IsNullOrEmpty(profile.InferenceProfileId))
            {
                continue;
            }

            foreach (InferenceProfileModel model in profile.Models ?? [])
            {
                string arn = model.ModelArn ?? string.Empty;
                string modelId = arn.Contains('/', StringComparison.Ordinal)
                    ? arn[(arn.LastIndexOf('/') + 1)..]
                    : arn;
                if (string.Equals(modelId, requestedModelId, StringComparison.Ordinal))
                {
                    matches.Add(profile.InferenceProfileId);
                    break;
                }
            }
        }

        return PreferredProfilePrefixes(region)
            .Select(prefix => matches.FirstOrDefault(id => id.StartsWith(prefix, StringComparison.Ordinal)))
            .FirstOrDefault(match => match is not null)
            ?? matches.FirstOrDefault();
    }

    private static async Task<List<InferenceProfileSummary>> ListInferenceProfilesAsync(
        BedrockClientFactory.Lease<AmazonBedrockClient> control,
        CancellationToken cancellationToken)
    {
        control.ThrowIfCanceled();
        ListInferenceProfilesResponse response = await control.Client.ListInferenceProfilesAsync(
            new ListInferenceProfilesRequest(),
            cancellationToken);
        control.ThrowIfCanceled();
        return response.InferenceProfileSummaries ?? [];
    }

    private static LlmProviderException Warn(ILogger<BedrockLlmClient> logger, LlmCredential credential,
        string operation, AmazonBedrockException error, IReadOnlyList<Exception>? earlierFailures = null)
    {
        System.Net.HttpStatusCode? status = (int)error.StatusCode == 0 ? null : error.StatusCode;
        string? code = error.ErrorCode is null ? null : SecretRedactor.Sanitize(error.ErrorCode, credential);
        string? requestId = error.RequestId is null ? null : SecretRedactor.Sanitize(error.RequestId, credential);
        var failure = new LlmProviderException("Bedrock model discovery request failed.",
            LlmTransport.AwsBedrockConverse, status, code, requestId, innerException: new SdkEvidence(error));
        try
        {
            logger.LogWarning("Bedrock {Operation} failed. StatusCode={StatusCode} ErrorCode={ErrorCode} RequestId={RequestId}",
                operation, status is null ? null : (int?)status.Value, code, requestId);
        }
        catch (Exception loggingFailure)
        {
            throw new AggregateException("Bedrock model discovery and diagnostic logging failed.",
                [.. earlierFailures ?? [], failure, new LoggingEvidence(loggingFailure)]);
        }
        return failure;
    }

    private sealed class SdkEvidence(AmazonBedrockException original) : Exception("Bedrock model discovery failed.")
    {
        internal AmazonBedrockException Original { get; } = original;
    }

    private sealed class LoggingEvidence(Exception original) : Exception("Bedrock diagnostic logging failed.")
    {
        internal Exception Original { get; } = original;
    }

    /// <summary>
    /// A provisioned-only model, such as a context-window variant like <c>amazon.nova-lite-v1:0:300k</c>,
    /// runs only through purchased provisioned throughput, so a connection cannot invoke it by model ID.
    /// A model that reports no inference types is kept.
    /// </summary>
    internal static bool IsProvisionedOnly(IEnumerable<string>? inferenceTypes)
    {
        string[] types = [.. inferenceTypes ?? []];
        return types.Length > 0 && types.All(type => string.Equals(type, "PROVISIONED", StringComparison.Ordinal));
    }

    internal static bool LooksLikeInferenceProfile(string modelId) =>
        modelId.StartsWith("arn:", StringComparison.Ordinal)
        || modelId.StartsWith("us.", StringComparison.Ordinal)
        || modelId.StartsWith("eu.", StringComparison.Ordinal)
        || modelId.StartsWith("apac.", StringComparison.Ordinal)
        || modelId.StartsWith("global.", StringComparison.Ordinal);

    internal static string[] PreferredProfilePrefixes(string region) =>
        region.StartsWith("us-", StringComparison.Ordinal)
            ? ["us.", "global."]
            : region.StartsWith("eu-", StringComparison.Ordinal)
                ? ["eu.", "global."]
                : region.StartsWith("ap-", StringComparison.Ordinal)
                    ? ["apac.", "global."]
                    : ["global."];
}
