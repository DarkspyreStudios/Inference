namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// What reported counts and too-long refusals taught about each model on each connection. A local server's limit is
/// its own setting, so nothing learned on one connection applies to another. Nothing is stored.
/// </summary>
internal sealed class LearnedModelContext
{
    // Below a quarter of the usable window, provider framing (CLI system prompt, tool schemas) distorts the rate; at 25%
    // of a 200K window a 5K framing moves it by about 11%, toward compacting early.
    private const double MinimumReportShare = 0.25;

    private readonly Lock _sync = new();
    private readonly Dictionary<(string ConnectionId, string ModelId), Facts> _facts = [];

    /// <summary>A step's full local estimate and the input tokens the provider reported for it.</summary>
    internal void ObserveReport(string connectionId, string modelId, long estimated, long reported, long usableWindow)
    {
        if (estimated <= 0 || reported <= 0 || estimated < MinimumReportShare * usableWindow) return;
        lock (_sync) { For(connectionId, modelId).Pair = (estimated, reported); }
    }

    /// <summary>A too-long refusal. Reported values come from the refusal text when present.</summary>
    internal void ObserveRefusal(string connectionId, string modelId, long estimated, long? reportedTokens,
        long? reportedLimit, long? knownLimit)
    {
        lock (_sync)
        {
            Facts facts = For(connectionId, modelId);
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
        }
    }

    /// <summary>The estimate times the learned rate; the estimate unchanged when nothing was learned.</summary>
    internal long Correct(string connectionId, string modelId, long estimated)
    {
        lock (_sync) { return _facts.TryGetValue((connectionId, modelId), out Facts? facts) ? Correct(facts, estimated) : estimated; }
    }

    /// <summary>The limit a refusal taught, or null.</summary>
    internal long? Limit(string connectionId, string modelId)
    {
        lock (_sync) { return _facts.TryGetValue((connectionId, modelId), out Facts? facts) ? facts.Limit : null; }
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

    private static double Rate(Facts facts) => facts.Pair is (long estimated, long reported) ? (double)reported / estimated : 1;

    private static long Correct(Facts facts, long estimated) =>
        facts.Pair is null ? estimated : (long)Math.Ceiling(estimated * Rate(facts));

    private sealed class Facts
    {
        internal (long Estimated, long Reported)? Pair { get; set; }
        internal long? Limit { get; set; }
        internal LlmRefusedSettings Refused { get; set; }
    }
}
