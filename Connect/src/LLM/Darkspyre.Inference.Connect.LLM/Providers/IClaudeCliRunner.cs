using System.Runtime.CompilerServices;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed record ClaudeCliInvocation(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? StandardInput,
    string WorkingDirectory)
{
    internal int? MaxOutputTokens { get; init; }
}

internal sealed record ClaudeCliSessionInvocation(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

internal sealed record ClaudeCliProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string? StandardInputError = null);

internal abstract record ClaudeCliProcessEvent;

internal sealed record ClaudeCliOutputLine(string Line) : ClaudeCliProcessEvent;

internal sealed record ClaudeCliProcessCompleted(ClaudeCliProcessResult Result)
    : ClaudeCliProcessEvent;

internal interface IClaudeCliRunner
{
    Task<ClaudeCliProcessResult> RunAsync(
        ClaudeCliInvocation invocation,
        CancellationToken cancellationToken = default);

    async IAsyncEnumerable<ClaudeCliProcessEvent> StreamAsync(
        ClaudeCliInvocation invocation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ClaudeCliProcessResult result = await RunAsync(invocation, cancellationToken);
        using var reader = new StringReader(result.StandardOutput);
        while (reader.ReadLine() is { } line)
        {
            yield return new ClaudeCliOutputLine(line);
        }
        yield return new ClaudeCliProcessCompleted(result);
    }
}

internal interface IClaudeCliSessionProcess : IAsyncDisposable
{
    ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default);

    ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default);

    Task<ClaudeCliProcessResult> WaitForExitAsync(CancellationToken cancellationToken = default);
}

internal interface IClaudeCliSessionProcessFactory
{
    Task<IClaudeCliSessionProcess> StartAsync(
        ClaudeCliSessionInvocation invocation,
        CancellationToken cancellationToken = default);
}
