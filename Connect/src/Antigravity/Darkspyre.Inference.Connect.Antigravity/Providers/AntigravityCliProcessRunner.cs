using System.Runtime.CompilerServices;
using System.Text;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>One-shot invocations over the session process: write the whole input, close it, read to exit.</summary>
internal sealed class AntigravityCliProcessRunner(IAntigravityCliSessionProcessFactory processFactory)
    : IAntigravityCliRunner
{
    private const int MaximumCapturedCharacters = 16 * 1024 * 1024;

    public async Task<AntigravityCliProcessResult> RunAsync(
        AntigravityCliInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        AntigravityCliProcessResult? completed = null;
        await foreach (AntigravityCliProcessEvent processEvent in StreamAsync(invocation, cancellationToken))
        {
            if (processEvent is AntigravityCliProcessCompleted completion)
            {
                completed = completion.Result;
            }
        }
        return completed ?? throw new InvalidOperationException(
            "The Antigravity CLI process ended without a completion event.");
    }

    public async IAsyncEnumerable<AntigravityCliProcessEvent> StreamAsync(
        AntigravityCliInvocation invocation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        IAntigravityCliSessionProcess process = processFactory.Start(
            invocation.Executable,
            invocation.Arguments,
            invocation.WorkingDirectory,
            new Dictionary<string, string>());
        bool exited = false;
        try
        {
            string? standardInputError = null;
            try
            {
                if (invocation.StandardInput is not null)
                {
                    await process.WriteAsync(invocation.StandardInput, cancellationToken);
                }
            }
            catch (IOException)
            {
                standardInputError =
                    "The Antigravity CLI closed standard input before the request was completely sent.";
            }
            process.CloseInput();

            var output = new StringBuilder();
            while (await process.ReadLineAsync(cancellationToken) is { } line)
            {
                output.AppendLine(line);
                if (output.Length > MaximumCapturedCharacters)
                {
                    throw new InvalidOperationException(
                        $"Antigravity CLI standard output exceeded the {MaximumCapturedCharacters}-character safety limit.");
                }
                yield return new AntigravityCliOutputLine(line);
            }
            (int exitCode, string error) = await process.WaitForExitAsync(cancellationToken);
            exited = true;
            yield return new AntigravityCliProcessCompleted(new AntigravityCliProcessResult(
                exitCode,
                output.ToString(),
                error,
                standardInputError));
        }
        finally
        {
            try
            {
                if (!exited)
                {
                    await process.KillAsync();
                }
            }
            finally
            {
                await process.DisposeAsync();
            }
        }
    }
}
