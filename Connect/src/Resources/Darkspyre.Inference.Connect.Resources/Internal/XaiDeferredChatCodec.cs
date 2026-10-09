using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.Resources.Internal;

/// <summary>Route policy over the existing canonical Chat body and complete-response codec.</summary>
internal sealed class XaiDeferredChatCodec(HttpClient http) : ChatCompletionsLlmClientBase(http)
{
    private static readonly FrozenSet<string> OwnedFields = new[]
    {
        "model", "messages", "stream", "stream_options", "deferred", "service_tier", "agent_count",
        "temperature", "top_p", "top_k", "seed", "presence_penalty", "frequency_penalty", "stop", "stop_sequences",
        "max_tokens", "max_completion_tokens", "reasoning", "reasoning_effort", "tools", "tool_choice",
        "parallel_tool_calls", "response_format", "prompt_cache_key"
    }.ToFrozenSet(StringComparer.Ordinal);

    public override LlmTransport Transport => LlmTransport.XaiResponses;
    public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.GrokSubscription;
    protected override bool SupportsResourcePreparation => true;
    protected override bool OffersUsageDeltas => false;
    protected override LlmProviderCapabilities DefaultCapabilities => base.DefaultCapabilities with
    {
        OutputLimitEnforcement = LlmOutputLimitEnforcement.ProviderEnforced,
        SupportsStreaming = InferenceSupport.Unsupported,
        SupportsStreamingUsage = InferenceSupport.Unsupported,
        SupportsJsonSchema = InferenceSupport.Supported,
        SupportsJsonObject = InferenceSupport.Supported,
        SupportsPromptImages = InferenceSupport.Supported,
        SupportsPromptDocuments = InferenceSupport.Supported,
        SupportsFileReferences = InferenceSupport.Supported,
        Tools = base.DefaultCapabilities.Tools with { StrictInputSchema = LlmStrictInputSchemaSupport.Supported }
    };

    public override LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
    {
        XaiRequestHeaders.ValidateEndpoint(connection);
        XaiModelProfile profile = XaiModelProfiles.Get(connection, modelId);
        bool reasoning = profile.DefaultReasoningLevel is not null;
        LlmProviderCapabilities defaults = DefaultCapabilities with
        {
            ReasoningLevels = profile.ReasoningLevels,
            SupportsTemperature = (profile.AllowsSampling ? InferenceSupport.Supported : InferenceSupport.Unsupported),
            SupportsTopP = (profile.AllowsSampling ? InferenceSupport.Supported : InferenceSupport.Unsupported),
            SupportsPromptImages = (profile.AcceptsImages ? InferenceSupport.Supported : InferenceSupport.Unsupported),
            SupportsPresencePenalty = (!reasoning ? InferenceSupport.Supported : InferenceSupport.Unsupported),
            SupportsFrequencyPenalty = (!reasoning ? InferenceSupport.Supported : InferenceSupport.Unsupported),
            MaxStopSequences = reasoning ? 0 : 4
        };
        LlmProviderCapabilities capabilities = LlmCapabilityOverrideLookup.Apply(defaults, connection, modelId, false);
        return capabilities with
        {
            SupportsStreaming = InferenceSupport.Unsupported,
            SupportsStreamingUsage = InferenceSupport.Unsupported,
            SupportsTopK = InferenceSupport.Unsupported,
            SupportsExplicitCache = InferenceSupport.Unsupported,
            SupportsPromptImages = ((capabilities.SupportsPromptImages != InferenceSupport.Unsupported) && profile.AcceptsImages ? InferenceSupport.Supported : InferenceSupport.Unsupported),
            SupportsShellTools = InferenceSupport.Unsupported,
            HostedTools = FrozenSet<LlmHostedToolKind>.Empty
        };
    }

    internal void ValidateSubmissionRequest(LlmConnection connection, LlmChatRequest request,
        LlmProviderCapabilities capabilities, IInferenceResourceAccess? admittedResources)
    {
        LlmResourcePreparation.ValidateArguments(request, admittedResources);
        ValidatePreparedRequest(connection, request, capabilities);
    }

    protected override void ValidateAdditionalRequest(LlmConnection connection, LlmChatRequest request)
    {
        request = ProviderControlPolicy.Resolve(connection, request);
        _ = XaiRequestHeaders.Require(connection);
        ProviderHttp.ValidateAdditionalHeaders(connection.AdditionalHeaders);
        if (connection.ProviderOptions.Keys.Concat(request.ProviderOptions.Keys).Any(OwnedFields.Contains))
            throw new ArgumentException("Deferred chat controls use typed request and connection properties.", nameof(request));
        if (request.Temperature is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(request), "Grok temperature must be in [0, 2].");
        if (request.PresencePenalty is < -2 or > 2 || request.FrequencyPenalty is < -2 or > 2)
            throw new ArgumentOutOfRangeException(nameof(request), "Grok sampling penalties must be in [-2, 2].");
        if (request.Tools.Count > 350)
            throw new NotSupportedException("Grok accepts at most 350 client functions.");
        foreach (LlmToolDefinition tool in ClientTools.Functions(request.Tools))
        {
            if (tool.Name == "web_search") throw new ArgumentException("A Grok client function cannot be named web_search.", nameof(request));
            if (tool.Strict) XaiSchemaLimits.ValidateStrictTool(tool.InputSchema);
        }
        foreach (LlmFileReference file in request.Messages.SelectMany(message => message.Attachments).OfType<LlmFileReference>())
            _ = XaiFileRequestData.Identity(file.File.FileId);
        foreach (LlmAttachment binary in request.Messages.SelectMany(message => message.Attachments).OfType<LlmAttachment>())
            if (binary.Kind == LlmBinaryKind.Image && binary.Data.Length > 20 * 1024 * 1024)
                throw new ArgumentException("Grok input images must not exceed 20 MiB.", nameof(request));
        _ = XaiSchemaLimits.ResolveEnforcement(request.StructuredOutput, GetCapabilities(connection, request.ModelId));
    }

    protected override JsonObject BuildPayload(LlmConnection connection, LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement, LlmProviderCapabilities capabilities,
        LlmResourcePreparation? resources = null)
    {
        request = ProviderControlPolicy.Resolve(connection, request);
        JsonObject body = base.BuildPayload(connection, request, enforcement, capabilities, resources);
        body["deferred"] = true;
        if (connection.Xai?.ServiceTier == XaiServiceTier.Priority) body["service_tier"] = "priority";
        if (request.ProviderControls is XaiMultiAgentControls { AgentCount: { } count })
            body["reasoning_effort"] = XaiModelProfiles.MultiAgentEffort(
                XaiModelProfiles.Get(connection, request.ModelId).MultiAgent!, count);
        return body;
    }

    protected override bool UsePromptCacheKey(LlmConnection connection) => true;
    protected override LlmUsage? ReadUsage(JsonElement root) => XaiUsage.Enrich(base.ReadUsage(root), root);
}
