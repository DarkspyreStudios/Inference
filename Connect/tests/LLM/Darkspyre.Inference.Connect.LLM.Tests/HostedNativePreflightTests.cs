using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HostedNativePreflightTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (bool claude in new[] { false, true })
            foreach (int operation in claude ? new[] { 0, 1, 2 } : new[] { 0, 1 })
                foreach (bool limitOnly in new[] { false, true })
                    yield return [claude, operation, limitOnly];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task UnsupportedHostedRequestsNeverStartNativeProcess(bool claude, int operation, bool limitOnly)
    {
        var factory = new Factory();
        ILlmAgentResumeClient client = claude ? new ClaudeAgentClient(factory) : new AntigravityAgentClient(factory);
        var connection = new LlmConnection
        {
            Transport = client.Transport,
            Version = "fixture",
            Endpoint = "controlled-executable"
        };
        var runtime = new LlmAgentNativeRuntimeOptions
        {
            WorkingDirectory = Environment.CurrentDirectory,
            Policy = claude ? new LlmAgentHostRuntimePolicy() : new LlmAgentProviderRuntimePolicy()
        };
        IReadOnlyList<LlmHostedTool> hosted = limitOnly ? [] : [new LlmCodeExecutionTool()];
        var resume = new LlmAgentResumeOptions { Runtime = runtime, HostedTools = hosted, MaxHostedTurns = limitOnly ? 1 : null };
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (operation == 0)
                await client.StartSessionAsync("connection", connection, new LlmAgentSessionOptions
                { ModelId = "model", Runtime = runtime, HostedTools = hosted, MaxHostedTurns = resume.MaxHostedTurns });
            else if (operation == 1)
                await client.ResumeSessionAsync("connection", connection, "36cb7cae-07bd-4772-91e6-98e8937a3a62", resume);
            else
                await ((ILlmAgentForkClient)client).ForkSessionAsync("connection", connection, "36cb7cae-07bd-4772-91e6-98e8937a3a62", options: resume);
        });
        Assert.Equal(0, factory.Starts);
    }

    private sealed class Factory : IClaudeCliSessionProcessFactory, IAntigravityCliSessionProcessFactory
    {
        internal int Starts { get; private set; }
        public Task<IClaudeCliSessionProcess> StartAsync(ClaudeCliSessionInvocation invocation, CancellationToken cancellationToken = default)
        { Starts++; throw new InvalidOperationException("Hosted preflight must precede native launch."); }
        public IAntigravityCliSessionProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
        { Starts++; throw new InvalidOperationException("Hosted preflight must precede native launch."); }
    }
}
