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
        PreservesOrder = true,
        AllowsEmpty = true
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
        PreservesOrder = true,
        AllowsEmpty = true
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
        PreservesOrder = true
    };

    /// <summary>Routes that carry a tool result as a single string or a text-only block.</summary>
    public static LlmRichContentSupported TextOnly { get; } = new()
    {
        TextDocuments = LlmRichContentCarriage.Text,
        ResourceLinks = LlmRichContentCarriage.Text,
        EmbeddedTextResources = LlmRichContentCarriage.Text,
        StructuredContent = LlmRichContentCarriage.Text,
        ErrorFlag = LlmRichContentCarriage.Text,
        PreservesOrder = true,
        AllowsEmpty = true
    };

    /// <summary>The AgentCore Harness tool result, which is text with a status.</summary>
    public static LlmRichContentSupported AgentCore { get; } = TextOnly with
    {
        ErrorFlag = LlmRichContentCarriage.Native,
        AllowsEmpty = false
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
        PreservesOrder = true,
        AllowsEmpty = true
    };
}
