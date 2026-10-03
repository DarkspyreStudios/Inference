using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;
using CommonLlm = Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// Runs a SelfHost connection on the host's in-process engine. The connection's endpoint is the engine ID;
/// the engine is Common's <see cref="CommonLlm.ILlmInference"/>, so Connect references no engine library.
/// </summary>
internal sealed class SelfHostLlmClient(ILlmSelfHost host) : ILlmClient, ILlmContextEstimator
{
    public LlmTransport Transport => LlmTransport.SelfHost;

    public LlmProviderDefinition ProviderDefinition => ProviderDefinitions.SelfHost;

    public string? EstimateDiagnostic => LocalTokenCount.CliDiagnostic;

    // Connect runs the managed lane: the host owns context and tools. The engine takes text tool results only.
    private static readonly LlmAgentCapabilities Agent = ManagedAgentPolicy.Capabilities with { RichContentSupported = new() };

    /// <summary>Lists the host's engines for the settings' engine choice.</summary>
    public Task<IReadOnlyList<LlmSelfHostEngine>> ListEnginesAsync(CancellationToken cancellationToken) =>
        host.ListEnginesAsync(cancellationToken);

    /// <summary>The loaded engine's capabilities; an engine that is not loaded offers streaming only.</summary>
    public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        RuntimeInputAdmission.Validate(connection);
        CommonLlm.LlmCapabilities? engine = host.GetCapabilities(connection.Endpoint, modelId);
        return engine is null
            ? new LlmProviderCapabilities { SupportsStreaming = true, SupportsModelListing = true, SupportsAgentSessions = true, Agent = Agent }
            : ToConnect(engine);
    }

    public async Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        RuntimeInputAdmission.Validate(connection);
        CommonLlm.ILlmInference engine = await host.GetInferenceAsync(connection.Endpoint, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CommonLlm.LlmModelInfo> models = await engine.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        return models.Select(model => new LlmModel(model.Id, model.DisplayName, model.OwnedBy,
            model.Capabilities is null ? null : ToConnect(model.Capabilities))
        {
            MaxContextTokens = model.MaxContextTokens,
            Availability = model.Availability,
            AvailabilityCheckedAt = model.AvailabilityCheckedAt,
            AvailabilityReason = model.AvailabilityReason,
            MaxOutputTokens = model.MaxOutputTokens
        }).ToArray();
    }

    public async IAsyncEnumerable<CommonLlm.LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        request = ClientTools.Snapshot(request);
        ValidateOptions(connection, request);
        HostedTools.Validate(request, GetCapabilities(connection, request.ModelId));
        ClientTools.Validate(request, GetCapabilities(connection, request.ModelId).SupportsShellTools);
        CommonLlm.ILlmInference engine = await host.GetInferenceAsync(connection.Endpoint, cancellationToken).ConfigureAwait(false);
        var capabilities = ToConnect(await engine.GetCapabilitiesAsync(request.ModelId, cancellationToken).ConfigureAwait(false));
        LlmClientBase.ValidateSampling(request, capabilities);
        HostedTools.Validate(request, capabilities);
        ClientTools.Validate(request, capabilities.SupportsShellTools);
        await foreach (CommonLlm.LlmStreamEvent item in engine.StreamAsync(ToCommon(request), cancellationToken).ConfigureAwait(false))
        {
            // Common's events pass through; only the completion carries Connect's result.
            yield return item is CommonLlm.LlmCompletionEvent completion ? new LlmChatCompletionEvent(ToConnect(completion.Result)) : item;
        }
    }

    /// <summary>Counts the request's text locally. The engine's own tokenizer can differ.</summary>
    public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        ValidateOptions(connection, request);
        HostedTools.Validate(request, GetCapabilities(connection, request.ModelId));
        ClientTools.Validate(request, GetCapabilities(connection, request.ModelId).SupportsShellTools);
        LlmClientBase.ValidateSampling(request, GetCapabilities(connection, request.ModelId));
        var text = new StringBuilder(request.Instructions);
        foreach (LlmChatMessage message in request.Messages)
        {
            text.Append('\n').Append(message.Content);
            foreach (CommonLlm.LlmToolCall call in ClientTools.Functions(message.ToolCalls)) { text.Append('\n').Append(call.Name).Append(call.ArgumentsJson); }
            foreach (CommonLlm.LlmToolResult result in ClientTools.Functions(message.ToolResults))
            {
                foreach (CommonLlm.LlmToolOutputText part in result.Output.Parts.OfType<CommonLlm.LlmToolOutputText>()) { text.Append('\n').Append(part.Text); }
            }
        }
        foreach (CommonLlm.LlmToolDefinition tool in ClientTools.Functions(request.Tools))
        {
            text.Append('\n').Append(tool.Name).Append(tool.Description).Append(tool.InputSchema.GetRawText());
        }
        return new ContextEstimate(LocalTokenCount.Count(text.ToString()));
    }

    private static void ValidateOptions(LlmConnection connection, LlmChatRequest request)
    {
        if (UnsupportedOptions(connection, request) is { } issue) { throw new NotSupportedException(issue.Message); }
    }

    internal static (string Code, string Message)? UnsupportedOptions(LlmConnection connection, LlmChatRequest? request = null)
    {
        try { RuntimeInputAdmission.Validate(connection); }
        catch (NotSupportedException error) { return ("self-host-runtime-inputs", error.Message); }
        if ((request?.ProviderControls ?? connection.Xai?.MultiAgent) is not null)
        { return ("self-host-provider-controls", "SelfHost does not forward connector-owned provider controls."); }
        if (request?.ExplicitCache is not null)
        { return ("self-host-explicit-cache", "SelfHost does not forward native provider cache receipts."); }
        if (request?.Resources is not null)
        { return ("self-host-resource-authority", "SelfHost does not materialize caller-owned resources."); }
        if (request?.Messages.Any(message => message.Attachments.Any(attachment => attachment is not CommonLlm.LlmAttachment)
            || message.ToolResults.Any(result => result is CommonLlm.LlmToolResult function
                && function.Output.Parts.Any(part => part is CommonLlm.LlmToolOutputSessionResource))) is true)
        { return ("self-host-resource-references", "SelfHost does not materialize referenced prompt or tool content."); }
        if (connection.ProviderOptions.Count > 0 || request?.ProviderOptions.Count > 0)
        { return ("self-host-provider-options", "A SelfHost engine accepts no provider request options. Remove ProviderOptions from the connection and request."); }
        if (connection.AdditionalHeaders.Count > 0)
        { return ("self-host-headers", "A SelfHost engine accepts no HTTP headers. Remove AdditionalHeaders from the connection."); }
        if (connection.CapabilityOverrides is not null || connection.ModelCapabilityOverrides.Count > 0)
        { return ("self-host-capability-overrides", "A SelfHost engine owns its capabilities. Remove connection and model capability overrides."); }
        return null;
    }

    private static CommonLlm.LlmInferenceRequest ToCommon(LlmChatRequest request) => new()
    {
        ModelId = request.ModelId,
        Instructions = request.Instructions,
        Messages = request.Messages.Select(static message => new CommonLlm.LlmMessage(message.Role, message.Content)
        {
            ContinuationState = message.ContinuationState,
            Attachments = message.Attachments,
            ToolCalls = message.ToolCalls,
            ToolResults = message.ToolResults
        }).ToArray(),
        Temperature = request.Temperature,
        TopP = request.TopP,
        TopK = request.TopK,
        Seed = request.Seed,
        PresencePenalty = request.PresencePenalty,
        FrequencyPenalty = request.FrequencyPenalty,
        StopSequences = request.StopSequences,
        MaxOutputTokens = request.MaxOutputTokens,
        ReasoningLevel = request.ReasoningLevel,
        StructuredOutput = request.StructuredOutput,
        Tools = request.Tools,
        HostedTools = request.HostedTools,
        MaxHostedTurns = request.MaxHostedTurns,
        ToolChoice = request.ToolChoice,
        AllowParallelToolCalls = request.AllowParallelToolCalls,
        UnboundContinuationState = request.UnboundContinuationState,
        ConversationKey = request.ConversationKey,
        PromptCache = request.PromptCache,
        ExplicitCache = request.ExplicitCache,
        ProviderControls = request.ProviderControls,
        Resources = request.Resources
    };

    private static LlmChatResult ToConnect(CommonLlm.LlmInferenceResult result) => new()
    {
        Text = result.Text,
        ModelId = result.Provenance?.ModelId,
        ModelRevision = result.Provenance?.ModelRevision,
        Refusal = result.Refusal,
        RefusalCategory = result.RefusalCategory,
        Status = result.Status,
        FinishReason = result.FinishReason,
        IncompleteReason = result.IncompleteReason,
        Usage = result.Usage,
        ContinuationState = result.ContinuationState,
        OmittedContinuationStateCount = result.OmittedContinuationStateCount,
        ToolCalls = result.ToolCalls,
        Citations = result.Citations,
        GeneratedResources = result.GeneratedResources,
        StructuredOutput = result.StructuredOutput,
        // An in-process call has no HTTP exchange; 200 records that it completed.
        Diagnostics = new(200, null, null)
    };

    private static LlmProviderCapabilities ToConnect(CommonLlm.LlmCapabilities capabilities) => new()
    {
        OutputLimitEnforcement = capabilities.OutputLimitEnforcement,
        SupportsStreaming = capabilities.SupportsStreaming,
        SupportsUsageDeltas = capabilities.SupportsUsageDeltas,
        SupportsReasoningText = capabilities.SupportsReasoningText,
        SupportsStreamingUsage = capabilities.SupportsStreamingUsage,
        SupportsModelListing = capabilities.SupportsModelListing,
        SupportsJsonSchema = capabilities.SupportsJsonSchema,
        SupportsJsonObject = capabilities.SupportsJsonObject,
        PromptCache = capabilities.PromptCache,
        ReasoningLevels = capabilities.ReasoningLevels,
        SupportsTemperature = capabilities.SupportsTemperature,
        SupportsTopP = capabilities.SupportsTopP,
        SupportsTopK = capabilities.SupportsTopK,
        SupportsSeed = capabilities.SupportsSeed,
        SupportsPresencePenalty = capabilities.SupportsPresencePenalty,
        SupportsFrequencyPenalty = capabilities.SupportsFrequencyPenalty,
        MaxStopSequences = capabilities.MaxStopSequences,
        SupportsToolUse = capabilities.SupportsToolUse,
        SupportsShellTools = capabilities.SupportsShellTools,
        Tools = capabilities.Tools,
        HostedTools = capabilities.HostedTools,
        ToolHistory = LlmToolHistory.Structured,
        SupportsAgentSessions = true,
        SupportsAgentToolUse = capabilities.SupportsToolUse,
        Agent = Agent,
        SupportsPromptImages = capabilities.SupportsPromptImages,
        SupportsPromptDocuments = capabilities.SupportsPromptDocuments,
        SupportsFileReferences = capabilities.SupportsFileReferences
    };
}
