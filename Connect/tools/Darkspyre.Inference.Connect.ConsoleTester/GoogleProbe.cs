using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.ConsoleTester;

/// <summary>Explicit metered qualification of a Gemini route (Vertex or the Gemini API) through the portable and managed public contracts.</summary>
internal sealed class GoogleProbe(ILlmInference inference, LlmAgentProviderService agents, string connectionId, string model)
{
    private static readonly JsonElement Schema = JsonElement.Parse("""{"type":"object","properties":{"key":{"type":"string"},"timeout":{"type":"integer"}},"required":["key"],"additionalProperties":false}""");
    private LlmInferenceRequest Request(string prompt) => new()
    { ModelId = model, Messages = [new("user", prompt)], MaxOutputTokens = 2048 };

    public static async Task<int> RunAsync(LlmProviderService llms, LlmAgentProviderService agents,
        ConsoleInvocation invocation, CancellationToken token)
    {
        var options = ProviderProbeOptions.Parse(invocation.CommandArguments, invocation.Provider.Definition.DefaultModelId);
        bool ready;
        try
        {
            await llms.ListModelsAsync(invocation.Provider.ConnectionId, token);
            ready = true;
        }
        catch (LlmProviderException) { ready = false; }
        var probe = new GoogleProbe(llms.Bind(invocation.Provider.ConnectionId), agents, invocation.Provider.ConnectionId, options.Model);
        Console.WriteLine($"{invocation.Provider.DisplayName} probe: explicit metered model calls; no automatic retries.");
        return await ProbeRunner.RunAsync(
            [new("text", probe.TextAsync), new("structured", probe.StructuredAsync), new("tools", probe.ToolsAsync),
                new("agent", probe.AgentAsync), new("cancel", probe.CancelAsync)], options, ready, Console.Out, token);
    }

    private async Task<string> TextAsync(CancellationToken token)
    {
        var result = await inference.CompleteAsync(Request("Reply with a short greeting.") with { ReasoningLevel = LlmReasoningLevel.Low }, token);
        Require(result.Status == LlmCompletionStatus.Completed && result.Text.Length > 0 && result.ContinuationState is not null,
            "Expected completed text and retained assistant state.");
        return "portable text, reasoning control and continuation capture";
    }

    private async Task<string> StructuredAsync(CancellationToken token)
    {
        var result = await inference.CompleteAsync(Request("Return key set to sample.") with
        { StructuredOutput = new() { Name = "sample", Schema = Schema } }, token);
        Require(result.StructuredOutput is { WasEvaluated: true, IsValid: true }, "The reply did not satisfy the original schema.");
        return "schema-enforced reply validated against the original host schema";
    }

    private async Task<string> ToolsAsync(CancellationToken token)
    {
        var request = Request("Call lookup with key sample to retrieve the reading. Do not invent it.") with
        {
            Tools = [new LlmToolDefinition() { Name = "lookup", InputSchema = Schema }],
            ToolChoice = new() { Mode = LlmToolChoiceMode.Required }
        };
        var calls = await inference.CompleteAsync(request, token);
        Require(calls.Status == LlmCompletionStatus.RequiresAction && calls.ToolCalls.Count > 0 && calls.ContinuationState is not null,
            "Expected non-strict tool calls with continuation state.");
        var result = await inference.CompleteAsync(request with
        {
            ToolChoice = new() { Mode = LlmToolChoiceMode.None },
            Messages = [request.Messages[0], new("assistant", calls.Text)
                { ToolCalls = calls.ToolCalls, ContinuationState = calls.ContinuationState },
                new("user", "Report the returned reading.") { ToolResults = calls.ToolCalls.Select(call =>
                    new LlmToolResult(call.Id, LlmToolOutput.FromText("The reading is seven."))).ToArray() }]
        }, token);
        Require(result.Status == LlmCompletionStatus.Completed && result.ToolCalls.Count == 0, "Tool continuation did not finish.");
        return "ordinary non-strict tools, required choice, signed continuation and tool results";
    }

    private async Task<string> AgentAsync(CancellationToken token)
    {
        int exitCode = await ManagedAgentProbe.RunAsync(agents, connectionId, model, token);
        Require(exitCode == 0, "Managed capture/import probe failed.");
        return "managed tools, capture/import, independent context fork and compaction";
    }

    private async Task<string> CancelAsync(CancellationToken token)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        bool cancelled = false;
        try
        {
            await foreach (var item in inference.StreamAsync(Request("Write a long story about a lighthouse."), cancel.Token))
            { if (item is LlmTextDeltaEvent { Delta.Length: > 0 }) { cancel.Cancel(); } }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested && !token.IsCancellationRequested) { cancelled = true; }
        token.ThrowIfCancellationRequested();
        Require(cancelled, "The stream did not propagate cancellation after a text delta.");
        return "stream cancellation propagated";
    }

    private static void Require(bool condition, string message)
    { if (!condition) { throw new ProbeFailure(message); } }
}
