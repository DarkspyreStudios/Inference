using System.Text;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// One request's context limits: the effective window, the output reserve, the usable window (window minus reserve),
/// the compaction trigger and the compaction target, all in tokens.
/// </summary>
internal sealed record CompactionLimits(long Window, long Reserve, long UsableWindow, long TriggerTokens, long TargetTokens)
{
    /// <summary>Compaction starts when a request measures at or above this share of the usable window.</summary>
    private const double TriggerRatio = 0.85;

    // 0.80 keeps a compaction from landing at the trigger, where the next send would compact again; 0.05 leaves room
    // for the current turn and a summary in a 200K window.
    private const double MinimumTargetRatio = 0.05;
    private const double MaximumTargetRatio = 0.80;

    // The Host's default output reserve. Without a reserve no trigger exists.
    private const long DefaultReserve = 8000;

    /// <summary>
    /// The one limit rule for every provider. The window is the smallest known value of the listed window, the learned
    /// limit and the policy cap; null when none is known. The reserve is the policy's, else the model's output limit,
    /// else 8,000.
    /// </summary>
    internal static CompactionLimits? For(long? listedWindow, long? learnedLimit, long? modelMaxOutput,
        LlmAgentContextPolicy policy)
    {
        long? window = null;
        foreach (long? known in (ReadOnlySpan<long?>)[listedWindow, learnedLimit, policy.MaxContextTokens])
            if (known is long value && (window is null || value < window)) window = value;
        if (window is not long limit) return null;
        long reserve = policy.ReservedOutputTokens ?? modelMaxOutput ?? DefaultReserve;
        long usable = Math.Max(0, limit - reserve);
        double ratio = Math.Clamp(policy.CompactionTargetRatio, MinimumTargetRatio, MaximumTargetRatio);
        return new(limit, reserve, usable, (long)Math.Floor(TriggerRatio * usable), (long)Math.Floor(ratio * usable));
    }
}

/// <summary>
/// A compacted context and what compaction did to reach it: the entries a summary replaced (null when no summary was
/// made), the summed usage of the summary side requests, and the tool results whose text was shortened or whose images
/// were removed.
/// </summary>
internal sealed record CompactionResult(LlmAgentContext Context, int? DroppedMessageCount, LlmUsage? Usage,
    int ShortenedToolOutputCount, int RemovedImageCount)
{
    /// <summary>False for a compaction that shortened nothing and summarized nothing; it is not reported.</summary>
    internal bool Changed => this is not { DroppedMessageCount: null, ShortenedToolOutputCount: 0, RemovedImageCount: 0 };
}

/// <summary>Compaction cannot bring a request under the usable window, so the request is not sent.</summary>
internal sealed class CompactionFailedException(string message, LlmUsage? usage) : Exception(message)
{
    internal LlmUsage? Usage { get; } = usage;
}

