using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.ConsoleTester;

/// <summary>Small live protocol checks; no agent engine or persistent conversation store.</summary>
internal static class CodexProtocolProbe
{
    public static async Task<int> RunAsync(
        LlmProviderService llms, string connectionId, string modelId, CancellationToken cancellationToken)
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false}""");
        var tool = new LlmToolDefinition { Name = "read_document", InputSchema = schema.RootElement.Clone() };
        var messages = new List<LlmChatMessage>
        {
            new("user", "Call read_document and report the verification word from its attachment.")
        };
        var request = new LlmChatRequest
        {
            ModelId = modelId,
            ReasoningLevel = LlmReasoningLevel.Low,
            ConversationKey = Guid.NewGuid().ToString(),
            Messages = messages,
            Tools = [tool],
            ToolChoice = new() { Mode = LlmToolChoiceMode.Required }
        };
        LlmChatResult result = await llms.CompleteAsync(connectionId, request, cancellationToken);
        if (result.Status != LlmCompletionStatus.RequiresAction || result.ToolCalls.Count != 1
            || result.ToolCalls[0].Name != tool.Name || result.ContinuationState is null)
        {
            throw new InvalidOperationException("Protocol probe expected one complete tool call with retained state.");
        }
        Console.WriteLine("PASS: tool call with retained continuation state.");
        messages.Add(new("assistant", result.Text) { ToolCalls = result.ToolCalls, ContinuationState = result.ContinuationState });
        messages.Add(new("user", "")
        {
            ToolResults = [new(result.ToolCalls[0].Id, new LlmToolOutput
            {
                Parts =
                [
                    new LlmToolOutputText("The document follows."),
                    new LlmToolOutputBinary(
                        LlmBinaryKind.Document, "text/plain", Encoding.UTF8.GetBytes("The verification word is cerulean."), "probe.txt")
                ]
            })]
        });
        request = request with { ToolChoice = new() { Mode = LlmToolChoiceMode.None } };
        result = await llms.CompleteAsync(connectionId, request, cancellationToken);
        RequireWord(result);
        Console.WriteLine("PASS: tool-result document attachment and stateless continuation.");
        messages.Add(new("assistant", result.Text) { ContinuationState = result.ContinuationState });
        messages.Add(new("user", "What was the verification word? Reply with the word only."));
        result = await llms.CompleteAsync(connectionId, request, cancellationToken);
        RequireWord(result);
        Console.WriteLine("PASS: multiple assistant states replay without server-stored history.");

        using var arguments = JsonDocument.Parse("""{"kind":"image"}""");
        LlmToolOutput imageOutput = await Program.ExecuteToolAsync(
            new("probe", "probe", "probe", null, "darkspyre_inference_connect_attachment", arguments.RootElement), cancellationToken);
        result = await llms.CompleteAsync(connectionId, new()
        {
            ModelId = modelId,
            Messages =
            [
                new("user", "Describe the attached image briefly.")
                {
                    Attachments =
                    [
                        .. imageOutput.Parts.OfType<LlmToolOutputBinary>().Select(image => new LlmAttachment
                        {
                            Kind = LlmBinaryKind.Image,
                            FileName = image.FileName ?? "image.png",
                            Data = image.Data
                        })
                    ]
                }
            ]
        }, cancellationToken);
        if (result.Status != LlmCompletionStatus.Completed || result.Text.Length == 0)
        {
            throw new InvalidOperationException("Image probe did not complete.");
        }
        Console.WriteLine("PASS: image prompt accepted.");
        return 0;
    }

    private static void RequireWord(LlmChatResult result)
    {
        if (result.Status != LlmCompletionStatus.Completed || result.ContinuationState is null
            || !result.Text.Contains("cerulean", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Protocol probe did not complete with the expected document word and continuation state.");
        }
    }
}
