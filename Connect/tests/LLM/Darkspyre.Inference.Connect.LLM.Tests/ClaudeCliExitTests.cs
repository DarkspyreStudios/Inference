using System.Diagnostics;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.Logging;

namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>
/// Stand-in <c>/bin/sh</c> scripts play a Claude CLI that is finishing a token refresh. A cancelled caller must not
/// wait for it, and the process must not receive SIGKILL before <see cref="ClaudeCliExits.Limit"/>.
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

    private static Task Bounded(Func<Task> scenario) => Task.Run(scenario).WaitAsync(TimeSpan.FromSeconds(20));

    private sealed class Scratch : IDisposable
    {
        internal string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("claude-exit-").FullName;
        internal string Log => Path.Combine(Directory, "log");
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed class RecordingLogger : ILogger<ClaudeCliExits>
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
        }
    }
}
