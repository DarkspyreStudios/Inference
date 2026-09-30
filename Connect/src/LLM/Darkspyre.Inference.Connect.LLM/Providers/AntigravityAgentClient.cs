using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// Native Antigravity CLI sessions. AGY owns the conversation, its Google sign-in, its native tools and
/// its configured permission policy; Connect owns one local process per open session.
/// </summary>
internal sealed class AntigravityAgentClient(IAntigravityCliSessionProcessFactory processFactory)
    : ILlmAgentResumeClient
{
    public LlmTransport Transport => LlmTransport.GoogleAntigravityCli;

    public async Task<ILlmAgentSession> StartSessionAsync(
        string connectionId,
        LlmConnection connection,
        LlmAgentSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        AntigravityCliLlmClient.ValidateConnection(connection);
        ValidateHostInputs(options.Instructions, options.Tools, options.ToolHandler);
        if (options.Context is not null)
        {
            throw new NotSupportedException(
                "Antigravity CLI owns its conversation and does not import host context.");
        }
        LlmAgentNativeRuntimeOptions runtime = NativeAgentContract.ValidateStart(options, NativeAgentContract.Antigravity);
        NativeAgentContract.ValidateConnection(connection, NativeAgentContract.Antigravity, supportsAgentToolUse: false,
            options.ModelId, options.Tools, options.ToolHandler);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelId);
        ValidateRuntime(runtime);
        return await AntigravityAgentSession.OpenAsync(
            processFactory,
            AntigravityCliLlmClient.ResolveExecutable(connection.Endpoint),
            CliInfrastructure.NormalizeWorkingDirectory(runtime.WorkingDirectory),
            new AntigravityLaunch(options.ModelId, null, null),
            conversationId: null,
            connection,
            cancellationToken);
    }

    public async Task<ILlmAgentSession> ResumeSessionAsync(
        string connectionId,
        LlmConnection connection,
        string threadId,
        LlmAgentResumeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        AntigravityCliLlmClient.ValidateConnection(connection);
        CliInfrastructure.ValidateThreadId(threadId);
        string conversationId = AntigravityProtocol.ConversationId(threadId)
            ?? throw new ArgumentException("The Antigravity conversation ID is not a valid identifier.", nameof(threadId));
        options ??= new LlmAgentResumeOptions();
        ValidateHostInputs(options.Instructions, options.Tools, options.ToolHandler);
        // A null Runtime selects the host-controlled resume defaults, which this runtime rejects: the host must
        // supply its stored provider-policy selection and working directory.
        LlmAgentNativeRuntimeOptions runtime = NativeAgentContract.ResumeRuntime(options, NativeAgentContract.Antigravity);
        NativeAgentContract.ValidateConnection(connection, NativeAgentContract.Antigravity, supportsAgentToolUse: false,
            options.ModelId, options.Tools, options.ToolHandler);
        ValidateRuntime(runtime);
        return await AntigravityAgentSession.OpenAsync(
            processFactory,
            AntigravityCliLlmClient.ResolveExecutable(connection.Endpoint),
            CliInfrastructure.NormalizeWorkingDirectory(runtime.WorkingDirectory),
            new AntigravityLaunch(options.ModelId, null, null),
            conversationId,
            connection,
            cancellationToken);
    }

    private static void ValidateHostInputs(
        string? instructions,
        IReadOnlyList<LlmAgentToolDefinition> tools,
        LlmAgentToolHandler? toolHandler)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (tools.Count > 0 || toolHandler is not null)
        {
            throw new NotSupportedException(
                "Antigravity CLI runs only its own native tools; it has no host tool callback.");
        }
        if (!string.IsNullOrEmpty(instructions))
        {
            throw new NotSupportedException(
                "Antigravity CLI has no system-instruction transport for agent sessions.");
        }
    }

    private static void ValidateRuntime(LlmAgentNativeRuntimeOptions runtime)
    {
        if (runtime.Ephemeral)
        {
            throw new NotSupportedException("Antigravity CLI persists every native conversation; Ephemeral is unsupported.");
        }
    }
}
