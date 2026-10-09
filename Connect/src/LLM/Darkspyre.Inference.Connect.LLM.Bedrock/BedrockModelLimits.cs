using System.Collections.Frozen;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

/// <summary>The published context window and maximum output of one Bedrock model. A null value is unknown.</summary>
internal sealed record BedrockModelLimit(long? ContextTokens, long? OutputTokens);

/// <summary>
/// Context and output limits for Bedrock models. Bedrock's ListFoundationModels and ListInferenceProfiles
/// report no sizes, so this table holds the limits the provider publishes. Claude models read the shared
/// <see cref="ClaudeModelLimits"/> by Anthropic model id; other keys use the exact Bedrock base id. Geographic
/// profiles share their base model's entry.
/// A model that is not in the table has unknown limits.
/// </summary>
internal static class BedrockModelLimits
{
    // Sources:
    // [NM] https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-amazon-nova-micro.html
    // [NL] https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-amazon-nova-lite.html
    // [NP] https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-amazon-nova-pro.html
    // [N2] https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-amazon-nova-2-lite.html
    //      Output remains unknown: the card says 64K, but the Converse guide says 65,000:
    //      https://docs.aws.amazon.com/nova/latest/nova2-userguide/using-converse-api.html
    // [G20] https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-openai-gpt-oss-20b.html
    // [G120] https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-openai-gpt-oss-120b.html
    //      AWS specifies 128,000 context tokens: https://docs.aws.amazon.com/bedrock/latest/userguide/model-parameters-openai.html
    private static readonly FrozenDictionary<string, BedrockModelLimit> Models = new Dictionary<string, BedrockModelLimit>
    {
        ["amazon.nova-micro-v1:0"] = new(128_000, 5_000), // [NM]
        ["amazon.nova-lite-v1:0"] = new(300_000, 5_000), // [NL]
        ["amazon.nova-pro-v1:0"] = new(300_000, 5_000), // [NP]
        ["amazon.nova-2-lite-v1:0"] = new(1_000_000, null), // [N2]
        ["openai.gpt-oss-20b-1:0"] = new(128_000, 16_000), // [G20]
        ["openai.gpt-oss-120b-1:0"] = new(128_000, 16_000) // [G120]
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly BedrockModelLimit Unknown = new(null, null);

    /// <summary>The limits of a foundation-model id or inference-profile id. An ARN or unlisted model is unknown.</summary>
    public static BedrockModelLimit Get(string modelId)
    {
        (BedrockModelFamily family, string baseModelId) = BedrockModelFamilies.Classify(modelId);
        string key = family == BedrockModelFamily.AnthropicClaude
            ? BedrockModelFamilies.AnthropicModelId(baseModelId) : baseModelId;
        if (family == BedrockModelFamily.AnthropicClaude)
        {
            ClaudeModelLimit claude = ClaudeModelLimits.Get(key);
            return new(claude.ContextTokens, claude.OutputTokens);
        }
        return Models.TryGetValue(key, out BedrockModelLimit? limit) ? limit : Unknown;
    }
}
