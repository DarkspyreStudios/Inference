using System.Diagnostics;
using AppleFoundationModelsPrototype;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ApplePrototypeProcessTests
{
    [UnixFact]
    public Task CompletedWorkerPreservesBothStreamsAndExitCode() => CliProcessTerminationTests.Bounded(async () =>
    {
        PrototypeProcessResult result = await PrototypeProcessRunner.RunAsync(
            Shell("printf 'output'; printf 'error' >&2; exit 3"),
            CliProcessTermination.ExitBound);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal("output", result.StandardOutput);
        Assert.Equal("error", result.StandardError);
        Assert.True(result.Seconds >= 0);
    });

    [UnixFact]
    public Task TimedOutWorkerEndsItsStoppedDescendantAndDrainsThePipes() => CliProcessTerminationTests.Bounded(async () =>
    {
        PrototypeProcessResult result = await PrototypeProcessRunner.RunAsync(
            Shell(CliProcessTerminationTests.StoppedDescendantScript),
            TimeSpan.FromSeconds(1));

        Assert.True(result.TimedOut);
        Assert.NotEqual(0, result.ExitCode);
        int descendant = CliProcessTerminationTests.ParseProcessId(result.StandardOutput.Trim());
        await CliProcessTerminationTests.AssertGoneAsync(descendant);
    });

    [UnixFact]
    public Task CancellationEndsTheWorkerBeforePropagating() => CliProcessTerminationTests.Bounded(async () =>
    {
        string directory = Directory.CreateTempSubdirectory("apple-prototype-process-").FullName;
        string pidPath = Path.Combine(directory, "pid");
        using var cancellation = new CancellationTokenSource();
        ProcessStartInfo start = Shell("printf '%s' \"$$\" > \"$1\"; exec sleep 300");
        start.ArgumentList.Add("prototype-test");
        start.ArgumentList.Add(pidPath);
        Task<PrototypeProcessResult> operation = PrototypeProcessRunner.RunAsync(
            start, TimeSpan.FromSeconds(45), cancellation.Token);
        try
        {
            int processId = 0;
            using var ready = new CancellationTokenSource(CliProcessTermination.ExitBound);
            while (processId == 0)
            {
                await Task.Delay(20, ready.Token);
                if (File.Exists(pidPath))
                {
                    _ = int.TryParse(await File.ReadAllTextAsync(pidPath, ready.Token),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out processId);
                }
            }

            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            await CliProcessTerminationTests.AssertGoneAsync(processId);
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await operation.WaitAsync(CliProcessTermination.ExitBound);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            Directory.Delete(directory, recursive: true);
        }
    });

    private static ProcessStartInfo Shell(string script)
    {
        var start = new ProcessStartInfo("/bin/sh");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        return start;
    }
}
