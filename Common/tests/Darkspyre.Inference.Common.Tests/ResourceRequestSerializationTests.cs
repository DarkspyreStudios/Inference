using System.Text.Json;
using Darkspyre.Inference.Common.Embedding;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.Tests;

public sealed class ResourceRequestSerializationTests
{
    [Fact]
    public void EmbeddingInputsRetainTheirContentAndOrderThroughJson()
    {
        var text = new TextEmbeddingInput("reference text", "document title");
        byte[] bytes = [0, 12, 255];
        var request = new EmbeddingRequest
        {
            ModelId = "embedding-model",
            Inputs = [text, new MediaEmbeddingInput(new("image/png", bytes, "reference.png"))]
        };

        EmbeddingRequest restored = JsonSerializer.Deserialize(
            JsonSerializer.SerializeToUtf8Bytes(request, InferenceJson.EmbeddingRequest), InferenceJson.EmbeddingRequest)!;

        Assert.Equal(request.ModelId, restored.ModelId);
        Assert.Equal(2, restored.Inputs.Count);
        Assert.Equal(text, Assert.IsType<TextEmbeddingInput>(restored.Inputs[0]));
        var image = Assert.IsType<MediaEmbeddingInput>(restored.Inputs[1]);
        Assert.Equal("image/png", image.Media.ContentType);
        Assert.Equal("reference.png", image.Media.FileName);
        Assert.Equal(bytes, image.Media.Data.ToArray());
    }

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
