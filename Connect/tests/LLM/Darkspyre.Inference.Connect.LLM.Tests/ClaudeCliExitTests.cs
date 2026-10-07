using System.Diagnostics;
using System.Globalization;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.Logging;

namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>
/// Stand-in <c>/bin/sh</c> scripts play a Claude CLI that is finishing a token refresh. A cancelled caller must not
/// wait for it, and the process must not receive SIGKILL before <see cref="ClaudeCliExits.Limit"/>.
/// Refused logging must not prevent termination or erase the actual exit failure.
/// </summary>
public sealed class ClaudeCliExitTests
{
    private static readonly TimeSpan Prompt = TimeSpan.FromMilliseconds(500);

    [UnixFact]
    public Task CancelledRunReturnsAtOnceAndTheCliFinishesAfterSigterm() => Bounded(async () =>
    {
        using var scratch = new Scratch();
        var logger = new RecordingLogger();
        var exits = new ClaudeCliExits(logger) { Limit = TimeSpan.FromSeconds(10) };
        var invocation = new ClaudeCliInvocation("/bin/sh",
            ["-c", "trap 'echo term >> \"$1\"; sleep 1; echo finished >> \"$1\"; exit 0' TERM; echo ready; while :; do sleep 0.1; done",
                "claude-test", scratch.Log], StandardInput: null, scratch.Directory);
        using var cancellation = new CancellationTokenSource();

        var clock = new Stopwatch();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (ClaudeCliProcessEvent processEvent in new ClaudeCliProcessRunner(exits).StreamAsync(invocation, cancellation.Token))
            {
                Assert.Equal("ready", Assert.IsType<ClaudeCliOutputLine>(processEvent).Line);
                clock.Start();
                await cancellation.CancelAsync();
            }
        });
        Assert.True(clock.Elapsed < Prompt, $"The cancelled run took {clock.Elapsed}.");

