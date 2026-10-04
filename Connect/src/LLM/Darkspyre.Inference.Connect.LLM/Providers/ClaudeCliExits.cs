using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
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
    private readonly HashSet<Task<Exception?>> _pending = [];

    /// <summary>How long a Claude CLI may take to exit on its own before its process group receives SIGKILL.</summary>
    internal TimeSpan Limit { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Runs an exit in the background. Its failure is logged and retained for disposal.</summary>
    internal void Track(Task exit)
    {
        ArgumentNullException.ThrowIfNull(exit);
        Task<Exception?> observed = ObserveAsync(exit);
        lock (_gate)
        {
            _pending.RemoveWhere(static task => task.IsCompletedSuccessfully && task.Result is null);
            _pending.Add(observed);
        }
    }

    /// <summary>Waits at most <see cref="Limit"/> for the process to exit on its own, then kills its process group.</summary>
    /// <exception cref="CliProcessTerminationException">The killed process was not reported as exited.</exception>
    [SuppressMessage("Design", "CA1031", Justification = "A refused warning does not skip the actual forced termination, and both failures remain visible.")]
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

        Exception? loggingFailure = null;
        try { LogForcedKill(logger, process.Id, Limit.TotalSeconds); }
        catch (Exception error) { loggingFailure = error; }
        try { await CliProcessTermination.KillAsync(process).ConfigureAwait(false); }
        catch (Exception error) when (loggingFailure is not null)
        { throw new AggregateException(loggingFailure, error); }
        if (loggingFailure is not null) ExceptionDispatchInfo.Throw(loggingFailure);
    }

    public async ValueTask DisposeAsync()
    {
        var observed = new HashSet<Task<Exception?>>();
        var failures = new List<Exception>();
        while (true)
        {
            Task<Exception?>[] pending;
            lock (_gate)
            {
                _pending.RemoveWhere(static task => task.IsCompletedSuccessfully && task.Result is null);
                pending = [.. _pending.Where(task => !observed.Contains(task))];
            }
            if (pending.Length == 0) break;
            Exception?[] outcomes = await Task.WhenAll(pending).ConfigureAwait(false);
            failures.AddRange(outcomes.OfType<Exception>());
            observed.UnionWith(pending);
        }
        Exception[] unique = new AggregateException(failures).Flatten().InnerExceptions
            .Distinct<Exception>(ReferenceEqualityComparer.Instance).ToArray();
        if (unique.Length == 1) ExceptionDispatchInfo.Throw(unique[0]);
        if (unique.Length > 1) throw new AggregateException(unique);
    }

    [SuppressMessage("Design", "CA1031", Justification = "Background exits log and retain their actual failure; a refused log does not hide the original failure.")]
    private async Task<Exception?> ObserveAsync(Task exit)
    {
        try { await exit.ConfigureAwait(false); return null; }
        catch (Exception error)
        {
            try { LogExitFailure(logger, error); }
            catch (Exception logging) { return new AggregateException(error, logging); }
            return error;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Claude CLI process {ProcessId} did not exit within {LimitSeconds} seconds of its input closing or SIGTERM; Connect is attempting SIGKILL on its process group.")]
    private static partial void LogForcedKill(ILogger logger, int processId, double limitSeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "A Claude CLI process did not end cleanly in the background.")]
    private static partial void LogExitFailure(ILogger logger, Exception error);
}
