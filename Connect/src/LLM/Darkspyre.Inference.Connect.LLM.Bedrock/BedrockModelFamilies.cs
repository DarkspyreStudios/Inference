using System.Text.RegularExpressions;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

/// <summary>The Bedrock model families whose request fields Converse maps.</summary>
internal enum BedrockModelFamily
{
    Unknown,
    AnthropicClaude,
    AmazonNova2,
    OpenAiGptOss,
    DeepSeekR1
}

internal static partial class BedrockModelFamilies
{
    private static readonly string[] ProfilePrefixes = ["us.", "eu.", "apac.", "global."];

    /// <summary>Classifies a model id or inference-profile id. An ARN classifies as Unknown.</summary>
    public static (BedrockModelFamily Family, string BaseModelId) Classify(string modelId)
    {
        string baseModelId = modelId;
        foreach (string prefix in ProfilePrefixes)
        {
            if (modelId.StartsWith(prefix, StringComparison.Ordinal))
            {
                baseModelId = modelId[prefix.Length..];
                break;
            }
        }

        BedrockModelFamily family = baseModelId switch
        {
            _ when baseModelId.StartsWith("anthropic.claude-", StringComparison.Ordinal) => BedrockModelFamily.AnthropicClaude,
            _ when baseModelId.StartsWith("amazon.nova-2-", StringComparison.Ordinal) => BedrockModelFamily.AmazonNova2,
            _ when baseModelId.StartsWith("openai.gpt-oss-", StringComparison.Ordinal) => BedrockModelFamily.OpenAiGptOss,
            _ when baseModelId.StartsWith("deepseek.r1", StringComparison.Ordinal) => BedrockModelFamily.DeepSeekR1,
            _ => BedrockModelFamily.Unknown
        };
        return (family, baseModelId);
    }

    /// <summary>
    /// The Anthropic model id for a Claude base id, such as claude-sonnet-4-5 from
    /// anthropic.claude-sonnet-4-5-20250929-v1:0.
    /// </summary>
    public static string AnthropicModelId(string baseModelId) =>
        BedrockSuffix().Replace(baseModelId.StartsWith("anthropic.", StringComparison.Ordinal)
            ? baseModelId["anthropic.".Length..]
            : baseModelId, string.Empty);

    /// <summary>An optional release date, an optional version and an optional throughput revision.</summary>
    [GeneratedRegex(@"(-\d{8})?(-v\d+)?(:\d+)?$")]
    private static partial Regex BedrockSuffix();
}
