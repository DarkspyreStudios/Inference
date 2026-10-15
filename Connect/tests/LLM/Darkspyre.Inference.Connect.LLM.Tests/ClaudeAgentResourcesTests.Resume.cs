using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed partial class ClaudeAgentResourcesTests
{
    [Fact]
    public async Task ResumeOpensTheSavedSessionNatively()
    {
        var process = new ManualProcess();
        var factory = new SavedSessions(new Factory(process), _directory, "saved-1");
        await using ILlmAgentSession session = await Client(factory).ResumeSessionAsync("connection", Connection(), "saved-1",
            Resuming(History(("t1", "first question", "first answer"))));

        IReadOnlyList<string> arguments = factory.Invocation.Arguments;
        Assert.Contains("--resume=saved-1", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--session-id", StringComparison.Ordinal));
        Assert.DoesNotContain("--no-session-persistence", arguments);
        Assert.Equal("saved-1", session.ThreadId);
        await CompleteTurnAsync(session, process);
        // The CLI holds the history, so nothing of it is sent again.
        Assert.DoesNotContain("first question", UserText(process), StringComparison.Ordinal);
        Assert.Equal(InferenceSupport.Supported, (await session.GetCapabilitiesAsync()).SupportsNativeResume);
    }

    [Fact]
    public async Task ResumeWithoutTheSavedSessionContinuesFromHistory()
    {
        // No session saved for this working folder: a missing session, or one saved for another folder.
        var process = new ManualProcess();
        var factory = new SavedSessions(new Factory(process), Path.Combine(_directory, "elsewhere"), "saved-1");
        await using ILlmAgentSession session = await Client(factory).ResumeSessionAsync("connection", Connection(), "saved-1",
            Resuming(History(("t1", "first question", "first answer"))));

        IReadOnlyList<string> arguments = factory.Invocation.Arguments;
        Assert.Contains("--session-id=saved-1", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--resume", StringComparison.Ordinal));
        await CompleteTurnAsync(session, process);
        Assert.Contains("first question", UserText(process), StringComparison.Ordinal);
        // The CLI saved this session under the thread id, so a later resume opens it natively.
        Assert.Equal(InferenceSupport.Supported, (await session.GetCapabilitiesAsync()).SupportsNativeResume);
    }

    [Fact]
    public async Task CompactionEndsNativeResume()
    {
        var first = new ManualProcess();
        var second = new ManualProcess();
        var factory = new SavedSessions(new Factory(first, second), _directory, "saved-1");
        await using ILlmAgentSession session = await Client(factory).ResumeSessionAsync("connection", Connection(), "saved-1",
            Resuming(History(("t1", Words("alpha", 2000), "first answer"), ("t2", "second question", "second answer"))) with
            {
                ExecutionOptions = Window(4000)
            });

        Assert.NotNull(await session.CompactNowAsync());

        // The conversation now runs in another CLI session, which a resume by the thread id does not reach.
        Assert.Equal("saved-1", session.ThreadId);
        Assert.Equal(InferenceSupport.Unsupported, (await session.GetCapabilitiesAsync()).SupportsNativeResume);
    }

    [Fact]
    public void SavedSessionIsFoundOnlyForItsWorkingFolder()
    {
        string root = Path.Combine(_directory, "config");
        string folder = Path.Combine(_directory, "work.folder");
        Directory.CreateDirectory(folder);
        string encoded = string.Concat(folder.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-'));
        Directory.CreateDirectory(Path.Combine(root, "projects", encoded));
        File.WriteAllText(Path.Combine(root, "projects", encoded, "saved-1.jsonl"), "{}");
        IClaudeCliSessionProcessFactory factory = new Factory(new ManualProcess());
        string? previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", root);
        try
        {
            Assert.True(factory.SessionSaved(folder, "saved-1"));
            Assert.False(factory.SessionSaved(folder, "saved-2"));
            Assert.False(factory.SessionSaved(_directory, "saved-1"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previous);
        }
    }

    private LlmAgentResumeOptions Resuming(LlmAgentContext context) => new()
    {
        ModelId = "claude-test",
        Runtime = new() { WorkingDirectory = _directory },
        Context = context
    };

    /// <summary>The CLI has saved <paramref name="sessionId"/> for <paramref name="folder"/> only.</summary>
    private sealed class SavedSessions(Factory inner, string folder, string sessionId) : IClaudeCliSessionProcessFactory
    {
        public ClaudeCliSessionInvocation Invocation => inner.Invocation!;

        public Task<IClaudeCliSessionProcess> StartAsync(ClaudeCliSessionInvocation invocation, CancellationToken cancellationToken = default) =>
            inner.StartAsync(invocation, cancellationToken);

        public bool SessionSaved(string workingDirectory, string id) =>
            workingDirectory == Path.GetFullPath(folder) && id == sessionId;
    }
}
