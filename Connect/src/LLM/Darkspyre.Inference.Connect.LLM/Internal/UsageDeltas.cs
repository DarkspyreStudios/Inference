using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Emits an observation only when the running usage changes.</summary>
internal sealed class UsageDeltas(bool enabled)
{
    private LlmUsage? _previous;

    public LlmUsageDeltaEvent? Next(LlmUsage? cumulative)
    {
        if (!enabled || cumulative is null || cumulative == _previous)
        {
            return null;
        }

        _previous = cumulative;
        return new(cumulative);
    }
}
