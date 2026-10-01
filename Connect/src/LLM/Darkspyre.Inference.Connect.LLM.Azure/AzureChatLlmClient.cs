using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Azure;

internal sealed class AzureChatLlmClient(HttpClient http) : ChatCompletionsLlmClientBase(http)
{
    public override LlmTransport Transport => LlmTransport.AzureOpenAiChat;
    public override LlmProviderDefinition ProviderDefinition => AzureProviderDefinition.Chat;
    public override LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
    {
        EnsureTransport(connection);
        if (connection.ChatCompletions?.OutputLimitField is { } field && !Enum.IsDefined(field))
            throw new ArgumentOutOfRangeException(nameof(connection), "The Chat Completions output-limit field is invalid.");
        return AzureModelProfiles.Resolve(connection, modelId, DefaultCapabilities, responses: false, OffersUsageDeltas);
    }
    protected override bool IsOpenRouter(LlmConnection connection) => false;
    protected override Uri BuildRequestUri(LlmConnection connection, LlmChatRequest request) => AzureEndpoints.Generation(connection, request.ModelId);
    protected override void ApplyRequestHeaders(HttpRequestMessage message, LlmConnection connection, LlmChatRequest request) => AzureEndpoints.Authorize(message, connection);
    protected override void ValidateModelSampling(LlmConnection connection, LlmChatRequest request) => AzureModelProfiles.ValidateSampling(connection, request);
    protected override string ReasoningWord(LlmReasoningLevel level) => LlmReasoningEffort.OpenAiWord(level);
    protected override LlmChatCompletionsOutputLimitField OutputLimitField(LlmConnection connection) =>
        connection.ChatCompletions?.OutputLimitField ?? LlmChatCompletionsOutputLimitField.MaxCompletionTokens;
    protected override bool UsePromptCacheKey(LlmConnection connection) => connection.ChatCompletions?.UsePromptCacheKey == true;
    protected override LlmAccountUsageSnapshot? ObserveAccountUsage(HttpResponseMessage response) => null;
}
