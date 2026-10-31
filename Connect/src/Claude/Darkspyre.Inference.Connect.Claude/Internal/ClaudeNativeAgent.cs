using Darkspyre.Inference.Common;
using System.Collections.Frozen;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>What the Claude CLI agent runtime declares, and the tool-output routes of the Claude CLI. The prompt and MCP
/// rows were measured live on 2026-09-24.</summary>
internal static class ClaudeNativeAgent
{
    /// <summary>Claude CLI stream-json prompt blocks appended to its flattened tool-history transcript.</summary>
    public static LlmRichContentSupported PromptContent { get; } = RichContentRoutes.TextOnly with
    {
        Images = LlmRichContentCarriage.Native,
        ImageMediaTypes = RichContentRoutes.CommonImageTypes,
        PdfDocuments = LlmRichContentCarriage.Native,
        TextDocuments = LlmRichContentCarriage.Native,
        PreservesOrder = InferenceSupport.Unsupported
    };

    /// <summary>
    /// The Claude CLI agent's MCP result. The CLI writes binary resources to disk and gives the model a path
    /// note, so PDFs and other binaries are unsupported; it drops text parts when MCP structured content is
    /// present, so structured content travels as a final text part.
    /// </summary>
    public static LlmRichContentSupported McpContent { get; } = new()
    {
        Images = LlmRichContentCarriage.Native,
        ImageMediaTypes = RichContentRoutes.CommonImageTypes,
        TextDocuments = LlmRichContentCarriage.Text,
        ResourceLinks = LlmRichContentCarriage.Native,
        EmbeddedTextResources = LlmRichContentCarriage.Native,
        StructuredContent = LlmRichContentCarriage.Text,
        ErrorFlag = LlmRichContentCarriage.Native,
        PreservesOrder = InferenceSupport.Supported,
        AllowsEmpty = InferenceSupport.Supported
    };

    // Declared after McpContent, which it reads: static initializers run in textual order.
    /// <summary>The Claude CLI agent runtime's capabilities.</summary>
    public static LlmAgentCapabilities Capabilities { get; } = new()
    {
        ContextImport = LlmAgentContextImport.OpeningContext,
        SupportsShellTools = InferenceSupport.Unsupported,
        SupportsContextCapture = InferenceSupport.Unsupported,
        SupportsContextReplacement = InferenceSupport.Unsupported,
        // ClaudeConversation measures its own saved history.
        SupportsContextStatus = InferenceSupport.Supported,
        SupportsCompactionHandoff = InferenceSupport.Unsupported,
        // A conversation reports Unsupported once its live CLI session is not the one its thread id names.
        SupportsNativeResume = InferenceSupport.Supported,
        // A steer goes to the CLI with priority "next": it joins the running turn at the next model step.
        SupportsSteering = InferenceSupport.Supported,
        // A steer is a user message, so it carries the same attachment content blocks a turn's input does.
        SupportsSteeringAttachments = InferenceSupport.Supported,
        SupportsInterrupt = InferenceSupport.Supported,
        SupportsExternalToolHandoff = InferenceSupport.Unknown,
        SupportsCompactNow = InferenceSupport.Supported,
        SupportsFallbackModels = InferenceSupport.Unknown,
        SupportsTurnCap = InferenceSupport.Unknown,
        RichContentSupported = McpContent,
        SupportedRuntimePolicies = FrozenSet.Create(LlmAgentRuntimePolicyKind.HostControlled),
        ResourceObservation = LlmOperationSupport.Supported
    };
}
