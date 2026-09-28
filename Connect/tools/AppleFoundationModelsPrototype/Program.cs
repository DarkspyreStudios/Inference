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
        if (args.Length is < 2 or > 3 || args[0] is not ("suite" or "worker") || args[0] == "worker" && args.Length != 3)
        {
            Console.Error.WriteLine("Usage: <tool> suite <absolute-library-path> [report-path] | worker <absolute-library-path> <experiment>");
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
        if (args[0] == "suite") return await SuiteAsync(library, args.Length == 3 ? args[2] : ".artifacts/results.json").ConfigureAwait(false);
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

    private static async Task<int> SuiteAsync(string library, string reportPath)
    {
        var observations = new List<object>();
        int errors = 0;
        bool unavailable = false;
        string assembly = Assembly.GetExecutingAssembly().Location;
        foreach (string experiment in Experiments.Names)
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(assembly);
            start.ArgumentList.Add("worker");
            start.ArgumentList.Add(library);
            start.ArgumentList.Add(experiment);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start worker.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            bool timedOut = false;
            var clock = Stopwatch.StartNew();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                timedOut = true;
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            string output = await stdout.ConfigureAwait(false);
            string error = await stderr.ConfigureAwait(false);
            observations.Add(new { experiment, process.ExitCode, timedOut, seconds = clock.Elapsed.TotalSeconds, stdout = output, stderr = error });
            Console.WriteLine($"{experiment}: exit={process.ExitCode}, timeout={timedOut}");
            Console.Write(output);
            if (error.Length != 0) Console.Error.Write(error);
            if (timedOut || process.ExitCode is not (0 or 3)) errors++;
            if (process.ExitCode == 3)
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
