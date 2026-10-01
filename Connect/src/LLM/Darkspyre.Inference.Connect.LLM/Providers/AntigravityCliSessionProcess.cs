using System.ComponentModel;
using System.Diagnostics;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Starts the one owner of an Antigravity CLI process with an open standard input pipe.</summary>
internal interface IAntigravityCliSessionProcessFactory
{
    /// <exception cref="AntigravityCliUnavailableException">The executable could not be started.</exception>
    IAntigravityCliSessionProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory);
}

/// <summary>
/// A running Antigravity CLI process. Writes are serialized; one caller reads standard output. Standard
/// error is captured up to a fixed bound for diagnostics.
/// </summary>
internal interface IAntigravityCliSessionProcess : IAsyncDisposable
{
    /// <exception cref="IOException">The process closed its input pipe.</exception>
    ValueTask WriteAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Closes standard input. The CLI ends its session after the current turn.</summary>
    void CloseInput();

    /// <summary>The next standard output line, or null at end of output.</summary>
    ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default);

    /// <summary>Waits for exit and returns the exit code and captured standard error.</summary>
    Task<(int ExitCode, string StandardError)> WaitForExitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Terminates the process group and waits at most <see cref="CliProcessTermination.ExitBound"/> for the exit.
    /// </summary>
    /// <exception cref="CliProcessTerminationException">The process was not reported as exited.</exception>
    Task KillAsync();
}

internal sealed class AntigravityCliSessionProcessFactory : IAntigravityCliSessionProcessFactory
{
    public IAntigravityCliSessionProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var process = new Process
        {
            StartInfo = CliProcessLauncher.CreateStartInfo(executable, arguments, workingDirectory)
        };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("The Antigravity CLI process did not start.");
            }
            return new AntigravityCliSessionProcess(process);
        }
        catch (Win32Exception exception)
        {
            process.Dispose();
            throw new AntigravityCliUnavailableException(executable, exception);
        }
    }

}

internal sealed class AntigravityCliSessionProcess : IAntigravityCliSessionProcess
{
    /// <summary>The longest standard output line accepted.</summary>
    internal const int MaximumLineCharacters = CliSessionProcess.MaximumLineCharacters;

    /// <summary>Standard error beyond this many characters is discarded.</summary>
    private const int MaximumStandardErrorCharacters = 64 * 1024;

    private readonly CliSessionProcess _process;

    public AntigravityCliSessionProcess(Process process)
    {
        _process = new(process, "Antigravity CLI", MaximumStandardErrorCharacters, truncateError: true);
    }

    public ValueTask WriteAsync(string text, CancellationToken cancellationToken = default) => _process.WriteAsync(text, newline: false, cancellationToken);

    public void CloseInput() => _process.CloseInput();

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default) => _process.ReadLineAsync(cancellationToken);

    public Task<(int ExitCode, string StandardError)> WaitForExitAsync(CancellationToken cancellationToken = default) => _process.WaitForExitAsync(cancellationToken);

    public Task KillAsync() => _process.KillAsync();

    public ValueTask DisposeAsync() => _process.DisposeAsync();
}

internal sealed class AntigravityCliUnavailableException(string executable, Exception innerException)
    : Exception($"The Antigravity CLI executable '{executable}' could not be started.", innerException);
