using System.Diagnostics;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class CliSessionProcessTests
{
    private static readonly TimeSpan Bound = CliProcessTerminationTests.TestBound;

    [UnixTheory]
    [InlineData(true)]
    [InlineData(false)]
    public Task RetirementCancelsActiveAndQueuedReadsAndSharesCompletion(bool claude) => Bounded(async () =>
    {
        await using var fixture = new SessionFixture(claude, "echo ready; exec sleep 300");
        Assert.Equal("ready", await fixture.ReadAsync());
        Task<string?> first = fixture.ReadAsync();
        Task<string?> queued = fixture.ReadAsync();
        Assert.False(first.IsCompleted);
        Assert.False(queued.IsCompleted);

        Task retirement = fixture.RetireAsync();
        Assert.Same(retirement, fixture.RetireAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await retirement;

        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.ReadAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.WriteAsync("late"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.WaitAsync());
        await CliProcessTerminationTests.AssertGoneAsync(fixture.ProcessId);
    });

    [UnixTheory]
    [InlineData(true)]
    [InlineData(false)]
    public Task RetirementCancelsActiveAndQueuedWritesWithoutFlushingAbandonedInput(bool claude) => Bounded(async () =>
    {
        await using var fixture = new SessionFixture(claude, "echo ready; exec sleep 300");
        Assert.Equal("ready", await fixture.ReadAsync());
        Task first = fixture.WriteAsync(new string('x', 16 * 1024 * 1024));
        Task queued = fixture.WriteAsync("queued");
        Assert.False(first.IsCompleted);
        Assert.False(queued.IsCompleted);
        if (!claude) Assert.Throws<InvalidOperationException>(fixture.CloseInput);

        Task retirement = fixture.RetireAsync();
        Assert.Same(retirement, fixture.RetireAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await retirement;

        await CliProcessTerminationTests.AssertGoneAsync(fixture.ProcessId);
    });

    [UnixTheory]
    [InlineData(true)]
    [InlineData(false)]
    public Task CallerReadCancellationLeavesOwnerUsableAndPreservesOutputErrorAndExit(bool claude) => Bounded(async () =>
    {
        await using var fixture = new SessionFixture(claude,
            "while IFS= read -r line; do if [ \"$line\" = exit ]; then printf diagnostic >&2; exit 7; fi; printf '%s\\n' \"$line\"; done");
        using var cancellation = new CancellationTokenSource();
        Task<string?> read = fixture.ReadAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);

        await fixture.WriteAsync("next");
        Assert.Equal("next", await fixture.ReadAsync());
        await fixture.WriteAsync("exit");
        Assert.Null(await fixture.ReadAsync());
        (int exitCode, string error) = await fixture.WaitAsync();
        Assert.Equal(7, exitCode);
        Assert.Equal("diagnostic", error);
        await fixture.RetireAsync();
        await CliProcessTerminationTests.AssertGoneAsync(fixture.ProcessId);
    });

    [UnixTheory]
    [InlineData(true)]
    [InlineData(false)]
    public Task RetirementCancelsExitWait(bool claude) => Bounded(async () =>
    {
        await using var fixture = new SessionFixture(claude, "echo ready; exec sleep 300");
        Assert.Equal("ready", await fixture.ReadAsync());
        Task<(int ExitCode, string StandardError)> wait = fixture.WaitAsync();

        Task retirement = fixture.RetireAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        await retirement;

        await CliProcessTerminationTests.AssertGoneAsync(fixture.ProcessId);
    });

    [UnixFact]
    public Task ClaudeStderrOverflowInterruptsQuietStdoutAndRemainsVisibleToEveryDisposer() => Bounded(async () =>
    {
        await using var fixture = new SessionFixture(true,
            "head -c 16777217 /dev/zero | tr '\\000' x >&2; exec sleep 300");

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
        Assert.Contains("StandardError", failure.Message, StringComparison.Ordinal);
        Task retirement = fixture.RetireAsync();
        Assert.Same(retirement, fixture.RetireAsync());
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => retirement));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RetireAsync()));
        await CliProcessTerminationTests.AssertGoneAsync(fixture.ProcessId);
    });

    [UnixFact]
    public Task AntigravityRetainsBoundedPrefixButDrainsAllStderr() => Bounded(async () =>
    {
        await using var fixture = new SessionFixture(false,
            "head -c 131072 /dev/zero | tr '\\000' x >&2; echo complete; exit 9");

        Assert.Equal("complete", await fixture.ReadAsync());
        Assert.Null(await fixture.ReadAsync());
        (int exitCode, string error) = await fixture.WaitAsync();
        Assert.Equal(9, exitCode);
        Assert.Equal(new string('x', 64 * 1024), error);
        await fixture.RetireAsync();
        await CliProcessTerminationTests.AssertGoneAsync(fixture.ProcessId);
    });

    [UnixTheory]
    [InlineData(true)]
    [InlineData(false)]
    public Task UnterminatedStdoutOverflowIsRejectedBeforeCallerTimeout(bool claude) => Bounded(async () =>
    {
        await using var fixture = new SessionFixture(claude,
            "head -c 16777217 /dev/zero | tr '\\000' x; exec sleep 300");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.ReadAsync(cancellation.Token));

        Assert.Contains("line limit", failure.Message, StringComparison.Ordinal);
        Assert.False(cancellation.IsCancellationRequested);
        await fixture.RetireAsync();
        await CliProcessTerminationTests.AssertGoneAsync(fixture.ProcessId);
    });

    [UnixFact]
    public Task ClaudeStderrOverflowInterruptsAnAlreadyBlockedWrite() => Bounded(async () =>
    {
        string directory = Directory.CreateTempSubdirectory("cli-error-gate-").FullName;
        string marker = Path.Combine(directory, "release");
        try
        {
            await using var fixture = new SessionFixture(true,
                "echo ready; until [ -f \"$1\" ]; do sleep 0.05; done; head -c 16777217 /dev/zero | tr '\\000' x >&2; exec sleep 300", marker);
            Assert.Equal("ready", await fixture.ReadAsync());
            Task write = fixture.WriteAsync(new string('x', 16 * 1024 * 1024));
            Assert.False(write.IsCompleted);

            await File.WriteAllTextAsync(marker, string.Empty);

            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => write);
            Assert.Contains("StandardError", failure.Message, StringComparison.Ordinal);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RetireAsync()));
            await CliProcessTerminationTests.AssertGoneAsync(fixture.ProcessId);
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    [UnixFact]
    public Task AntigravityCloseInputAfterCancelledWriteDoesNotFlushAbandonedBuffer() => Bounded(async () =>
    {
        await using var fixture = new SessionFixture(false, "echo ready; exec sleep 300");
        Assert.Equal("ready", await fixture.ReadAsync());
        using var cancellation = new CancellationTokenSource();
        Task write = fixture.WriteAsync(new string('x', 16 * 1024 * 1024), cancellation.Token);
        Assert.False(write.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);

        await Task.Run(fixture.CloseInput);

        await fixture.RetireAsync();
        await CliProcessTerminationTests.AssertGoneAsync(fixture.ProcessId);
    });

    [UnixFact]
    public Task AntigravityCloseInputEndsSessionAndFencesFurtherWrites() => Bounded(async () =>
    {
        await using var fixture = new SessionFixture(false, "cat; printf drained >&2");
        await fixture.WriteAsync("complete");
        Assert.Equal("complete", await fixture.ReadAsync());

        fixture.CloseInput();
        fixture.CloseInput();
        await Assert.ThrowsAsync<IOException>(() => fixture.WriteAsync("late"));
        Assert.Equal((0, "drained"), await fixture.WaitAsync());
        await fixture.RetireAsync();
    });

    [UnixFact]
    public Task AntigravityAgentRetirementDrainsItsRealCancelledReader() => Bounded(async () =>
    {
        await using var fixture = new SessionFixture(false,
            "printf '%s\\n' '{\"event\":\"init\",\"conversation_id\":\"d2fb65d7-e4c7-484a-9565-09b2cd4551b2\",\"init\":{\"tools\":[]}}'; exec sleep 300");
        ILlmAgentSession session = await AntigravityAgentSession.OpenAsync(fixture, "/bin/sh", Environment.CurrentDirectory,
            new("gemini-pro", null, null), null,
            new() { Version = "v1", Transport = LlmTransport.GoogleAntigravityCli, Endpoint = "agy" }, CancellationToken.None);

        await session.DisposeAsync();

        await fixture.RetireAsync();
        await CliProcessTerminationTests.AssertGoneAsync(fixture.ProcessId);
    });

    [UnixTheory]
    [InlineData(true)]
    [InlineData(false)]
    public Task ParentExitDoesNotReportSuccessfulRetirementWhileDescendantHoldsStderr(bool claude) => Bounded(async () =>
    {
        await using var fixture = new SessionFixture(claude, "sleep 30 >/dev/null & echo $!; exit 0");
        int child = CliProcessTerminationTests.ParseProcessId(await fixture.ReadAsync());
        try
        {
            await Assert.ThrowsAsync<CliProcessDrainException>(() => fixture.WaitAsync());
            Task retirement = fixture.RetireAsync();
            Assert.Same(retirement, fixture.RetireAsync());
            await Assert.ThrowsAsync<CliProcessDrainException>(() => retirement);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.ReadAsync());
        }
        finally
        {
            // This fixture deliberately outlives its group leader. Clean up its reported PID,
            // not a descendant snapshot or an unverified/reused process-group identifier.
            using Process descendant = Process.GetProcessById(child);
            descendant.Kill();
            await CliProcessTerminationTests.AssertGoneAsync(child);
        }
    });

    private static Task Bounded(Func<Task> scenario) => Task.Run(scenario).WaitAsync(Bound);

    private sealed class SessionFixture : IAsyncDisposable, IAntigravityCliSessionProcessFactory
    {
        private readonly ClaudeCliSessionProcess? _claude;
        private readonly AntigravityCliSessionProcess? _antigravity;

        internal SessionFixture(bool claude, string script, string argument = "")
        {
            ProcessStartInfo start = CliProcessLauncher.CreateStartInfo("/bin/sh", ["-c", script, "fixture", argument], Environment.CurrentDirectory);
            Process process = Process.Start(start) ?? throw new InvalidOperationException("The fixture did not start.");
            ProcessId = process.Id;
            if (claude) _claude = new ClaudeCliSessionProcess(process);
            else _antigravity = new AntigravityCliSessionProcess(process);
        }

        internal int ProcessId { get; }
        public IAntigravityCliSessionProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory) => _antigravity!;
        internal Task<string?> ReadAsync(CancellationToken cancellationToken = default) =>
            _claude?.ReadLineAsync(cancellationToken).AsTask() ?? _antigravity!.ReadLineAsync(cancellationToken).AsTask();
        internal Task WriteAsync(string text, CancellationToken cancellationToken = default) =>
            _claude?.WriteLineAsync(text, cancellationToken).AsTask() ?? _antigravity!.WriteAsync(text + "\n", cancellationToken).AsTask();
        internal void CloseInput() => _antigravity!.CloseInput();
        internal Task RetireAsync() => _claude?.DisposeAsync().AsTask() ?? _antigravity!.DisposeAsync().AsTask();

        internal async Task<(int ExitCode, string StandardError)> WaitAsync(CancellationToken cancellationToken = default)
        {
            if (_claude is null) return await _antigravity!.WaitForExitAsync(cancellationToken);
            ClaudeCliProcessResult result = await _claude.WaitForExitAsync(cancellationToken);
            return (result.ExitCode, result.StandardError);
        }

        public async ValueTask DisposeAsync()
        {
            // Expected retirement failures are asserted by each test. The fixture still cleans
            // its known root after a failed assertion, so failure does not leak an owned child.
            try { _ = await Record.ExceptionAsync(RetireAsync); }
            finally
            {
                try
                {
                    using Process process = Process.GetProcessById(ProcessId);
                    if (!process.HasExited) await CliProcessTermination.KillAsync(process);
                }
                catch (ArgumentException) { }
            }
        }
    }
}

internal sealed class UnixTheoryAttribute : TheoryAttribute
{
    public UnixTheoryAttribute() { if (OperatingSystem.IsWindows()) Skip = "Requires macOS or Linux."; }
}
