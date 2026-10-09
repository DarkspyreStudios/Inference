using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Examples;

/// <summary>
/// Complete host-mediated tool loop for OpenAI Responses, OpenAI-compatible Chat Completions,
/// Anthropic Messages with an API key, and AWS Bedrock Converse.
/// </summary>
public static class ChatToolLane
{
    private const int MaximumToolRounds = 8;

    public static async Task<LlmChatResult> RunAsync(
        LlmProviderService providers,
        string connectionId,
        string modelId,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providers);
        LlmProviderCapabilities capabilities = await providers.GetModelCapabilitiesAsync(
            connectionId,
            modelId,
            cancellationToken);
        if ((capabilities.SupportsToolUse == InferenceSupport.Unsupported))
        {
            throw new NotSupportedException(
                "This connection does not support the chat tool lane. Use an agent session when SupportsAgentToolUse is true.");
        }

        LlmToolDefinition tool = WeatherTool.ChatDefinition;
        var messages = new List<LlmChatMessage>
        {
            new("user", prompt)
        };

        for (int round = 0; round < MaximumToolRounds; round++)
        {
            LlmChatResult result = await providers.CompleteAsync(
                connectionId,
                new LlmChatRequest
                {
                    ModelId = modelId,
                    Messages = messages,
                    Tools = [tool],
                    ToolChoice = new LlmToolChoice { Mode = LlmToolChoiceMode.Auto }
                },
                cancellationToken);

            if (result.Status != LlmCompletionStatus.RequiresAction)
            {
                return result;
            }

            if (result.ToolCalls.Count == 0)
            {
                throw new InvalidOperationException(
                    "The provider requested host action without returning a tool call.");
            }

            LlmToolResult[] toolResults =
            [
                .. result.ToolCalls.Select(call => call is LlmToolCall function
                    ? WeatherTool.Execute(function)
                    : throw new NotSupportedException("The weather example cannot execute shell tool calls."))
            ];
            messages.Add(new LlmChatMessage("assistant", result.Text)
            {
                ToolCalls = result.ToolCalls,
                ContinuationState = result.ContinuationState
            });
            messages.Add(new LlmChatMessage("user", string.Empty)
            {
                ToolResults = toolResults
            });

        }

        throw new InvalidOperationException(
            $"The model exceeded the {MaximumToolRounds}-round host tool limit.");
    }
}
