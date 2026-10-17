using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.ConsoleTester;

/// <summary>
/// Opt-in live Grok checks with synthetic inputs and no retries: a schema-checked host tool, hosted web and X search,
/// the fast tier, host compaction and the context estimate. Each needs a signed-in Grok subscription.
/// </summary>
internal static class GrokProbe
{
    private const string ArgumentMarker = "CONNECT_STRICT_ARGUMENT_OK";
    private const string ToolName = "connect_strict_marker";

    internal static async Task<int> RunAsync(LlmProviderService llms, LlmAgentProviderService agents,
        ConsoleInvocation invocation, CancellationToken token)
    {
        ProviderProbeOptions options = ProviderProbeOptions.Parse(invocation.CommandArguments,
            invocation.Provider.Definition.DefaultModelId ?? "grok-4.7");
        string connectionId = invocation.Provider.ConnectionId;
        bool ready = await llms.IsAuthenticatedAsync(connectionId, token);
        ProbeCase[] cases =
        [
            new("strict", cancellation => StrictAsync(agents, connectionId, options.Model, Console.Out, cancellation)),
            new("hosted-search", cancellation => HostedSearchAsync(agents, connectionId, options.Model, Console.Out, cancellation)),
            new("fast-tier", cancellation => FastTierAsync(agents, connectionId, options.Model, Console.Out, cancellation)),
            new("compaction", cancellation => CompactionAsync(agents, connectionId, options.Model, Console.Out, cancellation)),
            new("estimate", cancellation => EstimateAsync(agents, connectionId, options.Model, Console.Out, cancellation))
        ];
        return await ProbeRunner.RunAsync(cases, options, ready, Console.Out, token);
    }

    /// <summary>One turn with web and X search: hosted calls stream as events and the answer carries citations (S4).</summary>
    internal static async Task<string> HostedSearchAsync(LlmAgentProviderService agents, string connectionId, string model,
        TextWriter output, CancellationToken token)
    {
        await using ILlmAgentSession session = await agents.StartSessionAsync(connectionId, new()
        {
            ModelId = model,
            HostedTools = [new LlmWebSearchTool(), new LlmSocialSearchTool()],
            // A search, a follow-up search and the answer.
            MaxHostedTurns = 3
        }, token);
        var kinds = new HashSet<LlmHostedToolKind>();
        int citations = 0;
        LlmAgentTurnCompletedEvent? completed = null;
        await foreach (LlmAgentEvent item in session.RunTurnAsync(new()
        {
            Input = [new LlmAgentTextContent("Search the web and X for today's top technology news story. Answer in two sentences and cite your sources.")]
        }, token))
        {
            Program.WriteAgentEvent(item, output, output);
            if (item is LlmAgentHostedToolCallCompletedEvent hosted) kinds.Add(hosted.Call.Kind);
            if (item is LlmAgentCitationEvent) citations++;
            if (item is LlmAgentTurnCompletedEvent terminal) completed = terminal;
        }
        if (completed?.Status != LlmAgentTurnStatus.Completed || kinds.Count == 0 || citations == 0)
            throw new ProbeFailure("A hosted-search turn must complete with at least one hosted call and one citation.");
        return $"hosted calls: {string.Join(", ", kinds.Order())}; {citations} citation(s); Completed";
    }

    /// <summary>A turn on the fast tier completes (S5).</summary>
    internal static async Task<string> FastTierAsync(LlmAgentProviderService agents, string connectionId, string model,
        TextWriter output, CancellationToken token)
    {
        await using ILlmAgentSession session = await agents.StartSessionAsync(connectionId, new() { ModelId = model }, token);
        var answer = new StringBuilder();
        LlmAgentTurnCompletedEvent? completed = null;
        await foreach (LlmAgentEvent item in session.RunTurnAsync(new()
        {
            Input = [new LlmAgentTextContent("Reply only with the word READY.")],
            ServiceTier = LlmServiceTier.Fast
        }, token))
        {
            Program.WriteAgentEvent(item, output, output);
            if (item is LlmAgentTextDeltaEvent text) answer.Append(text.Delta);
            if (item is LlmAgentTurnCompletedEvent terminal) completed = terminal;
        }
        if (completed?.Status != LlmAgentTurnStatus.Completed || !answer.ToString().Contains("READY", StringComparison.Ordinal))
            throw new ProbeFailure("A fast-tier turn must complete with the requested word.");
        return "fast tier; Completed";
    }

