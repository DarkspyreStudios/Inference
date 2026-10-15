using System.Runtime.CompilerServices;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed record ClaudeCliInvocation(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? StandardInput,
    string WorkingDirectory)
{
    internal int? MaxOutputTokens { get; init; }
    /// <summary>The token of the Claude account the connection is signed in to, or null to run as the machine's active login.</summary>
    internal string? AccountToken { get; init; }
}

internal sealed record ClaudeCliSessionInvocation(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory)
{
    /// <summary>The token of the Claude account the connection is signed in to, or null to run as the machine's active login.</summary>
    internal string? AccountToken { get; init; }
}

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

    /// <summary>
    /// Whether the CLI saved the session <paramref name="sessionId"/> for <paramref name="workingDirectory"/>, so
    /// <c>--resume</c> finds it. The CLI keeps each session at
    /// <c>&lt;config folder&gt;/projects/&lt;working folder&gt;/&lt;session id&gt;.jsonl</c>: the config folder is
    /// CLAUDE_CONFIG_DIR or <c>~/.claude</c>, and the folder name is the working folder with every character other than
    /// an ASCII letter or digit replaced by <c>-</c>.
    /// </summary>
    bool SessionSaved(string workingDirectory, string sessionId)
    {
        string root = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        string folder = string.Concat(workingDirectory.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-'));
        return File.Exists(Path.Combine(root, "projects", folder, sessionId + ".jsonl"));
    }
}
