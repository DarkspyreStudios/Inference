namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// What reported counts and too-long refusals taught about each model on each connection. A local server's limit is
/// its own setting, so nothing learned on one connection applies to another. A learned context limit is saved through
/// the host's <see cref="ILlmLearnedLimitStore"/> and comes back on the connection; the rate and the framing last only
/// for the process, so a model's first request after the process starts has no rate or framing.
/// </summary>
internal sealed class LearnedModelContext(ILlmLearnedLimitStore? store = null)
{
    // Below a quarter of the usable window, provider framing (CLI system prompt, tool schemas) distorts the rate; at 25%
    // of a 200K window a 5K framing moves it by about 11%, toward compacting early.
    private const double MinimumReportShare = 0.25;

    private readonly Lock _sync = new();
    private readonly Dictionary<(string ConnectionId, string ModelId), Facts> _facts = [];

    /// <summary>
    /// A step's full local estimate and the input tokens the provider reported for it. At a quarter of the usable window
    /// or more the pair teaches the rate. Below it, the reported surplus over the estimate is the provider's framing: text
    /// the service adds to every request that the request does not show, such as a hidden system prompt.
    /// </summary>
    internal void ObserveReport(string connectionId, string modelId, long estimated, long reported, long usableWindow)
    {
        if (estimated <= 0 || reported <= 0) return;
        lock (_sync)
        {
            Facts facts = For(connectionId, modelId);
            if (estimated >= MinimumReportShare * usableWindow) facts.Pair = (estimated, reported);
            // A surplus below zero is the local count running high, which an estimate may do; it teaches no framing.
            else facts.Framing = Math.Max(0, reported - estimated);
        }
    }

    /// <summary>
    /// A too-long refusal. Reported values come from the refusal text when present. A limit it teaches is saved through
    /// the store before this returns.
    /// </summary>
    internal async Task ObserveRefusalAsync(string connectionId, string modelId, long estimated, long? reportedTokens,
        long? reportedLimit, long? knownLimit, CancellationToken cancellationToken)
    {
        long? learned;
        lock (_sync)
        {
            Facts facts = For(connectionId, modelId);
            long? before = facts.Limit;
            if (estimated > 0)
            {
                if (reportedTokens is > 0)
                {
                    facts.Pair = (estimated, reportedTokens.Value);
                }
                else if ((reportedLimit ?? knownLimit) is long limit and > 0
                    && (double)limit / estimated > Rate(facts))
                {
                    // The refused request was larger than the limit, so the rate is at least limit / estimate.
                    facts.Pair = (estimated, limit);
                }
            }
            if (reportedLimit is > 0) facts.Limit = reportedLimit;
            // With no limit at all, the window is below the refused request.
            else if (knownLimit is null && estimated > 0) facts.Limit = Correct(facts, estimated);
            learned = facts.Limit != before ? facts.Limit : null;
        }
        if (learned is long saved && store is not null)
            await store.SaveContextLimitAsync(connectionId, modelId, saved, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The estimate times the learned rate, plus the learned framing; the estimate unchanged when nothing was learned.
    /// The rate is taken with the framing removed from the reported count, so a rate learned on a small capped window
    /// does not absorb the framing and leave a short request low.
    /// </summary>
    internal long Correct(string connectionId, string modelId, long estimated)
    {
        lock (_sync) { return _facts.TryGetValue((connectionId, modelId), out Facts? facts) ? Correct(facts, estimated) : estimated; }
    }

    /// <summary>The limit a refusal taught in this process, else the one the connection carries as saved, or null.</summary>
    internal long? Limit(string connectionId, LlmConnection connection, string modelId)
    {
        lock (_sync)
        {
            if (_facts.TryGetValue((connectionId, modelId), out Facts? facts) && facts.Limit is long learned) return learned;
        }
        return connection.LearnedContextLimits.TryGetValue(modelId, out long saved) && saved > 0 ? saved : null;
    }

    /// <summary>Adds the values an "unsupported" refusal named; later requests on the connection and model leave them out.</summary>
    internal void ObserveRefusedSettings(string connectionId, string modelId, LlmRefusedSettings settings)
    {
        lock (_sync) { For(connectionId, modelId).Refused |= settings; }
    }

    /// <summary>The values refusals named for the connection and model.</summary>
    internal LlmRefusedSettings RefusedSettings(string connectionId, string modelId)
    {
        lock (_sync) { return _facts.TryGetValue((connectionId, modelId), out Facts? facts) ? facts.Refused : LlmRefusedSettings.None; }
    }

    private Facts For(string connectionId, string modelId)
    {
        if (!_facts.TryGetValue((connectionId, modelId), out Facts? facts)) _facts[(connectionId, modelId)] = facts = new();
        return facts;
    }

    private static double Rate(Facts facts) =>
        facts.Pair is (long estimated, long reported) ? (double)Math.Max(1, reported - facts.Framing) / estimated : 1;

    private static long Correct(Facts facts, long estimated) => checked((long)Math.Ceiling(estimated * Rate(facts)) + facts.Framing);

    private sealed class Facts
    {
        internal (long Estimated, long Reported)? Pair { get; set; }
        internal long Framing { get; set; }
        internal long? Limit { get; set; }
        internal LlmRefusedSettings Refused { get; set; }
    }
}
