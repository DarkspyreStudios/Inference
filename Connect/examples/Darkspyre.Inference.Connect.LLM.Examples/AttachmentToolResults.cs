namespace Darkspyre.Inference.Connect.LLM.Examples;

/// <summary>
/// Provider-neutral image and document content. Prompts carry attachments; tool results carry one ordered
/// output of parts. Darkspyre.Inference.Connect.LLM owns the provider-specific encoding and wire representation.
/// </summary>
public static class AttachmentToolResults
{
    public static LlmChatMessage ForChatPrompt(
        string prompt,
        ReadOnlyMemory<byte> imageBytes,
        ReadOnlyMemory<byte> documentBytes) =>
        new("user", prompt)
        {
            Attachments = CreateAttachments(imageBytes, documentBytes)
        };

    public static LlmAgentTurnRequest ForAgentPrompt(
        string prompt,
        ReadOnlyMemory<byte> imageBytes,
        ReadOnlyMemory<byte> documentBytes) => new()
        {
            Input = [new LlmAgentTextContent(prompt)],
            Attachments = CreateAttachments(imageBytes, documentBytes)
        };

    public static LlmToolResult ForChat(
        string toolCallId,
        ReadOnlyMemory<byte> imageBytes,
        ReadOnlyMemory<byte> documentBytes) =>
        new(toolCallId, ToolOutput(imageBytes, documentBytes));

    /// <summary>What an agent tool handler returns: text, then the chart, then the report, in that order.</summary>
    public static LlmToolOutput ToolOutput(
        ReadOnlyMemory<byte> imageBytes,
        ReadOnlyMemory<byte> documentBytes) => new()
        {
            Parts =
            [
                new LlmToolOutputText("The generated chart and report follow."),
                new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", imageBytes, "chart.png"),
                new LlmToolOutputBinary(LlmBinaryKind.Document, "application/pdf", documentBytes, "report.pdf")
            ]
        };

    private static IReadOnlyList<LlmAttachment> CreateAttachments(
        ReadOnlyMemory<byte> imageBytes,
        ReadOnlyMemory<byte> documentBytes) =>
    [
        new LlmAttachment
        {
            Kind = LlmAttachmentKind.Image,
            FileName = "chart.png",
            Data = imageBytes
        },
        new LlmAttachment
        {
            Kind = LlmAttachmentKind.Document,
            FileName = "report.pdf",
            Data = documentBytes
        }
    ];
}
