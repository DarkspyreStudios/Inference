using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.Tests;

public sealed class ResourceRequestSerializationTests
{
    [Fact]
    public void SessionResourceReferencesSurviveANestedPortableRequestRoundTrip()
    {
        var reference = new InferenceResourceReference("workspace", "reference-text");
        var request = new LlmInferenceRequest
        {
            ModelId = "destination-model",
            Messages =
            [
                new("user", "Read the reference") { Attachments = [new LlmResourceAttachment(reference)] },
                new("tool", "")
                {
                    ToolResults = [new LlmToolResult("read-file", new() { Parts = [new LlmToolOutputSessionResource(reference)] })]
                }
            ]
        };

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, InferenceJson.LlmRequest);
        LlmInferenceRequest restored = JsonSerializer.Deserialize(json, InferenceJson.LlmRequest)!;

        Assert.Equal(request.ModelId, restored.ModelId);
        Assert.Equal(2, restored.Messages.Count);
        Assert.Equal(reference, Assert.IsType<LlmResourceAttachment>(Assert.Single(restored.Messages[0].Attachments)).Resource);
        var toolResult = Assert.IsType<LlmToolResult>(Assert.Single(restored.Messages[1].ToolResults));
        Assert.Equal("read-file", toolResult.ToolCallId);
        Assert.Equal(reference, Assert.IsType<LlmToolOutputSessionResource>(Assert.Single(toolResult.Output.Parts)).Resource);
        Assert.Null(restored.Resources);
    }
}
