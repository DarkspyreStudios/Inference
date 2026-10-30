using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// Native Antigravity CLI sessions. AGY owns its Google sign-in, its native tools and its configured permission policy;
/// Connect owns one local process per open AGY conversation. The Host holds an <see cref="AntigravityConversation"/>,
/// which keeps the saved history, measures it and compacts it. A start opens a new AGY conversation from the host's
/// history and a resume reopens one by its conversation ID.
/// </summary>
internal sealed class AntigravityAgentClient(IAntigravityCliSessionProcessFactory processFactory, IAntigravityCliRunner runner,
    AntigravityCliLlmClient chat, LearnedModelContext learned, ILlmDataFolder dataFolder) : ILlmAgentResumeClient
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
        ValidateOpening(connection, options.ModelId, options.Tools, options.ToolHandler, options.ToolHandling, options.HostedTools,
            options.MaxHostedTurns);
        LlmAgentNativeRuntimeOptions runtime = ValidateRuntime(options.Runtime);
        LlmAgentContext history = options.Context is null ? new() : TextOnly(connection, options.ModelId, options.Context);
        return await OpenConversationAsync(connectionId, connection, options.ModelId, options.Instructions, options.ExecutionOptions,
            options.CallerServices, new(options.Tools, options.ToolHandler), runtime, history, resumeConversationId: null,
            cancellationToken);
    }

    /// <summary>
    /// Reopens the AGY conversation <paramref name="threadId"/> names. <see cref="LlmAgentResumeOptions.Context"/> is the
    /// saved history the conversation measures and compacts from; AGY already holds it, so none of it is sent.
    /// </summary>
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
        ValidateOpening(connection, options.ModelId, options.Tools, options.ToolHandler, options.ToolHandling, options.HostedTools,
            options.MaxHostedTurns);
        LlmAgentNativeRuntimeOptions runtime = ValidateRuntime(options.Runtime);
        // The session default; the CLI picks the model when none is named.
        string modelId = options.ModelId ?? AntigravityDefinition.Value.DefaultModelId;
        LlmAgentContext history = options.Context is null ? new() : TextOnly(connection, modelId, options.Context);
        return await OpenConversationAsync(connectionId, connection, modelId, options.Instructions, options.ExecutionOptions,
            options.CallerServices, new(options.Tools, options.ToolHandler), runtime, history, conversationId, cancellationToken);
    }

    private async Task<ILlmAgentSession> OpenConversationAsync(string connectionId, LlmConnection connection, string modelId,
        string? instructions, LlmAgentExecutionOptions? execution, LlmAgentCallerServices? callerServices, HostToolSet hostTools,
        LlmAgentNativeRuntimeOptions runtime, LlmAgentContext history, string? resumeConversationId,
        CancellationToken cancellationToken)
    {
        string executable = AntigravityCliLlmClient.ResolveExecutable(connection.Endpoint);
        string workingDirectory = CliInfrastructure.NormalizeWorkingDirectory(runtime.WorkingDirectory);
        var conversation = new AntigravityConversation(connectionId, connection, chat, learned, modelId, execution,
            callerServices?.Transcript, history, resumeConversationId, async (opening, conversationId, token) =>
            {
                // A reopened conversation already holds its history; a new one receives it once, before its first input.
                AgentOpeningContext? text = conversationId is not null ? null
                    : AgentOpeningContext.For(new LlmAgentSessionOptions { ModelId = modelId, Context = opening }, carriesAttachments: false);
                // Each AGY process generation registers the tools under its own session ID; the session owns the registration.
                AntigravityHostTools? tools = await AntigravityHostTools.OpenAsync(connection, modelId, hostTools.Tools, hostTools.Handler,
                    callerServices?.Resources, runner, executable, workingDirectory, await dataFolder.ReadAsync(token), token);
                return await AntigravityAgentSession.OpenAsync(processFactory, executable, workingDirectory, dataFolder,
                    new AntigravityLaunch(modelId, null, null), conversationId, connection, tools, Introduction(instructions, text?.Text),
                    text?.Notice, token);
            });
        try
        {
            await conversation.OpenAsync(cancellationToken);
            return conversation;
        }
        catch
        {
            await conversation.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The history as AGY can take it: every attachment it cannot carry, resource references included, is replaced in its
    /// own message by the shared notice. A start from any host history, such as a fork, then rebuilds the conversation.
    /// </summary>
    private static LlmAgentContext TextOnly(LlmConnection connection, string modelId, LlmAgentContext context)
    {
        LlmProviderCapabilities capabilities = AntigravityCliLlmClient.ResolveCapabilities(connection, modelId);
        return context with
        {
            Entries = [.. context.Entries.Select(entry =>
            {
                if (entry.Message.Attachments.Count == 0) return entry;
                LlmChatMessage normalized = LlmRequestNormalizer.Normalize(connection, new LlmChatRequest
                {
                    ModelId = modelId,
                    Messages = [new("user", entry.Message.Content) { Attachments = entry.Message.Attachments }]
                }, capabilities).Messages[0];
                return entry with { Message = entry.Message with { Content = normalized.Content, Attachments = normalized.Attachments } };
            })]
        };
    }

    /// <summary>
    /// Refuses host tools on a connection that turns agent tool use off, returned tool requests, which AGY cannot hand back,
    /// and hosted tools it does not offer. An image tool is dropped.
    /// </summary>
    private static void ValidateOpening(LlmConnection connection, string? modelId,
        IReadOnlyList<LlmAgentClientToolDefinition> tools, LlmAgentToolHandler? toolHandler, LlmAgentToolHandling toolHandling,
        IReadOnlyList<LlmHostedTool> hostedTools, int? maxHostedTurns)
    {
        ArgumentNullException.ThrowIfNull(tools);
        NativeAgentContract.ValidateConnection(connection, AntigravityNativeAgent.Capabilities, supportsAgentToolUse: true,
            modelId, tools, toolHandler);
        if (toolHandling == LlmAgentToolHandling.ReturnRequests)
            throw new NotSupportedException("Antigravity CLI runs each host tool call to its result; SupportsExternalToolHandoff is Unsupported.");
        HostedTools.Validate(HostedTools.WithoutUnofferedImages(hostedTools, AntigravityNativeAgent.Capabilities.HostedTools),
            maxHostedTurns, AntigravityNativeAgent.Capabilities.HostedTools);
    }

    /// <summary>
    /// The runtime settings AGY can honour. A null runtime is the host-controlled default, which AGY cannot enforce, so it
    /// is refused like any other policy outside <see cref="LlmAgentCapabilities.SupportedRuntimePolicies"/>.
    /// </summary>
    private static LlmAgentNativeRuntimeOptions ValidateRuntime(LlmAgentNativeRuntimeOptions? runtime)
    {
        runtime ??= new LlmAgentNativeRuntimeOptions { WorkingDirectory = Environment.CurrentDirectory };
        if (!AntigravityNativeAgent.Capabilities.SupportedRuntimePolicies.Contains(runtime.Policy.Kind))
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

    /// <summary>The host tools an opening offers and the handler that runs them.</summary>
    private sealed record HostToolSet(IReadOnlyList<LlmAgentClientToolDefinition> Tools, LlmAgentToolHandler? Handler);
}
