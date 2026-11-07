using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ToolOutputProjectorTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47];

    private static readonly LlmRichContentSupported AllNative = new()
    {
        Images = LlmRichContentCarriage.Native,
        ImageMediaTypes = new[] { "image/png" }.ToFrozenSet(),
        PdfDocuments = LlmRichContentCarriage.Native,
        TextDocuments = LlmRichContentCarriage.Native,
        ResourceLinks = LlmRichContentCarriage.Native,
        EmbeddedTextResources = LlmRichContentCarriage.Native,
        StructuredContent = LlmRichContentCarriage.Native,
        ErrorFlag = LlmRichContentCarriage.Native,
        PreservesOrder = InferenceSupport.Supported,
        AllowsEmpty = InferenceSupport.Supported
    };

    [Fact]
    public void NativeCarriagesDeliverEveryKindInSuppliedOrder()
    {
        LlmToolOutput output = new()
        {
            Parts =
            [
                new LlmToolOutputText("first"),
                new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", Png, "chart.png"),
                new LlmToolOutputBinary(LlmBinaryKind.Document, "application/pdf", "%PDF-"u8.ToArray(), "report.pdf"),
                new LlmToolOutputBinary(LlmBinaryKind.Document, "text/plain", "notes"u8.ToArray(), "notes.txt"),
                new LlmToolOutputResource("https://example.test/spec") { Name = "spec", MediaType = "text/html" },
                new LlmToolOutputResource("file:///a.md") { MediaType = "text/markdown", Text = "# A" }
            ],
            StructuredContent = JsonElement.Parse("{\"a\":1}"),
            IsError = true
        };

        ProjectedToolOutput projected = ToolOutputProjector.Project(output, AllNative, AllNative);

        Assert.Equal(new SentText("first"), projected.Parts[0]);
        SentImage image = Assert.IsType<SentImage>(projected.Parts[1]);
        Assert.Equal(("image/png", "chart.png"), (image.MediaType, image.FileName));
        Assert.True(Assert.IsType<SentFile>(projected.Parts[2]).IsPdf);
        Assert.Equal("notes.txt", Assert.IsType<SentFile>(projected.Parts[3]).FileName);
        Assert.Equal(new SentResourceLink("https://example.test/spec", "spec", "text/html"), projected.Parts[4]);
        Assert.Equal(new SentEmbeddedText("file:///a.md", "text/markdown", "# A"), projected.Parts[5]);
        // The output has a text part, so its structured content is not sent.
        Assert.Equal(6, projected.Parts.Count);
        Assert.True(projected.NativeErrorFlag);
        Assert.All(projected.Report.Parts, part => Assert.Equal(new LlmToolPartProjection(LlmToolPartDelivery.Delivered), part));
        Assert.Equal(LlmToolPartDelivery.Omitted, projected.Report.StructuredContent!.Delivery);
        Assert.Equal(LlmToolPartDelivery.Delivered, projected.Report.ErrorFlag.Delivery);
    }
}
