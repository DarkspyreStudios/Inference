using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// One host turn's usage totals across the provider sessions it ran: the usage of finished sessions and side requests,
/// plus the open provider session's own totals for its turn. Every member is thread-safe.
/// </summary>
internal sealed class TurnUsage
{
    private readonly Lock _sync = new();
    private LlmUsage? _base;
    private bool _hasBase;
    private LlmUsage? _innerTotals;
    private bool _hasInnerTotals;

    /// <summary>The open provider session's totals for its own turn so far.</summary>
    public LlmUsage? InnerTotals
    {
        set { lock (_sync) { _innerTotals = value; _hasInnerTotals = true; } }
    }

    /// <summary>The turn's totals so far plus a provider session's own totals.</summary>
    public LlmUsage? Totals(LlmUsage? inner)
    {
        lock (_sync)
        {
            if (inner is null) return _hasBase ? _base : null;
            return _hasBase ? UsageTotals.Add(_base, inner, first: false) : inner;
        }
    }

    /// <summary>Adds usage outside the open provider session, such as a summary side request.</summary>
    public void Add(LlmUsage? usage)
    {
        lock (_sync)
        {
            _base = UsageTotals.Add(_base, usage, first: !_hasBase);
            _hasBase = true;
        }
    }

    /// <summary>Adds an ended provider session's final totals to the turn's totals.</summary>
    public void FoldInner()
    {
        LlmUsage? inner;
        lock (_sync)
        {
            if (!_hasInnerTotals) return;
            inner = _innerTotals;
            _hasInnerTotals = false;
            _innerTotals = null;
        }
        Add(inner);
    }
}