    /// <summary>
    /// A fact survives a host compaction, and the feature list reports the host side (S3). The session caps the window at
    /// 30,000 tokens, so two turns of 4,000 words exceed the compaction target and the older turn, which holds the fact,
    /// is summarized.
    /// </summary>
    internal static async Task<string> CompactionAsync(LlmAgentProviderService agents, string connectionId, string model,
        TextWriter output, CancellationToken token)
    {
        // Grok refuses demands to repeat a token as prompt injection, so the fact is an ordinary one from a conversation.
        string[] cities = ["Lisbon", "Kyoto", "Tallinn", "Hobart", "Bergen", "Oslo", "Dublin", "Perth"];
        string fact = cities[Random.Shared.Next(cities.Length)];
        await using ILlmAgentSession session = await agents.StartSessionAsync(connectionId, new()
        {
            ModelId = model,
            ExecutionOptions = new() { ContextPolicy = new() { MaxContextTokens = 30000, ReservedOutputTokens = 4000 } }
        }, token);
        if ((await session.GetCapabilitiesAsync(token)).CompactionSide != LlmCompactionSide.Host)
            throw new ProbeFailure("Grok must report host-side compaction.");
        await RunAsync(session, $"I am planning a trip to {fact} next spring. Here are my notes; just acknowledge them briefly.\n"
            + Notes(4000), output, token);
        await RunAsync(session, "More notes; just acknowledge them briefly.\n" + Notes(4000), output, token);
        LlmAgentCompactedEvent compacted = await session.CompactNowAsync(token)
            ?? throw new ProbeFailure("Compact now must compact the conversation.");
        string answer = await RunAsync(session, "Which city did I say I am planning to visit?", output, token);
        if (!answer.Contains(fact, StringComparison.Ordinal))
            throw new ProbeFailure("The turn after compaction must recall the city.");
        return $"compacted to revision {compacted.ContextRevision}; the next turn recalled the city";
    }

    /// <summary>
    /// After a one-line turn, the session's estimate for a turn of 3,000 words, taken before that turn, is at least the
    /// input tokens Grok then reports for it. The proxy adds framing the request does not show (about 1,400 tokens on
    /// grok-4.7); the first turn's report teaches it, and later estimates add it. An estimate under the reported count
    /// fails; the difference is reported either way.
    /// </summary>
    internal static async Task<string> EstimateAsync(LlmAgentProviderService agents, string connectionId, string model,
        TextWriter output, CancellationToken token)
    {
        await using ILlmAgentSession session = await agents.StartSessionAsync(connectionId, new() { ModelId = model }, token);
        await RunAsync(session, "Reply only with the word READY.", output, token);
        var turn = new LlmAgentTurnRequest
        {
            Input = [new LlmAgentTextContent("Read these notes and reply only with the word DONE.\n" + Notes(3000))]
        };
        LlmAgentContextStatus status = await session.GetContextStatusAsync(turn, token);
        long estimate = status.UsedContextTokens ?? throw new ProbeFailure("The session must estimate the turn.");
        long? reported = null;
        await foreach (LlmAgentEvent item in session.RunTurnAsync(turn, token))
        {
            if (item is not LlmAgentTextDeltaEvent) Program.WriteAgentEvent(item, output, output);
            if (item is LlmAgentUsageEvent { Usage.InputTokens: { } input }) reported ??= input;
        }
        if (reported is not { } actual) throw new ProbeFailure("Grok must report the turn's input tokens.");
        string summary = $"estimate {estimate} ({status.Source}), reported {actual}, difference {estimate - actual}";
        if (estimate < actual) throw new ProbeFailure("The estimate must not be under the reported count: " + summary);
        return summary;
    }

