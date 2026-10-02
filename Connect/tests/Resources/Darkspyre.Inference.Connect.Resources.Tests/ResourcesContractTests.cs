using System.Text.Json;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Connect.Image;

namespace Darkspyre.Inference.Connect.Resources.Tests;

public sealed class ResourcesContractTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Id\":\"b\",\"Name\":\"n\",\"Scope\":{\"Owner\":\"o\",\"Identity\":\"i\"}}")]
    public void RequiredBatchFactsCannotBeInvented(string json) =>
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize(json, ConnectResourcesJson.LlmBatch));

    [Fact]
    public void RequiredMaterializationBudgetsCannotBecomeZero() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{}", ConnectResourcesJson.LlmMediaMaterializationOptions));

    [Fact]
    public void PagesCopyListsOnConstructionAndWith()
    {
        var items = new List<LlmBatchRequestMetadata> { new("id", null, null, null) };
        var page = new LlmBatchRequestPage(items, null);
        items.Clear();
        Assert.Single(page.Items);
        Assert.Throws<ArgumentNullException>(() => page with { Items = null! });
    }

    [Fact]
    public void OwnedCodecCopiesNestedImageBytesAndReferences()
    {
        byte[] bytes = [1, 2];
        using var configuration = JsonDocument.Parse("{}");
        var references = new List<InferenceMediaSource> { new BinaryMediaSource(null, bytes) };
        var operation = new LlmBatchImageOperation(new ImageGenerationRequest { ModelId = "m", Prompt = "p", References = references },
            new ImageWorkflow { Id = "w", Label = "w", Transport = ImageTransport.ComfyUi, TransportConfiguration = configuration.RootElement });
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(operation, ConnectResourcesJson.LlmBatchImageOperation), ConnectResourcesJson.LlmBatchImageOperation)!;
        bytes[0] = 9; references.Clear();
        Assert.Single(copy.Request.References);
        Assert.Equal(1, Assert.IsType<BinaryMediaSource>(copy.Request.References[0]).Data.Span[0]);
    }
}
