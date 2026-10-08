using System.Collections.Frozen;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

/// <summary>The published context window and maximum output of one Bedrock model. A null value is unknown.</summary>
internal sealed record BedrockModelLimit(long? ContextTokens, long? OutputTokens);

/// <summary>
/// Context and output limits for Bedrock models. Bedrock's ListFoundationModels and ListInferenceProfiles
/// report no sizes, so this table holds the limits the provider publishes. Claude keys use the Anthropic model
/// id; other keys use the exact Bedrock base id. Geographic profiles share their base model's entry.
/// A model that is not in the table has unknown limits.
/// </summary>
internal static class BedrockModelLimits
{
    // Sources:
    // [B] Claude on Amazon Bedrock, "Context window":
    //     https://platform.claude.com/docs/en/build-with-claude/claude-on-amazon-bedrock-legacy#context-window
    //     Fable 5.1, Fable 5, Opus 5.5, Opus 5, Opus 4.8, Opus 4.7, Opus 4.6, Sonnet 5.5, Sonnet 5 and Sonnet 4.6
    //     have a 1M-token context window on Amazon Bedrock; other Claude models, including Sonnet 4.5 and
    //     Sonnet 4, have 200k.
    // [C] Context windows, "Context window sizes by model":
    //     https://platform.claude.com/docs/en/build-with-claude/context-windows#context-window-sizes-by-model
    //     Every 1M-token model, Mythos 5.1, Mythos 5 and Mythos Preview included, generates up to 128k output tokens.
    // [M] Models overview: https://platform.claude.com/docs/en/models/overview (Haiku 4.5: 200K, 64K output).
    // [O] Claude Opus 4.5: https://platform.claude.com/docs/en/models/opus-4-5/overview (200K, 64K output).
    // [S] Claude Sonnet 4.5: https://platform.claude.com/docs/en/models/sonnet-4-5/overview (200K, 64K output).
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
        ["openai.gpt-oss-120b-1:0"] = new(128_000, 16_000), // [G120]
        ["claude-fable-5-1"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-fable-5"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-mythos-5-1"] = new(1_000_000, 128_000), // [C]
        ["claude-mythos-5"] = new(1_000_000, 128_000), // [C]
        ["claude-mythos-preview"] = new(1_000_000, 128_000), // [C]
        ["claude-opus-5-5"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-opus-5"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-opus-4-8"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-opus-4-7"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-opus-4-6"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-sonnet-5-5"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-sonnet-5"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-sonnet-4-6"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-opus-4-5"] = new(200_000, 64_000), // [B] [O]
        ["claude-sonnet-4-5"] = new(200_000, 64_000), // [B] [S]
        ["claude-haiku-4-5"] = new(200_000, 64_000), // [B] [M]
        ["claude-sonnet-4"] = new(200_000, null), // [B]; no current Anthropic page states its output limit.
        ["claude-3-5-haiku"] = new(200_000, null) // [B]; no current Anthropic page states its output limit.
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly BedrockModelLimit Unknown = new(null, null);

    /// <summary>The limits of a foundation-model id or inference-profile id. An ARN or unlisted model is unknown.</summary>
    public static BedrockModelLimit Get(string modelId)
    {
        (BedrockModelFamily family, string baseModelId) = BedrockModelFamilies.Classify(modelId);
        string key = family == BedrockModelFamily.AnthropicClaude
            ? BedrockModelFamilies.AnthropicModelId(baseModelId) : baseModelId;
        return Models.TryGetValue(key, out BedrockModelLimit? limit) ? limit : Unknown;
    }
}
