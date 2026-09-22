namespace Darkspyre.Inference.Connect.LLM.Examples;

/// <summary>
/// Provider-neutral image and document results. The caller supplies a kind, file name, and bytes;
/// Darkspyre.Inference.Connect.LLM owns media detection plus the provider-specific encoding and wire representation.
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
        new(toolCallId, "The generated chart and report are attached.")
        {
            Attachments = CreateAttachments(imageBytes, documentBytes)
        };

    public static LlmAgentToolResult ForAgent(
        ReadOnlyMemory<byte> imageBytes,
        ReadOnlyMemory<byte> documentBytes) => new()
        {
            Content = [new LlmAgentTextContent("The generated chart and report are attached.")],
            Attachments = CreateAttachments(imageBytes, documentBytes)
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
