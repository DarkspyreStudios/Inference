using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// Native Antigravity CLI sessions. AGY owns the conversation, its Google sign-in, its native tools and its configured
/// permission policy; Connect owns one local process per open session. A start opens a new AGY conversation and a
/// resume reopens one by its conversation ID.
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
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelId);
        // AGY assigns the conversation ID, so ThreadId and SessionId are not read.
        ValidateOpening(connection, options.ModelId, options.Tools, options.ToolHandler, options.HostedTools, options.MaxHostedTurns);
        LlmAgentNativeRuntimeOptions runtime = ValidateRuntime(options.Runtime);
        AgentOpeningContext? opening = AgentOpeningContext.For(options, carriesAttachments: false);
        return await AntigravityAgentSession.OpenAsync(
            processFactory,
            AntigravityCliLlmClient.ResolveExecutable(connection.Endpoint),
            CliInfrastructure.NormalizeWorkingDirectory(runtime.WorkingDirectory),
            new AntigravityLaunch(options.ModelId, null, null),
            conversationId: null,
            connection,
            Introduction(options.Instructions, opening?.Text),
            opening?.Notice,
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
        ValidateOpening(connection, options.ModelId, options.Tools, options.ToolHandler, options.HostedTools, options.MaxHostedTurns);
        LlmAgentNativeRuntimeOptions runtime = ValidateRuntime(options.Runtime);
        return await AntigravityAgentSession.OpenAsync(
            processFactory,
            AntigravityCliLlmClient.ResolveExecutable(connection.Endpoint),
            CliInfrastructure.NormalizeWorkingDirectory(runtime.WorkingDirectory),
            new AntigravityLaunch(options.ModelId, null, null),
            conversationId,
            connection,
            Introduction(options.Instructions, null),
            openingNotice: null,
            cancellationToken);
    }

    /// <summary>Refuses host tools, which AGY cannot call, and hosted tools it does not offer. An image tool is dropped.</summary>
    private static void ValidateOpening(LlmConnection connection, string? modelId,
        IReadOnlyList<LlmAgentClientToolDefinition> tools, LlmAgentToolHandler? toolHandler,
        IReadOnlyList<LlmHostedTool> hostedTools, int? maxHostedTurns)
    {
        ArgumentNullException.ThrowIfNull(tools);
        NativeAgentContract.ValidateConnection(connection, NativeAgentContract.Antigravity, supportsAgentToolUse: false,
            modelId, tools, toolHandler);
        HostedTools.Validate(HostedTools.WithoutUnofferedImages(hostedTools, NativeAgentContract.Antigravity.HostedTools),
            maxHostedTurns, NativeAgentContract.Antigravity.HostedTools);
    }

    /// <summary>
    /// The runtime settings AGY can honour. A null runtime is the host-controlled default, which AGY cannot enforce, so it
    /// is refused like any other policy outside <see cref="LlmAgentCapabilities.SupportedRuntimePolicies"/>.
    /// </summary>
    private static LlmAgentNativeRuntimeOptions ValidateRuntime(LlmAgentNativeRuntimeOptions? runtime)
    {
        runtime ??= new LlmAgentNativeRuntimeOptions { WorkingDirectory = Environment.CurrentDirectory };
        if (!NativeAgentContract.Antigravity.SupportedRuntimePolicies.Contains(runtime.Policy.Kind))
        {
            throw new NotSupportedException(
                $"Antigravity CLI does not accept a {runtime.Policy.Kind} runtime policy; check SupportedRuntimePolicies.");
        }
        if (runtime.Ephemeral)
        {
            throw new NotSupportedException("Antigravity CLI persists every conversation; Ephemeral is unsupported.");
        }
        return runtime;
    }

    /// <summary>
    /// Text sent once, ahead of the session's first input: the session instructions, which AGY has no system-prompt
    /// transport for, then the opening history.
    /// </summary>
    private static string? Introduction(string? instructions, string? opening)
    {
        string[] parts =
        [
            .. string.IsNullOrEmpty(instructions) ? [] : new[] { $"Instructions for this conversation:\n{instructions}" },
            .. string.IsNullOrEmpty(opening) ? [] : new[] { opening }
        ];
        return parts.Length == 0 ? null : string.Join("\n\n", parts);
    }
}
