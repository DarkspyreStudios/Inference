using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// Contains wrapped CLI processes and ends them without the runtime's tree kill. On macOS and Linux a wrapped
/// process leads its own session and process group, and termination sends SIGKILL to that group.
/// </summary>
/// <remarks>
/// Every wrapped process in Connect starts through <see cref="Isolate"/> and ends through this type.
/// <c>Process.Kill(entireProcessTree: true)</c> must not be used on macOS or Linux: on .NET 11 it stops every process
/// in the tree before killing it, and the runtime's child reaper can then block every process start and exit wait in
/// the host.
/// </remarks>
internal static partial class CliProcessTermination
{
    /// <summary>How long a killed process may take before the runtime reports it as exited.</summary>
    internal static readonly TimeSpan ExitBound = TimeSpan.FromSeconds(5);

    private const int SigKill = 9;
    private const int NoSuchProcess = 3;

    /// <summary>On macOS and Linux, starts the process as the leader of a new session and process group.</summary>
    /// <remarks>
    /// The process has no controlling terminal, and its process group ID equals its process ID. Windows start
    /// information is unchanged.
    /// </remarks>
    public static void Isolate(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!OperatingSystem.IsWindows())
        {
            startInfo.StartDetached = true;
        }
    }

    /// <summary>Sends SIGKILL to the process group (Unix) or kills the process tree (Windows). Does not wait.</summary>
    /// <remarks>
    /// An exited process is not signalled, because its reaped process ID may later name another process group.
    /// When the group cannot be signalled for a reason other than its absence, only the root process is killed.
    /// </remarks>
    public static void Signal(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                // Windows kills trees through job objects and has no SIGCHLD reaper.
                process.Kill(entireProcessTree: true);
                return;
            }

            if (kill(-process.Id, SigKill) == 0 || Marshal.GetLastPInvokeError() == NoSuchProcess)
            {
                return;
            }

            // A member that changed user returns EPERM. The root still receives SIGKILL alone.
            process.Kill();
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and the kill.
        }
        catch (Win32Exception)
        {
            // The operating system has already torn down the process.
        }
    }

    /// <summary>Signals the process, then waits at most <see cref="ExitBound"/> for its exit.</summary>
    /// <exception cref="CliProcessTerminationException">The process was not reported as exited in time.</exception>
    public static Task KillAsync(Process process)
    {
        Signal(process);
        return WaitForExitAsync(process);
    }

    /// <summary>Waits at most <see cref="ExitBound"/> for a process that was already signalled.</summary>
    /// <exception cref="CliProcessTerminationException">The process was not reported as exited in time.</exception>
    public static async Task WaitForExitAsync(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        using var bound = new CancellationTokenSource(ExitBound);
        try
        {
            await process.WaitForExitAsync(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (bound.IsCancellationRequested)
        {
            throw new CliProcessTerminationException(process.Id);
        }
    }

    [LibraryImport("libc", SetLastError = true)]
    [UnsupportedOSPlatform("windows")]
    private static partial int kill(int pid, int signal);
}

/// <summary>A wrapped process that was killed but not reported as exited within the bound.</summary>
internal sealed class CliProcessTerminationException(int processId)
    : Exception($"The CLI process {processId} did not exit within {CliProcessTermination.ExitBound.TotalSeconds:0} seconds after it was killed.")
{
    public int ProcessId { get; } = processId;
}
