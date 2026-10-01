namespace Darkspyre.Inference.Connect.LLM;

/// <summary>The field that carries an explicit output-token limit on a Chat Completions endpoint.</summary>
public enum LlmChatCompletionsOutputLimitField
{
    MaxTokens,
    MaxCompletionTokens
}

/// <summary>Non-secret protocol choices for an OpenAI-compatible Chat Completions connection.</summary>
public sealed record LlmChatCompletionsConfiguration
{
    /// <summary>Null selects max_completion_tokens on api.openai.com and max_tokens elsewhere.</summary>
    public LlmChatCompletionsOutputLimitField? OutputLimitField { get; init; }

    /// <summary>Null maps ConversationKey to prompt_cache_key only on api.openai.com.</summary>
    public bool? UsePromptCacheKey { get; init; }
}
