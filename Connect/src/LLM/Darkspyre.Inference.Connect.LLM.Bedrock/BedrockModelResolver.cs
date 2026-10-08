using System.Collections.Concurrent;
using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.Logging;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

internal sealed class BedrockModelResolver
{
    private static readonly Action<ILogger, string, int?, string?, string?, string, Exception?> DiscoveryFailed =
        LoggerMessage.Define<string, int?, string?, string?, string>(LogLevel.Warning, new EventId(1, "DiscoveryFailed"),
            "Bedrock {Operation} failed. StatusCode={StatusCode} ErrorCode={ErrorCode} RequestId={RequestId} Reason={Reason}");
    private static readonly Action<ILogger, Exception?> ListingFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(2, "ListingFailed"),
            "Bedrock model listing failed after both catalog requests failed.");
    private const int AvailabilityConcurrency = 8;

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

    /// <summary>
    /// Lists the models the account can call, each under an identifier it can invoke. A foundation model is
    /// listed by its model ID only when it supports on-demand inference; a model that runs only through an
    /// inference profile appears through the system inference profiles that include it. Models whose
    /// availability check reports no agreement or no authorization are omitted, and so are profiles whose
    /// models are all such models. A failed availability check fails the listing with that request's error.
    /// Each model carries the context and output limits of its base model from <see cref="BedrockModelLimits"/>.
    /// </summary>
    public static async Task<IReadOnlyList<LlmModel>> ListAsync(
        BedrockClientFactory.Lease<AmazonBedrockClient> control,
        string region,
        ILogger<BedrockLlmClient> logger,
        LlmCredential credential,
        CancellationToken cancellationToken)
    {
        control.ThrowIfCanceled();
        var failures = new List<Exception>();
        List<InferenceProfileSummary>? profiles = null;
        List<FoundationModelSummary>? foundations = null;
        try
        {
            profiles = await ListInferenceProfilesAsync(control, cancellationToken);
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
            foundations = [.. (models.ModelSummaries ?? []).Where(model =>
                !string.IsNullOrEmpty(model.ModelId) && !IsProvisionedOnly(model.InferenceTypesSupported))];
        }
        catch (AmazonBedrockException error)
        {
            failures.Add(Warn(logger, credential, "ListFoundationModels", error, failures));
            control.ThrowIfCanceled();
        }

        control.ThrowIfCanceled();
        if (profiles is null && foundations is null)
        {
            try { ListingFailed(logger, null); }
            catch (Exception error) { failures.Add(new LoggingEvidence(error)); }
            throw new AggregateException("Bedrock model listing failed.", failures);
        }

        Dictionary<string, bool>? callable = foundations is null
            ? null
            : await CheckAvailabilityAsync(control, foundations, logger, credential, cancellationToken);
        var byId = new Dictionary<string, LlmModel>(StringComparer.Ordinal);
        foreach (InferenceProfileSummary profile in profiles ?? [])
        {
            if (!string.IsNullOrEmpty(profile.InferenceProfileId)
                && (callable is null || !AllModelsUncallable(profile, callable)))
            {
                byId.TryAdd(
                    profile.InferenceProfileId,
                    Listed(profile.InferenceProfileId, profile.InferenceProfileName, region));
            }
        }

        foreach (FoundationModelSummary model in foundations ?? [])
        {
            if (callable is null
                || (callable[model.ModelId] && !IsProfileOnly(model.InferenceTypesSupported)))
            {
                byId.TryAdd(
                    model.ModelId,
                    Listed(model.ModelId, model.ModelName, region));
            }
        }

        return [.. byId.Values.OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A listed model carries its base model's published limits, or none when they are unknown.</summary>
    private static LlmModel Listed(string id, string? name, string region)
    {
        BedrockModelLimit limit = BedrockModelLimits.Get(id);
        return new LlmModel(id, name ?? id, region)
        {
            MaxContextTokens = limit.ContextTokens,
            MaxOutputTokens = limit.OutputTokens
        };
    }

    /// <summary>
    /// Returns, per foundation model ID, whether the account holds an available agreement and authorization
    /// for it. Each failed availability request is logged once and preserved in the listing failure.
    /// </summary>
    private static async Task<Dictionary<string, bool>> CheckAvailabilityAsync(
        BedrockClientFactory.Lease<AmazonBedrockClient> control,
        List<FoundationModelSummary> foundations,
        ILogger<BedrockLlmClient> logger,
        LlmCredential credential,
        CancellationToken cancellationToken)
    {
        var callable = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        var errors = new ConcurrentQueue<AmazonBedrockException>();
        await Parallel.ForEachAsync(
            foundations.Select(model => model.ModelId).Distinct(StringComparer.Ordinal),
            new ParallelOptions { MaxDegreeOfParallelism = AvailabilityConcurrency, CancellationToken = cancellationToken },
            async (modelId, token) =>
            {
                if (!errors.IsEmpty)
                {
                    return;
                }

                try
                {
                    GetFoundationModelAvailabilityResponse availability =
                        await control.Client.GetFoundationModelAvailabilityAsync(
                            new GetFoundationModelAvailabilityRequest { ModelId = modelId },
                            token);
                    control.ThrowIfCanceled();
                    callable[modelId] = availability.AgreementAvailability?.Status == AgreementStatus.AVAILABLE
                        && availability.AuthorizationStatus == AuthorizationStatus.AUTHORIZED;
                }
                catch (AmazonBedrockException error)
                {
                    errors.Enqueue(error);
                }
            });
        control.ThrowIfCanceled();
        if (!errors.IsEmpty)
        {
            var failures = new List<Exception>();
            foreach (AmazonBedrockException error in errors)
            {
                try { failures.Add(Warn(logger, credential, "GetFoundationModelAvailability", error)); }
                catch (AggregateException diagnosticFailure) { failures.AddRange(diagnosticFailure.InnerExceptions); }
            }
            if (failures.Count == 1) throw failures[0];
            throw new AggregateException("Bedrock model availability checks failed.", failures);
        }

        return new Dictionary<string, bool>(callable, StringComparer.Ordinal);
    }

    /// <summary>
    /// A profile is uncallable when it names at least one model and every named model was checked and is
    /// not callable. A model that was not checked keeps the profile.
    /// </summary>
    private static bool AllModelsUncallable(InferenceProfileSummary profile, Dictionary<string, bool> callable)
    {
        List<InferenceProfileModel> models = profile.Models ?? [];
        return models.Count > 0 && models.All(model =>
            callable.TryGetValue(ModelIdFromArn(model.ModelArn), out bool available) && !available);
    }

    private static bool IsProfileOnly(IEnumerable<string>? inferenceTypes)
    {
        string[] types = [.. inferenceTypes ?? []];
        return types.Contains("INFERENCE_PROFILE", StringComparer.Ordinal)
            && !types.Contains("ON_DEMAND", StringComparer.Ordinal);
    }

    private static string ModelIdFromArn(string? arn)
    {
        string value = arn ?? string.Empty;
        return value.Contains('/', StringComparison.Ordinal) ? value[(value.LastIndexOf('/') + 1)..] : value;
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
                if (string.Equals(ModelIdFromArn(model.ModelArn), requestedModelId, StringComparison.Ordinal))
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
        var profiles = new List<InferenceProfileSummary>();
        string? nextToken = null;
        do
        {
            control.ThrowIfCanceled();
            ListInferenceProfilesResponse response = await control.Client.ListInferenceProfilesAsync(
                new ListInferenceProfilesRequest { NextToken = nextToken },
                cancellationToken);
            control.ThrowIfCanceled();
            profiles.AddRange(response.InferenceProfileSummaries ?? []);
            nextToken = response.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return profiles;
    }

    private static LlmProviderException Warn(ILogger<BedrockLlmClient> logger, LlmCredential credential,
        string operation, AmazonBedrockException error, IReadOnlyList<Exception>? earlierFailures = null)
    {
        System.Net.HttpStatusCode? status = (int)error.StatusCode == 0 ? null : error.StatusCode;
        string? code = error.ErrorCode is null ? null : SecretRedactor.Sanitize(error.ErrorCode, credential);
        string? requestId = error.RequestId is null ? null : SecretRedactor.Sanitize(error.RequestId, credential);
        string reason = SecretRedactor.Sanitize(error.Message, credential);
        var failure = new LlmProviderException($"Bedrock model discovery request failed: {reason}",
            LlmTransport.AwsBedrockConverse, status, code, requestId, innerException: new SdkEvidence(error));
        try
        {
            DiscoveryFailed(logger, operation, status is null ? null : (int?)status.Value, code, requestId, reason, null);
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
