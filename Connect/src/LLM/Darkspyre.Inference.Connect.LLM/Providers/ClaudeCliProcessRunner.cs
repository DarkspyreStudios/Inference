using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Runs one Claude CLI invocation. A process still running when the caller stops is ended by <see cref="ClaudeCliExits"/>.</summary>
internal sealed class ClaudeCliProcessRunner(ClaudeCliExits exits) : IClaudeCliRunner
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

        Process process = StartProcess(invocation);
        using var reads = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The error reader outlives a cancelled caller, because a process that is still finishing must not block
        // on a full standard-error pipe.
        var errorStop = new CancellationTokenSource();
        Task<string> standardError = ReadStandardErrorAsync(process.StandardError, reads, errorStop.Token);
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
                    await Observe(process.StandardInput.WriteAsync(invocation.StandardInput.AsMemory(), reads.Token),
                        standardError, reads, cancellationToken);
                }
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                standardInputError =
                    "The Claude CLI closed standard input before the request was completely sent.";
                CloseStandardInput(process);
            }

            while (await Observe(lines.ReadLineAsync(reads.Token).AsTask(), standardError, reads, cancellationToken) is { } line)
            {
                EnsureCaptureLimit(output.Length + line.Length + Environment.NewLine.Length, nameof(process.StandardOutput));
                output.AppendLine(line);
                yield return new ClaudeCliOutputLine(line);
            }
            await Observe(process.WaitForExitAsync(reads.Token), standardError, reads, cancellationToken);
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
            if (process.HasExited)
            {
                await ReleaseAsync(process, standardError, errorStop);
            }
            else
            {
                exits.Track(EndAsync(process, standardError, errorStop));
            }
        }
    }

    /// <summary>
    /// Ends a process the caller no longer waits for: standard-input EOF and SIGTERM, then at most
    /// <see cref="ClaudeCliExits.Limit"/> before SIGKILL. Output is drained so a full pipe cannot block the exit.
    /// </summary>
    private async Task EndAsync(Process process, Task<string> standardError, CancellationTokenSource errorStop)
    {
        Task output = Task.CompletedTask;
        try
        {
            CloseStandardInput(process);
            CliProcessTermination.Terminate(process);
            output = DrainAsync(process.StandardOutput.BaseStream, errorStop.Token);
            await exits.AwaitExitAsync(process).ConfigureAwait(false);
            using var bound = new CancellationTokenSource(CliProcessTermination.ExitBound);
            try
            {
                await standardError.WaitAsync(bound.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (bound.IsCancellationRequested)
            {
                throw new CliProcessDrainException(process.Id);
            }
        }
        finally
        {
            await errorStop.CancelAsync().ConfigureAwait(false);
            await output.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await ReleaseAsync(process, standardError, errorStop).ConfigureAwait(false);
        }
    }

    private static async Task ReleaseAsync(Process process, Task<string> standardError, CancellationTokenSource errorStop)
    {
        try
        {
            await errorStop.CancelAsync().ConfigureAwait(false);
            try
            {
                await standardError.WaitAsync(CliProcessTermination.ExitBound, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (errorStop.IsCancellationRequested)
            {
                // Retirement cancels a pipe reader after the owned process exits.
            }
        }
        finally
        {
            process.Dispose();
            errorStop.Dispose();
        }
    }

    /// <summary>Reports a standard-error failure in place of the cancellation it caused.</summary>
    private static async Task<T> Observe<T>(Task<T> operation, Task standardError, CancellationTokenSource reads, CancellationToken caller)
    {
        try { return await operation; }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested && reads.IsCancellationRequested)
        {
            await standardError;
            throw;
        }
    }

    private static async Task Observe(ValueTask operation, Task standardError, CancellationTokenSource reads, CancellationToken caller) =>
        await Observe(Completed(operation), standardError, reads, caller);

    private static async Task Observe(Task operation, Task standardError, CancellationTokenSource reads, CancellationToken caller) =>
        await Observe(Completed(operation), standardError, reads, caller);

    private static async Task<bool> Completed(ValueTask operation)
    {
        await operation;
        return true;
    }

    private static async Task<bool> Completed(Task operation)
    {
        await operation;
        return true;
    }

    private static async Task DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0) { }
    }

    private static async Task<string> ReadStandardErrorAsync(
        StreamReader reader,
        CancellationTokenSource reads,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
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

    /// <summary>Closes standard input without flushing a request that a cancelled write left in the buffer.</summary>
    private static void CloseStandardInput(Process process)
    {
        try
        {
            process.StandardInput.BaseStream.Dispose();
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The child process has already closed its end of the input pipe.
        }
        catch (InvalidOperationException)
        {
            // The process has already exited and disposed the redirected stream, or input is already closed.
        }
    }
}

internal sealed class ClaudeCliUnavailableException(string executable, Exception innerException)
    : Exception($"The Claude CLI executable '{executable}' could not be started.", innerException);
