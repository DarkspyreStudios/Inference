using Darkspyre.Inference.Common;
using System.Collections.Frozen;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// Each route's declared tool-output support. Direct Codex and the Claude CLI agent rows were measured live on
/// 2026-09-24; the others follow their protocol and are not live-verified.
/// </summary>
internal static class RichContentRoutes
{
    private static readonly FrozenSet<string> CommonImageTypes =
        new[] { "image/png", "image/jpeg", "image/gif", "image/webp" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>OpenAI Responses and Direct Codex <c>function_call_output</c>.</summary>
    public static LlmRichContentSupported Responses { get; } = new()
    {
        Images = LlmRichContentCarriage.Native,
        ImageMediaTypes = CommonImageTypes,
        PdfDocuments = LlmRichContentCarriage.Native,
        TextDocuments = LlmRichContentCarriage.Native,
        ResourceLinks = LlmRichContentCarriage.Text,
        EmbeddedTextResources = LlmRichContentCarriage.Text,
        StructuredContent = LlmRichContentCarriage.Text,
        ErrorFlag = LlmRichContentCarriage.Text,
        PreservesOrder = InferenceSupport.Supported,
        AllowsEmpty = InferenceSupport.Supported
    };

    /// <summary>Anthropic Messages <c>tool_result</c>.</summary>
    public static LlmRichContentSupported Anthropic { get; } = new()
    {
        Images = LlmRichContentCarriage.Native,
        ImageMediaTypes = CommonImageTypes,
        PdfDocuments = LlmRichContentCarriage.Native,
        TextDocuments = LlmRichContentCarriage.Native,
        ResourceLinks = LlmRichContentCarriage.Text,
        EmbeddedTextResources = LlmRichContentCarriage.Text,
        StructuredContent = LlmRichContentCarriage.Text,
        ErrorFlag = LlmRichContentCarriage.Native,
        PreservesOrder = InferenceSupport.Supported,
        AllowsEmpty = InferenceSupport.Supported
    };

    /// <summary>Bedrock Converse <c>ToolResultBlock</c>.</summary>
    public static LlmRichContentSupported BedrockConverse { get; } = new()
    {
        Images = LlmRichContentCarriage.Native,
        ImageMediaTypes = CommonImageTypes,
        PdfDocuments = LlmRichContentCarriage.Native,
        TextDocuments = LlmRichContentCarriage.Native,
        ResourceLinks = LlmRichContentCarriage.Text,
        EmbeddedTextResources = LlmRichContentCarriage.Text,
        StructuredContent = LlmRichContentCarriage.Native,
        ErrorFlag = LlmRichContentCarriage.Native,
        PreservesOrder = InferenceSupport.Supported
    };

    /// <summary>Routes that carry a tool result as a single string or a text-only block.</summary>
    public static LlmRichContentSupported TextOnly { get; } = new()
    {
        TextDocuments = LlmRichContentCarriage.Text,
        ResourceLinks = LlmRichContentCarriage.Text,
        EmbeddedTextResources = LlmRichContentCarriage.Text,
        StructuredContent = LlmRichContentCarriage.Text,
        ErrorFlag = LlmRichContentCarriage.Text,
        PreservesOrder = InferenceSupport.Supported,
        AllowsEmpty = InferenceSupport.Supported
    };

    /// <summary>Claude CLI stream-json prompt blocks appended to its flattened tool-history transcript.</summary>
    public static LlmRichContentSupported ClaudePrompt { get; } = TextOnly with
    {
        Images = LlmRichContentCarriage.Native,
        ImageMediaTypes = CommonImageTypes,
        PdfDocuments = LlmRichContentCarriage.Native,
        TextDocuments = LlmRichContentCarriage.Native,
        PreservesOrder = InferenceSupport.Unsupported
    };

    /// <summary>The AgentCore Harness tool result, which is text with a status.</summary>
    public static LlmRichContentSupported AgentCore { get; } = TextOnly with
    {
        ErrorFlag = LlmRichContentCarriage.Native,
        AllowsEmpty = InferenceSupport.Unsupported
    };

    /// <summary>Gemini 3 functionResponse media referenced by nested inlineData display names.</summary>
    public static LlmRichContentSupported GeminiFunctionResponse { get; } = TextOnly with
    {
        Images = LlmRichContentCarriage.Native,
        ImageMediaTypes = new[] { "image/png", "image/jpeg", "image/webp" }.ToFrozenSet(StringComparer.Ordinal),
        PdfDocuments = LlmRichContentCarriage.Native,
        ErrorFlag = LlmRichContentCarriage.Native,
        PreservesOrder = InferenceSupport.Unsupported,
        MaxBinaryBytes = 7 * 1024 * 1024
    };

    /// <summary>
    /// The Claude CLI agent's MCP result. The CLI writes binary resources to disk and gives the model a path
    /// note, so PDFs and other binaries are unsupported; it drops text parts when MCP structured content is
    /// present, so structured content travels as a final text part.
    /// </summary>
    public static LlmRichContentSupported ClaudeMcp { get; } = new()
    {
        Images = LlmRichContentCarriage.Native,
        ImageMediaTypes = CommonImageTypes,
        TextDocuments = LlmRichContentCarriage.Text,
        ResourceLinks = LlmRichContentCarriage.Native,
        EmbeddedTextResources = LlmRichContentCarriage.Native,
        StructuredContent = LlmRichContentCarriage.Text,
        ErrorFlag = LlmRichContentCarriage.Native,
        PreservesOrder = InferenceSupport.Supported,
        AllowsEmpty = InferenceSupport.Supported
    };
}
