using System.Text;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>Host-supplied history on a new target: projection, transcript text and opening context.</summary>
public sealed class ContextTransferTests
{
    [Fact]
    public void TranscriptWritesRolesToolBlocksAndAttachmentNamesInOrder()
    {
        var text = new StringBuilder();
        TranscriptText.Append(text,
        [
            new LlmChatMessage("user", "Population of Lyon?"),
            new LlmChatMessage("assistant", string.Empty) { ToolCalls = [new LlmToolCall("c1", "lookup", """{"city":"Lyon"}""")] },
            new LlmChatMessage("user", string.Empty)
            {
                ToolResults =
                [
                    new LlmToolResult("c1", new LlmToolOutput
                    {
                        Parts =
                        [
                            new LlmToolOutputText("522,250"),
                            new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", new byte[] { 1 }, "chart.png"),
                            new LlmToolOutputBinary(LlmBinaryKind.Document, "text/plain", "census 2021"u8.ToArray(), "source.txt")
                        ]
                    }),
                    new LlmToolResult("c2", LlmToolOutput.FromText("timeout", isError: true))
                ]
            },
            new LlmChatMessage("user", "Round it.") { Attachments = [Attachment("note.txt")] },
        ]);

        Assert.Equal(
            """
            [USER]
            Population of Lyon?
            [ASSISTANT]

            [TOOL CALL id=c1 name=lookup]
            {"city":"Lyon"}
            [USER]

            [TOOL RESULT id=c1 status=ok]
            522,250
            [File: source.txt]
            census 2021
            [TOOL RESULT id=c2 status=error]
            timeout
            [ATTACHMENTS: chart.png]
            [USER]
            Round it.
            [ATTACHMENTS: note.txt]

            """.ReplaceLineEndings(Environment.NewLine),
            text.ToString());
    }

    [Fact]
    public void RejectLeavesTheRequestUnchanged()
    {
        LlmChatRequest request = Request("other", Unbound()) with { UnboundResponseState = LlmUnboundResponseState.Reject };

        (LlmChatRequest projected, int omitted) = ResponseStateProjection.Project(request, Connection());

        Assert.Same(request, projected);
        Assert.Equal(0, omitted);
    }

    [Fact]
    public void OmitRemovesOnlyUnboundStateAndLeavesTheCallersMessagesIntact()
    {
        LlmChatMessage bound = new("assistant", "Hello") { ResponseState = State("model") };
        LlmChatMessage unbound = Unbound();
        LlmChatRequest request = Request("model", bound, unbound) with { UnboundResponseState = LlmUnboundResponseState.Omit };

        (LlmChatRequest projected, int omitted) = ResponseStateProjection.Project(request, Connection());

        Assert.Equal(1, omitted);
        Assert.Same(bound.ResponseState, projected.Messages[1].ResponseState);
        Assert.Null(projected.Messages[2].ResponseState);
        Assert.Equal(unbound.Content, projected.Messages[2].Content);
        Assert.NotNull(request.Messages[2].ResponseState);
    }

    [Fact]
    public void OmitOnATransportWithoutStateRemovesAllOfIt()
    {
        LlmConnection cli = new() { Version = "v1", Transport = LlmTransport.AnthropicClaudeCli, Endpoint = "claude" };
        LlmChatMessage reply = new("assistant", "Hello") { ResponseState = State("model") };
        LlmChatRequest request = Request("model", reply) with { UnboundResponseState = LlmUnboundResponseState.Omit };

        (LlmChatRequest projected, int omitted) = ResponseStateProjection.Project(request, cli);

        Assert.Equal(1, omitted);
        Assert.Null(projected.Messages[1].ResponseState);
    }

