using System.Diagnostics;
using GpuArbiter.Gpu;
using GpuArbiter.Logging;

namespace GpuArbiter.Supervision;

public sealed class LlamaServerSupervisor(ArbiterOptions options, GpuCoordinator coordinator,
    IHttpClientFactory clients, FileLoggerProvider files, TimeProvider clock, ILogger<LlamaServerSupervisor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The GPU arbiter host requires Windows x64.");
        var backoff = 1;
        while (!stoppingToken.IsCancellationRequested)
        {
            using var job = new JobObject();
            using var process = new Process
            {
                StartInfo = new(options.Llama.Exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(options.Llama.Exe)!
                }
            };
            foreach (var argument in new[] { "--models-preset", options.Llama.Preset, "--no-models-autoload", "--models-max", "1",
                "--host", "127.0.0.1", "--port", options.Llama.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--no-webui" })
                process.StartInfo.ArgumentList.Add(argument);
            var started = false;
            Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
            try
            {
                process.Start();
                started = true;
                // Autoload is disabled. No requests reach this router until it belongs to the job.
                job.Assign(process);
                stdout = PumpAsync(process.StandardOutput, stoppingToken);
                stderr = PumpAsync(process.StandardError, stoppingToken);
                using var client = clients.CreateClient("health");
                var start = clock.GetUtcNow();
                while (!process.HasExited)
                {
                    try
                    {
                        using var response = await client.GetAsync(options.Llama.Url + "/health", stoppingToken).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode) { coordinator.RouterReady(); break; }
                    }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
                    await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken).ConfigureAwait(false);
                    if ((clock.GetUtcNow() - start).TotalSeconds > options.Llama.LoadTimeoutSeconds)
                        throw new InvalidOperationException("Router health timeout.");
                }
                await process.WaitForExitAsync(stoppingToken).ConfigureAwait(false);
                if ((clock.GetUtcNow() - start).TotalSeconds >= 60) backoff = 1;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception) { logger.LogError("llama-server supervision failed; restarting after backoff"); }
            finally
            {
                if (started && !process.HasExited) process.Kill(entireProcessTree: true);
                if (started) await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                coordinator.RouterExited(started ? process.ExitCode : null);
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            }
            if (stoppingToken.IsCancellationRequested) break;
            await Task.Delay(TimeSpan.FromSeconds(backoff), clock, stoppingToken).ConfigureAwait(false);
            backoff = Math.Min(30, backoff * 2);
        }
    }
    private async Task PumpAsync(StreamReader stream, CancellationToken token)
    {
        while (await stream.ReadLineAsync(token).ConfigureAwait(false) is { } line) files.Write("llama-server", line);
    }
}
