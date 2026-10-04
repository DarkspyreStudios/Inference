using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Ends Claude CLI processes without an early SIGKILL and reaps them in the background.</summary>
/// <remarks>
/// The Claude CLI refreshes its OAuth tokens under a lock and saves the rotated pair to the keychain. A CLI killed
/// between the token endpoint's answer and that save leaves a spent refresh token behind, and the next refresh signs
/// the user out. Every Claude CLI therefore ends by standard-input EOF, or by SIGTERM when its input is already
/// closed, and is left to exit on its own. Its process group receives SIGKILL only after <see cref="Limit"/>.
/// A caller that cancels stops waiting at once, and the exit continues here. Disposal waits for every pending exit,
/// so host shutdown runs the same path with the same limit.
/// </remarks>
internal sealed partial class ClaudeCliExits(ILogger<ClaudeCliExits> logger) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly HashSet<Task> _pending = [];

    /// <summary>How long a Claude CLI may take to exit on its own before its process group receives SIGKILL.</summary>
    internal TimeSpan Limit { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Runs an exit in the background. Its failure is logged.</summary>
    internal void Track(Task exit)
    {
        ArgumentNullException.ThrowIfNull(exit);
        Task observed = ObserveAsync(exit);
        lock (_gate)
        {
            _pending.RemoveWhere(static task => task.IsCompleted);
            _pending.Add(observed);
        }
    }

    /// <summary>Waits at most <see cref="Limit"/> for the process to exit on its own, then kills its process group.</summary>
    /// <exception cref="CliProcessTerminationException">The killed process was not reported as exited.</exception>
    internal async Task AwaitExitAsync(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        using (var limit = new CancellationTokenSource(Limit))
        {
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested)
            {
            }
        }

        LogForcedKill(logger, process.Id, Limit.TotalSeconds);
        await CliProcessTermination.KillAsync(process).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_gate)
            {
                _pending.RemoveWhere(static task => task.IsCompleted);
                pending = [.. _pending];
            }
            if (pending.Length == 0) return;
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "A background exit has no caller left to receive its failure, so the log records it.")]
    private async Task ObserveAsync(Task exit)
    {
        try { await exit.ConfigureAwait(false); }
        catch (Exception error) { LogExitFailure(logger, error); }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Claude CLI process {ProcessId} did not exit within {LimitSeconds} seconds of its input closing or SIGTERM; its process group received SIGKILL.")]
    private static partial void LogForcedKill(ILogger logger, int processId, double limitSeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "A Claude CLI process did not end cleanly in the background.")]
    private static partial void LogExitFailure(ILogger logger, Exception error);
}