/// <summary>
/// Compacts one conversation's context for every provider: it shortens unprotected tool output, keeps the current turn
/// and the newest whole turns that fit, and replaces the rest with a summary built from the transcript, never from an
/// earlier summary.
/// </summary>
/// <param name="measure">The corrected size of the whole request the provider would send with a context.</param>
/// <param name="sideRequest">Sends one <see cref="SummaryRequest"/> on the conversation's own connection and model.</param>
/// <param name="estimateText">The corrected size of a text, for splitting parts.</param>
/// <param name="transcript">The Host's full transcript; null summarizes the live context's real entries.</param>
internal sealed class ConversationCompactor(
    Func<LlmAgentContext, CancellationToken, ValueTask<long>> measure,
    Func<LlmChatRequest, CancellationToken, Task<LlmChatResult>> sideRequest,
    Func<string, long?> estimateText,
    ILlmConversationTranscript? transcript)
{
    /// <summary>The TurnId of a summary entry. A summary entry is never kept as a turn and never summarized.</summary>
    internal const string SummaryTurnId = "compaction-summary";

    // 2,000 characters is the Host's summary cut; a shortened result keeps its first and last 1,000.
    private const int KeptCharacters = 1000;

    // 0.75 is the English words-per-token average.
    private const double WordsPerToken = 0.75;

    // About 2,000 tokens: the smallest summary that holds decisions and open work when the kept entries fill the target.
    private const int MinimumSummaryWords = 1500;

    // About 1,000 tokens for each part's summary.
    private const int MinimumPartWords = 750;

    // The Host's own transcript page size.
    private const int TranscriptPageSize = 200;

    private const string SummaryPrefix = "Summary of the earlier conversation:\n";

    /// <summary>
    /// Compacts the context toward <see cref="CompactionLimits.TargetTokens"/>. Calls <paramref name="stage"/> with
    /// today's working-indicator text before shortening and before summarizing.
    /// </summary>
    /// <exception cref="CompactionFailedException">The result stays at or above the usable window.</exception>
    /// <exception cref="LlmProviderException">A summary side request failed; the caller keeps its context.</exception>
    internal async Task<CompactionResult> CompactAsync(LlmAgentContext context, string modelId, string currentTurnId,
        IReadOnlySet<string> protectedEntryIds, CompactionLimits limits,
        Func<string, CancellationToken, ValueTask> stage, CancellationToken cancellationToken)
    {
        await stage("Pruning old context", cancellationToken).ConfigureAwait(false);
        (LlmAgentContext shortened, int shortenedCount, int removedImages) = Shorten(context, protectedEntryIds);
        if (await measure(shortened, cancellationToken).ConfigureAwait(false) <= limits.TargetTokens)
            return new(shortened, null, null, shortenedCount, removedImages);

        IReadOnlyList<LlmAgentContextEntry> entries = shortened.Entries;
        var current = entries.Where(entry => entry.TurnId == currentTurnId).ToArray();
        long currentMeasure = await Measure(shortened, current, cancellationToken).ConfigureAwait(false);
        var keptTurns = new HashSet<string>(StringComparer.Ordinal) { currentTurnId };
        long keptMeasure = currentMeasure;
        // Older whole turns, newest first, take at most half the room under the target; the summary gets the rest.
        long olderRoom = Math.Max(0, limits.TargetTokens - currentMeasure) / 2;
        foreach (string turnId in entries.Select(entry => entry.TurnId)
            .Where(turnId => turnId != currentTurnId && turnId != SummaryTurnId).Distinct(StringComparer.Ordinal).Reverse())
        {
            var candidate = new HashSet<string>(keptTurns, StringComparer.Ordinal) { turnId };
            long candidateMeasure = await Measure(shortened, Kept(entries, candidate), cancellationToken).ConfigureAwait(false);
            if (candidateMeasure - currentMeasure > olderRoom) break;
            keptTurns = candidate;
            keptMeasure = candidateMeasure;
        }
        LlmAgentContextEntry[] kept = Kept(entries, keptTurns);
        LlmAgentContextEntry[] carried = transcript is null ? [.. entries.Where(entry => entry.TurnId == SummaryTurnId)] : [];
        bool nothingNew = !entries.Any(entry => !keptTurns.Contains(entry.TurnId) && entry.TurnId != SummaryTurnId);

        IReadOnlyList<LlmAgentContextEntry> source = nothingNew ? []
            : await SourceAsync(entries, keptTurns, cancellationToken).ConfigureAwait(false);
        if (source.Count == 0)
            return await FinishAsync(shortened, current, null, null, shortenedCount, removedImages, limits, cancellationToken)
                .ConfigureAwait(false);

        await stage("Summarizing earlier messages", cancellationToken).ConfigureAwait(false);
        int words = (int)Math.Max(MinimumSummaryWords, WordsPerToken * (limits.TargetTokens - keptMeasure));
        (string summary, LlmUsage? usage) = await SummarizeAsync(source, modelId, words, limits, cancellationToken)
            .ConfigureAwait(false);
        var summaryEntry = new LlmAgentContextEntry(Guid.NewGuid().ToString("N"), SummaryTurnId,
            new LlmChatMessage("user", SummaryPrefix + summary));
        LlmAgentContext compacted = shortened with { Entries = [.. carried, summaryEntry, .. kept] };
        return await FinishAsync(compacted, current, entries.Count - kept.Length - carried.Length, usage,
            shortenedCount, removedImages, limits, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the entries left empty when a provider's own compaction state was omitted with one summary of the
    /// transcript turns the context no longer holds (steps 4-7). Without a transcript, or when the transcript holds no
    /// such turn, the empty entries are dropped and nothing is summarized.
    /// </summary>
    /// <exception cref="CompactionFailedException">The result stays at or above the usable window.</exception>
    /// <exception cref="LlmProviderException">A summary side request failed; the caller keeps its context.</exception>
    internal async Task<CompactionResult> SummarizeOmittedAsync(LlmAgentContext context, string modelId,
        string currentTurnId, IReadOnlySet<string> emptyEntryIds, CompactionLimits limits,
        Func<string, CancellationToken, ValueTask> stage, CancellationToken cancellationToken)
    {
        LlmAgentContext remaining = context with { Entries = [.. context.Entries.Where(entry => !emptyEntryIds.Contains(entry.Id))] };
        if (transcript is null) return new(remaining, null, null, 0, 0);
        var keptTurns = remaining.Entries.Select(entry => entry.TurnId).Where(turnId => turnId != SummaryTurnId)
            .ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<LlmAgentContextEntry> source = await SourceAsync(remaining.Entries, keptTurns, cancellationToken)
            .ConfigureAwait(false);
        if (source.Count == 0) return new(remaining, null, null, 0, 0);
        LlmAgentContextEntry[] kept = Kept(remaining.Entries, keptTurns);
        await stage("Summarizing earlier messages", cancellationToken).ConfigureAwait(false);
        long keptMeasure = await Measure(remaining, kept, cancellationToken).ConfigureAwait(false);
        int words = (int)Math.Max(MinimumSummaryWords, WordsPerToken * (limits.TargetTokens - keptMeasure));
        (string summary, LlmUsage? usage) = await SummarizeAsync(source, modelId, words, limits, cancellationToken)
            .ConfigureAwait(false);
        var summaryEntry = new LlmAgentContextEntry(Guid.NewGuid().ToString("N"), SummaryTurnId,
            new LlmChatMessage("user", SummaryPrefix + summary));
        LlmAgentContextEntry[] current = [.. kept.Where(entry => entry.TurnId == currentTurnId)];
        return await FinishAsync(remaining with { Entries = [summaryEntry, .. kept] }, current,
            context.Entries.Count - kept.Length, usage, 0, 0, limits, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The current turn's entries that hold a result of a tool marked <see cref="LlmAgentToolDefinition.ProtectedResults"/>,
    /// or hold its call.
    /// </summary>
    internal static HashSet<string> ProtectedEntries(LlmAgentContext context, string turnId,
        IReadOnlyDictionary<string, LlmAgentToolDefinition> tools)
    {
        var protectedCalls = context.Entries.Where(entry => entry.TurnId == turnId)
            .SelectMany(entry => entry.Message.ToolCalls).OfType<LlmToolCall>()
            .Where(call => tools.TryGetValue(call.Name, out var tool) && tool.ProtectedResults)
            .Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
        return context.Entries.Where(entry => entry.TurnId == turnId
                && (entry.Message.ToolCalls.Any(call => protectedCalls.Contains(call.Id))
                    || entry.Message.ToolResults.Any(result => protectedCalls.Contains(result.ToolCallId))))
            .Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Returns the result below the usable window, or throws with the sizes when it cannot be sent.</summary>
    private async Task<CompactionResult> FinishAsync(LlmAgentContext result, LlmAgentContextEntry[] current, int? dropped,
        LlmUsage? usage, int shortenedCount, int removedImages, CompactionLimits limits, CancellationToken cancellationToken)
    {
        long measured = await measure(result, cancellationToken).ConfigureAwait(false);
        if (measured < limits.UsableWindow) return new(result, dropped, usage, shortenedCount, removedImages);
        long mandatory = await Measure(result, current, cancellationToken).ConfigureAwait(false);
        throw new CompactionFailedException(
            $"Compaction could not bring this request under the limit: {measured} tokens remain in a {limits.UsableWindow}-token "
            + $"usable window. The current turn alone measures {mandatory} tokens.", usage);
    }

    private ValueTask<long> Measure(LlmAgentContext context, IEnumerable<LlmAgentContextEntry> entries,
        CancellationToken cancellationToken) => measure(context with { Entries = [.. entries] }, cancellationToken);

    private static LlmAgentContextEntry[] Kept(IReadOnlyList<LlmAgentContextEntry> entries, HashSet<string> turns) =>
        [.. entries.Where(entry => turns.Contains(entry.TurnId))];

    /// <summary>The entries to summarize: the transcript's turns that are not kept, or the live context's real entries.</summary>
    private async Task<IReadOnlyList<LlmAgentContextEntry>> SourceAsync(IReadOnlyList<LlmAgentContextEntry> entries,
        HashSet<string> keptTurns, CancellationToken cancellationToken)
    {
        if (transcript is null)
            return [.. entries.Where(entry => !keptTurns.Contains(entry.TurnId) && entry.TurnId != SummaryTurnId)];
        var source = new List<LlmAgentContextEntry>();
        string? cursor = null;
        do
        {
            LlmTranscriptPage page = await transcript.ReadAsync(cursor, TranscriptPageSize, cancellationToken).ConfigureAwait(false);
            source.AddRange(page.Entries.Where(entry => !keptTurns.Contains(entry.TurnId) && entry.TurnId != SummaryTurnId));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return source;
    }

    /// <summary>Summarizes the source in parts within half the usable window, then combines the part summaries.</summary>
    private async Task<(string Summary, LlmUsage? Usage)> SummarizeAsync(IReadOnlyList<LlmAgentContextEntry> source,
        string modelId, int words, CompactionLimits limits, CancellationToken cancellationToken)
    {
        // Half the usable window leaves the other half for the prompt and the answer.
        long partLimit = Math.Max(1, limits.UsableWindow / 2);
        var pieces = new List<(string Text, long Tokens)>();
        int shortened = 0, removedImages = 0;
        // The summary input cuts tool output as step 1 does; the counts describe the context, not the input.
        foreach (LlmAgentContextEntry entry in source)
            pieces.AddRange(Split(Render(Shorten(entry, ref shortened, ref removedImages).Message), partLimit));
        var usage = new UsageSum();
        List<(string Text, long Tokens)> summaries = await SummarizePartsAsync(Group(pieces, partLimit), modelId, words,
            usage, cancellationToken).ConfigureAwait(false);
        // Part summaries that together exceed the word limit are summarized together, until one fits.
        while (summaries.Sum(summary => WordCount(summary.Text)) > words && summaries.Count > 1)
        {
            List<(string Text, long Tokens)> groups = Group(summaries, partLimit);
            if (groups.Count >= summaries.Count) break;
            summaries = await SummarizePartsAsync(groups, modelId, words, usage, cancellationToken).ConfigureAwait(false);
        }
        return (string.Join("\n\n", summaries.Select(summary => summary.Text)), usage.Total);
    }

    /// <summary>One side request per part, one at a time; each part's words are in proportion to its size.</summary>
    private async Task<List<(string Text, long Tokens)>> SummarizePartsAsync(List<(string Text, long Tokens)> parts,
        string modelId, int words, UsageSum usage, CancellationToken cancellationToken)
    {
        long total = Math.Max(1, parts.Sum(part => part.Tokens));
        var summaries = new List<(string Text, long Tokens)>(parts.Count);
        foreach ((string text, long tokens) in parts)
        {
            int partWords = (int)Math.Max(MinimumPartWords, (double)words * tokens / total);
            LlmChatResult result = await sideRequest(SummaryRequest.Create(modelId, text, partWords), cancellationToken)
                .ConfigureAwait(false);
            usage.Add(result.Usage);
            summaries.Add((result.Text, Estimate(result.Text)));
        }
        return summaries;
    }

    /// <summary>Joins pieces in order into parts whose estimates stay within the part limit.</summary>
    private static List<(string Text, long Tokens)> Group(IReadOnlyList<(string Text, long Tokens)> pieces, long partLimit)
    {
        var parts = new List<(string Text, long Tokens)>();
        var text = new StringBuilder();
        long tokens = 0;
        foreach ((string piece, long size) in pieces)
        {
            if (text.Length > 0 && tokens + size > partLimit)
            {
                parts.Add((text.ToString(), tokens));
                text.Clear();
                tokens = 0;
            }
            if (text.Length > 0) text.Append("\n\n");
            text.Append(piece);
            tokens += size;
        }
        if (text.Length > 0) parts.Add((text.ToString(), tokens));
        return parts;
    }

    /// <summary>A text within the part limit as one piece; a larger one split at line boundaries, then by characters.</summary>
    private List<(string Text, long Tokens)> Split(string text, long partLimit)
    {
        long tokens = Estimate(text);
        if (tokens <= partLimit) return [(text, tokens)];
        string[] lines = text.Split('\n');
        if (lines.Length > 1) return Group([.. lines.SelectMany(line => Split(line, partLimit))], partLimit);
        int half = text.Length / 2;
        if (half == 0) return [(text, tokens)];
        return [.. Split(text[..half], partLimit), .. Split(text[half..], partLimit)];
    }

    private long Estimate(string text) => estimateText(text) ?? 0;

    /// <summary>The entry's transcript text, attachments as names. A reference attachment carries no name here.</summary>
    private static string Render(LlmChatMessage message)
    {
        var text = new StringBuilder();
        TranscriptText.Append(text, [message with { Attachments = [.. message.Attachments.OfType<LlmAttachment>()] }]);
        return text.ToString();
    }

    private static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// Shortens every unprotected tool result: each text part over 2,000 characters keeps its first and last 1,000 with a
    /// marker between them, and each binary part becomes a marker. A result counts once in each count.
    /// </summary>
    private static (LlmAgentContext Context, int Shortened, int RemovedImages) Shorten(LlmAgentContext context,
        IReadOnlySet<string> protectedEntryIds)
    {
        int shortened = 0, removedImages = 0;
        var entries = new List<LlmAgentContextEntry>(context.Entries.Count);
        foreach (LlmAgentContextEntry entry in context.Entries)
            entries.Add(protectedEntryIds.Contains(entry.Id) ? entry : Shorten(entry, ref shortened, ref removedImages));
        return (context with { Entries = entries }, shortened, removedImages);
    }

    /// <summary>Shortens the tool results of one entry and adds them to the counts.</summary>
    private static LlmAgentContextEntry Shorten(LlmAgentContextEntry entry, ref int shortened, ref int removedImages)
    {
        if (entry.Message.ToolResults.Count == 0) return entry;
        var results = new List<LlmClientToolResult>(entry.Message.ToolResults.Count);
        foreach (LlmClientToolResult result in entry.Message.ToolResults)
        {
            if (result is not LlmToolResult function)
            {
                results.Add(result);
                continue;
            }
            bool cut = false, image = false;
            var parts = new List<LlmToolOutputPart>(function.Output.Parts.Count);
            foreach (LlmToolOutputPart part in function.Output.Parts)
            {
                switch (part)
                {
                    case LlmToolOutputText { Text: var text } when text.Length > 2 * KeptCharacters && !IsShortened(text):
                        parts.Add(new LlmToolOutputText(Cut(text)));
                        cut = true;
                        break;
                    case LlmToolOutputBinary binary:
                        parts.Add(new LlmToolOutputText($"[{binary.FileName ?? binary.MediaType} removed by compaction.]"));
                        image |= binary.Kind == LlmBinaryKind.Image;
                        break;
                    case LlmToolOutputResource { Data: not null } resource:
                        parts.Add(new LlmToolOutputText($"[{resource.Name ?? resource.Uri} removed by compaction.]"));
                        image |= resource.BodyKind == LlmBinaryKind.Image;
                        break;
                    default:
                        parts.Add(part);
                        break;
                }
            }
            if (cut) shortened++;
            if (image) removedImages++;
            results.Add(function with { Output = function.Output with { Parts = parts } });
        }
        return entry with { Message = entry.Message with { ToolResults = results } };
    }

    private static string Cut(string text) =>
        $"{text[..KeptCharacters]}[{text.Length - 2 * KeptCharacters} of {text.Length} characters removed by compaction.]"
        + text[^KeptCharacters..];

    // A text an earlier compaction cut is not cut or counted again.
    private static bool IsShortened(string text) =>
        text.AsSpan(KeptCharacters).StartsWith("[") && text.Contains(" characters removed by compaction.]", StringComparison.Ordinal);

    /// <summary>Side-request usage summed the way a run sums its steps.</summary>
    private sealed class UsageSum
    {
        private int _calls;
        internal LlmUsage? Total { get; private set; }

        internal void Add(LlmUsage? usage)
        {
            Total = UsageTotals.Add(Total, usage, _calls == 0);
            _calls++;
        }
    }
}
