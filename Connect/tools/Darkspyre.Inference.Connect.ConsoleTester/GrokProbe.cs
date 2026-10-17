using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.ConsoleTester;

/// <summary>
/// Opt-in live Grok checks with synthetic inputs and no retries: a schema-checked host tool, hosted web and X search,
/// a multi-agent turn and provider compaction. Each needs a signed-in Grok subscription.
/// </summary>
internal static class GrokProbe
{
    private const string ArgumentMarker = "CONNECT_STRICT_ARGUMENT_OK";
    private const string ToolName = "connect_strict_marker";
    private const string MultiAgentModel = "grok-4.20-multi-agent";

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
            new("multi-agent", cancellation => MultiAgentAsync(agents, connectionId, Console.Out, cancellation)),
            new("compaction", cancellation => CompactionAsync(agents, connectionId, options.Model, Console.Out, cancellation))
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

    /// <summary>A multi-agent turn that asks for 16 agents and the fast tier completes on the nearest count (S5).</summary>
    internal static async Task<string> MultiAgentAsync(LlmAgentProviderService agents, string connectionId,
        TextWriter output, CancellationToken token)
    {
        await using ILlmAgentSession session = await agents.StartSessionAsync(connectionId, new() { ModelId = MultiAgentModel }, token);
        var answer = new StringBuilder();
        LlmAgentTurnCompletedEvent? completed = null;
        await foreach (LlmAgentEvent item in session.RunTurnAsync(new()
        {
            Input = [new LlmAgentTextContent("Reply only with the word READY.")],
            AgentCount = 16,
            ServiceTier = LlmServiceTier.Fast
        }, token))
        {
            Program.WriteAgentEvent(item, output, output);
            if (item is LlmAgentTextDeltaEvent text) answer.Append(text.Delta);
            if (item is LlmAgentTurnCompletedEvent terminal) completed = terminal;
        }
        if (completed?.Status != LlmAgentTurnStatus.Completed || !answer.ToString().Contains("READY", StringComparison.Ordinal))
            throw new ProbeFailure("A 16-agent fast-tier turn must complete with the requested word.");
        return "16 agents on the fast tier; Completed";
    }

    /// <summary>A fact survives a compaction on the provider side, and the feature list reports that side (S3).</summary>
    internal static async Task<string> CompactionAsync(LlmAgentProviderService agents, string connectionId, string model,
        TextWriter output, CancellationToken token)
    {
        // Grok refuses demands to repeat a token as prompt injection, so the fact is an ordinary one from a conversation.
        string[] cities = ["Lisbon", "Kyoto", "Tallinn", "Hobart", "Bergen", "Oslo", "Dublin", "Perth"];
        string fact = cities[Random.Shared.Next(cities.Length)];
        await using ILlmAgentSession session = await agents.StartSessionAsync(connectionId, new() { ModelId = model }, token);
        if ((await session.GetCapabilitiesAsync(token)).CompactionSide != LlmCompactionSide.Provider)
            throw new ProbeFailure("Grok must report provider-side compaction.");
        await RunAsync(session, $"I am planning a trip to {fact} next spring. Just acknowledge that briefly.", output, token);
        LlmAgentCompactedEvent compacted = await session.CompactNowAsync(token)
            ?? throw new ProbeFailure("Compact now must compact the conversation.");
        string answer = await RunAsync(session, "Which city did I say I am planning to visit?", output, token);
        if (!answer.Contains(fact, StringComparison.Ordinal))
            throw new ProbeFailure("The turn after compaction must recall the city.");
        return $"compacted to revision {compacted.ContextRevision}; the next turn recalled the city";
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
