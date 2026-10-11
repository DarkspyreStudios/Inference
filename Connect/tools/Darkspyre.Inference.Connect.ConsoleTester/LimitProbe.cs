using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.ConsoleTester;

/// <summary>
/// Live context-limit measurement. Every request is one preserved turn, so Inference sends it unchanged and returns the
/// provider's own refusal. Each request makes a paid call on the connection's subscription; no request is retried.
/// </summary>
internal static partial class LimitProbe
{
    public static async Task<int> RunAsync(LlmProviderService llms, LlmAgentProviderService agents,
        ConsoleInvocation invocation, CancellationToken token)
    {
        string? only = ParseModel(invocation.CommandArguments);
        string connectionId = invocation.Provider.ConnectionId;
        IReadOnlyList<LlmModel> models = await llms.ListModelsAsync(connectionId, token);
        models = [.. models.Where(model => only is null || model.Id == only)];
        if (models.Count == 0) throw new ArgumentException(only is null ? "The connection lists no models." : $"Model '{only}' is not listed.");
        return invocation.Provider.Transport switch
        {
            LlmTransport.AnthropicClaudeCli => await ClaudeAsync(agents, connectionId, models, token),
            _ => throw new ArgumentException("limit-probe supports --provider claude; the Codex probe runs on a saved Host connection (agent-ds CodexLimitProbeTests).")
        };
    }

    private static async Task<int> ClaudeAsync(LlmAgentProviderService agents, string connectionId,
        IReadOnlyList<LlmModel> models, CancellationToken token)
    {
        foreach (LlmModel model in models)
        {
            long? window = ClaudeModelLimits.Get(model.Id).ContextTokens;
            if (window is null)
            {
                Console.WriteLine($"MODEL {model.Id}: no table window; not sent.");
                continue;
            }
            // 5% above the table window: past any rounding in the CLI's own size estimate.
            long size = window.Value + window.Value / 20;
            Console.WriteLine($"MODEL {model.Id}: table window {window.Value.ToString(CultureInfo.InvariantCulture)}");
            Outcome outcome = await SendAsync(agents, connectionId, model.Id, size, token);
            if (outcome.Accepted)
            {
                Console.WriteLine($"RESULT {model.Id}: accepted above the table window.");
                continue;
            }
            (long? tokens, long? limit) = ClaudeAgentClient.ReadRefusalSizes(outcome.Error);
            Match greater = GreaterForm().Match(outcome.Error ?? string.Empty);
            Console.WriteLine($"RESULT {model.Id}: refusal limit {limit?.ToString(CultureInfo.InvariantCulture) ?? "not stated"}, " +
                $"request {tokens?.ToString(CultureInfo.InvariantCulture) ?? "not stated"}; " +
                $"'~N tokens (limit M)' form {(limit is null ? "absent" : "present")}; " +
                $"'N tokens > M' form {(greater.Success ? "present" : "absent")}.");
        }
        return 0;
    }

    private sealed record Outcome(bool Accepted, string? Error);

    /// <summary>Sends one preserved turn of about <paramref name="size"/> local tokens and reports what the provider did.</summary>
    private static async Task<Outcome> SendAsync(LlmAgentProviderService agents, string connectionId, string modelId, long size,
        CancellationToken token)
    {
        (string text, long local) = Filler(size);
        await using ILlmAgentSession session = await agents.StartSessionAsync(connectionId, new LlmAgentSessionOptions
        {
            ModelId = modelId,
            Instructions = "Reply with the single word OK.",
            ExecutionOptions = new() { ContextPolicy = new() { PreserveSuppliedContext = true } }
        }, token);
        LlmAgentTurnCompletedEvent? completed = null;
        LlmUsage? usage = null;
        try
        {
            await foreach (LlmAgentEvent item in session.RunTurnAsync(new LlmAgentTurnRequest
                           {
                               Input = [new LlmAgentTextContent(text)],
                               ReasoningLevel = LlmReasoningLevel.Low
                           }, token))
            {
                if (item is LlmAgentUsageEvent { Usage: { } reported }) usage = reported;
                if (item is LlmAgentTurnCompletedEvent end) completed = end;
            }
        }
        catch (LlmProviderException error) when (error.IsContextOverflow)
        {
            Console.WriteLine($"  {modelId} {local.ToString(CultureInfo.InvariantCulture)} local tokens: refused (exception): {error.Message}");
            return new(false, error.Message);
        }
        bool accepted = completed is { Status: LlmAgentTurnStatus.Completed };
        string reportedInput = usage?.InputTokens?.ToString(CultureInfo.InvariantCulture) ?? "none";
        Console.WriteLine($"  {modelId} {local.ToString(CultureInfo.InvariantCulture)} local tokens: " +
            (accepted ? $"accepted; reported input {reportedInput}"
                : $"refused ({completed?.Reason.ToString() ?? "no completion"}; overflow={completed?.IsContextOverflow}): {completed?.Error}"));
        return new(accepted, completed?.Error);
    }

    /// <summary>
    /// Distinct text of about <paramref name="tokens"/> o200k tokens. A random first line keeps every request off the
    /// provider's prompt cache, so each one is charged as new input.
    /// </summary>
    internal static (string Text, long Tokens) Filler(long tokens)
    {
        string[] words =
        [
            "river", "stone", "lantern", "harbor", "meadow", "copper", "window", "garden", "signal", "engine", "forest",
            "candle", "bridge", "valley", "anchor", "pocket", "thunder", "silver", "marble", "orchard", "compass", "ladder",
            "violet", "planet", "canvas", "feather", "harvest", "island", "jacket", "kettle", "lemon", "mirror", "needle",
            "ocean", "pepper", "quarry", "rocket", "saddle", "tunnel", "velvet", "wagon", "yellow", "zipper", "basket",
            "cobalt", "desert", "ember", "falcon", "glacier", "hollow"
        ];
        var random = new Random();
        var text = new StringBuilder($"Probe {Guid.NewGuid():N}. The words below are filler; ignore them.\n");
        long counted = LocalTokenCount.Count(text.ToString());
        var chunk = new StringBuilder();
        while (counted < tokens)
        {
            chunk.Clear();
            for (int index = 0; index < 4_000; index++) chunk.Append(' ').Append(words[random.Next(words.Length)]);
            counted += LocalTokenCount.Count(chunk.ToString());
            text.Append(chunk);
        }
        text.Append("\n\nReply with the single word OK.");
        return (text.ToString(), counted);
    }

    private static string? ParseModel(string[] args) => args switch
    {
        [] => null,
        ["--model", { Length: > 0 } model] => model,
        _ => throw new ArgumentException("limit-probe accepts only --model <id>.")
    };

    [GeneratedRegex(@"(\d+) tokens > (\d+)")]
    private static partial Regex GreaterForm();
}
