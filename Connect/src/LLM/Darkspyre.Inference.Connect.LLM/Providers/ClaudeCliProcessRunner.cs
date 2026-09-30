using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed class ClaudeCliProcessRunner : IClaudeCliRunner
{
    private const int MaximumCapturedCharacters = 16 * 1024 * 1024;
    private static readonly Encoding Utf8WithoutPreamble = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public async Task<ClaudeCliProcessResult> RunAsync(
        ClaudeCliInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ClaudeCliProcessResult? completed = null;
        await foreach (ClaudeCliProcessEvent processEvent in StreamAsync(invocation, cancellationToken))
        {
            if (processEvent is ClaudeCliProcessCompleted completion)
            {
                completed = completion.Result;
            }
        }
        return completed ?? throw new InvalidOperationException(
            "The Claude CLI process ended without a completion event.");
    }

    public async IAsyncEnumerable<ClaudeCliProcessEvent> StreamAsync(
        ClaudeCliInvocation invocation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        using Process process = StartProcess(invocation);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        string? standardInputError = null;
        try
        {
            if (invocation.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(
                    invocation.StandardInput.AsMemory(),
                    cancellationToken);
            }
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            standardInputError =
                "The Claude CLI closed standard input before the request was completely sent.";
            TryCloseStandardInput(process);
        }

        var output = new StringBuilder();
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                output.AppendLine(line);
                EnsureCaptureLimit(output.Length, nameof(process.StandardOutput));
                yield return new ClaudeCliOutputLine(line);
            }
            await process.WaitForExitAsync(cancellationToken);
            string error = await standardError;
            EnsureCaptureLimit(error.Length, nameof(process.StandardError));
            yield return new ClaudeCliProcessCompleted(new ClaudeCliProcessResult(
                process.ExitCode,
                output.ToString(),
                error,
                standardInputError));
        }
        finally
        {
            if (!process.HasExited)
            {
                await CliProcessTermination.KillAsync(process);
            }
        }
    }

    private static Process StartProcess(ClaudeCliInvocation invocation)
    {
        var process = new Process
        {
            StartInfo = CreateStartInfo(invocation)
        };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("The Claude CLI process did not start.");
            }
            return process;
        }
        catch (Win32Exception exception)
        {
            process.Dispose();
            throw new ClaudeCliUnavailableException(invocation.Executable, exception);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(ClaudeCliInvocation invocation)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = invocation.WorkingDirectory,
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

        string extension = Path.GetExtension(invocation.Executable);
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
            startInfo.ArgumentList.Add(BuildWindowsScriptCommand(invocation));
            return startInfo;
        }

        startInfo.FileName = invocation.Executable;
        foreach (string argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string BuildWindowsScriptCommand(ClaudeCliInvocation invocation)
    {
        IEnumerable<string> values = new[] { invocation.Executable }.Concat(invocation.Arguments);
        string[] quoted = [.. values.Select(QuoteWindowsScriptArgument)];
        return $"\"{string.Join(' ', quoted)}\"";
    }

    private static string QuoteWindowsScriptArgument(string value)
    {
        if (value.IndexOfAny(['\"', '%', '!', '\r', '\n']) >= 0)
        {
            throw new ArgumentException(
                "Claude CLI executable paths and arguments cannot contain quotes, percent signs, "
                + "exclamation marks, or line breaks on Windows.");
        }

        return $"\"{value}\"";
    }

    internal static void EnsureCaptureLimit(int characterCount, string streamName)
    {
        if (characterCount > MaximumCapturedCharacters)
        {
            throw new InvalidOperationException(
                $"Claude CLI {streamName} exceeded the {MaximumCapturedCharacters}-character safety limit.");
        }
    }

    private static void TryCloseStandardInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
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
}

internal sealed class ClaudeCliUnavailableException(string executable, Exception innerException)
    : Exception($"The Claude CLI executable '{executable}' could not be started.", innerException);
