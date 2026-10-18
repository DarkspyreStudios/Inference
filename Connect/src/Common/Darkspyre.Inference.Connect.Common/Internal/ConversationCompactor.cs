using System.Text.Json;
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

    /// <summary>
    /// The event that reports this compaction, with the context revision it produced. With
    /// <paramref name="carriesContext"/>, for a conversation whose context the host cannot capture, it carries the
    /// compacted context at that revision.
    /// </summary>
    internal LlmAgentCompactedEvent ToEvent(string threadId, string turnId, string requestId, long revision, bool carriesContext) =>
        new(threadId, turnId, requestId, revision, DroppedMessageCount, Usage)
        {
            ShortenedToolOutputCount = ShortenedToolOutputCount,
            RemovedImageCount = RemovedImageCount,
            Context = carriesContext ? Context with { Revision = revision } : null
        };
}

/// <summary>Compaction cannot bring a request under the usable window, so the request is not sent.</summary>
internal sealed class CompactionFailedException(string message, LlmUsage? usage) : Exception(message)
{
    internal LlmUsage? Usage { get; } = usage;
}

/// <summary>
/// Compacts one conversation's context for every provider: it shortens unprotected tool output, keeps the current turn
/// and the newest whole turns that fit, or only the current turn's input and newest steps when the turn alone does not
/// fit, and replaces the earlier summary and everything else with one new summary of them.
/// </summary>
/// <param name="measure">The corrected size of the whole request the provider would send with a context.</param>
/// <param name="sideRequest">Sends one <see cref="SummaryRequest"/> on the conversation's own connection and model.</param>
/// <param name="estimateText">The corrected size of a text, for splitting parts.</param>
/// <param name="transcript">The Host's full transcript, read only to summarize the turns that an omitted provider
/// compaction state left out; null drops those turns unsummarized.</param>
internal sealed class ConversationCompactor(
    Func<LlmAgentContext, CancellationToken, ValueTask<long>> measure,
    Func<LlmChatRequest, CancellationToken, Task<LlmChatResult>> sideRequest,
    Func<string, CancellationToken, ValueTask<long?>> estimateText,
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

    // About 4,000 tokens: long enough for decisions and open work, short enough to write in about a minute
    // (Brandon, 2026-10-10: compaction takes 1 to 3 minutes).
    private const int MaximumSummaryWords = 3000;

    // The longest summary's size in tokens.
    private const long MaximumSummaryTokens = (long)(MaximumSummaryWords / WordsPerToken);

    // About 1,000 tokens for each part's summary.
    private const int MinimumPartWords = 750;

    // The Host's own transcript page size.
    private const int TranscriptPageSize = 200;

    private const string SummaryPrefix = "Summary of the earlier conversation:\n";

    /// <summary>
    /// How long one summary request may take before the compaction fails. Ten minutes is well above a half-window part
    /// of a 1M-token window, which a sound request summarizes in a few minutes, and ends a stalled request
    /// (Brandon, 2026-10-18).
    /// </summary>
    internal TimeSpan PartTimeout { get; init; } = TimeSpan.FromMinutes(10);

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
        // A current turn that leaves no room under the target for the longest summary keeps only its input and newest
        // steps, and its older steps join the summary. Otherwise the result would stay above the target, and a turn
        // above the trigger would compact again at its next step. Under a target too small for that summary, the
        // summary keeps half the target.
        long currentRoom = limits.TargetTokens - Math.Min(MaximumSummaryTokens, limits.TargetTokens / 2);
        (HashSet<string> keptIds, long keptMeasure) = currentMeasure > currentRoom
            ? await KeepNewestStepsAsync(shortened, current, protectedEntryIds, currentRoom, cancellationToken).ConfigureAwait(false)
            : await KeepNewestTurnsAsync(shortened, currentTurnId, currentMeasure, limits, cancellationToken).ConfigureAwait(false);
        LlmAgentContextEntry[] kept = [.. entries.Where(entry => keptIds.Contains(entry.Id))];
        // The new summary replaces the earlier summary and every entry not kept, so it is built from both.
        LlmAgentContextEntry[] source = [.. entries.Where(entry => !keptIds.Contains(entry.Id))];
        if (source.All(entry => entry.TurnId == SummaryTurnId))
            return await FinishAsync(shortened, current, null, null, shortenedCount, removedImages, limits, cancellationToken)
                .ConfigureAwait(false);

        await stage("Summarizing earlier messages", cancellationToken).ConfigureAwait(false);
        int words = (int)Math.Clamp(WordsPerToken * (limits.TargetTokens - keptMeasure), MinimumSummaryWords, MaximumSummaryWords);
        (string summary, LlmUsage? usage) = await SummarizeAsync(source, modelId, words, limits, cancellationToken)
            .ConfigureAwait(false);
        var summaryEntry = new LlmAgentContextEntry(Guid.NewGuid().ToString("N"), SummaryTurnId,
            new LlmChatMessage("user", SummaryPrefix + summary));
        LlmAgentContext compacted = shortened with { Entries = [summaryEntry, .. kept] };
        return await FinishAsync(compacted, current, entries.Count - kept.Length, usage,
            shortenedCount, removedImages, limits, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The current turn and the newest older whole turns that fit in the room under the target that the longest summary
    /// leaves, plus the newest part of the next turn from the earliest tool call that still fits, with their measure.
    /// </summary>
    private async Task<(HashSet<string> Ids, long Measure)> KeepNewestTurnsAsync(LlmAgentContext shortened,
        string currentTurnId, long currentMeasure, CompactionLimits limits, CancellationToken cancellationToken)
    {
        IReadOnlyList<LlmAgentContextEntry> entries = shortened.Entries;
        var keptTurns = new HashSet<string>(StringComparer.Ordinal) { currentTurnId };
        long keptMeasure = currentMeasure;
        // Under a target too small for the longest summary, the summary keeps half the room.
        long room = Math.Max(0, limits.TargetTokens - currentMeasure);
        long olderRoom = room - Math.Min(MaximumSummaryTokens, room / 2);
        string? partialTurn = null;
        foreach (string turnId in entries.Select(entry => entry.TurnId)
            .Where(turnId => turnId != currentTurnId && turnId != SummaryTurnId).Distinct(StringComparer.Ordinal).Reverse())
        {
            var candidate = new HashSet<string>(keptTurns, StringComparer.Ordinal) { turnId };
            long candidateMeasure = await Measure(shortened, Kept(entries, candidate), cancellationToken).ConfigureAwait(false);
            if (candidateMeasure - currentMeasure > olderRoom)
            {
                partialTurn = turnId;
                break;
            }
            keptTurns = candidate;
            keptMeasure = candidateMeasure;
        }
        LlmAgentContextEntry[] whole = Kept(entries, keptTurns);
        var keptIds = new HashSet<string>(whole.Select(entry => entry.Id), StringComparer.Ordinal);
        // The turn that does not fit whole keeps its newest part, from the earliest tool call that still fits, so a
        // turn of many tool calls fills the room instead of being summarized whole (Brandon, 2026-10-10).
        if (partialTurn is not null)
        {
            LlmAgentContextEntry[] turn = [.. entries.Where(entry => entry.TurnId == partialTurn)];
            if (await EarliestStartAsync(shortened, whole, turn, Starts(turn),
                    measured => measured - currentMeasure <= olderRoom, cancellationToken).ConfigureAwait(false) is { } fit)
            {
                keptIds.UnionWith(turn[fit.Start..].Select(entry => entry.Id));
                keptMeasure = fit.Measure;
            }
        }
        return (keptIds, keptMeasure);
    }

    /// <summary>
    /// The current turn's first entry, its input, and its newest steps from the earliest tool call whose kept part
    /// measures within <paramref name="room"/>, with their measure. A start never passes the turn's first protected
    /// entry, so a protected call and its results stay. When no start fits, the latest allowed start keeps the least.
    /// </summary>
    private async Task<(HashSet<string> Ids, long Measure)> KeepNewestStepsAsync(LlmAgentContext shortened,
        LlmAgentContextEntry[] turn, IReadOnlySet<string> protectedEntryIds, long room, CancellationToken cancellationToken)
    {
        int firstProtected = Array.FindIndex(turn, entry => protectedEntryIds.Contains(entry.Id));
        int[] starts = [.. Starts(turn).Where(index => firstProtected < 0 || index <= firstProtected)];
        if (starts.Length == 0)
            return (turn.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal),
                await Measure(shortened, turn, cancellationToken).ConfigureAwait(false));
        LlmAgentContextEntry[] input = [turn[0]];
        (int start, long kept) = await EarliestStartAsync(shortened, input, turn, starts, measured => measured <= room,
                cancellationToken).ConfigureAwait(false)
            ?? (starts[^1], await Measure(shortened, [.. input, .. turn[starts[^1]..]], cancellationToken).ConfigureAwait(false));
        return ([turn[0].Id, .. turn[start..].Select(entry => entry.Id)], kept);
    }

    /// <summary>The indexes after the first of a turn's entries that hold a tool call: where a kept newest part can start.</summary>
    private static int[] Starts(LlmAgentContextEntry[] turn) =>
        [.. Enumerable.Range(1, Math.Max(0, turn.Length - 1)).Where(index => turn[index].Message.ToolCalls.Count > 0)];

    /// <summary>
    /// The earliest start whose kept part, after <paramref name="before"/>, measures as <paramref name="fits"/> accepts,
    /// with that measure; null when none fits. A later start keeps less, so the start is found by bisection.
    /// </summary>
    private async Task<(int Start, long Measure)?> EarliestStartAsync(LlmAgentContext shortened,
        LlmAgentContextEntry[] before, LlmAgentContextEntry[] turn, int[] starts, Func<long, bool> fits,
        CancellationToken cancellationToken)
    {
        int low = 0, high = starts.Length;
        long found = 0;
        while (low < high)
        {
            int middle = (low + high) / 2;
            long candidateMeasure = await Measure(shortened, [.. before, .. turn[starts[middle]..]], cancellationToken)
                .ConfigureAwait(false);
            if (fits(candidateMeasure))
            {
                high = middle;
                found = candidateMeasure;
            }
            else low = middle + 1;
        }
        return low < starts.Length ? (starts[low], found) : null;
    }

    /// <summary>
    /// Replaces the entries left empty when a provider's own compaction state was omitted with one summary of the
    /// transcript turns the context no longer holds (steps 4-7). Without a transcript, or when the transcript holds no
    /// such turn, the empty entries are dropped and nothing is summarized.
    /// </summary>
    /// <exception cref="LlmProviderException">A summary side request failed; the caller keeps its context.</exception>
    internal async Task<CompactionResult> SummarizeOmittedAsync(LlmAgentContext context, string modelId,
        IReadOnlySet<string> emptyEntryIds, CompactionLimits limits,
        Func<string, CancellationToken, ValueTask> stage, CancellationToken cancellationToken)
    {
        LlmAgentContext remaining = context with { Entries = [.. context.Entries.Where(entry => !emptyEntryIds.Contains(entry.Id))] };
        if (transcript is null) return new(remaining, null, null, 0, 0);
        var keptTurns = remaining.Entries.Select(entry => entry.TurnId).Where(turnId => turnId != SummaryTurnId)
            .ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<LlmAgentContextEntry> source = await SourceAsync(transcript, keptTurns, cancellationToken)
            .ConfigureAwait(false);
        if (source.Count == 0) return new(remaining, null, null, 0, 0);
        LlmAgentContextEntry[] kept = Kept(remaining.Entries, keptTurns);
        await stage("Summarizing earlier messages", cancellationToken).ConfigureAwait(false);
        long keptMeasure = await Measure(remaining, kept, cancellationToken).ConfigureAwait(false);
        int words = (int)Math.Clamp(WordsPerToken * (limits.TargetTokens - keptMeasure), MinimumSummaryWords, MaximumSummaryWords);
        (string summary, LlmUsage? usage) = await SummarizeAsync(source, modelId, words, limits, cancellationToken)
            .ConfigureAwait(false);
        var summaryEntry = new LlmAgentContextEntry(Guid.NewGuid().ToString("N"), SummaryTurnId,
            new LlmChatMessage("user", SummaryPrefix + summary));
        // The result is not checked against the window: the step's own decision point compacts what is still too large.
        return new(remaining with { Entries = [summaryEntry, .. kept] }, context.Entries.Count - kept.Length, usage, 0, 0);
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

    /// <summary>The entries to summarize: the transcript's real entries in turns that are not kept.</summary>
    private static async Task<IReadOnlyList<LlmAgentContextEntry>> SourceAsync(ILlmConversationTranscript transcript,
        HashSet<string> keptTurns, CancellationToken cancellationToken)
    {
        bool Summarized(LlmAgentContextEntry entry) => !keptTurns.Contains(entry.TurnId) && entry.TurnId != SummaryTurnId;
        var source = new List<LlmAgentContextEntry>();
        string? cursor = null;
        do
        {
            LlmTranscriptPage page = await transcript.ReadAsync(cursor, TranscriptPageSize, cancellationToken).ConfigureAwait(false);
            source.AddRange(page.Entries.Where(Summarized));
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
        {
            string rendered = Render(Shorten(entry, ref shortened, ref removedImages).Message);
            pieces.AddRange(await SplitAsync(rendered, partLimit, cancellationToken).ConfigureAwait(false));
        }
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

    /// <summary>
    /// One side request per part, one at a time, so a compaction never sends a burst of large requests on one account.
    /// Each part's words are in proportion to its size. The first failure ends the compaction, and so does a request
    /// that runs past <see cref="PartTimeout"/>.
    /// </summary>
    /// <exception cref="CompactionFailedException">A summary request ran past <see cref="PartTimeout"/>.</exception>
    private async Task<List<(string Text, long Tokens)>> SummarizePartsAsync(List<(string Text, long Tokens)> parts,
        string modelId, int words, UsageSum usage, CancellationToken cancellationToken)
    {
        long total = Math.Max(1, parts.Sum(part => part.Tokens));
        var summaries = new List<(string Text, long Tokens)>(parts.Count);
        foreach ((string text, long tokens) in parts)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(PartTimeout);
            LlmChatResult result;
            try
            {
                result = await sideRequest(SummaryRequest.Create(modelId, text,
                    (int)Math.Max(MinimumPartWords, (double)words * tokens / total)), limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CompactionFailedException(
                    $"Compaction stopped: a summary request of about {tokens} tokens did not finish within {PartTimeout.TotalMinutes:0} "
                    + "minutes. The context is unchanged.", usage.Total);
            }
            usage.Add(result.Usage);
            summaries.Add((result.Text, await EstimateAsync(result.Text, cancellationToken).ConfigureAwait(false)));
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
    private async Task<List<(string Text, long Tokens)>> SplitAsync(string text, long partLimit,
        CancellationToken cancellationToken)
    {
        long tokens = await EstimateAsync(text, cancellationToken).ConfigureAwait(false);
        if (tokens <= partLimit) return [(text, tokens)];
        string[] lines = text.Split('\n');
        if (lines.Length > 1)
        {
            var pieces = new List<(string Text, long Tokens)>();
            foreach (string line in lines) pieces.AddRange(await SplitAsync(line, partLimit, cancellationToken).ConfigureAwait(false));
            return Group(pieces, partLimit);
        }
        int half = text.Length / 2;
        if (half == 0) return [(text, tokens)];
        return [.. await SplitAsync(text[..half], partLimit, cancellationToken).ConfigureAwait(false),
            .. await SplitAsync(text[half..], partLimit, cancellationToken).ConfigureAwait(false)];
    }

    private async ValueTask<long> EstimateAsync(string text, CancellationToken cancellationToken) =>
        await estimateText(text, cancellationToken).ConfigureAwait(false) ?? 0;

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
            // Structured content reaches the model as text, so a large one is cut like a text part and sent as one.
            JsonElement? structured = function.Output.StructuredContent;
            if (structured is { } json && json.GetRawText() is { Length: > 2 * KeptCharacters } raw)
            {
                parts.Add(new LlmToolOutputText(Cut(raw)));
                structured = null;
                cut = true;
            }
            if (cut) shortened++;
            if (image) removedImages++;
            results.Add(function with { Output = function.Output with { Parts = parts, StructuredContent = structured } });
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
