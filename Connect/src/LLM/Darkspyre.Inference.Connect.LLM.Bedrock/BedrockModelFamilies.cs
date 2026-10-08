using System.Text.RegularExpressions;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

/// <summary>The Bedrock model families whose request fields Converse maps.</summary>
internal enum BedrockModelFamily
{
    Unknown,
    AnthropicClaude,
    AmazonNova1,
    AmazonNova2,
    OpenAiGptOss,
    DeepSeekR1
}

internal static partial class BedrockModelFamilies
{
    private static readonly string[] ProfilePrefixes = ["us.", "us-gov.", "eu.", "apac.", "jp.", "au.", "in.", "global."];

    /// <summary>Recognizes explicit ARN references and documented geographic/global inference profiles.</summary>
    internal static bool IsInferenceProfile(string modelId) =>
        modelId.StartsWith("arn:", StringComparison.Ordinal) || ProfilePrefix(modelId) is not null;

    private static string? ProfilePrefix(string modelId) =>
        ProfilePrefixes.FirstOrDefault(prefix => modelId.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Classifies a model id or inference-profile id. An ARN classifies as Unknown.</summary>
    public static (BedrockModelFamily Family, string BaseModelId) Classify(string modelId)
    {
        string baseModelId = ProfilePrefix(modelId) is { } prefix ? modelId[prefix.Length..] : modelId;

        BedrockModelFamily family = baseModelId switch
        {
            _ when baseModelId.StartsWith("anthropic.claude-", StringComparison.Ordinal) => BedrockModelFamily.AnthropicClaude,
            "amazon.nova-micro-v1:0" or "amazon.nova-lite-v1:0" or "amazon.nova-pro-v1:0"
                or "amazon.nova-premier-v1:0" => BedrockModelFamily.AmazonNova1,
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
