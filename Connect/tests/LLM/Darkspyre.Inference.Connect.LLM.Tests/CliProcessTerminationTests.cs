using System.Diagnostics;
using System.Globalization;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>
/// Real <c>/bin/sh</c> children exercise the process-group kill. Every scenario runs under <see cref="TestBound"/>,
/// so a teardown that deadlocks the runtime's child reaper fails the test instead of hanging the run.
/// </summary>
public sealed class CliProcessTerminationTests
{
    /// <summary>The longest any teardown scenario may take.</summary>
    internal static readonly TimeSpan TestBound = TimeSpan.FromSeconds(20);

    /// <summary>Prints the PID of a stopped background child, then stalls in the foreground.</summary>
    internal const string StoppedDescendantScript = "sleep 300 & echo $!; kill -STOP $!; sleep 300";

    [Fact]
    public void IsolateStartsDetachedOnlyOnUnix()
    {
        var startInfo = new ProcessStartInfo();

        CliProcessTermination.Isolate(startInfo);

        Assert.Equal(!OperatingSystem.IsWindows(), startInfo.StartDetached);
    }

    [UnixFact]
    public Task KillAsyncEndsStoppedDescendantWithoutHanging() => Bounded(KillStoppedDescendantAsync);

    [UnixFact]
    public Task KillAsyncEndsAStoppedDirectChild() => Bounded(async () =>
    {
        // While a direct child is stopped, an affected runtime cannot start another process in the host. A
        // background watcher in the child's group therefore runs ps and reports the stop through standard output.
        using Process process = StartShell(
            "(until ps -o stat= -p $$ | grep -q T; do sleep 0.05; done; echo stopped) & "
            + "echo ready; kill -STOP $$; sleep 300");
        Assert.Equal("ready", await process.StandardOutput.ReadLineAsync());
        Assert.Equal("stopped", await process.StandardOutput.ReadLineAsync());

        await CliProcessTermination.KillAsync(process);

        Assert.True(process.HasExited);
    });

    [UnixFact]
    public Task KillAsyncOnAnExitedProcessIsANoOp() => Bounded(async () =>
    {
        using Process process = StartShell("exit 0");
        await process.WaitForExitAsync();

        await CliProcessTermination.KillAsync(process);

        Assert.True(process.HasExited);
    });

    [UnixFact]
    public Task SignalDoesNotReachTheHostGroup() => Bounded(async () =>
    {
        await KillStoppedDescendantAsync();

        // The test host survived the group kill, and the runtime can still start and reap a process.
        using Process probe = Process.Start("/bin/sh", "-c true");
        await probe.WaitForExitAsync().WaitAsync(CliProcessTermination.ExitBound);
        Assert.Equal(0, probe.ExitCode);
    });

    /// <summary>Runs a scenario off the test thread, because an affected runtime blocks synchronously.</summary>
    internal static Task Bounded(Func<Task> scenario) => Task.Run(scenario).WaitAsync(TestBound);

    /// <summary>Waits until a PID has no process table entry. An orphan is reaped by PID 1 or launchd.</summary>
    internal static async Task AssertGoneAsync(int processId)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
            }
            catch (ArgumentException)
            {
                return;
            }

            Assert.True(clock.Elapsed < CliProcessTermination.ExitBound, $"Process {processId} is still running.");
            await Task.Delay(50);
        }
    }

    internal static int ParseProcessId(string? line) =>
        int.Parse(line ?? throw new InvalidOperationException("The script printed no PID."), CultureInfo.InvariantCulture);

    private static async Task KillStoppedDescendantAsync()
    {
        using Process process = StartShell(StoppedDescendantScript);
        int grandchild = ParseProcessId(await process.StandardOutput.ReadLineAsync());

        await CliProcessTermination.KillAsync(process);

        Assert.True(process.HasExited);
        await AssertGoneAsync(grandchild);
    }

    private static Process StartShell(string script)
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        CliProcessTermination.Isolate(startInfo);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The shell did not start.");
    }
}

/// <summary>An empty event stream whose disposal reports a process that did not exit after it was killed.</summary>
internal sealed class ExitTimeoutEvents<T> : IAsyncEnumerable<T>, IAsyncEnumerator<T>
{
    internal const int ProcessId = 4242;

    public T Current => throw new InvalidOperationException("The stream has no events.");

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

    public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);

    public ValueTask DisposeAsync() => ValueTask.FromException(new CliProcessTerminationException(ProcessId));
}
