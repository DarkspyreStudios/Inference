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
    private readonly Process _process;
    private readonly Task<string> _standardError;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public ClaudeCliSessionProcess(Process process)
    {
        _process = process;
        _standardError = process.StandardError.ReadToEndAsync();
    }

    public async ValueTask WriteLineAsync(
        string line,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "The Claude CLI closed its input stream while the agent session was active.",
                exception);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string? line = await _process.StandardOutput.ReadLineAsync(cancellationToken);
        if (line is not null)
        {
            ClaudeCliProcessRunner.EnsureCaptureLimit(
                line.Length,
                nameof(_process.StandardOutput));
        }

        return line;
    }

    public async Task<ClaudeCliProcessResult> WaitForExitAsync(
        CancellationToken cancellationToken = default)
    {
        await _process.WaitForExitAsync(cancellationToken);
        string error = await _standardError.WaitAsync(cancellationToken);
        ClaudeCliProcessRunner.EnsureCaptureLimit(error.Length, nameof(_process.StandardError));
        return new ClaudeCliProcessResult(_process.ExitCode, string.Empty, error);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _process.StandardInput.Close();
            if (!_process.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    await CliProcessTermination.KillAsync(_process);
                }
            }

            try
            {
                // A descendant that left the process group can hold the pipe open after the kill.
                await _standardError.WaitAsync(CliProcessTermination.ExitBound);
            }
            catch (TimeoutException)
            {
                // Disposal only releases resources. Disposing the process closes the pipe.
            }
        }
        finally
        {
            _writeLock.Dispose();
            _process.Dispose();
        }
    }
}
