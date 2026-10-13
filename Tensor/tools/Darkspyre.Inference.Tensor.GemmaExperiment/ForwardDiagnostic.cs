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
        if (mode == null) return;
        double elapsed = Stopwatch.GetElapsedTime(previous).TotalMilliseconds;
        if (mode == "trace")
        {
            float[] values = tensor.GetElementsAsFloat(checked((int)tensor.ElementCount()));
            using var stream = File.Create(Path.Combine(output, $"run-{run:D2}-{name}.f32"));
            stream.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
        }
        phases.Add(new { name, elapsedMs = elapsed, shape = tensor.Sizes.ToArray() });
        previous = Stopwatch.GetTimestamp();
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
