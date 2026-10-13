using System.Runtime.CompilerServices;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed record AntigravityCliInvocation(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? StandardInput,
    string WorkingDirectory);

internal sealed record AntigravityCliProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string? StandardInputError = null);

internal abstract record AntigravityCliProcessEvent;

internal sealed record AntigravityCliOutputLine(string Line) : AntigravityCliProcessEvent;

internal sealed record AntigravityCliProcessCompleted(AntigravityCliProcessResult Result)
    : AntigravityCliProcessEvent;

internal interface IAntigravityCliRunner
{
    Task<AntigravityCliProcessResult> RunAsync(
        AntigravityCliInvocation invocation,
        CancellationToken cancellationToken = default);

    async IAsyncEnumerable<AntigravityCliProcessEvent> StreamAsync(
        AntigravityCliInvocation invocation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        AntigravityCliProcessResult result = await RunAsync(invocation, cancellationToken);
        using var reader = new StringReader(result.StandardOutput);
        while (reader.ReadLine() is { } line)
        {
            yield return new AntigravityCliOutputLine(line);
        }
        yield return new AntigravityCliProcessCompleted(result);
    }
}
