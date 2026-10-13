using System.Diagnostics;
using System.Text.Json;
using TsTensor = TensorSharp.Tensor;

namespace Darkspyre.Inference.Tensor.GemmaExperiment;

// Diagnostics belong to this isolated executable. Trace reads synchronize native work;
// profile mode records elapsed phases without reading tensor contents.
internal sealed class ForwardDiagnostic
{
    private readonly string output;
    private readonly int run;
    private readonly string? mode = Environment.GetEnvironmentVariable("GEMMA_DIAGNOSTIC");
    private readonly string? tracePoint = Environment.GetEnvironmentVariable("GEMMA_TRACE_POINT");
    private readonly List<object> phases = [];
    private long previous = Stopwatch.GetTimestamp();
    private readonly long allocated = GC.GetTotalAllocatedBytes();
    private readonly int[] collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];

    internal ForwardDiagnostic(string output, int run)
    {
        this.output = output;
        this.run = run;
        if (mode is not (null or "trace" or "profile"))
            throw new ArgumentException("GEMMA_DIAGNOSTIC accepts trace or profile.");
    }

    internal void Mark(string name, TsTensor tensor)
    {
        if (mode == null || (mode == "trace" && tracePoint != null && tracePoint != name)) return;
        double elapsed = Stopwatch.GetElapsedTime(previous).TotalMilliseconds;
        if (mode == "trace")
        {
            WriteTensor(name, tensor);
        }
        phases.Add(new { name, elapsedMs = elapsed, shape = tensor.Sizes.ToArray() });
        previous = Stopwatch.GetTimestamp();
    }

    private void WriteTensor(string name, TsTensor tensor)
    {
        float[] values = tensor.GetElementsAsFloat(checked((int)tensor.ElementCount()));
        using var stream = File.Create(Path.Combine(output, $"run-{run:D2}-{name}.f32"));
        stream.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
    }

    internal void AttentionInputs(string name, TsTensor query, TsTensor key, TsTensor value)
    {
        if (mode != "trace" || tracePoint != name) return;
        // Read inputs only after the attention result has completed. This preserves
        // the failing invocation's schedule before its result checkpoint.
        WriteTensor(name + "-input-query", query);
        WriteTensor(name + "-input-key", key);
        WriteTensor(name + "-input-value", value);
    }

    // Read-only debugger-style inspection of the pinned runtime's BCL process slot.
    // No shipped consumer uses this private representation or mutates its registry.
    private static object InspectOwnership()
    {
        var state = (object[])AppDomain.CurrentDomain.GetData("Darkspyre.TensorSharp.NativeQuarantine")!;
        lock (state[1])
        {
            var owners = (Dictionary<Guid, object[]>)state[4];
            return new
            {
                count = owners.Count,
                liveTargets = owners.Values.Count(cell => ((WeakReference<object>)cell[2]).TryGetTarget(out _))
            };
        }
    }

    internal void Write()
    {
        if (mode == null) return;
        File.WriteAllText(Path.Combine(output, $"diagnostic-{run:D2}.json"), JsonSerializer.Serialize(new
        {
            mode,
            run,
            ownership = InspectOwnership(),
            phases,
            allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
            collections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray(),
            tailMs = Stopwatch.GetElapsedTime(previous).TotalMilliseconds
        }));
    }
}
