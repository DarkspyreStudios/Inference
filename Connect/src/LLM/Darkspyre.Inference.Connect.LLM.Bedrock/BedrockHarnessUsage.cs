using Amazon.BedrockAgentCore.Model;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

/// <summary>Replaces one invocation's observations and sums only distinct settled invocations.</summary>
internal sealed class BedrockHarnessUsage(bool interimEnabled)
{
    private UsageDeltas _deltas = new(interimEnabled);
    private bool _observed;
    private bool _settled;
    private int _completedRounds;
    private LlmUsage? _totals;

    internal LlmUsage? Current { get; private set; }
    internal LlmUsageDeltaEvent? Delta { get; private set; }
    internal LlmUsage? RunningTotals => UsageTotals.Add(_totals, Current, _completedRounds == 0);

    internal void BeginRound()
    {
        Current = null;
        Delta = null;
        _observed = false;
        _settled = false;
        _deltas = new(interimEnabled);
    }

    internal bool Observe(HarnessMetadataEvent metadata)
    {
        LlmUsage? previous = Current;
        Current = metadata.Usage is { } usage
            ? new(usage.InputTokens, usage.OutputTokens, usage.TotalTokens)
            : null;
        // Cache inclusion is unqualified. Never apply Anthropic's additive normalization here.
        bool changed = !_observed || Current != previous;
        _observed = true;
        if (Current is null) _deltas = new(interimEnabled);
        Delta = _deltas.Next(Current);
        return interimEnabled && changed;
    }

    internal LlmUsage? Settle()
    {
        if (!_settled)
        {
            _totals = RunningTotals;
            _completedRounds++;
            _settled = true;
        }
        return _totals;
    }
}
