namespace Darkspyre.Inference.Connect.LLM.Examples;

public sealed record AgentToolExampleResult(
    string ThreadId,
    string Text,
    int CompletedToolCalls,
    LlmAgentTurnStatus Status);

/// <summary>In-turn callback example for session-oriented agent transports.</summary>
public static class AgentToolLane
{
    public static async Task<AgentToolExampleResult> RunAsync(
        LlmAgentProviderService agents,
        string connectionId,
        string modelId,
        string workingDirectory,
        string prompt,
        bool ephemeral,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agents);
        await using ILlmAgentSession session = await agents.StartSessionAsync(
            connectionId,
            new LlmAgentSessionOptions
            {
                ModelId = modelId,
                Runtime = new()
                {
                    WorkingDirectory = Path.GetFullPath(workingDirectory),
                    Sandbox = LlmAgentSandboxMode.ReadOnly,
                    ApprovalPolicy = LlmAgentApprovalPolicy.Never,
                    Ephemeral = ephemeral
                },
                Tools = [WeatherTool.AgentDefinition],
                ToolHandler = WeatherTool.ExecuteAsync
            },
            cancellationToken);

        string finalText = string.Empty;
        int completedToolCalls = 0;
        LlmAgentTurnCompletedEvent? completion = null;
        await foreach (LlmAgentEvent agentEvent in session.RunTurnAsync(
                           new LlmAgentTurnRequest
                           {
                               Input = [new LlmAgentTextContent(prompt)]
                           },
                           cancellationToken))
        {
            switch (agentEvent)
            {
                case LlmAgentMessageCompletedEvent message:
                    finalText = message.Text;
                    break;
                case LlmAgentToolEvent { Status: "completed", Success: true }:
                    completedToolCalls++;
                    break;
                case LlmAgentTurnCompletedEvent completed:
                    completion = completed;
                    break;
            }
        }

        if (completion is null)
        {
            throw new InvalidOperationException("The agent turn ended without a completion event.");
        }

        if (completion.Status == LlmAgentTurnStatus.Completed && completedToolCalls == 0)
        {
            throw new InvalidOperationException(
                "The agent completed without exercising the registered host tool.");
        }

        return new AgentToolExampleResult(
            session.ThreadId,
            finalText,
            completedToolCalls,
            completion.Status);
    }
}
