using System.Diagnostics;
using System.Globalization;

namespace GpuArbiter.Backends;

public sealed class NvidiaReleaseGuard(CoordinatorOptions options) : IGpuReleaseGuard
{
    public async Task<GpuReleaseObservation> ReadAsync(CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--query-gpu=memory.used");
        process.StartInfo.ArgumentList.Add("--format=csv,noheader,nounits");
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            var values = (await stdout.ConfigureAwait(false)).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (process.ExitCode != 0 || values.Length != 1 || !int.TryParse(values[0], NumberStyles.None,
                CultureInfo.InvariantCulture, out var used)) throw new InvalidOperationException("Cannot read single-GPU VRAM usage.");
            return new(used <= options.Vram.ReleasedMaxUsedMiB, "nvidia-global-memory", used);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
