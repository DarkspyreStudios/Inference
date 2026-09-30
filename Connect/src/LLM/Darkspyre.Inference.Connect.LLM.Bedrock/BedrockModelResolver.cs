using Amazon.Bedrock;
using Amazon.Bedrock.Model;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

internal sealed class BedrockModelResolver
{
    public static async Task<string> ResolveAsync(
        AmazonBedrockClient control,
        string region,
        string requestedModelId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (LooksLikeInferenceProfile(requestedModelId))
        {
            return requestedModelId;
        }

        try
        {
            GetFoundationModelResponse details = await control.GetFoundationModelAsync(
                new GetFoundationModelRequest { ModelIdentifier = requestedModelId },
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
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

            return await FindProfileForModelAsync(
                       control,
                       region,
                       requestedModelId,
                       cancellationToken)
                ?? requestedModelId;
        }
        catch (AmazonBedrockException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return requestedModelId;
        }
    }

    public static async Task<IReadOnlyList<LlmModel>> ListAsync(
        AmazonBedrockClient control,
        string region,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var byId = new Dictionary<string, LlmModel>(StringComparer.Ordinal);
        foreach (InferenceProfileSummary profile in await ListInferenceProfilesAsync(
                     control,
                     cancellationToken))
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

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListFoundationModelsResponse models = await control.ListFoundationModelsAsync(
                new ListFoundationModelsRequest(),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
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
        catch (AmazonBedrockException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Inference profiles alone remain useful when foundation-model listing is denied.
        }

        return [.. byId.Values.OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)];
    }

    private static async Task<string?> FindProfileForModelAsync(
        AmazonBedrockClient control,
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
        AmazonBedrockClient control,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListInferenceProfilesResponse response = await control.ListInferenceProfilesAsync(
                new ListInferenceProfilesRequest(),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return response.InferenceProfileSummaries ?? [];
        }
        catch (AmazonBedrockException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return [];
        }
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
