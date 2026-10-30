using Darkspyre.Inference.Common;
using System.Collections.Frozen;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// The tool-output support of the routes several providers share. The Responses row was measured live through Direct
/// Codex on 2026-09-24; the others follow their protocol and are not live-verified. A route only one provider uses lives
/// in that provider's library.
/// </summary>
internal static class RichContentRoutes
{
    /// <summary>The image media types most tool-output routes carry natively.</summary>
    internal static readonly FrozenSet<string> CommonImageTypes =
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

    // Anthropic Messages and Google Vertex AI use this; they are out of the build (Providers/AnthropicMessagesLlmClient.cs,
    // under Compile Remove; Connect/src/LLM/Darkspyre.Inference.Connect.LLM.Vertex, outside the solution). It moves into
    // their own libraries when they return to the build.
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

    // AWS Bedrock uses this; AWS Bedrock is out of the build (Connect/src/LLM/Darkspyre.Inference.Connect.LLM.Bedrock,
    // outside the solution). It moves into that provider's own library when the provider returns to the build.
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

    // The Gemini API and Google Vertex AI use this; they are out of the build (Providers/GeminiApiLlmClient*.cs and
    // Internal/Gemini/, under Compile Remove; Connect/src/LLM/Darkspyre.Inference.Connect.LLM.Vertex, outside the
    // solution). It moves into their own libraries when they return to the build.
    /// <summary>Gemini functionResponse media referenced by nested inlineData display names.</summary>
    public static LlmRichContentSupported GeminiFunctionResponse { get; } = TextOnly with
    {
        Images = LlmRichContentCarriage.Native,
        ImageMediaTypes = new[] { "image/png", "image/jpeg", "image/webp" }.ToFrozenSet(StringComparer.Ordinal),
        PdfDocuments = LlmRichContentCarriage.Native,
        ErrorFlag = LlmRichContentCarriage.Native,
        PreservesOrder = InferenceSupport.Unsupported
    };
}
