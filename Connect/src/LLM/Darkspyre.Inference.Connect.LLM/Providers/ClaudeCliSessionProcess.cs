using System.ComponentModel;
using System.Diagnostics;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed class ClaudeCliSessionProcessFactory(ClaudeCliExits exits) : IClaudeCliSessionProcessFactory
{
    public Task<IClaudeCliSessionProcess> StartAsync(
        ClaudeCliSessionInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        cancellationToken.ThrowIfCancellationRequested();
        var cliInvocation = new ClaudeCliInvocation(
            invocation.Executable,
            invocation.Arguments,
            StandardInput: null,
            invocation.WorkingDirectory);
        var process = new Process
        {
            StartInfo = CliProcessLauncher.CreateStartInfo(cliInvocation.Executable, cliInvocation.Arguments, cliInvocation.WorkingDirectory)
        };
        // The conversation compacts its own history; the CLI never compacts on its own.
        process.StartInfo.Environment["DISABLE_COMPACT"] = "1";
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("The Claude CLI process did not start.");
            }

            return Task.FromResult<IClaudeCliSessionProcess>(new ClaudeCliSessionProcess(process, exits));
        }
        catch (Win32Exception exception)
        {
            process.Dispose();
            throw new ClaudeCliUnavailableException(invocation.Executable, exception);
        }
    }
}

/// <summary>A Claude CLI session process. Disposal closes its input and leaves the exit to <see cref="ClaudeCliExits"/>.</summary>
internal sealed class ClaudeCliSessionProcess : IClaudeCliSessionProcess
{
    private readonly CliSessionProcess _process;
    private readonly ClaudeCliExits _exits;
    private int _disposed;

    public ClaudeCliSessionProcess(Process process, ClaudeCliExits exits)
    {
        _exits = exits;
        _process = new(process, "Claude CLI", 16 * 1024 * 1024, truncateError: false, exits.AwaitExitAsync);
    }

    public async ValueTask WriteLineAsync(
        string line,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _process.WriteAsync(line, newline: true, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "The Claude CLI closed its input stream while the agent session was active.",
                exception);
        }
    }

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default) => _process.ReadLineAsync(cancellationToken);

    public async Task<ClaudeCliProcessResult> WaitForExitAsync(
        CancellationToken cancellationToken = default)
    {
        (int exitCode, string error) = await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ClaudeCliProcessResult(exitCode, string.Empty, error);
    }

    /// <summary>
    /// Cancels active operations and returns. The process receives standard-input EOF and exits in the background;
    /// a retirement failure is logged rather than thrown.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _exits.Track(_process.DisposeAsync().AsTask());
        return ValueTask.CompletedTask;
    }
}
