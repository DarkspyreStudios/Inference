namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class RuntimeInputAdmission
{
    internal static void Validate(LlmConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Acp is not null && connection.Transport != LlmTransport.AgentClientProtocol
            || connection.Copilot is not null && connection.Transport != LlmTransport.GitHubCopilot
            || connection.RuntimeLaunchInputs is not null && !IsRuntimeTransport(connection.Transport))
            throw new NotSupportedException("This transport does not accept ACP or Copilot runtime configuration or launch inputs.");
        ValidateCredential(connection.Transport, LlmCredential.Parse(connection.Credential));
    }

    internal static void ValidateCredential(LlmTransport transport, LlmCredential? credential)
    {
        if (transport != LlmTransport.GitHubCopilot && (credential?.Copilot is not null || credential?.CopilotRuntime is not null))
            throw new NotSupportedException("This transport does not accept Copilot runtime credentials.");
    }

    private static bool IsRuntimeTransport(LlmTransport transport) =>
        transport is LlmTransport.GitHubCopilot or LlmTransport.AgentClientProtocol;
}
