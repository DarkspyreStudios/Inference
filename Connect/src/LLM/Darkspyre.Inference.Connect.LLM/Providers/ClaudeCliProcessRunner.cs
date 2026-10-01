using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed class ClaudeCliProcessRunner : IClaudeCliRunner
{
    private const int MaximumCapturedCharacters = 16 * 1024 * 1024;

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
        using var reads = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<string> standardError = ReadStandardErrorAsync(process.StandardError, reads);
        string? standardInputError = null;
        var output = new StringBuilder();
        var lines = new CliLineReader(process.StandardOutput, MaximumCapturedCharacters,
            $"Claude CLI StandardOutput exceeded the {MaximumCapturedCharacters}-character safety limit.");
        try
        {
            try
            {
                if (invocation.StandardInput is not null)
                {
                    await process.StandardInput.WriteAsync(
                        invocation.StandardInput.AsMemory(),
                        reads.Token);
                }
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                standardInputError =
                    "The Claude CLI closed standard input before the request was completely sent.";
                TryCloseStandardInput(process);
            }

            while (await lines.ReadLineAsync(reads.Token) is { } line)
            {
                EnsureCaptureLimit(output.Length + line.Length + Environment.NewLine.Length, nameof(process.StandardOutput));
                output.AppendLine(line);
                yield return new ClaudeCliOutputLine(line);
            }
            await process.WaitForExitAsync(reads.Token);
            string error = await standardError.WaitAsync(CliProcessTermination.ExitBound, reads.Token);
            yield return new ClaudeCliProcessCompleted(new ClaudeCliProcessResult(
                process.ExitCode,
                output.ToString(),
                error,
                standardInputError));
        }
        finally
        {
            lines.ReleaseBufferedText();
            try
            {
                if (!process.HasExited)
                {
                    await CliProcessTermination.KillAsync(process);
                }
            }
            finally
            {
                await reads.CancelAsync();
                try
                {
                    await standardError.WaitAsync(CliProcessTermination.ExitBound, CancellationToken.None);
                }
                catch (OperationCanceledException) when (reads.IsCancellationRequested)
                {
                    // Retirement cancels a pipe reader after the owned process is terminated.
                }
            }
        }
    }

    private static async Task<string> ReadStandardErrorAsync(
        StreamReader reader,
        CancellationTokenSource reads)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), reads.Token)) != 0)
            {
                EnsureCaptureLimit(output.Length + count, "StandardError");
                output.Append(buffer, 0, count);
            }
            return output.ToString();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or DecoderFallbackException)
        {
            // A failed stderr reader must also interrupt a blocked stdin/stdout operation.
            await reads.CancelAsync();
            throw;
        }
    }

    private static Process StartProcess(ClaudeCliInvocation invocation)
    {
        var process = new Process
        {
            StartInfo = CliProcessLauncher.CreateStartInfo(invocation.Executable, invocation.Arguments, invocation.WorkingDirectory)
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
