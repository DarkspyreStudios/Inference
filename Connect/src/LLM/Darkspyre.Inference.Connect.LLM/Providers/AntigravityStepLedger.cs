using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Completed step observations for one turn, excluding previously accounted process-generation steps.</summary>
internal sealed class AntigravityStepLedger(int generation = 0, ISet<(int Generation, int Index)>? accounted = null)
{
    private readonly Dictionary<int, AntigravityStep> _done = [];

    /// <summary>Returns whether to project the step's content. Repeated usage replaces the previous observation.</summary>
    public bool Record(AntigravityStep step)
    {
        if (!step.IsDone) { return !(_done.ContainsKey(step.StepIndex) || accounted?.Contains((generation, step.StepIndex)) == true); }
        if (_done.TryGetValue(step.StepIndex, out AntigravityStep? existing))
        {
            if (!SameContent(existing, step))
            {
                throw new InvalidDataException("Antigravity CLI reported conflicting final data for one step.");
            }
            _done[step.StepIndex] = step;
            return false;
        }
        if (accounted is not null && !accounted.Add((generation, step.StepIndex))) { return false; }
        _done.Add(step.StepIndex, step);
        return true;
    }

    public LlmUsage? Usage
    {
        get
        {
            LlmUsage? total = null;
            bool first = true;
            foreach (AntigravityStep step in _done.Values.Where(step => step.StepType is "agent_response" or "checkpoint"))
            {
                total = UsageTotals.Add(total, Normalize(step.Usage), first);
                first = false;
            }
            return total;
        }
    }

    public static LlmUsage? Normalize(AntigravityUsage? usage)
    {
        if (usage is null || usage.Invalid) { return null; }
        int? input = Count(usage.InputTokens), output = Count(usage.OutputTokens);
        int? total = Count(usage.TotalTokens);
        if (usage.TotalTokens is null && input is not null && output is not null)
        {
            total = Count((long)input.Value + output.Value);
        }
        return input is null && output is null && total is null ? null : new(input, output, total);
    }

    private static int? Count(long? value) => value is >= 0 and <= int.MaxValue ? (int)value : null;

    private static bool SameContent(AntigravityStep left, AntigravityStep right) =>
        left.StepType == right.StepType
        && left.TextDelta == right.TextDelta
        && left.Tool?.Name == right.Tool?.Name
        && left.Tool?.Output == right.Tool?.Output
        && left.Tool?.ErrorMessage == right.Tool?.ErrorMessage;
}
