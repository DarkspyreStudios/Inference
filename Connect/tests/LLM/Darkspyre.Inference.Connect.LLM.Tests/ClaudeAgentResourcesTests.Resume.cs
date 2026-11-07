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
        // The CLI holds its own saved session, which the saved history does not measure, until a step reports.
        Assert.Null((await session.GetContextStatusAsync()).UsedContextTokens);
        await CompleteTurnAsync(session, process);
        // The CLI holds the history, so nothing of it is sent again.
        Assert.DoesNotContain("first question", UserText(process), StringComparison.Ordinal);
        Assert.Equal(InferenceSupport.Supported, (await session.GetCapabilitiesAsync()).SupportsNativeResume);
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
