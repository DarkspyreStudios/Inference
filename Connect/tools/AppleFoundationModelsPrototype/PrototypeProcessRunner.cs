using System.Diagnostics;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace AppleFoundationModelsPrototype;

internal static class PrototypeProcessRunner
{
    internal static async Task<PrototypeProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        CliProcessTermination.Isolate(startInfo);

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Cannot start worker.");
        using var reads = new CancellationTokenSource();
        Task<string[]> output = Task.WhenAll(
            process.StandardOutput.ReadToEndAsync(reads.Token),
            process.StandardError.ReadToEndAsync(reads.Token));
        var clock = Stopwatch.StartNew();
        bool timedOut = false;
        try
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                timedOut = true;
                await CliProcessTermination.KillAsync(process).ConfigureAwait(false);
            }

            string[] captured = await output.WaitAsync(CliProcessTermination.ExitBound, cancellationToken).ConfigureAwait(false);
            return new PrototypeProcessResult(process.ExitCode, timedOut, clock.Elapsed.TotalSeconds, captured[0], captured[1]);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    await CliProcessTermination.KillAsync(process).ConfigureAwait(false);
                }
            }
            finally
            {
                await reads.CancelAsync().ConfigureAwait(false);
                try
                {
                    await output.WaitAsync(CliProcessTermination.ExitBound, CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (reads.IsCancellationRequested)
                {
                    // Retirement cancels pipe readers after the owned worker is terminated.
                }
            }
        }
    }
}

internal sealed record PrototypeProcessResult(
    int ExitCode,
    bool TimedOut,
    double Seconds,
    string StandardOutput,
    string StandardError);