        await exits.DisposeAsync();
        Assert.Equal(["term", "finished"], await File.ReadAllLinesAsync(scratch.Log));
        Assert.Empty(logger.Entries);
    });

    [UnixFact]
    public Task DisposedSessionReturnsAtOnceAndTheCliFinishesAfterEof() => Bounded(async () =>
    {
        using var scratch = new Scratch();
        var logger = new RecordingLogger();
        var exits = new ClaudeCliExits(logger) { Limit = TimeSpan.FromSeconds(10) };
        var factory = new ClaudeCliSessionProcessFactory(exits);
        IClaudeCliSessionProcess process = await factory.StartAsync(new("/bin/sh",
            ["-c", "trap '' TERM; echo ready; cat >/dev/null; echo eof >> \"$1\"; sleep 1; echo finished >> \"$1\"", "claude-test", scratch.Log],
            scratch.Directory));
        Assert.Equal("ready", await process.ReadLineAsync());

        var clock = Stopwatch.StartNew();
        await process.DisposeAsync();
        Assert.True(clock.Elapsed < Prompt, $"Disposal took {clock.Elapsed}.");

        await exits.DisposeAsync();
        Assert.Equal(["eof", "finished"], await File.ReadAllLinesAsync(scratch.Log));
        Assert.Empty(logger.Entries);
    });

    [UnixFact]
    public Task ForcedExitEndsTheOwnedChildThatIgnoresSigterm() => Bounded(async () =>
    {
        using var scratch = new Scratch();
        var logger = new RecordingLogger();
        // A short fixture grace period reaches the real SIGKILL path without a 30-second test.
        var exits = new ClaudeCliExits(logger) { Limit = TimeSpan.FromMilliseconds(250) };
        var invocation = new ClaudeCliInvocation("/bin/sh",
            ["-c", "trap '' TERM; /bin/sh -c 'trap \"\" TERM; echo \"$$\"; while :; do sleep 1; done' & wait"],
            StandardInput: null, scratch.Directory);
        using var cancellation = new CancellationTokenSource();
        Process? child = null;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (ClaudeCliProcessEvent processEvent in new ClaudeCliProcessRunner(exits).StreamAsync(invocation, cancellation.Token))
                {
                    string line = Assert.IsType<ClaudeCliOutputLine>(processEvent).Line;
                    child = Process.GetProcessById(int.Parse(line, CultureInfo.InvariantCulture));
                    Assert.False(child.HasExited);
                    await cancellation.CancelAsync();
                }
            });
            await exits.DisposeAsync();
            Assert.NotNull(child);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(child.HasExited);
            Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
        }
        finally
        {
            // The handle identifies only this fixture's child, including on assertion failure.
            if (child is not null)
            {
                if (!child.HasExited) child.Kill();
                child.Dispose();
            }
            await exits.DisposeAsync();
        }
    });

    [UnixFact]
    public Task RefusedForcedKillWarningStillEndsTheActualProcess() => Bounded(async () =>
    {
        using var scratch = new Scratch();
        var warningFailure = new InvalidOperationException("The fixture refuses the forced-kill warning.");
        var logger = new RecordingLogger(warningFailure: warningFailure);
        var exits = new ClaudeCliExits(logger) { Limit = TimeSpan.FromMilliseconds(250) };
        var invocation = new ClaudeCliInvocation("/bin/sh",
            ["-c", "trap '' TERM; echo \"$$\"; while :; do sleep 1; done"],
            StandardInput: null, scratch.Directory);
        using var cancellation = new CancellationTokenSource();
        Process? root = null;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (ClaudeCliProcessEvent processEvent in new ClaudeCliProcessRunner(exits).StreamAsync(invocation, cancellation.Token))
                {
                    string line = Assert.IsType<ClaudeCliOutputLine>(processEvent).Line;
                    root = Process.GetProcessById(int.Parse(line, CultureInfo.InvariantCulture));
                    Assert.False(root.HasExited);
                    await cancellation.CancelAsync();
                }
            });
            for (int pass = 0; pass < 2; pass++)
                Assert.Same(warningFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => exits.DisposeAsync().AsTask()));
            Assert.NotNull(root);
            await root.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(root.HasExited);
            Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        }
        finally
        {
            if (root is not null)
            {
                if (!root.HasExited) root.Kill();
                root.Dispose();
            }
        }
    });

    [Fact]
    public async Task BackgroundExitAndRefusedErrorLogRemainVisibleOnRepeatedDisposal()
    {
        var exitFailure = new IOException("The fixture exit fails.");
        var loggingFailure = new InvalidOperationException("The fixture refuses the exit-error log.");
        var logger = new RecordingLogger(errorFailure: loggingFailure);
        var exits = new ClaudeCliExits(logger);
        exits.Track(Task.FromException(exitFailure));

        for (int pass = 0; pass < 2; pass++)
        {
            AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => exits.DisposeAsync().AsTask());
            Assert.Equal(2, error.InnerExceptions.Count);
            Assert.Same(exitFailure, error.InnerExceptions[0]);
            Assert.Same(loggingFailure, error.InnerExceptions[1]);
        }
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [UnixFact]
    public Task RealStderrCaptureFailureRemainsVisibleDuringRetirement() => Bounded(async () =>
    {
        using var scratch = new Scratch();
        var logger = new RecordingLogger();
        var exits = new ClaudeCliExits(logger) { Limit = TimeSpan.FromMilliseconds(250) };
        var invocation = new ClaudeCliInvocation("/bin/sh",
            ["-c", "echo ready; /usr/bin/head -c 16777217 /dev/zero | /usr/bin/tr '\\000' x >&2; sleep 10"],
            StandardInput: null, scratch.Directory);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (ClaudeCliProcessEvent processEvent in new ClaudeCliProcessRunner(exits).StreamAsync(invocation))
                Assert.Equal("ready", Assert.IsType<ClaudeCliOutputLine>(processEvent).Line);
        });
        Assert.Equal("Claude CLI StandardError exceeded the 16777216-character safety limit.", failure.Message);
        for (int pass = 0; pass < 2; pass++)
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => exits.DisposeAsync().AsTask()));
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
    });

    private static Task Bounded(Func<Task> scenario) => Task.Run(scenario).WaitAsync(TimeSpan.FromSeconds(20));

    private sealed class Scratch : IDisposable
    {
        internal string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("claude-exit-").FullName;
        internal string Log => Path.Combine(Directory, "log");
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed class RecordingLogger(Exception? warningFailure = null, Exception? errorFailure = null) : ILogger<ClaudeCliExits>
    {
        private readonly Lock _gate = new();
        private readonly List<(LogLevel, string)> _entries = [];

        internal IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_gate) return [.. _entries]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _entries.Add((logLevel, formatter(state, exception)));
            if (logLevel == LogLevel.Warning && warningFailure is not null) throw warningFailure;
            if (logLevel == LogLevel.Error && errorFailure is not null) throw errorFailure;
        }
    }
}
