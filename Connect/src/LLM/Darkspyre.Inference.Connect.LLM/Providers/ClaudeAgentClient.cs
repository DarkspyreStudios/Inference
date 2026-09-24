using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Session-oriented agent adapter over the official Claude CLI SDK protocol.</summary>
internal sealed class ClaudeAgentClient(IClaudeCliSessionProcessFactory processFactory)
    : ILlmAgentClient
{
    private const string McpServerName = "darkspyre-inference-connect";

    public LlmTransport Transport => LlmTransport.AnthropicClaudeCli;

    public async Task<ILlmAgentSession> StartSessionAsync(
        LlmConnection connection,
        LlmAgentSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        NativeAgentContract.ValidateStart(options);
        NativeAgentContract.ValidateConnection(connection, NativeAgentContract.Claude,
            options.ModelId, options.Tools, options.Handlers);
        ClaudeCliConfiguration.ValidateConnection(connection);
        ValidateOptions(options.ModelId, options.WorkingDirectory, options.Tools, options.Handlers);
        AgentOpeningContext? opening = AgentOpeningContext.For(options, carriesAttachments: true);
        string threadId = Guid.NewGuid().ToString();
        return await CreateSessionAsync(
            threadId,
            threadId,
            resumeThreadId: null,
            fork: false,
            ClaudeCliConfiguration.ResolveExecutable(connection.Endpoint),
            options.ModelId,
            options.WorkingDirectory,
            options.Instructions,
            options.WritableRoots,
            options.Sandbox,
            options.NetworkAllowed,
            options.Tools,
            options.Handlers,
            options.Ephemeral,
            connection,
            NativeAgentContract.Claude.RichContentSupported.For(connection, options.ModelId),
            opening,
            cancellationToken);
    }

    public async Task<ILlmAgentSession> ResumeSessionAsync(
        LlmConnection connection,
        string threadId,
        LlmAgentResumeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ClaudeCliConfiguration.ValidateConnection(connection);
        CliInfrastructure.ValidateThreadId(threadId);
        options ??= new LlmAgentResumeOptions();
        NativeAgentContract.ValidateConnection(connection, NativeAgentContract.Claude,
            options.ModelId, options.Tools, options.Handlers);
        string workingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory;
        ValidateOptions(options.ModelId, workingDirectory, options.Tools, options.Handlers, modelOptional: true);
        return await CreateSessionAsync(
            threadId,
            threadId,
            threadId,
            fork: false,
            ClaudeCliConfiguration.ResolveExecutable(connection.Endpoint),
            options.ModelId,
            workingDirectory,
            options.Instructions,
            options.WritableRoots ?? [workingDirectory],
            options.Sandbox ?? LlmAgentSandboxMode.WorkspaceWrite,
            options.NetworkAllowed ?? true,
            options.Tools,
            options.Handlers,
            ephemeral: false,
            connection,
            NativeAgentContract.Claude.RichContentSupported.For(connection, options.ModelId),
            opening: null,
            cancellationToken);
    }

    public async Task<ILlmAgentSession> ForkSessionAsync(
        LlmConnection connection,
        string threadId,
        string? lastTurnId = null,
        LlmAgentResumeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ClaudeCliConfiguration.ValidateConnection(connection);
        CliInfrastructure.ValidateThreadId(threadId);
        if (!string.IsNullOrWhiteSpace(lastTurnId))
        {
            throw new NotSupportedException(
                "Claude CLI can fork a session only at its current persisted position.");
        }

        options ??= new LlmAgentResumeOptions();
        NativeAgentContract.ValidateConnection(connection, NativeAgentContract.Claude,
            options.ModelId, options.Tools, options.Handlers);
        string workingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory;
        ValidateOptions(options.ModelId, workingDirectory, options.Tools, options.Handlers, modelOptional: true);
        string forkedThreadId = Guid.NewGuid().ToString();
        return await CreateSessionAsync(
            forkedThreadId,
            threadId,
            threadId,
            fork: true,
            ClaudeCliConfiguration.ResolveExecutable(connection.Endpoint),
            options.ModelId,
            workingDirectory,
            options.Instructions,
            options.WritableRoots ?? [workingDirectory],
            options.Sandbox ?? LlmAgentSandboxMode.WorkspaceWrite,
            options.NetworkAllowed ?? true,
            options.Tools,
            options.Handlers,
            ephemeral: false,
            connection,
            NativeAgentContract.Claude.RichContentSupported.For(connection, options.ModelId),
            opening: null,
            cancellationToken);
    }

    private async Task<ILlmAgentSession> CreateSessionAsync(
        string threadId,
        string sessionId,
        string? resumeThreadId,
        bool fork,
        string executable,
        string? modelId,
        string workingDirectory,
        string? instructions,
        IReadOnlyList<string> writableRoots,
        LlmAgentSandboxMode sandbox,
        bool networkAllowed,
        IReadOnlyList<LlmAgentToolDefinition> tools,
        LlmAgentSessionHandlers handlers,
        bool ephemeral,
        LlmConnection connection,
        LlmRichContentSupported richContent,
        AgentOpeningContext? opening,
        CancellationToken cancellationToken)
    {
        string normalizedDirectory = CliInfrastructure.NormalizeWorkingDirectory(workingDirectory);
        IReadOnlyList<string> arguments = BuildArguments(
            threadId,
            resumeThreadId,
            fork,
            modelId,
            tools,
            ephemeral,
            normalizedDirectory,
            writableRoots,
            sandbox,
            networkAllowed);
        IClaudeCliSessionProcess process;
        try
        {
            process = await processFactory.StartAsync(
                new ClaudeCliSessionInvocation(
                    executable,
                    arguments,
                    normalizedDirectory),
                cancellationToken);
        }
        catch (ClaudeCliUnavailableException exception)
        {
            throw ClaudeCliConfiguration.Unavailable(exception);
        }

        var session = new ClaudeAgentSession(
            process,
            threadId,
            sessionId,
            modelId,
            tools,
            handlers,
            NativeAgentContract.Claude.RichContentSupported.For(connection, null),
            richContent,
            opening);
        try
        {
            await session.InitializeAsync(instructions, cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private static List<string> BuildArguments(
        string threadId,
        string? resumeThreadId,
        bool fork,
        string? modelId,
        IReadOnlyList<LlmAgentToolDefinition> tools,
        bool ephemeral,
        string workingDirectory,
        IReadOnlyList<string> writableRoots,
        LlmAgentSandboxMode sandbox,
        bool networkAllowed)
    {
        var arguments = new List<string>
        {
            "--verbose",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--include-partial-messages",
            "--strict-mcp-config",
            "--disable-slash-commands",
            "--tools", ClaudeTools(sandbox, networkAllowed),
            "--permission-mode", PermissionMode(sandbox),
            "--permission-prompts", "none"
        };
        if (sandbox is not LlmAgentSandboxMode.DangerFullAccess)
        {
            arguments.Add("--restricted");
        }
        else
        {
            arguments.Add("--dangerously-skip-permissions");
        }

        string[] additionalRoots =
        [
            .. writableRoots
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(CliInfrastructure.NormalizeWorkingDirectory)
                .Where(root => !string.Equals(root, workingDirectory, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
        ];
        if (additionalRoots.Length > 0)
        {
            arguments.Add("--add-dir");
            arguments.AddRange(additionalRoots);
        }
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            arguments.AddRange(["--model", modelId]);
        }

        if (tools.Count > 0)
        {
            arguments.Add("--allowedTools");
            arguments.Add(string.Join(
                ',',
                tools.Select(tool => McpToolName(tool.Name)).Append("Read")));
        }

        if (resumeThreadId is null)
        {
            arguments.Add($"--session-id={threadId}");
        }
        else
        {
            arguments.Add($"--resume={resumeThreadId}");
            if (fork)
            {
                arguments.Add("--fork-session");
                arguments.Add($"--session-id={threadId}");
            }
        }

        if (ephemeral)
        {
            arguments.Add("--no-session-persistence");
        }

        return arguments;
    }

    private static string ClaudeTools(LlmAgentSandboxMode sandbox, bool networkAllowed) =>
        sandbox switch
        {
            LlmAgentSandboxMode.ReadOnly => "Read",
            LlmAgentSandboxMode.WorkspaceWrite when networkAllowed =>
                "Read,Write,Edit,Bash,WebFetch,WebSearch",
            LlmAgentSandboxMode.WorkspaceWrite =>
                throw new NotSupportedException(
                    "Claude CLI cannot isolate network access while allowing workspace commands."),
            LlmAgentSandboxMode.DangerFullAccess when networkAllowed => "default",
            LlmAgentSandboxMode.DangerFullAccess =>
                throw new NotSupportedException(
                    "Claude CLI cannot disable network access in danger-full-access mode."),
            _ => throw new ArgumentOutOfRangeException(nameof(sandbox))
        };

    private static string PermissionMode(LlmAgentSandboxMode sandbox) => sandbox switch
    {
        LlmAgentSandboxMode.ReadOnly => "plan",
        LlmAgentSandboxMode.WorkspaceWrite => "dontAsk",
        LlmAgentSandboxMode.DangerFullAccess => "bypassPermissions",
        _ => throw new ArgumentOutOfRangeException(nameof(sandbox))
    };

    private static void ValidateOptions(
        string? modelId,
        string workingDirectory,
        IReadOnlyList<LlmAgentToolDefinition> tools,
        LlmAgentSessionHandlers handlers,
        bool modelOptional = false)
    {
        ArgumentNullException.ThrowIfNull(tools);
        NativeAgentContract.ValidateTools(tools);
        ArgumentNullException.ThrowIfNull(handlers);
        if (!modelOptional && string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException("A model id is required.", nameof(modelId));
        }

        _ = CliInfrastructure.NormalizeWorkingDirectory(workingDirectory);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (LlmAgentToolDefinition tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name)
                || !IsValidMcpToolName(tool.Name)
                || tool.InputSchema.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "Every Claude agent tool requires a valid MCP name containing only letters, "
                    + "numbers, underscores, hyphens, or periods, and a JSON object input schema.",
                    nameof(tools));
            }

            if (!names.Add(tool.Name))
            {
                throw new ArgumentException(
                    $"Agent tool names must be unique: '{tool.Name}'.",
                    nameof(tools));
            }
        }

        if (tools.Count > 0 && handlers.ToolHandler is null)
        {
            throw new ArgumentException(
                "Claude agent sessions with tools require a tool handler.",
                nameof(handlers));
        }
    }

    private static bool IsValidMcpToolName(string name) =>
        name.Length is > 0 and <= 128
        && name.All(character =>
            character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_'
            or '-'
            or '.');

    private static string McpToolName(string toolName) => $"mcp__{McpServerName}__{toolName}";

    private sealed record PendingToolCall(string Id, string Name, JsonElement Arguments);

    private sealed record McpResponse(JsonElement Message, LlmAgentToolEvent? ToolEvent);

    private sealed class ClaudeAgentSession : ILlmAgentSession
    {
        private readonly IClaudeCliSessionProcess _process;
        private readonly string? _initialModelId;
        private readonly IReadOnlyList<LlmAgentToolDefinition> _tools;
        private readonly Dictionary<string, LlmAgentToolDefinition> _toolsByName;
        private readonly LlmAgentSessionHandlers _handlers;
        // The agent lane's tool-output support after the connection's overrides, and after the model's too.
        private readonly LlmRichContentSupported _richRoute;
        private readonly LlmRichContentSupported _richContent;
        private readonly SemaphoreSlim _turnGate = new(1, 1);
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly Queue<string> _deferredLines = new();
        private readonly List<PendingToolCall> _pendingToolCalls = [];
        // Sent with the first turn, then cleared once the provider has received it.
        private AgentOpeningContext? _opening;
        private string? _activeTurnId;
        private bool _disposed;

        public ClaudeAgentSession(
            IClaudeCliSessionProcess process,
            string threadId,
            string sessionId,
            string? initialModelId,
            IReadOnlyList<LlmAgentToolDefinition> tools,
            LlmAgentSessionHandlers handlers,
            LlmRichContentSupported richRoute,
            LlmRichContentSupported richContent,
            AgentOpeningContext? opening)
        {
            _process = process;
            _richRoute = richRoute;
            _richContent = richContent;
            _opening = opening;
            ThreadId = threadId;
            SessionId = sessionId;
            _initialModelId = initialModelId;
            _tools = tools;
            _toolsByName = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
            _handlers = handlers;
        }

        public string ThreadId { get; }

        public string SessionId { get; }

        public Task<LlmAgentCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(NativeAgentContract.Claude);
        }

        public Task<LlmRequestAccounting> MeasureContextAsync(LlmAgentContext proposedContext,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(proposedContext);
            return Task.FromResult(new LlmRequestAccounting
            {
                ModelId = null, // The provider owns effective model/run selection; this is not a measurement.
                Diagnostic = "Provider-owned context does not support host replacement accounting."
            });
        }

        public Task<LlmAgentContextStatus> GetContextStatusAsync(
            LlmAgentTurnRequest? prospectiveTurn = null,
            CancellationToken cancellationToken = default) =>
            throw NativeAgentContract.UnsupportedContext(cancellationToken);

        public Task<LlmAgentContext> CaptureContextAsync(
            LlmAgentContextCaptureOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw NativeAgentContract.UnsupportedContext(cancellationToken);

        public Task ReplaceContextAsync(
            LlmAgentContext context,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw NativeAgentContract.UnsupportedContext(cancellationToken);

        public IAsyncEnumerable<LlmAgentEvent> ContinueTurnAsync(
            LlmAgentTurnContinuation continuation,
            CancellationToken cancellationToken = default) =>
            throw NativeAgentContract.UnsupportedContext(cancellationToken);


        public async Task InitializeAsync(
            string? instructions,
            CancellationToken cancellationToken)
        {
            var request = new Dictionary<string, object?>
            {
                ["subtype"] = "initialize",
                ["systemPrompt"] = string.IsNullOrWhiteSpace(instructions)
                    ? null
                    : new[] { instructions },
                ["systemPromptSnapshot"] = false
            };
            if (_tools.Count > 0)
            {
                request["sdkMcpServers"] = new[] { McpServerName };
                request["sdkMcpServerManifests"] = new Dictionary<string, object?>
                {
                    [McpServerName] = new
                    {
                        initializeResult = McpInitializeResult("2025-06-18"),
                        toolsListResult = McpToolsListResult(_tools)
                    }
                };
            }

            await SendControlRequestAsync(request, cancellationToken);
        }

        public async IAsyncEnumerable<LlmAgentEvent> RunTurnAsync(
            LlmAgentTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(request);
            NativeAgentContract.ValidateTurn(request);
            if (request.Input.Count == 0 && request.Attachments.Count == 0)
            {
                throw new ArgumentException(
                    "An agent turn requires at least one input item or attachment.",
                    nameof(request));
            }
            if (request.OutputSchema is not null)
            {
                throw new NotSupportedException(
                    "Claude CLI agent sessions do not support changing output schemas per turn.");
            }
            LlmAttachments.Validate(
                request.Attachments,
                supportsImages: true,
                supportsDocuments: true,
                parameterName: nameof(request));

            await _turnGate.WaitAsync(cancellationToken);
            string turnId = Guid.NewGuid().ToString();
            bool completed = false;
            _activeTurnId = turnId;
            _pendingToolCalls.Clear();
            try
            {
                if (!string.IsNullOrWhiteSpace(request.ModelId)
                    && !string.Equals(request.ModelId, _initialModelId, StringComparison.Ordinal))
                {
                    await SendControlRequestAsync(
                        new { subtype = "set_model", model = request.ModelId },
                        cancellationToken);
                }
                if (request.ReasoningLevel is { } level)
                {
                    LlmReasoningEffort.Validate(level, ClaudeCliLlmClient.ReasoningLevels);
                    await SendControlRequestAsync(
                        new
                        {
                            subtype = "apply_flag_settings",
                            settings = new { effortLevel = LlmReasoningEffort.Word(level) }
                        },
                        cancellationToken);
                }

                yield return new LlmAgentTurnStartedEvent(ThreadId, turnId);
                AgentOpeningContext? opening = _opening;
                if (opening is not null)
                {
                    yield return new LlmAgentWarningEvent(ThreadId, turnId, opening.Notice);
                }
                await WriteJsonLineAsync(
                    new
                    {
                        type = "user",
                        session_id = string.Empty,
                        uuid = turnId,
                        message = new
                        {
                            role = "user",
                            content = opening is null
                                ? SerializeInput(request.Input, request.Attachments)
                                : SerializeInput(
                                    [new LlmAgentTextContent(opening.Text), .. request.Input],
                                    [.. opening.Attachments, .. request.Attachments])
                        },
                        parent_tool_use_id = (string?)null
                    },
                    cancellationToken);
                _opening = null;

                var completedMessages = new HashSet<string>(StringComparer.Ordinal);
                while (true)
                {
                    string line = await ReadRequiredLineAsync(cancellationToken);
                    JsonDocument document;
                    try
                    {
                        document = JsonDocument.Parse(line);
                    }
                    catch (JsonException exception)
                    {
                        throw ProtocolFailure("Claude CLI returned malformed agent JSON.", exception);
                    }

                    using (document)
                    {
                        JsonElement root = document.RootElement;
                        string type = OptionalString(root, "type") ?? string.Empty;
                        switch (type)
                        {
                            case "control_request":
                                {
                                    LlmAgentToolEvent? toolEvent = await HandleControlRequestAsync(
                                        root,
                                        cancellationToken);
                                    if (toolEvent is not null)
                                    {
                                        yield return toolEvent;
                                    }
                                    break;
                                }
                            case "control_response":
                                break;
                            case "stream_event":
                                foreach (LlmAgentEvent streamEvent in MapStreamEvent(root, turnId))
                                {
                                    yield return streamEvent;
                                }
                                break;
                            case "assistant":
                                foreach (LlmAgentEvent assistantEvent in MapAssistantMessage(
                                             root,
                                             turnId,
                                             completedMessages))
                                {
                                    yield return assistantEvent;
                                }
                                break;
                            case "system":
                                if (MapSystemMessage(root, turnId) is { } systemEvent)
                                {
                                    yield return systemEvent;
                                }
                                break;
                            case "result":
                                {
                                    string text = OptionalString(root, "result") ?? string.Empty;
                                    if (text.Length > 0 && completedMessages.Count == 0)
                                    {
                                        yield return new LlmAgentMessageCompletedEvent(
                                            ThreadId,
                                            turnId,
                                            $"result-{turnId}",
                                            text);
                                    }

                                    bool isError = OptionalBoolean(root, "is_error") == true;
                                    string subtype = OptionalString(root, "subtype") ?? string.Empty;
                                    LlmAgentTurnStatus status = subtype.Contains(
                                        "interrupt",
                                        StringComparison.OrdinalIgnoreCase)
                                            ? LlmAgentTurnStatus.Interrupted
                                            : isError
                                                ? LlmAgentTurnStatus.Failed
                                                : LlmAgentTurnStatus.Completed;
                                    completed = true;
                                    yield return new LlmAgentTurnCompletedEvent(
                                        ThreadId,
                                        turnId,
                                        status,
                                        isError ? "Claude CLI reported a turn error." : null);
                                    yield break;
                                }
                        }
                    }
                }
            }
            finally
            {
                if (!completed && !_disposed)
                {
                    await TryInterruptAsync(turnId);
                }
                _activeTurnId = null;
                _turnGate.Release();
            }
        }

        public Task SteerAsync(
            string turnId,
            IReadOnlyList<LlmAgentContent> input,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            throw new NotSupportedException(
                "Claude CLI does not expose mid-turn steering through this adapter.");
        }

        public async Task InterruptAsync(
            string turnId,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!string.Equals(turnId, _activeTurnId, StringComparison.Ordinal))
            {
                throw new ArgumentException("The requested turn is not active.", nameof(turnId));
            }

            await WriteControlRequestWithoutWaitingAsync(
                new { subtype = "interrupt", cancel_queued = false },
                cancellationToken);
        }

        public Task CompactAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            throw new NotSupportedException(
                "Claude CLI does not expose compaction through the supported streaming control protocol.");
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _process.DisposeAsync();
            _turnGate.Dispose();
            _writeLock.Dispose();
        }

        private async Task<JsonElement> SendControlRequestAsync(
            object request,
            CancellationToken cancellationToken)
        {
            string requestId = Guid.NewGuid().ToString("N");
            await WriteJsonLineAsync(
                new { type = "control_request", request_id = requestId, request },
                cancellationToken);
            var deferred = new List<string>();
            try
            {
                while (true)
                {
                    string line = await ReadProcessLineAsync(cancellationToken);
                    JsonDocument document;
                    try
                    {
                        document = JsonDocument.Parse(line);
                    }
                    catch (JsonException exception)
                    {
                        throw ProtocolFailure("Claude CLI returned malformed control JSON.", exception);
                    }

                    using (document)
                    {
                        JsonElement root = document.RootElement;
                        string type = OptionalString(root, "type") ?? string.Empty;
                        if (string.Equals(type, "control_request", StringComparison.Ordinal))
                        {
                            await HandleControlRequestAsync(root, cancellationToken);
                            continue;
                        }
                        if (string.Equals(type, "control_response", StringComparison.Ordinal)
                            && root.TryGetProperty("response", out JsonElement response)
                            && string.Equals(
                                OptionalString(response, "request_id"),
                                requestId,
                                StringComparison.Ordinal))
                        {
                            if (string.Equals(
                                OptionalString(response, "subtype"),
                                "error",
                                StringComparison.Ordinal))
                            {
                                throw ProtocolFailure("Claude CLI rejected an agent control request.");
                            }

                            return response.TryGetProperty("response", out JsonElement payload)
                                ? payload.Clone()
                                : JsonSerializer.SerializeToElement(new { });
                        }

                        deferred.Add(line);
                    }
                }
            }
            finally
            {
                foreach (string line in deferred)
                {
                    _deferredLines.Enqueue(line);
                }
            }
        }

        private async Task WriteControlRequestWithoutWaitingAsync(
            object request,
            CancellationToken cancellationToken)
        {
            await WriteJsonLineAsync(
                new
                {
                    type = "control_request",
                    request_id = Guid.NewGuid().ToString("N"),
                    request
                },
                cancellationToken);
        }

        private async Task<LlmAgentToolEvent?> HandleControlRequestAsync(
            JsonElement root,
            CancellationToken cancellationToken)
        {
            string requestId = RequiredString(root, "request_id");
            if (!root.TryGetProperty("request", out JsonElement request)
                || request.ValueKind != JsonValueKind.Object)
            {
                await WriteControlErrorAsync(requestId, cancellationToken);
                return null;
            }

            string subtype = OptionalString(request, "subtype") ?? string.Empty;
            if (!string.Equals(subtype, "mcp_message", StringComparison.Ordinal)
                || !string.Equals(
                    OptionalString(request, "server_name"),
                    McpServerName,
                    StringComparison.Ordinal)
                || !request.TryGetProperty("message", out JsonElement message))
            {
                await WriteControlErrorAsync(requestId, cancellationToken);
                return null;
            }

            McpResponse response = await HandleMcpMessageAsync(
                message,
                requestId,
                cancellationToken);
            await WriteJsonLineAsync(
                new
                {
                    type = "control_response",
                    response = new
                    {
                        subtype = "success",
                        request_id = requestId,
                        response = new { mcp_response = response.Message }
                    }
                },
                cancellationToken);
            return response.ToolEvent;
        }

        private async Task<McpResponse> HandleMcpMessageAsync(
            JsonElement message,
            string controlRequestId,
            CancellationToken cancellationToken)
        {
            JsonElement id = message.TryGetProperty("id", out JsonElement messageId)
                ? messageId.Clone()
                : JsonSerializer.SerializeToElement(0);
            string method = OptionalString(message, "method") ?? string.Empty;
            switch (method)
            {
                case "initialize":
                    {
                        string protocolVersion = "2025-06-18";
                        if (message.TryGetProperty("params", out JsonElement initializeParameters))
                        {
                            protocolVersion = OptionalString(initializeParameters, "protocolVersion")
                                ?? protocolVersion;
                        }
                        return new McpResponse(
                            McpResult(id, McpInitializeResult(protocolVersion)),
                            null);
                    }
                case "notifications/initialized":
                case "notifications/cancelled":
                    return new McpResponse(McpResult(id, new { }), null);
                case "ping":
                    return new McpResponse(McpResult(id, new { }), null);
                case "tools/list":
                    return new McpResponse(McpResult(id, McpToolsListResult(_tools)), null);
                case "tools/call":
                    return await HandleMcpToolCallAsync(
                        message,
                        id,
                        controlRequestId,
                        cancellationToken);
                default:
                    return new McpResponse(McpError(id, -32601, "Method not found."), null);
            }
        }

        private async Task<McpResponse> HandleMcpToolCallAsync(
            JsonElement message,
            JsonElement id,
            string controlRequestId,
            CancellationToken cancellationToken)
        {
            if (!message.TryGetProperty("params", out JsonElement parameters)
                || parameters.ValueKind != JsonValueKind.Object)
            {
                return new McpResponse(McpError(id, -32602, "Invalid tool parameters."), null);
            }

            string? name = OptionalString(parameters, "name");
            if (name is null || !_toolsByName.ContainsKey(name))
            {
                return new McpResponse(McpError(id, -32602, "Unknown tool."), null);
            }

            JsonElement arguments = parameters.TryGetProperty("arguments", out JsonElement suppliedArguments)
                && suppliedArguments.ValueKind == JsonValueKind.Object
                    ? suppliedArguments.Clone()
                    : JsonSerializer.SerializeToElement(new { });
            PendingToolCall? pending = TakePendingToolCall(name, arguments);
            string callId = pending?.Id ?? ReadJsonRpcId(id) ?? controlRequestId;
            LlmToolOutput output;
            try
            {
                output = await _handlers.ToolHandler!(
                    new LlmAgentToolCall(
                        ThreadId,
                        _activeTurnId ?? string.Empty,
                        callId,
                        McpServerName,
                        name,
                        arguments),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                output = LlmToolOutput.FromText("The host tool failed.", isError: true);
            }

            // Unsupported parts are omitted by the projection; they never fail the tool or the turn.
            ProjectedToolOutput projected = ToolOutputProjector.Project(output, _richRoute, _richContent);
            var toolEvent = new LlmAgentToolEvent(
                ThreadId,
                _activeTurnId ?? string.Empty,
                callId,
                McpServerName,
                name,
                arguments,
                !output.IsError,
                output.IsError ? "failed" : "completed");
            return new McpResponse(
                McpResult(
                    id,
                    new
                    {
                        content = SerializeToolResult(projected),
                        isError = projected.IsError
                    }),
                toolEvent);
        }

        private PendingToolCall? TakePendingToolCall(string name, JsonElement arguments)
        {
            int index = _pendingToolCalls.FindIndex(candidate =>
                string.Equals(candidate.Name, name, StringComparison.Ordinal)
                && JsonElement.DeepEquals(candidate.Arguments, arguments));
            if (index < 0)
            {
                index = _pendingToolCalls.FindIndex(candidate =>
                    string.Equals(candidate.Name, name, StringComparison.Ordinal));
            }
            if (index < 0)
            {
                return null;
            }

            PendingToolCall pending = _pendingToolCalls[index];
            _pendingToolCalls.RemoveAt(index);
            return pending;
        }

        private IEnumerable<LlmAgentEvent> MapAssistantMessage(
            JsonElement root,
            string turnId,
            HashSet<string> completedMessages)
        {
            if (!root.TryGetProperty("message", out JsonElement message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out JsonElement content)
                || content.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            string messageId = OptionalString(message, "id") ?? $"message-{turnId}";
            var text = new List<string>();
            foreach (JsonElement block in content.EnumerateArray())
            {
                string blockType = OptionalString(block, "type") ?? string.Empty;
                if (string.Equals(blockType, "text", StringComparison.Ordinal))
                {
                    string? value = OptionalString(block, "text");
                    if (!string.IsNullOrEmpty(value))
                    {
                        text.Add(value);
                    }
                    continue;
                }
                if (!string.Equals(blockType, "tool_use", StringComparison.Ordinal))
                {
                    continue;
                }

                string rawName = OptionalString(block, "name") ?? string.Empty;
                string name = NormalizeToolName(rawName);
                string callId = OptionalString(block, "id") ?? Guid.NewGuid().ToString("N");
                JsonElement arguments = block.TryGetProperty("input", out JsonElement input)
                    ? input.Clone()
                    : JsonSerializer.SerializeToElement(new { });
                _pendingToolCalls.Add(new PendingToolCall(callId, name, arguments));
                yield return new LlmAgentToolEvent(
                    ThreadId,
                    turnId,
                    callId,
                    McpServerName,
                    name,
                    arguments,
                    null,
                    "inProgress");
            }

            if (text.Count > 0 && completedMessages.Add(messageId))
            {
                yield return new LlmAgentMessageCompletedEvent(
                    ThreadId,
                    turnId,
                    messageId,
                    string.Concat(text));
            }
        }

        private IEnumerable<LlmAgentEvent> MapStreamEvent(JsonElement root, string turnId)
        {
            if (!root.TryGetProperty("event", out JsonElement streamEvent)
                || !string.Equals(
                    OptionalString(streamEvent, "type"),
                    "content_block_delta",
                    StringComparison.Ordinal)
                || !streamEvent.TryGetProperty("delta", out JsonElement delta))
            {
                yield break;
            }

            string deltaType = OptionalString(delta, "type") ?? string.Empty;
            string itemId = streamEvent.TryGetProperty("index", out JsonElement index)
                ? $"content-{index.GetRawText()}"
                : $"content-{turnId}";
            if (string.Equals(deltaType, "text_delta", StringComparison.Ordinal))
            {
                string value = OptionalString(delta, "text") ?? string.Empty;
                if (value.Length > 0)
                {
                    yield return new LlmAgentTextDeltaEvent(ThreadId, turnId, itemId, value);
                }
            }
            else if (string.Equals(deltaType, "thinking_delta", StringComparison.Ordinal))
            {
                string value = OptionalString(delta, "thinking") ?? string.Empty;
                if (value.Length > 0)
                {
                    yield return new LlmAgentReasoningDeltaEvent(
                        ThreadId,
                        turnId,
                        itemId,
                        value,
                        IsSummary: false);
                }
            }
        }

        private LlmAgentWarningEvent? MapSystemMessage(JsonElement root, string turnId)
        {
            string subtype = OptionalString(root, "subtype") ?? string.Empty;
            if (!subtype.Contains("retry", StringComparison.OrdinalIgnoreCase)
                && !subtype.Contains("warning", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new LlmAgentWarningEvent(
                ThreadId,
                turnId,
                "Claude CLI reported a provider retry or warning.");
        }

        private async Task WriteControlErrorAsync(
            string requestId,
            CancellationToken cancellationToken)
        {
            await WriteJsonLineAsync(
                new
                {
                    type = "control_response",
                    response = new
                    {
                        subtype = "error",
                        request_id = requestId,
                        error = "Unsupported control request."
                    }
                },
                cancellationToken);
        }

        private async Task WriteJsonLineAsync(object value, CancellationToken cancellationToken)
        {
            string line = JsonSerializer.Serialize(value);
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await _process.WriteLineAsync(line, cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task<string> ReadRequiredLineAsync(CancellationToken cancellationToken)
        {
            if (_deferredLines.Count > 0)
            {
                return _deferredLines.Dequeue();
            }

            return await ReadProcessLineAsync(cancellationToken);
        }

        private async Task<string> ReadProcessLineAsync(CancellationToken cancellationToken)
        {
            string? line = await _process.ReadLineAsync(cancellationToken);
            if (line is not null)
            {
                return line;
            }

            ClaudeCliProcessResult result = await _process.WaitForExitAsync(cancellationToken);
            throw new LlmProviderException(
                $"Claude CLI agent process exited with code {result.ExitCode}.",
                LlmTransport.AnthropicClaudeCli,
                providerCode: $"claude_cli_exit_{result.ExitCode}");
        }

        private async Task TryInterruptAsync(string turnId)
        {
            if (!string.Equals(turnId, _activeTurnId, StringComparison.Ordinal))
            {
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await WriteControlRequestWithoutWaitingAsync(
                    new { subtype = "interrupt", cancel_queued = false },
                    timeout.Token);
            }
            catch (Exception exception) when (exception is
                OperationCanceledException or InvalidOperationException or ObjectDisposedException)
            {
                // Best effort when enumeration ends before the CLI emits a terminal result.
            }
        }

        private static IReadOnlyList<object> SerializeInput(
            IReadOnlyList<LlmAgentContent> content,
            IReadOnlyList<LlmAttachment> attachments) =>
        [
            .. content.Select(item => item switch
            {
                LlmAgentTextContent text => (object)new { type = "text", text = text.Text },
                LlmAgentImageContent image => SerializeImageInput(image.ImageUrl),
                LlmAgentAudioContent => throw new NotSupportedException(
                    "Claude CLI agent input does not support audio content."),
                _ => throw new ArgumentException(
                    $"Unsupported agent content type '{item.GetType().Name}'.",
                    nameof(content))
            }),
            .. attachments.Select(attachment =>
                (object)AnthropicAttachments.BuildContentBlock(attachment))
        ];

        private static object SerializeImageInput(string imageUrl)
        {
            if (TryParseDataUrl(imageUrl, out string mediaType, out string data))
            {
                return new
                {
                    type = "image",
                    source = new { type = "base64", media_type = mediaType, data }
                };
            }

            return new
            {
                type = "image",
                source = new { type = "url", url = imageUrl }
            };
        }

        private static IReadOnlyList<object> SerializeToolResult(ProjectedToolOutput projected) =>
        [
            .. projected.Parts.Select(part => part switch
            {
                SentText text => (object)new { type = "text", text = text.Text },
                SentImage image => new Dictionary<string, object?>
                {
                    ["type"] = "image",
                    ["mimeType"] = image.MediaType,
                    ["data"] = Convert.ToBase64String(image.Data.Span)
                },
                SentResourceLink { MediaType: null } link => new Dictionary<string, object?>
                {
                    ["type"] = "resource_link",
                    ["uri"] = link.Uri,
                    ["name"] = link.Name ?? link.Uri
                },
                SentResourceLink link => new Dictionary<string, object?>
                {
                    ["type"] = "resource_link",
                    ["uri"] = link.Uri,
                    ["name"] = link.Name ?? link.Uri,
                    ["mimeType"] = link.MediaType
                },
                SentEmbeddedText embedded => new Dictionary<string, object?>
                {
                    ["type"] = "resource",
                    ["resource"] = new Dictionary<string, object?>
                    {
                        ["uri"] = embedded.Uri,
                        ["mimeType"] = embedded.MediaType ?? "text/plain",
                        ["text"] = embedded.Text
                    }
                },
                _ => throw new InvalidOperationException("The Claude MCP route does not carry this part natively.")
            })
        ];

        private static bool TryParseDataUrl(
            string value,
            out string mediaType,
            out string data)
        {
            mediaType = string.Empty;
            data = string.Empty;
            if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int separator = value.IndexOf(',');
            if (separator <= 5)
            {
                return false;
            }

            string metadata = value[5..separator];
            if (!metadata.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            mediaType = metadata[..^7];
            data = value[(separator + 1)..];
            return mediaType.Length > 0 && data.Length > 0;
        }

        private static object McpInitializeResult(string protocolVersion) => new
        {
            protocolVersion,
            capabilities = new { tools = new { listChanged = false } },
            serverInfo = new
            {
                name = McpServerName,
                version = typeof(ClaudeAgentClient).Assembly.GetName().Version?.ToString() ?? "0.0.0"
            }
        };

        private static object McpToolsListResult(IReadOnlyList<LlmAgentToolDefinition> tools) => new
        {
            tools = tools.Select(tool => new Dictionary<string, object?>
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema,
                ["_meta"] = new Dictionary<string, object?>
                {
                    ["anthropic/alwaysLoad"] = !tool.DeferLoading
                }
            }).ToArray()
        };

        private static JsonElement McpResult(JsonElement id, object result) =>
            JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id, result });

        private static JsonElement McpError(JsonElement id, int code, string message) =>
            JsonSerializer.SerializeToElement(new
            {
                jsonrpc = "2.0",
                id,
                error = new { code, message }
            });

        private static string NormalizeToolName(string name)
        {
            string prefix = $"mcp__{McpServerName}__";
            return name.StartsWith(prefix, StringComparison.Ordinal)
                ? name[prefix.Length..]
                : name;
        }

        private static string? ReadJsonRpcId(JsonElement id) => id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null
        };

        private static string RequiredString(JsonElement element, string propertyName) =>
            OptionalString(element, propertyName)
            ?? throw ProtocolFailure($"Claude CLI agent message omitted '{propertyName}'.");

        private static string? OptionalString(JsonElement element, string propertyName) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static bool? OptionalBoolean(JsonElement element, string propertyName) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement value)
                ? value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null
                }
                : null;

        private static LlmProviderException ProtocolFailure(
            string message,
            Exception? exception = null) => new(
                message,
                LlmTransport.AnthropicClaudeCli,
                providerCode: "claude_cli_agent_protocol",
                innerException: exception);
    }
}
