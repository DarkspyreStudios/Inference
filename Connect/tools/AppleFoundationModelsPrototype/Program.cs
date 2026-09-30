using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AppleFoundationModelsPrototype;

internal static class Program
{
    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };

    [SuppressMessage("Design", "CA1031", Justification = "The isolated research worker records unexpected native/probe failures for the parent.")]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length is < 2 or > 3 || args[0] is not ("suite" or "lifecycle" or "batches" or "worker") || args[0] == "worker" && args.Length != 3)
        {
            Console.Error.WriteLine("Usage: <tool> suite|lifecycle|batches <library-path> [report-path] | worker <library-path> <experiment>");
            return 2;
        }
        if (!OperatingSystem.IsMacOSVersionAtLeast(26) || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { outcome = "unavailable", reason = "Requires macOS 26+ on arm64." }));
            return 3;
        }
        string library = Path.GetFullPath(args[1]);
        if (!File.Exists(library))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { outcome = "unavailable", reason = "Build the pinned native bridge first.", library }));
            return 3;
        }
        if (args[0] != "worker") return await SuiteAsync(library,
            args.Length == 3 ? args[2] : $".artifacts/{args[0]}-results.json",
            args[0] switch
            {
                "lifecycle" => Experiments.LifecycleNames,
                "batches" => Experiments.BatchNames,
                _ => Experiments.Names
            }).ConfigureAwait(false);
        try
        {
            Native.Load(library);
            Evidence evidence = await Experiments.RunAsync(args[2]).ConfigureAwait(false);
            await Task.Delay(100).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                experiment = args[2],
                evidence.Outcome,
                evidence.Details,
                lateCallbacks = NativeOperation.UnboundCallbacks,
                processId = Environment.ProcessId
            }));
            return evidence.Outcome switch { "unavailable" => 3, "unexpected" or "inconclusive" => 2, _ => NativeOperation.UnboundCallbacks == 0 ? 0 : 2 };
        }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { experiment = args[2], outcome = "error", error = error.ToString() }));
            return 2;
        }
    }

    private static async Task<int> SuiteAsync(string library, string reportPath, string[] experiments)
    {
        var observations = new List<object>();
        int errors = 0;
        bool unavailable = false;
        string assembly = Assembly.GetExecutingAssembly().Location;
        foreach (string experiment in experiments)
        {
            var start = new ProcessStartInfo("dotnet");
            start.ArgumentList.Add(assembly);
            start.ArgumentList.Add("worker");
            start.ArgumentList.Add(library);
            start.ArgumentList.Add(experiment);
            PrototypeProcessResult result = await PrototypeProcessRunner.RunAsync(start, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
            observations.Add(new { experiment, result.ExitCode, timedOut = result.TimedOut, seconds = result.Seconds, stdout = result.StandardOutput, stderr = result.StandardError });
            Console.WriteLine($"{experiment}: exit={result.ExitCode}, timeout={result.TimedOut}");
            Console.Write(result.StandardOutput);
            if (result.StandardError.Length != 0) Console.Error.Write(result.StandardError);
            if (result.TimedOut || result.ExitCode is not (0 or 3)) errors++;
            if (result.ExitCode == 3)
            {
                unavailable = true;
                break; // An unavailable environment is never presented as passing.
            }
        }
        var report = new
        {
            recordedAt = DateTimeOffset.UtcNow,
            bridgeCommit = "e868e60811aa0706feb2ccb33cfe7e27626287b7",
            operatingSystem = RuntimeInformation.OSDescription,
            runtime = RuntimeInformation.FrameworkDescription,
            library,
            note = "Observed is evidence from synthetic live probes, not a production capability guarantee. Limitation records an upstream or mapping gap.",
            observations
        };
        string path = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, ReportJson)).ConfigureAwait(false);
        Console.WriteLine($"Evidence: {path}");
        return unavailable ? 3 : errors == 0 ? 0 : 2;
    }
}
