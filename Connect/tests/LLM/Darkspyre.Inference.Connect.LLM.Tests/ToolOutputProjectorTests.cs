using System.Collections.Frozen;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ToolOutputProjectorTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47];

    private static readonly int[] NotAnObject = [1, 2];

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
        PreservesOrder = true,
        AllowsEmpty = true
    };

    private static readonly LlmRichContentSupported AllText = new()
    {
        TextDocuments = LlmRichContentCarriage.Text,
        ResourceLinks = LlmRichContentCarriage.Text,
        EmbeddedTextResources = LlmRichContentCarriage.Text,
        StructuredContent = LlmRichContentCarriage.Text,
        ErrorFlag = LlmRichContentCarriage.Text,
        PreservesOrder = true,
        AllowsEmpty = true
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
            StructuredContent = JsonSerializer.SerializeToElement(new { a = 1 }),
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
    public void TextCarriagesUseTheFixedRenderings()
    {
        LlmToolOutput output = new()
        {
            Parts =
            [
                new LlmToolOutputResource("https://example.test/spec") { Name = "spec" },
                new LlmToolOutputResource("file:///a.md") { Text = "# A" },
                new LlmToolOutputBinary(LlmBinaryKind.Document, "application/json", "{}"u8.ToArray(), "data.json")
            ],
            StructuredContent = JsonSerializer.SerializeToElement(new { a = 1 }),
            IsError = true
        };

        ProjectedToolOutput projected = ToolOutputProjector.Project(output, AllText, AllText);

        Assert.Equal(
            [
                new SentText(ToolOutputProjector.ErrorMarker),
                new SentText("[Resource link: spec] https://example.test/spec"),
                new SentText("[Resource: file:///a.md]\n# A"),
                new SentText("[File: data.json]\n{}"),
                new SentText("{\"a\":1}")
            ],
            projected.Parts);
        Assert.False(projected.NativeErrorFlag);
        Assert.True(projected.IsError);
        Assert.All(projected.Report.Parts, part => Assert.Equal(LlmToolPartDelivery.Converted, part.Delivery));
        Assert.Equal(LlmToolPartDelivery.Converted, projected.Report.StructuredContent!.Delivery);
        Assert.Equal(LlmToolPartDelivery.Converted, projected.Report.ErrorFlag.Delivery);
    }

    [Fact]
    public void TextDocumentsBecomeEmbeddedTextWhereTheRouteCarriesEmbeddedTextNatively()
    {
        LlmToolOutput output = new()
        {
            Parts = [new LlmToolOutputBinary(LlmBinaryKind.Document, "text/csv", "a,b"u8.ToArray(), "rows.csv")]
        };

        ProjectedToolOutput projected = ToolOutputProjector.Project(output, RichContentRoutes.ClaudeMcp, RichContentRoutes.ClaudeMcp);

        Assert.Equal([new SentEmbeddedText("file:///rows.csv", "text/csv", "a,b")], projected.Parts);
        Assert.Equal(LlmToolPartDelivery.Converted, Assert.Single(projected.Report.Parts).Delivery);
    }

    [Fact]
    public void OmissionNamesTheRouteOrTheModelOverride()
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
        LlmRichContentSupported lowered = RichContentRoutes.Responses.Restrict(images: false, documents: false);

        ProjectedToolOutput projected = ToolOutputProjector.Project(output, RichContentRoutes.Responses, lowered);

        Assert.Empty(projected.Parts);
        Assert.Equal(
            [
                LlmToolPartOmission.UnsupportedByModel,
                LlmToolPartOmission.UnsupportedByModel,
                LlmToolPartOmission.UnsupportedByRoute,
                LlmToolPartOmission.UnsupportedByRoute
            ],
            projected.Report.Parts.Select(part => part.Omission));
    }

    [Fact]
    public void OverridesLowerACarriageButNeverRaiseOne()
    {
        LlmConnection connection = new()
        {
            Version = "v1",
            Transport = LlmTransport.OpenAiCodex,
            Endpoint = OpenAiCodexLlmClient.DefaultEndpoint,
            CapabilityOverrides = new() { SupportsToolResultDocuments = false },
            ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides>
            {
                ["vision-off"] = new() { SupportsToolResultImages = false }
            }
        };

        LlmRichContentSupported route = RichContentRoutes.Responses.For(connection, null);
        LlmRichContentSupported model = RichContentRoutes.Responses.For(connection, "vision-off");
        LlmRichContentSupported raised = RichContentRoutes.TextOnly.Restrict(images: true, documents: true);

        Assert.Equal(
            (LlmRichContentCarriage.Native, LlmRichContentCarriage.Unsupported, LlmRichContentCarriage.Unsupported),
            (route.Images, route.PdfDocuments, route.TextDocuments));
        Assert.Equal(LlmRichContentCarriage.Unsupported, model.Images);
        Assert.Equal(RichContentRoutes.TextOnly, raised);
    }

    [Fact]
    public void InvalidAndEmptyContentIsOmittedWithoutThrowing()
    {
        LlmToolOutput output = new()
        {
            Parts =
            [
                new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", ReadOnlyMemory<byte>.Empty, "empty.png"),
                new LlmToolOutputBinary(LlmBinaryKind.Document, "text/plain", new byte[] { 0xff, 0xfe }, "bad.txt"),
                new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", new byte[16], "large.png"),
                new LlmToolOutputResource("file:///both") { Text = "t", Data = new byte[] { 1 } },
                new LlmToolOutputResource(" ")
            ],
            StructuredContent = JsonSerializer.SerializeToElement(NotAnObject)
        };
        LlmRichContentSupported limited = AllNative with { MaxBinaryBytes = 8 };

        ProjectedToolOutput projected = ToolOutputProjector.Project(output, limited, limited);

        Assert.Empty(projected.Parts);
        Assert.Equal(
            [
                LlmToolPartOmission.Empty,
                LlmToolPartOmission.InvalidContent,
                LlmToolPartOmission.InvalidContent,
                LlmToolPartOmission.InvalidContent,
                LlmToolPartOmission.InvalidContent
            ],
            projected.Report.Parts.Select(part => part.Omission));
        Assert.Equal(LlmToolPartOmission.InvalidContent, projected.Report.StructuredContent!.Omission);
    }

    [Fact]
    public void AnEmptyOutputProjectsToNoParts()
    {
        ProjectedToolOutput projected = ToolOutputProjector.Project(new LlmToolOutput(), AllNative, AllNative);

        Assert.Empty(projected.Parts);
        Assert.Empty(projected.Report.Parts);
        Assert.Null(projected.Report.StructuredContent);
        Assert.Equal(LlmToolPartDelivery.Delivered, projected.Report.ErrorFlag.Delivery);
        Assert.Equal(string.Empty, projected.Text);
    }

    [Fact]
    public void AnErrorFlagTheRouteCannotCarryIsOmittedAndTheOutputStaysAnError()
    {
        LlmRichContentSupported silent = AllText with { ErrorFlag = LlmRichContentCarriage.Unsupported };

        ProjectedToolOutput projected = ToolOutputProjector.Project(LlmToolOutput.FromText("failed", isError: true), silent, silent);

        Assert.Equal([new SentText("failed")], projected.Parts);
        Assert.True(projected.IsError);
        Assert.False(projected.NativeErrorFlag);
        Assert.Equal(
            new LlmToolPartProjection(LlmToolPartDelivery.Omitted, LlmToolPartOmission.UnsupportedByRoute),
            projected.Report.ErrorFlag);
    }

    [Fact]
    public void ResourceByteBodiesAreClassifiedByMediaType()
    {
        LlmToolOutput output = new()
        {
            Parts =
            [
                new LlmToolOutputResource("https://example.test/img/chart.png") { MediaType = "image/png", Data = Png },
                new LlmToolOutputResource("https://example.test/r") { Name = "sound", MediaType = "audio/mpeg", Data = new byte[] { 1 } }
            ]
        };

        ProjectedToolOutput projected = ToolOutputProjector.Project(output, AllNative, AllNative);

        Assert.Equal("chart.png", Assert.IsType<SentImage>(Assert.Single(projected.Parts)).FileName);
        Assert.Equal(LlmToolPartOmission.UnsupportedByRoute, projected.Report.Parts[1].Omission);
        Assert.Equal(LlmBinaryKind.Document, ToolOutputProjector.Classify("application/json"));
        Assert.Equal(LlmBinaryKind.Other, ToolOutputProjector.Classify(null));
    }

    [Fact]
    public async Task ProjectToolOutputAsyncAppliesTheRequestedLane()
    {
        using var http = new HttpClient();
        var providers = new LlmProviderService(
            new Source(new LlmConnection { Version = "v1", Transport = LlmTransport.AnthropicClaudeCli, Endpoint = "claude" }),
            [new ClaudeCliLlmClient(http, new NoRunner())]);
        LlmToolOutput output = new()
        {
            Parts =
            [
                new LlmToolOutputText("see"),
                new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", Png, "chart.png")
            ]
        };

        LlmToolOutputProjection chat = await providers.ProjectToolOutputAsync("claude", "claude-test", output);
        LlmToolOutputProjection agent = await providers.ProjectToolOutputAsync(
            "claude", "claude-test", output, LlmToolOutputLane.AgentSession);

        Assert.Equal(LlmToolPartOmission.UnsupportedByRoute, chat.Parts[1].Omission);
        Assert.Equal(LlmToolPartDelivery.Delivered, agent.Parts[1].Delivery);
    }

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(connection);

        public Task SaveCredentialAsync(
            string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoRunner : IClaudeCliRunner
    {
        public Task<ClaudeCliProcessResult> RunAsync(ClaudeCliInvocation invocation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
