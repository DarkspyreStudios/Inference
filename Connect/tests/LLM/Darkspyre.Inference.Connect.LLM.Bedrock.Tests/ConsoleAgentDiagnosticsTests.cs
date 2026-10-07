using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.ConsoleTester;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class ConsoleAgentDiagnosticsTests
{
    [Fact]
    public void ManagedContextPersistenceRetainsDocumentBytesWithReflectionDisabled()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        byte[] document = "attachment smoke test document"u8.ToArray();
        var context = new LlmAgentContext
        {
            Revision = 7,
            SelectedResources = [new("workspace", "reference")],
            ResourceSelectionVersion = new("workspace", "session", 3),
            Entries =
            [
                new("prompt", "turn", new("user", "Read the reference.")),
                new("call", "turn", new("assistant", string.Empty)
                {
                    ToolCalls = [new LlmToolCall("attachment", "read", "{}")]
                }),
                new("result", "turn", new("user", string.Empty)
                {
                    ToolResults = [new LlmToolResult("attachment", new()
                    {
                        Parts = [new LlmToolOutputBinary(LlmBinaryKind.Document,
                            "text/plain", document, "reference.txt")]
                    })]
                })
            ]
        };

        LlmAgentContext restored = ManagedAgentProbe.RoundTripContext(context);

        Assert.Equal(3, restored.FormatVersion);
        Assert.Equal(context.Revision, restored.Revision);
        Assert.Equal(context.SelectedResources, restored.SelectedResources);
        Assert.Equal(context.ResourceSelectionVersion, restored.ResourceSelectionVersion);
        Assert.Equal(context.Entries.Select(entry => entry.Id), restored.Entries.Select(entry => entry.Id));
        Assert.Equal("attachment", Assert.Single(restored.Entries[1].Message.ToolCalls).Id);
        var result = Assert.IsType<LlmToolResult>(Assert.Single(restored.Entries[2].Message.ToolResults));
        Assert.Equal("attachment", result.ToolCallId);
        var binary = Assert.IsType<LlmToolOutputBinary>(Assert.Single(result.Output.Parts));
        Assert.Equal((LlmBinaryKind.Document, "text/plain", "reference.txt"),
            (binary.Kind, binary.MediaType, binary.FileName));
        Assert.Equal(document, binary.Data.ToArray());
    }

    [Fact]
    public void FailedAgentCompletionPrintsItsReasonAndExistingError()
    {
        using var diagnostics = new StringWriter();
        var completed = new LlmAgentTurnCompletedEvent("thread", "turn", LlmAgentCompletionReason.ProviderFailure,
            "The selected model requires an inference profile.");

        Program.WriteAgentCompletion(completed, diagnostics);

        string output = diagnostics.ToString();
        Assert.Contains("status=Failed", output, StringComparison.Ordinal);
        Assert.Contains("reason=ProviderFailure", output, StringComparison.Ordinal);
        Assert.Contains("[error] The selected model requires an inference profile.", output, StringComparison.Ordinal);
    }
}
