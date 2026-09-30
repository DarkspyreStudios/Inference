using System.ComponentModel;
using System.Diagnostics;
using System.Text;

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
            StartInfo = CreateStartInfo(executable, arguments, workingDirectory)
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

    private static readonly Encoding Utf8WithoutPreamble = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static ProcessStartInfo CreateStartInfo(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8WithoutPreamble,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        CliProcessTermination.Isolate(startInfo);

        string extension = Path.GetExtension(executable);
        if (OperatingSystem.IsWindows()
            && (string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase)))
        {
            startInfo.FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/q");
            startInfo.ArgumentList.Add("/v:off");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(BuildWindowsScriptCommand(executable, arguments));
            return startInfo;
        }

        startInfo.FileName = executable;
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string BuildWindowsScriptCommand(string executable, IReadOnlyList<string> arguments)
    {
        IEnumerable<string> values = new[] { executable }.Concat(arguments);
        string[] quoted = [.. values.Select(QuoteWindowsScriptArgument)];
        return $"\"{string.Join(' ', quoted)}\"";
    }

    private static string QuoteWindowsScriptArgument(string value)
    {
        if (value.IndexOfAny(['\"', '%', '!', '\r', '\n']) >= 0)
        {
            throw new ArgumentException(
                "Antigravity CLI executable paths and arguments cannot contain quotes, percent signs, "
                + "exclamation marks, or line breaks on Windows.");
        }

        return $"\"{value}\"";
    }
}

internal sealed class AntigravityCliSessionProcess : IAntigravityCliSessionProcess
{
    /// <summary>The longest standard output line accepted.</summary>
    internal const int MaximumLineCharacters = 16 * 1024 * 1024;

    /// <summary>Standard error beyond this many characters is discarded.</summary>
    private const int MaximumStandardErrorCharacters = 64 * 1024;

    private static readonly TimeSpan GracefulExit = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly Lock _standardErrorLock = new();
    private readonly StringBuilder _standardErrorText = new();
    private readonly Task _standardError;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;

    public AntigravityCliSessionProcess(Process process)
    {
        _process = process;
        _standardError = ReadBoundedAsync(process.StandardError);
    }

    public async ValueTask WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void CloseInput()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The child process has already closed its end of the input pipe.
        }
        catch (InvalidOperationException)
        {
            // The process has already exited and disposed the redirected stream.
        }
    }

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        string? line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is { Length: > MaximumLineCharacters })
        {
            throw new InvalidOperationException(
                $"Antigravity CLI output exceeded the {MaximumLineCharacters}-character line limit.");
        }
        return line;
    }

    public async Task<(int ExitCode, string StandardError)> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A descendant that left the process group can hold the pipe open after the process exits.
            await _standardError.WaitAsync(CliProcessTermination.ExitBound, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The text captured so far is returned.
        }
        return (_process.ExitCode, CapturedStandardError());
    }

    public Task KillAsync() => CliProcessTermination.KillAsync(_process);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            CloseInput();
            if (!_process.HasExited)
            {
                using var timeout = new CancellationTokenSource(GracefulExit);
                try
                {
                    await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await CliProcessTermination.KillAsync(_process).ConfigureAwait(false);
                }
            }
            try
            {
                await _standardError.WaitAsync(CliProcessTermination.ExitBound).ConfigureAwait(false);
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

    private async Task ReadBoundedAsync(StreamReader reader)
    {
        char[] buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            lock (_standardErrorLock)
            {
                int room = MaximumStandardErrorCharacters - _standardErrorText.Length;
                if (room > 0)
                {
                    _standardErrorText.Append(buffer, 0, Math.Min(room, read));
                }
            }
        }
    }

    private string CapturedStandardError()
    {
        lock (_standardErrorLock)
        {
            return _standardErrorText.ToString();
        }
    }
}

internal sealed class AntigravityCliUnavailableException(string executable, Exception innerException)
    : Exception($"The Antigravity CLI executable '{executable}' could not be started.", innerException);
