namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Streaming adapter for OpenAI's Responses API.</summary>
internal sealed class OpenAiResponsesLlmClient(HttpClient http) : ResponsesLlmClientBase(http)
{
    public override LlmTransport Transport => LlmTransport.OpenAiResponses;

    public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.OpenAiApiKey;

    protected override LlmProviderCapabilities DefaultCapabilities { get; } = new()
    {
        SupportsStreaming = true,
        SupportsStreamingUsage = true,
        SupportsModelListing = true,
        SupportsJsonSchema = true,
        SupportsJsonObject = true,
        SupportsReasoningEffort = true,
        SupportsTemperature = true,
        SupportsToolUse = true,
        SupportsPromptImages = true,
        SupportsPromptDocuments = true,
        SupportsToolResultImages = true,
        SupportsToolResultDocuments = true
    };

}
