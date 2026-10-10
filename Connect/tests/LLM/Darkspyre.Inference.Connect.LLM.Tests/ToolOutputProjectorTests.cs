using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Examples;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ToolOutputProjectorTests
{
    [Theory]
    [InlineData("{\"city\":\"Chicago\"}", false)]
    [InlineData("{\"city\":\"Oslo\"}", true)]
    [InlineData("not-json", true)]
    public void WeatherFixtureSchemaAndResultsDoNotRequireReflection(string arguments, bool error)
    {
        LlmToolDefinition definition = WeatherTool.ChatDefinition;
        Assert.False(definition.InputSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("city", Assert.Single(definition.InputSchema.GetProperty("required").EnumerateArray()).GetString());
        Assert.Equal(definition.InputSchema.GetRawText(), WeatherTool.AgentDefinition.InputSchema.GetRawText());

        LlmToolResult result = WeatherTool.Execute(new("call-1", definition.Name, arguments));
        Assert.Equal("call-1", result.ToolCallId);
        Assert.Equal(error, result.Output.IsError);
        string text = Assert.IsType<LlmToolOutputText>(Assert.Single(result.Output.Parts)).Text;
        using JsonDocument output = JsonDocument.Parse(text);
        if (!error)
        {
            Assert.Equal("Chicago", output.RootElement.GetProperty("city").GetString());
            Assert.Equal(72, output.RootElement.GetProperty("temperature_f").GetInt32());
            Assert.Equal("clear", output.RootElement.GetProperty("conditions").GetString());
        }
        else
            Assert.Equal(arguments == "not-json" ? "Invalid get_weather arguments." : "No fixture data is available.",
                output.RootElement.GetProperty("error").GetString());
    }

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
        Assert.Equal(1, Assert.IsType<SentStructured>(projected.Parts[6]).Value.GetProperty("a").GetInt32());
        Assert.True(projected.NativeErrorFlag);
        Assert.All(projected.Report.Parts, part => Assert.Equal(new LlmToolPartProjection(LlmToolPartDelivery.Delivered), part));
        Assert.Equal(LlmToolPartDelivery.Delivered, projected.Report.StructuredContent!.Delivery);
        Assert.Equal(LlmToolPartDelivery.Delivered, projected.Report.ErrorFlag.Delivery);
    }

    [Fact]
    public void OmittedPartCarriesNotice()
    {
        LlmToolOutput output = new()
        {
            Parts =
            [
                new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", Png, "chart.png"),
                new LlmToolOutputBinary(LlmBinaryKind.Document, "application/pdf", "%PDF-"u8.ToArray(), "report.pdf"),
                new LlmToolOutputBinary(LlmBinaryKind.Audio, "audio/wav", new byte[] { 1 }, "clip.wav"),
                new LlmToolOutputBinary(LlmBinaryKind.Other, "application/zip", new byte[] { 1 }, "files.zip")
            ]
        };
        LlmRichContentSupported lowered = RichContentRoutes.Responses.Restrict(images: InferenceSupport.Unsupported, documents: InferenceSupport.Unsupported);

        ProjectedToolOutput projected = ToolOutputProjector.Project(output, RichContentRoutes.Responses, lowered);

        // The Host no longer learns how a part is carried, so the model is told about each part it does not receive.
        Assert.Equal(
            [
                "[Content[0]: The selected model does not support tool-result images. This content was not supplied to the model.]",
                "[Content[1]: The selected model does not support tool-result documents. This content was not supplied to the model.]",
                "[Content[2]: The transport cannot carry this binary kind. This content was not supplied to the model.]",
                "[Content[3]: The transport cannot carry this binary kind. This content was not supplied to the model.]"
            ],
            projected.Parts.Select(part => Assert.IsType<SentText>(part).Text));
        Assert.Equal(
            [
                LlmToolPartOmission.UnsupportedByModel,
                LlmToolPartOmission.UnsupportedByModel,
                LlmToolPartOmission.UnsupportedByRoute,
                LlmToolPartOmission.UnsupportedByRoute
            ],
            projected.Report.Parts.Select(part => part.Omission));
    }

}
