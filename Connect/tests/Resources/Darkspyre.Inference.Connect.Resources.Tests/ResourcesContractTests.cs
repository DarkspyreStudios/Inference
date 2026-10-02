using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Connect.Image;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Resources.Tests;

public sealed class ResourcesContractTests
{
    private static readonly string[] CorrelationOrder = ["second", "first", "second"];
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

    [Fact]
    public void PositionalNamesDeconstructionWithAndOrderedPagesRetainTheirContract()
    {
        var result = new LlmChatResult
        {
            Text = "actual",
            Status = LlmCompletionStatus.Completed,
            Diagnostics = new(200, null, null)
        };
        var first = new LlmBatchChatResult(CustomId: "second", Result: result);
        var (customId, chatResult) = first;
        Assert.Equal("second", customId); Assert.Same(result, chatResult);
        LlmBatchResult baseResult = first;
        baseResult.Deconstruct(out string baseId); Assert.Equal("second", baseId);
        var changed = first with { CustomId = "first" };
        var items = new List<LlmBatchResult> { first, changed, first };
        var page = new LlmBatchResultPage(Items: items, PaginationToken: "repeat-token");
        items.Clear(); Assert.Equal(CorrelationOrder, page.Items.Select(item => item.CustomId));
        Assert.Equal("repeat-token", page.PaginationToken);
        Assert.Throws<ArgumentNullException>(() => changed with { Result = null! });
        Assert.Throws<ArgumentNullException>(() => changed with { CustomId = null! });
        Assert.Throws<ArgumentNullException>(() => page with { Items = null! });
        Assert.Throws<ArgumentException>(() => page with { Items = [null!] });
        var replacement = new List<LlmBatchResult> { first };
        var newPage = page with { Items = replacement };
        replacement.Clear(); Assert.Single(newPage.Items);
    }

    [Fact]
    public void UnknownCountsAndCallerBudgetsDoNotInventProviderDefaults()
    {
        var counts = new LlmBatchCounts(Requests: null, Pending: 0, Success: long.MaxValue, Error: null, Cancelled: 0);
        var (requests, pending, success, error, cancelled) = counts;
        Assert.Null(requests); Assert.Equal(0, pending); Assert.Equal(long.MaxValue, success);
        Assert.Null(error); Assert.Equal(0, cancelled);
        Assert.Null(JsonSerializer.Deserialize("{}", ConnectResourcesJson.LlmBatchCounts)!.Requests);
        Assert.Throws<ArgumentOutOfRangeException>(() => counts with { Error = -1 });
        var budget = new LlmMediaMaterializationOptions(MaximumInlineImageBytes: 1, MaximumInlineResultBytes: long.MaxValue);
        var (imageBytes, resultBytes) = budget;
        Assert.Equal(1, imageBytes); Assert.Equal(long.MaxValue, resultBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => budget with { MaximumInlineImageBytes = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => budget with { MaximumInlineResultBytes = -1 });
        Assert.Null(new LlmResourcePageRequest().Limit);
        Assert.Null(new LlmResourcePageRequest().PaginationToken);
        Assert.Null(new LlmDeferredCompletion("r", new("endpoint", "account")).ExpiresAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void PageLimitPreservesRawCallerValueWithoutInventedProviderBounds(int? limit)
    {
        var page = new LlmResourcePageRequest(limit, "opaque");
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(page, ConnectResourcesJson.LlmResourcePageRequest), ConnectResourcesJson.LlmResourcePageRequest)!;
        Assert.Equal(limit, restored.Limit); Assert.Equal("opaque", restored.PaginationToken);
        Assert.Equal(limit, (page with { Limit = limit }).Limit);
    }
}
