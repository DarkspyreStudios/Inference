using System.Runtime.ExceptionServices;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Retains only genuine admitted cleanup carriers for one lifetime owner.</summary>
internal sealed class LlmCleanupOutcomes
{
    private readonly Lock _gate = new();
    private readonly List<Exception> _failures = [];

    internal void Record(Exception error)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var failures = new List<Exception>();
        pending.Push(error);
        while (pending.TryPop(out Exception? current))
        {
            if (!visited.Add(current)) continue;
            if (current is InferenceResourceOperationException resource && resource.Failure.Phase == InferenceResourceOperationPhase.Cleanup
                || current is InferenceResourceOperationCanceledException canceled && canceled.Failure.Phase == InferenceResourceOperationPhase.Cleanup)
                failures.Add(current);
            if (current is AggregateException aggregate)
                for (int index = aggregate.InnerExceptions.Count - 1; index >= 0; index--) pending.Push(aggregate.InnerExceptions[index]);
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
        lock (_gate)
            foreach (Exception failure in failures)
                if (!_failures.Any(existing => ReferenceEquals(existing, failure))) _failures.Add(failure);
    }

    internal Task ReleaseAsync()
    {
        Exception[] failures;
        lock (_gate) failures = _failures.ToArray();
        if (failures.Length == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Length > 1) throw new AggregateException(failures);
        return Task.CompletedTask;
    }
}
