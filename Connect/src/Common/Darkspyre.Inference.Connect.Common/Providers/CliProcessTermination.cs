using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// Contains wrapped CLI processes and ends them without the runtime's tree kill. On macOS and Linux a wrapped
/// process leads its own session and process group. Termination sends SIGKILL to that group, or SIGTERM where a
/// CLI must be allowed to finish on its own.
/// </summary>
/// <remarks>
/// Every wrapped process in Connect starts through <see cref="Isolate"/> and ends through this type.
/// <c>Process.Kill(entireProcessTree: true)</c> must not be used on macOS or Linux: on .NET 11 it stops every process
/// in the tree before killing it, and the runtime's child reaper can then block every process start and exit wait in
/// the host.
/// </remarks>
// Exception to the operating-system boundaries rule: Connect keeps its macOS, Linux and Windows CLI process handling
// inside this library and picks the OS commands at runtime, not behind a separate OS capability contract. This code is
// battle-tested on all three platforms. Brandon approved this on 2026-10-12.
internal static partial class CliProcessTermination
{
    /// <summary>How long a killed process may take before the runtime reports it as exited.</summary>
    internal static readonly TimeSpan ExitBound = TimeSpan.FromSeconds(5);

    private const int SigTerm = 15;
    private static readonly int SigCont = OperatingSystem.IsLinux() ? 18 : 19;
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
    /// When the group cannot be signalled for a reason other than its absence, the root is killed and the group
    /// failure remains visible. An operating-system error is not proof that the process has exited.
    /// </remarks>
    public static void Signal(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.HasExited) return;
        if (OperatingSystem.IsWindows())
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process exited between the state check and the kill.
            }
            return;
        }

        if (kill(-process.Id, SigKill) == 0) return;
        int nativeError = Marshal.GetLastPInvokeError();
        if (nativeError == NoSuchProcess) return;
        var groupFailure = new Win32Exception(nativeError);

        // Preserve a failed group signal even when killing the root succeeds.
        try { process.Kill(); }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // The process exited between the state check and the kill.
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        { throw new AggregateException(groupFailure, error); }
        throw groupFailure;
    }

    /// <summary>Sends SIGTERM, then SIGCONT, to the process group (Unix). Does not wait.</summary>
    /// <remarks>
    /// SIGCONT lets a stopped member act on its pending SIGTERM. An exited process is not signalled. When the group
    /// cannot be signalled for a reason other than its absence, the root is signalled and the group failure remains
    /// visible. Windows has no SIGTERM, so this does nothing there.
    /// </remarks>
    public static void Terminate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        if (process.HasExited) return;

        var failures = new List<Exception>();
        int target = -process.Id;
        if (kill(target, SigTerm) != 0)
        {
            int nativeError = Marshal.GetLastPInvokeError();
            if (nativeError == NoSuchProcess) return;
            failures.Add(new Win32Exception(nativeError));
            target = process.Id;
            if (kill(target, SigTerm) != 0)
            {
                nativeError = Marshal.GetLastPInvokeError();
                if (nativeError != NoSuchProcess) failures.Add(new Win32Exception(nativeError));
            }
        }
        if (kill(target, SigCont) != 0)
        {
            int nativeError = Marshal.GetLastPInvokeError();
            if (nativeError != NoSuchProcess) failures.Add(new Win32Exception(nativeError));
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    /// <summary>Signals the process, then waits at most <see cref="ExitBound"/> for its exit.</summary>
    /// <exception cref="CliProcessTerminationException">The process was not reported as exited in time.</exception>
    [SuppressMessage("Design", "CA1031", Justification = "A failed signal does not skip the bounded exit wait; both actual failures remain visible.")]
    public static async Task KillAsync(Process process)
    {
        Exception? signalFailure = null;
        try { Signal(process); }
        catch (Exception error) { signalFailure = error; }
        try { await WaitForExitAsync(process).ConfigureAwait(false); }
        catch (Exception error) when (signalFailure is not null)
        { throw new AggregateException(signalFailure, error); }
        if (signalFailure is not null) ExceptionDispatchInfo.Throw(signalFailure);
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

/// <summary>A wrapped process whose exit is not reported within the termination bound.</summary>
internal sealed class CliProcessTerminationException(int processId)
    : Exception($"The CLI process {processId} did not exit within {CliProcessTermination.ExitBound.TotalSeconds:0} seconds after termination was requested.")
{
    public int ProcessId { get; } = processId;
}