    /// <summary>Deterministic filler of <paramref name="count"/> ordinary words in sentences, with a number every 50 words.</summary>
    private static string Notes(int count)
    {
        string[] words = ["river", "lantern", "copper", "meadow", "seventeen", "harbor", "violet", "engine", "quartz", "pilgrim",
            "ledger", "monsoon", "tundra", "falcon", "orchard", "cinder", "atlas", "biscuit", "glacier", "mosaic"];
        var text = new StringBuilder();
        for (int index = 0; index < count; index++)
        {
            if (index % 50 == 0) text.Append(index).Append(' ');
            text.Append(words[index * 7 % words.Length]).Append(index % 12 == 11 ? ".\n" : " ");
        }
        return text.ToString();
    }

    private static async Task<string> RunAsync(ILlmAgentSession session, string prompt, TextWriter output, CancellationToken token)
    {
        var answer = new StringBuilder();
        LlmAgentTurnCompletedEvent? completed = null;
        await foreach (LlmAgentEvent item in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent(prompt)] }, token))
        {
            Program.WriteAgentEvent(item, output, output);
            if (item is LlmAgentTextDeltaEvent text) answer.Append(text.Delta);
            if (item is LlmAgentTurnCompletedEvent terminal) completed = terminal;
        }
        if (completed?.Status != LlmAgentTurnStatus.Completed) throw new ProbeFailure("The turn must complete.");
        return answer.ToString();
    }

    internal static async Task<string> StrictAsync(LlmAgentProviderService agents, string connectionId,
        string model, TextWriter output, CancellationToken token)
    {
        string replayMarker = $"CONNECT_STRICT_REPLAY_{Guid.NewGuid():N}";
        int callbacks = 0, successfulResults = 0;
        var options = new LlmAgentSessionOptions
        {
            ModelId = model,
            // One tool-request step and one final-answer step; no additional model iteration.
            ExecutionOptions = new() { MaxModelIterations = 2 },
            Tools = [CreateStrictTool()],
            ToolHandler = (call, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref callbacks) != 1 || call.Name != ToolName)
                    throw new ProbeFailure("Expected exactly one invocation of the declared strict function.");
                ValidateStrictArguments(call.Arguments);
                return Task.FromResult(LlmToolOutput.FromText(replayMarker));
            }
        };
        await using ILlmAgentSession session = await agents.StartSessionAsync(connectionId, options, token);
        var answer = new StringBuilder();
        LlmAgentTurnCompletedEvent? completed = null;
        await foreach (LlmAgentEvent item in session.RunTurnAsync(new()
        {
            Input = [new LlmAgentTextContent($"Call {ToolName} exactly once with marker {ArgumentMarker}. "
                + "Use no other tools. Reply only with the exact verification word returned by the tool.")]
        }, token))
        {
            Program.WriteAgentEvent(item, output, output);
            if (item is LlmAgentTextDeltaEvent text) answer.Append(text.Delta);
            if (item is LlmAgentToolEvent { Output.IsError: false } tool && tool.Tool == ToolName)
                successfulResults++;
            if (item is LlmAgentTurnCompletedEvent terminal) completed = terminal;
        }
        if (completed?.Status != LlmAgentTurnStatus.Completed || callbacks != 1 || successfulResults != 1
            || answer.ToString().Trim() != replayMarker)
            throw new ProbeFailure("Strict function must validate its original arguments, return one successful tool result and complete with that result's exact word.");
        return "original closed-schema arguments validated; one successful callback; exact result replay; Completed";
    }

    internal static LlmAgentToolDefinition CreateStrictTool() => new()
    {
        Name = ToolName,
        Description = "Returns a verification word. Call once when requested.",
        InputSchema = JsonElement.Parse(
            """{"type":"object","properties":{"marker":{"type":"string","enum":["CONNECT_STRICT_ARGUMENT_OK"]}},"required":["marker"],"additionalProperties":false}""")
    };

    internal static void ValidateStrictArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Count() != 1
            || !arguments.TryGetProperty("marker", out JsonElement marker)
            || marker.ValueKind != JsonValueKind.String || marker.GetString() != ArgumentMarker)
            throw new ProbeFailure("Strict function arguments do not satisfy the original required marker enum and closed object schema.");
    }
}
