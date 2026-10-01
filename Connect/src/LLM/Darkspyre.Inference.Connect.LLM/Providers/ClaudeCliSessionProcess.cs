using System.ComponentModel;
using System.Diagnostics;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed class ClaudeCliSessionProcessFactory : IClaudeCliSessionProcessFactory
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
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("The Claude CLI process did not start.");
            }

            return Task.FromResult<IClaudeCliSessionProcess>(new ClaudeCliSessionProcess(process));
        }
        catch (Win32Exception exception)
        {
            process.Dispose();
            throw new ClaudeCliUnavailableException(invocation.Executable, exception);
        }
    }
}

internal sealed class ClaudeCliSessionProcess : IClaudeCliSessionProcess
{
    private readonly CliSessionProcess _process;

    public ClaudeCliSessionProcess(Process process)
    {
        _process = new(process, "Claude CLI", 16 * 1024 * 1024, truncateError: false);
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

    public ValueTask DisposeAsync() => _process.DisposeAsync();
}
