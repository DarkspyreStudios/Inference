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
    // 260K sits below Codex's 272K default window and 860K below its 872K catalog maximum; 400K and 600K split the range.
    private static readonly long[] CodexSizes = [260_000, 400_000, 600_000, 860_000];
    private const long CodexDefaultWindow = 272_000;

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
            LlmTransport.OpenAiCodex => await CodexAsync(llms, agents, connectionId, models, token),
            LlmTransport.AnthropicClaudeCli => await ClaudeAsync(agents, connectionId, models, token),
            _ => throw new ArgumentException("limit-probe supports --provider codex and --provider claude.")
        };
    }

    private static async Task<int> CodexAsync(LlmProviderService llms, LlmAgentProviderService agents, string connectionId,
        IReadOnlyList<LlmModel> models, CancellationToken token)
    {
        // The allowance comparison runs on one model whose catalog entry marks experimental context.
        string? allowanceModel = models.FirstOrDefault(model => Flag(model, "supports_experimental_context") == true)?.Id;
        var allowance = new List<(long Input, double? Primary, double? Secondary)>();
        foreach (LlmModel model in models)
        {
            long? catalogMaximum = Number(model, "max_context_window");
            Console.WriteLine($"MODEL {model.Id}: context_window={Number(model, "context_window")?.ToString(CultureInfo.InvariantCulture) ?? "none"} " +
                $"max_context_window={catalogMaximum?.ToString(CultureInfo.InvariantCulture) ?? "none"} " +
                $"supports_experimental_context={Flag(model, "supports_experimental_context")?.ToString() ?? "none"}");
            bool tracked = model.Id == allowanceModel;
            long? accepted = null, refused = null;
            foreach (long size in CodexSizes.Where(size => catalogMaximum is null || size <= catalogMaximum))
            {
                Outcome outcome = await SendAsync(llms, agents, connectionId, model.Id, size, tracked, allowance, token);
                if (outcome.Accepted) accepted = size;
                else { refused = size; break; }
            }
            if (accepted is null)
            {
                Console.WriteLine($"RESULT {model.Id}: no size accepted; no bracket to bisect.");
                continue;
            }
            if (refused is null)
            {
                Console.WriteLine($"RESULT {model.Id}: every size accepted; limit >= {accepted.Value.ToString(CultureInfo.InvariantCulture)} tokens.");
                continue;
            }
            // Bisection stops when the bracket is within 1% of the accepted size.
            long low = accepted.Value, high = refused.Value;
            while (high - low > low / 100)
            {
                long middle = low + (high - low) / 2;
                Outcome outcome = await SendAsync(llms, agents, connectionId, model.Id, middle, tracked, allowance, token);
                if (outcome.Accepted) low = middle;
                else high = middle;
            }
            Console.WriteLine($"RESULT {model.Id}: largest accepted {low.ToString(CultureInfo.InvariantCulture)}, " +
                $"smallest refused {high.ToString(CultureInfo.InvariantCulture)} (local o200k tokens).");
        }
        if (allowanceModel is null)
        {
            Console.WriteLine("ALLOWANCE: no listed model marks supports_experimental_context.");
            return 0;
        }
        WriteAllowance(allowanceModel, "below", allowance.Where(item => item.Input < CodexDefaultWindow));
        WriteAllowance(allowanceModel, "above", allowance.Where(item => item.Input >= CodexDefaultWindow));
        return 0;
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
            Outcome outcome = await SendAsync(null, agents, connectionId, model.Id, size, false, null, token);
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
    private static async Task<Outcome> SendAsync(LlmProviderService? llms, LlmAgentProviderService agents, string connectionId,
        string modelId, long size, bool tracked, List<(long Input, double? Primary, double? Secondary)>? allowance,
        CancellationToken token)
    {
        (string text, long local) = Filler(size);
        (double? Primary, double? Secondary) before = tracked ? await UsedAsync(llms!, connectionId, token) : default;
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
        if (tracked && accepted && usage?.InputTokens is { } input)
        {
            (double? Primary, double? Secondary) after = await UsedAsync(llms!, connectionId, token);
            allowance!.Add((input, after.Primary - before.Primary, after.Secondary - before.Secondary));
            Console.WriteLine($"    used_percent primary {Percent(before.Primary)} -> {Percent(after.Primary)}, " +
                $"secondary {Percent(before.Secondary)} -> {Percent(after.Secondary)}");
        }
        return new(accepted, completed?.Error);
    }

    private static async Task<(double? Primary, double? Secondary)> UsedAsync(LlmProviderService llms, string connectionId,
        CancellationToken token)
    {
        LlmAccountUsageSnapshot snapshot = await llms.GetAccountUsageAsync(connectionId, token);
        double? Used(string id) => snapshot.Quotas.FirstOrDefault(quota => quota.Id == id)?.UsedFraction * 100d;
        return (Used("codex:primary"), Used("codex:secondary"));
    }

    private static void WriteAllowance(string modelId, string side,
        IEnumerable<(long Input, double? Primary, double? Secondary)> items)
    {
        var list = items.ToList();
        long input = list.Sum(item => item.Input);
        if (input == 0)
        {
            Console.WriteLine($"ALLOWANCE {modelId} {side} 272K: no accepted request.");
            return;
        }
        double? PerMillion(Func<(long Input, double? Primary, double? Secondary), double?> pick) =>
            list.All(item => pick(item) is not null) ? list.Sum(item => pick(item)!.Value) * 1_000_000d / input : null;
        Console.WriteLine($"ALLOWANCE {modelId} {side} 272K: {list.Count} request(s), {input.ToString(CultureInfo.InvariantCulture)} input tokens; " +
            $"primary {Percent(PerMillion(item => item.Primary))} and secondary {Percent(PerMillion(item => item.Secondary))} percent per million input tokens.");
    }

    private static string Percent(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown";

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

    private static long? Number(LlmModel model, string name) =>
        model.CatalogEntry is { ValueKind: JsonValueKind.Object } entry && entry.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : null;

    private static bool? Flag(LlmModel model, string name) =>
        model.CatalogEntry is { ValueKind: JsonValueKind.Object } entry && entry.TryGetProperty(name, out JsonElement value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static string? ParseModel(string[] args) => args switch
    {
        [] => null,
        ["--model", { Length: > 0 } model] => model,
        _ => throw new ArgumentException("limit-probe accepts only --model <id>.")
    };

    [GeneratedRegex(@"(\d+) tokens > (\d+)")]
    private static partial Regex GreaterForm();
}