    [Fact]
    public void AnUndefinedHandlingValueIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ResponseStateProjection.Project(
            Request("model") with { UnboundResponseState = (LlmUnboundResponseState)9 }, Connection()));

    [Fact]
    public void ManagedImportWithOmitKeepsBoundStateAndCountsTheRest()
    {
        LlmAgentContext context = Context(
            new LlmChatMessage("assistant", "Hello") { ResponseState = State("model") },
            Unbound());

        (LlmAgentContext imported, int omitted) = ManagedAgentContext.Import(
            context, Connection(), "model", LlmUnboundResponseState.Omit);

        Assert.Equal(1, omitted);
        Assert.NotNull(imported.Entries[1].Message.ResponseState);
        Assert.Null(imported.Entries[2].Message.ResponseState);
        Assert.Throws<ArgumentException>(() => ManagedAgentContext.Import(
            context, Connection(), "model", LlmUnboundResponseState.Reject));
    }

    [Fact]
    public void OpeningContextCarriesTheTranscriptAndReportsWhatWasLeftBehind()
    {
        LlmAgentSessionOptions options = Options(Context(
            new LlmChatMessage("assistant", "Hello") { ResponseState = State("model") },
            new LlmChatMessage("user", "See this.") { Attachments = [Attachment("note.txt")] }));

        AgentOpeningContext carried = AgentOpeningContext.For(options, carriesAttachments: true)!;
        AgentOpeningContext textOnly = AgentOpeningContext.For(options, carriesAttachments: false)!;

        Assert.StartsWith(AgentOpeningContext.Header + Environment.NewLine + "[USER]", carried.Text, StringComparison.Ordinal);
        Assert.Contains("[ASSISTANT]" + Environment.NewLine + "Hello", carried.Text, StringComparison.Ordinal);
        Assert.Equal(["note.txt"], carried.Attachments.Select(attachment => attachment.FileName));
        Assert.Contains("response state from 1 earlier messages was not carried.", carried.Notice, StringComparison.Ordinal);
        Assert.DoesNotContain("attachments", carried.Notice, StringComparison.Ordinal);
        Assert.Empty(textOnly.Attachments);
        Assert.EndsWith("1 history attachments could not be carried.", textOnly.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void OpeningContextNeedsOmitAndIsAbsentWithoutEntries()
    {
        LlmAgentSessionOptions options = Options(Context(new LlmChatMessage("assistant", "Hello")));

        Assert.Throws<NotSupportedException>(() => AgentOpeningContext.For(
            options with { UnboundResponseState = LlmUnboundResponseState.Reject }, carriesAttachments: true));
        Assert.Null(AgentOpeningContext.For(options with { Context = null }, carriesAttachments: true));
        Assert.Null(AgentOpeningContext.For(options with { Context = new LlmAgentContext() }, carriesAttachments: true));
    }

    [Theory]
    [InlineData(LlmAgentContextOwnership.Provider, LlmAgentContextImport.Structured)]
    [InlineData(LlmAgentContextOwnership.Host, LlmAgentContextImport.OpeningContext)]
    public void ContextImportMustMatchContextOwnership(LlmAgentContextOwnership ownership, LlmAgentContextImport import)
    {
        LlmAgentCapabilities capabilities = new() { ContextOwnership = ownership, ContextImport = import };

        Assert.Throws<ArgumentException>(capabilities.Validate);
    }

    [Fact]
    public void RuntimesDeclareHowTheyTakeAStartContext()
    {
        Assert.Equal(LlmAgentContextImport.Structured, ManagedAgentPolicy.Capabilities.ContextImport);
        Assert.Equal(LlmAgentContextImport.OpeningContext, NativeAgentContract.Claude.ContextImport);
        Assert.Equal(LlmAgentContextImport.OpeningContext, NativeAgentContract.AgentCore.ContextImport);
        ManagedAgentPolicy.Capabilities.Validate();
        NativeAgentContract.Claude.Validate();
        NativeAgentContract.AgentCore.Validate();
    }

    private static LlmConnection Connection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.OpenAiResponses,
        Endpoint = "https://example.test/v1",
        Credential = new LlmCredential { ApiKey = "synthetic-test-key" }.Serialize()
    };

    private static LlmProviderResponseState State(string modelId) => ProviderResponseStateCodec.Encode(Connection(), modelId,
        (JsonArray)JsonNode.Parse("""
            [{"type":"reasoning","id":"r1","summary":[],"encrypted_content":"opaque"},
             {"type":"message","id":"m1","role":"assistant","content":[{"type":"output_text","text":"Hello"}]}]
            """)!);

    /// <summary>An assistant reply whose state belongs to another model.</summary>
    private static LlmChatMessage Unbound() => new("assistant", "Hello") { ResponseState = State("other-model") };

    private static LlmChatRequest Request(string modelId, params LlmChatMessage[] replies) => new()
    {
        ModelId = modelId,
        Messages = [new LlmChatMessage("user", "Hi"), .. replies, new LlmChatMessage("user", "Next")]
    };

    private static LlmAgentContext Context(params LlmChatMessage[] replies) => new()
    {
        Entries = [new("e0", "t0", new LlmChatMessage("user", "Hi")),
            .. replies.Select((message, index) => new LlmAgentContextEntry($"e{index + 1}", "t0", message))]
    };

    private static LlmAgentSessionOptions Options(LlmAgentContext context) => new()
    {
        ModelId = "model",
        WorkingDirectory = ".",
        Context = context,
        UnboundResponseState = LlmUnboundResponseState.Omit
    };

    private static LlmAttachment Attachment(string name) => new()
    {
        Kind = LlmAttachmentKind.Document,
        FileName = name,
        Data = "hello"u8.ToArray()
    };
}
