using Darkspyre.Inference.Connect.ConsoleTester;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class ConsoleAgentDiagnosticsTests
{
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
