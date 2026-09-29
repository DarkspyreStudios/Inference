using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Session-oriented agent adapter over the official Claude CLI SDK protocol.</summary>
internal sealed class ClaudeAgentClient(IClaudeCliSessionProcessFactory processFactory)
    : ILlmAgentResumeClient, ILlmAgentForkClient
{
    private const string McpServerName = "darkspyre-inference-connect";

    public LlmTransport Transport => LlmTransport.AnthropicClaudeCli;

    public async Task<ILlmAgentSession> StartSessionAsync(
        string connectionId,
        LlmConnection connection,
        LlmAgentSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        LlmAgentNativeRuntimeOptions runtime = NativeAgentContract.ValidateStart(options, NativeAgentContract.Claude);
        LlmAgentHostRuntimePolicy policy = NativeAgentContract.HostPolicy(runtime);
        NativeAgentContract.ValidateConnection(connection, NativeAgentContract.Claude, supportsAgentToolUse: true,
            options.ModelId, options.Tools, options.ToolHandler);
        ClaudeCliConfiguration.ValidateConnection(connection);
        ValidateOptions(options.ModelId, runtime.WorkingDirectory, options.Tools, options.ToolHandler);
        AgentOpeningContext? opening = AgentOpeningContext.For(options, carriesAttachments: true);
        string threadId = Guid.NewGuid().ToString();
        return await CreateSessionAsync(
            threadId,
            threadId,
            resumeThreadId: null,
            fork: false,
            ClaudeCliConfiguration.ResolveExecutable(connection.Endpoint),
            options.ModelId,
            runtime.WorkingDirectory,
            options.Instructions,
            policy.WritableRoots,
            policy.Sandbox,
            policy.NetworkAllowed,
            options.Tools,
            options.ToolHandler,
            runtime.Ephemeral,
            connection,
            NativeAgentContract.Claude.RichContentSupported.For(connection, options.ModelId),
            opening,
            policy.AllowRuntimeBackgroundWork,
            options.ResourceOutputRetentionBytes,
            cancellationToken);
    }

    public async Task<ILlmAgentSession> ResumeSessionAsync(
        string connectionId,
        LlmConnection connection,
        string threadId,
        LlmAgentResumeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ClaudeCliConfiguration.ValidateConnection(connection);
        CliInfrastructure.ValidateThreadId(threadId);
        options ??= new LlmAgentResumeOptions();
        NativeAgentContract.ValidateConnection(connection, NativeAgentContract.Claude, supportsAgentToolUse: true,
            options.ModelId, options.Tools, options.ToolHandler);
        LlmAgentNativeRuntimeOptions runtime = NativeAgentContract.ResumeRuntime(options, NativeAgentContract.Claude);
        LlmAgentHostRuntimePolicy policy = NativeAgentContract.HostPolicy(runtime);
        ValidateOptions(options.ModelId, runtime.WorkingDirectory, options.Tools, options.ToolHandler, modelOptional: true);
        return await CreateSessionAsync(
            threadId,
            threadId,
            threadId,
            fork: false,
            ClaudeCliConfiguration.ResolveExecutable(connection.Endpoint),
            options.ModelId,
            runtime.WorkingDirectory,
            options.Instructions,
            policy.WritableRoots,
            policy.Sandbox,
            policy.NetworkAllowed,
            options.Tools,
            options.ToolHandler,
            runtime.Ephemeral,
            connection,
            NativeAgentContract.Claude.RichContentSupported.For(connection, options.ModelId),
            opening: null,
            policy.AllowRuntimeBackgroundWork,
            options.ResourceOutputRetentionBytes,
            cancellationToken);
    }

    public async Task<ILlmAgentSession> ForkSessionAsync(
        string connectionId,
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
        NativeAgentContract.ValidateConnection(connection, NativeAgentContract.Claude, supportsAgentToolUse: true,
            options.ModelId, options.Tools, options.ToolHandler);
        LlmAgentNativeRuntimeOptions runtime = NativeAgentContract.ResumeRuntime(options, NativeAgentContract.Claude);
        LlmAgentHostRuntimePolicy policy = NativeAgentContract.HostPolicy(runtime);
        ValidateOptions(options.ModelId, runtime.WorkingDirectory, options.Tools, options.ToolHandler, modelOptional: true);
        string forkedThreadId = Guid.NewGuid().ToString();
        return await CreateSessionAsync(
            forkedThreadId,
            threadId,
            threadId,
            fork: true,
            ClaudeCliConfiguration.ResolveExecutable(connection.Endpoint),
            options.ModelId,
            runtime.WorkingDirectory,
            options.Instructions,
            policy.WritableRoots,
            policy.Sandbox,
            policy.NetworkAllowed,
            options.Tools,
            options.ToolHandler,
            runtime.Ephemeral,
            connection,
            NativeAgentContract.Claude.RichContentSupported.For(connection, options.ModelId),
            opening: null,
            policy.AllowRuntimeBackgroundWork,
            options.ResourceOutputRetentionBytes,
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
        LlmAgentToolHandler? toolHandler,
        bool ephemeral,
        LlmConnection connection,
        LlmRichContentSupported richContent,
        AgentOpeningContext? opening,
        bool allowRuntimeBackgroundWork,
        long retentionBytes,
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
            networkAllowed,
            allowRuntimeBackgroundWork);
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
            toolHandler,
            NativeAgentContract.Claude.RichContentSupported.For(connection, null),
            richContent,
            opening,
            retentionBytes);
        try
        {
            session.Start();
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
        bool networkAllowed,
        bool allowRuntimeBackgroundWork)
    {
        string[] background = allowRuntimeBackgroundWork ? BackgroundTools(sandbox) : [];
        string builtIn = ClaudeTools(sandbox, networkAllowed);
        var arguments = new List<string>
        {
            "--verbose",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            // The echo marks where each host turn starts; output with no echo before it is the runtime's own turn.
            "--replay-user-messages",
            "--include-partial-messages",
            "--strict-mcp-config",
            "--disable-slash-commands",
            "--tools", builtIn == "default" || background.Length == 0 ? builtIn : string.Join(',', [builtIn, .. background]),
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
            if (!allowRuntimeBackgroundWork)
            {
                // The default tool set includes both; background work stays off unless the session allows it.
                arguments.AddRange(["--disallowedTools", "Task,Monitor"]);
            }
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

        string[] allowed =
        [
            .. tools.Select(tool => McpToolName(tool.Name)),
            .. tools.Count > 0 ? ["Read"] : Array.Empty<string>(),
            .. background
        ];
        if (allowed.Length > 0)
        {
            arguments.Add("--allowedTools");
            arguments.Add(string.Join(',', allowed));
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

    /// <summary>Subagents in every sandbox; monitors run commands, so not in read-only.</summary>
    private static string[] BackgroundTools(LlmAgentSandboxMode sandbox) =>
        sandbox == LlmAgentSandboxMode.ReadOnly ? ["Task"] : ["Task", "Monitor"];

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
        LlmAgentToolHandler? toolHandler,
        bool modelOptional = false)
    {
        ArgumentNullException.ThrowIfNull(tools);
        NativeAgentContract.ValidateTools(tools);
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

        if (tools.Count > 0 && toolHandler is null)
        {
            throw new ArgumentException(
                "Claude agent sessions with tools require a tool handler.",
                nameof(toolHandler));
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
        private readonly LlmAgentToolHandler? _toolHandler;
        // The agent lane's tool-output support after the connection's overrides, and after the model's too.
        private readonly LlmRichContentSupported _richRoute;
        private readonly LlmRichContentSupported _richContent;
        private readonly SemaphoreSlim _turnGate = new(1, 1);
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly List<PendingToolCall> _pendingToolCalls = [];
        private readonly Dictionary<string, RunningToolCall> _toolCalls = new(StringComparer.Ordinal);
        // Everything below is read for the session's lifetime by one reader; _route guards turn routing.
        private readonly Lock _route = new();
        private readonly Channel<LlmAgentEvent> _sessionEvents = Channel.CreateUnbounded<LlmAgentEvent>(
            new UnboundedChannelOptions { SingleReader = true });
        private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _controlWaiters = new(StringComparer.Ordinal);
        // Host turns written to the CLI and not yet echoed back, in the order written.
        private readonly List<HostTurn> _written = [];
        private readonly ClaudeBackgroundTasks _tasks;
        private readonly CancellationTokenSource _readerStop = new();
        private Task _reader = Task.CompletedTask;
        private HostTurn? _host;
        private ProviderTurn? _provider;
        private Exception? _failure;
        private int _observing;
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
            LlmAgentToolHandler? toolHandler,
            LlmRichContentSupported richRoute,
            LlmRichContentSupported richContent,
            AgentOpeningContext? opening,
            long retentionBytes)
        {
            _process = process;
            _tasks = new ClaudeBackgroundTasks(threadId, retentionBytes, item => _sessionEvents.Writer.TryWrite(item),
                TimeSpan.FromMilliseconds(500));
            _richRoute = richRoute;
            _richContent = richContent;
            _opening = opening;
            ThreadId = threadId;
            SessionId = sessionId;
            _initialModelId = initialModelId;
            _tools = tools;
            _toolsByName = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
            _toolHandler = toolHandler;
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
            NativeAgentContract.UnsupportedContinuation(cancellationToken);


        /// <summary>Starts reading the CLI for the session's lifetime; control requests need it running.</summary>
        public void Start() => _reader = Task.Run(() => ReadAsync(_readerStop.Token));

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

            if (!_turnGate.Wait(0, cancellationToken))
            {
                throw new InvalidOperationException("The session is running or paused.");
            }
            string turnId = Guid.NewGuid().ToString();
            var turn = new HostTurn(turnId);
            bool completed = false;
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
                lock (_route)
                {
                    if (_failure is { } failure) { throw failure; }
                    _written.Add(turn);
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

                // The reader routes this turn's events here once the CLI echoes the message back.
                await foreach (LlmAgentEvent item in turn.Events.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return item;
                    if (item is LlmAgentTurnCompletedEvent)
                    {
                        completed = true;
                        yield break;
                    }
                }
                throw ProtocolFailure("Claude CLI ended the turn without a result.");
            }
            finally
            {
                if (!completed && !_disposed)
                {
                    await AbandonAsync(turn);
                }
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
            lock (_route)
            {
                if (_written.FirstOrDefault(turn => turn.Id == turnId) is { } queued)
                {
                    // Not started yet: the CLI has it queued. Interrupt it the moment it starts.
                    queued.InterruptOnStart = true;
                    return;
                }
                if (_host?.Id != turnId && _provider?.Id != turnId)
                {
                    throw new ArgumentException("The requested turn is not active.", nameof(turnId));
                }
            }

            await WriteControlRequestWithoutWaitingAsync(
                new { subtype = "interrupt", cancel_queued = false },
                cancellationToken);
        }

        public async IAsyncEnumerable<LlmAgentEvent> ObserveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Interlocked.Exchange(ref _observing, 1) == 1)
            {
                throw new InvalidOperationException("The session feed has one reader at a time.");
            }
            try
            {
                await foreach (LlmAgentEvent item in _sessionEvents.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return item;
                }
            }
            finally
            {
                Volatile.Write(ref _observing, 0);
            }
        }

        public Task<IReadOnlyList<LlmAgentResource>> GetResourcesAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_tasks.Snapshot());
        }

        public Task<LlmAgentResourceOutput> ReadResourceOutputAsync(
            string resourceId,
            string? cursor,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
            return _tasks.ReadAsync(resourceId, cursor, cancellationToken);
        }

        public async Task<LlmAgentResourceStopStatus> StopResourceAsync(
            string resourceId,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
            if (!_tasks.TryGet(resourceId, out LlmAgentResource resource))
            {
                return LlmAgentResourceStopStatus.Unknown;
            }
            if (resource.Ended is not null)
            {
                return LlmAgentResourceStopStatus.Exited;
            }
            try
            {
                await SendControlRequestAsync(new { subtype = "stop_task", task_id = resourceId }, cancellationToken);
                // The request is not the exit; the resource reports Stopped when the CLI confirms.
                return LlmAgentResourceStopStatus.Requested;
            }
            catch (LlmProviderException)
            {
                return LlmAgentResourceStopStatus.Failed;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _readerStop.CancelAsync();
            await _process.DisposeAsync();
            try
            {
                await _reader;
            }
            catch (OperationCanceledException)
            {
                // The reader stops with the session.
            }
            await _tasks.DisposeAsync();
            // Stopping the reader cancelled every host tool call; wait for them before releasing the writer.
            Task[] calls;
            lock (_route) { calls = [.. _toolCalls.Values.Select(call => call.Completion)]; }
            await Task.WhenAll(calls);
            _sessionEvents.Writer.TryComplete();
            _readerStop.Dispose();
            _turnGate.Dispose();
            _writeLock.Dispose();
        }

        private async Task<JsonElement> SendControlRequestAsync(
            object request,
            CancellationToken cancellationToken)
        {
            string requestId = Guid.NewGuid().ToString("N");
            var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_route)
            {
                if (_failure is { } failure) { throw failure; }
                _controlWaiters[requestId] = waiter;
            }
            try
            {
                await WriteJsonLineAsync(
                    new { type = "control_request", request_id = requestId, request },
                    cancellationToken);
                return await waiter.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                lock (_route) { _controlWaiters.Remove(requestId); }
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

            if (string.Equals(OptionalString(message, "method"), "tools/call", StringComparison.Ordinal))
            {
                JsonElement callId = message.TryGetProperty("id", out JsonElement suppliedId)
                    ? suppliedId.Clone()
                    : JsonSerializer.SerializeToElement(0);
                if (PrepareToolCall(message, callId, requestId, out LlmAgentToolCall? call) is { } refusal)
                {
                    await WriteMcpResponseAsync(requestId, refusal.Message, cancellationToken);
                    return null;
                }
                // A host tool can run for minutes. The reader keeps routing meanwhile, including the CLI's
                // cancellation of this call when its turn is interrupted.
                StartToolCall(call!, callId, requestId, cancellationToken);
                return null;
            }

            McpResponse response = HandleMcpMessage(message);
            await WriteMcpResponseAsync(requestId, response.Message, cancellationToken);
            return response.ToolEvent;
        }

        private async Task WriteMcpResponseAsync(string requestId, JsonElement message, CancellationToken cancellationToken)
        {
            await WriteJsonLineAsync(
                new
                {
                    type = "control_response",
                    response = new
                    {
                        subtype = "success",
                        request_id = requestId,
                        response = new { mcp_response = message }
                    }
                },
                cancellationToken);
        }

        private McpResponse HandleMcpMessage(JsonElement message)
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
                    return new McpResponse(McpResult(id, new { }), null);
                case "notifications/cancelled":
                    CancelToolCall(message);
                    return new McpResponse(McpResult(id, new { }), null);
                case "ping":
                    return new McpResponse(McpResult(id, new { }), null);
                case "tools/list":
                    return new McpResponse(McpResult(id, McpToolsListResult(_tools)), null);
                default:
                    return new McpResponse(McpError(id, -32601, "Method not found."), null);
            }
        }

        /// <summary>
        /// Checks a tool call on the reader, in order with the tool uses it matches. Returns the refusal, or null
        /// with the call to run.
        /// </summary>
        private McpResponse? PrepareToolCall(
            JsonElement message,
            JsonElement id,
            string controlRequestId,
            out LlmAgentToolCall? call)
        {
            call = null;
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
            call = new LlmAgentToolCall(ThreadId, _activeTurnId ?? string.Empty, callId, McpServerName, name, arguments);
            return null;
        }

        /// <summary>Runs a checked call on its own task, so the reader keeps reading while the host tool works.</summary>
        private void StartToolCall(LlmAgentToolCall call, JsonElement id, string requestId, CancellationToken cancellationToken)
        {
            TurnTarget? origin = CurrentTarget();
            string key = ReadJsonRpcId(id) ?? requestId;
            var running = new RunningToolCall(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            lock (_route)
            {
                _toolCalls[key] = running;
                running.Completion = Task.Run(() => CompleteToolCallAsync(call, id, requestId, key, running, origin), CancellationToken.None);
            }
        }

        private async Task CompleteToolCallAsync(LlmAgentToolCall call, JsonElement id, string requestId, string key,
            RunningToolCall running, TurnTarget? origin)
        {
            McpResponse response;
            try
            {
                response = await RunToolCallAsync(call, id, running.Cancellation.Token);
            }
            catch (OperationCanceledException) when (running.Cancellation.IsCancellationRequested)
            {
                // The CLI cancelled the call, or the session ended. The call's control request is still answered.
                response = new McpResponse(McpResult(id, new
                {
                    content = new[] { new { type = "text", text = "The tool call was cancelled." } },
                    isError = true
                }), null);
            }
            finally
            {
                lock (_route) { _toolCalls.Remove(key); }
                running.Cancellation.Dispose();
            }
            if (response.ToolEvent is { } toolEvent)
            {
                // Emitted before the answer: the CLI continues only after it, so the event precedes the turn's later output.
                EmitFor(origin, toolEvent);
            }
            try
            {
                await WriteMcpResponseAsync(requestId, response.Message, _readerStop.Token);
            }
            catch (Exception failure)
            {
                lock (_route)
                {
                    // A session that has ended has no process to take the answer.
                    if (_disposed || _failure is not null) { return; }
                }
                Fail(ProtocolFailure("Claude CLI did not accept a host tool result.", failure));
            }
        }

        private async Task<McpResponse> RunToolCallAsync(LlmAgentToolCall call, JsonElement id, CancellationToken cancellationToken)
        {
            LlmToolOutput output;
            try
            {
                output = await _toolHandler!(call, cancellationToken);
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
                call.TurnId,
                call.CallId,
                McpServerName,
                call.Name,
                call.Arguments,
                !output.IsError,
                output.IsError ? "failed" : "completed",
                output);
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

        /// <summary>The CLI cancels a call it no longer waits for, as when its turn is interrupted.</summary>
        private void CancelToolCall(JsonElement message)
        {
            if (!message.TryGetProperty("params", out JsonElement parameters)
                || !parameters.TryGetProperty("requestId", out JsonElement requested)
                || ReadJsonRpcId(requested) is not { } key)
            {
                return;
            }
            RunningToolCall? running;
            lock (_route) { _toolCalls.TryGetValue(key, out running); }
            Cancel(running);
        }

        private static void Cancel(RunningToolCall? running)
        {
            try { running?.Cancellation.Cancel(); }
            catch (ObjectDisposedException)
            {
                // The call finished as the cancellation arrived.
            }
        }

        /// <summary>Emits to the turn that made the call. A turn that has ended takes no more events.</summary>
        private void EmitFor(TurnTarget? origin, LlmAgentEvent item)
        {
            lock (_route)
            {
                TurnTarget? running = (TurnTarget?)_host ?? _provider;
                if (origin is null || !ReferenceEquals(running, origin))
                {
                    return;
                }
                if (_host is { } host)
                {
                    host.Events.Writer.TryWrite(item);
                }
                else
                {
                    _sessionEvents.Writer.TryWrite(item);
                }
            }
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

        /// <summary>
        /// Reads the CLI for the session's lifetime. With <c>--replay-user-messages</c> the CLI echoes each host
        /// message when it starts processing it: output after the echo belongs to that host turn, and output with
        /// no echo before it is a turn the runtime started itself.
        /// </summary>
        private async Task ReadAsync(CancellationToken cancellationToken)
        {
            Exception failure;
            try
            {
                while (await _process.ReadLineAsync(cancellationToken) is { } line)
                {
                    await RouteAsync(line, cancellationToken);
                }
                ClaudeCliProcessResult result = await _process.WaitForExitAsync(cancellationToken);
                failure = new LlmProviderException(
                    $"Claude CLI agent process exited with code {result.ExitCode}.",
                    LlmTransport.AnthropicClaudeCli,
                    providerCode: $"claude_cli_exit_{result.ExitCode}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                failure = new ObjectDisposedException(nameof(ClaudeAgentSession));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            Fail(failure);
        }

        private async Task RouteAsync(string line, CancellationToken cancellationToken)
        {
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
                string? parent = OptionalString(root, "parent_tool_use_id");
                switch (OptionalString(root, "type"))
                {
                    case "control_response":
                        CompleteControlRequest(root);
                        break;
                    case "control_request":
                        if (await HandleControlRequestAsync(root, cancellationToken) is { } toolEvent)
                        {
                            Emit(toolEvent);
                        }
                        break;
                    case "user" when OptionalBoolean(root, "isReplay") == true:
                        await StartHostTurnAsync(OptionalString(root, "uuid"));
                        break;
                    case "user" or "assistant" when parent is not null:
                        // A subagent's messages are its own output, not the main turn's.
                        if (root.TryGetProperty("message", out JsonElement subagentMessage))
                        {
                            _tasks.SubagentMessage(parent, subagentMessage);
                        }
                        break;
                    case "user":
                        NoteToolResults(root);
                        break;
                    case "assistant":
                        {
                            TurnTarget target = EnsureTarget();
                            foreach (LlmAgentEvent item in MapAssistantMessage(root, target.Id, target.Completed))
                            {
                                Emit(item);
                            }
                            break;
                        }
                    case "stream_event" when parent is null:
                        {
                            TurnTarget target = EnsureTarget();
                            foreach (LlmAgentEvent item in MapStreamEvent(root, target.Id))
                            {
                                Emit(item);
                            }
                            break;
                        }
                    case "system":
                        _tasks.Handle(root);
                        if (MapSystemMessage(root, CurrentTarget()?.Id ?? string.Empty) is { } warning)
                        {
                            Emit(warning);
                        }
                        break;
                    case "result":
                        EndTurn(root);
                        break;
                }
            }
        }

        private void CompleteControlRequest(JsonElement root)
        {
            if (!root.TryGetProperty("response", out JsonElement response)
                || OptionalString(response, "request_id") is not { } requestId)
            {
                return;
            }
            TaskCompletionSource<JsonElement>? waiter;
            lock (_route) { _controlWaiters.TryGetValue(requestId, out waiter); }
            if (waiter is null)
            {
                return;
            }
            if (string.Equals(OptionalString(response, "subtype"), "error", StringComparison.Ordinal))
            {
                waiter.TrySetException(ProtocolFailure("Claude CLI rejected an agent control request."));
                return;
            }
            waiter.TrySetResult(response.TryGetProperty("response", out JsonElement payload)
                ? payload.Clone()
                : JsonSerializer.SerializeToElement(new { }));
        }

        private async Task StartHostTurnAsync(string? turnId)
        {
            HostTurn? turn;
            lock (_route)
            {
                turn = _written.FirstOrDefault(candidate => candidate.Id == turnId);
                if (turn is null)
                {
                    return;
                }
                _written.Remove(turn);
                _host = turn;
                _activeTurnId = turn.Id;
                _pendingToolCalls.Clear();
            }
            if (turn.InterruptOnStart || turn.Abandoned)
            {
                await TryInterruptAsync();
            }
        }

        private void NoteToolResults(JsonElement root)
        {
            if (!root.TryGetProperty("message", out JsonElement message)
                || !message.TryGetProperty("content", out JsonElement content)
                || content.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            foreach (JsonElement block in content.EnumerateArray())
            {
                if (OptionalString(block, "type") == "tool_result" && OptionalString(block, "tool_use_id") is { } toolUseId)
                {
                    string text = block.TryGetProperty("content", out JsonElement value) && value.ValueKind == JsonValueKind.String
                        ? value.GetString() ?? string.Empty
                        : value.ValueKind == JsonValueKind.Array
                            ? string.Join("\n", value.EnumerateArray().Select(item => OptionalString(item, "text")).Where(item => item is not null))
                            : string.Empty;
                    _tasks.ToolResult(toolUseId, text);
                }
            }
        }

        private TurnTarget? CurrentTarget()
        {
            lock (_route) { return (TurnTarget?)_host ?? _provider; }
        }

        /// <summary>The running turn, starting a provider turn when output arrives with no turn running.</summary>
        private TurnTarget EnsureTarget()
        {
            lock (_route)
            {
                TurnTarget? running = (TurnTarget?)_host ?? _provider;
                if (running is not null)
                {
                    return running;
                }
                _provider = new ProviderTurn(Guid.NewGuid().ToString());
                _activeTurnId = _provider.Id;
                _pendingToolCalls.Clear();
                _sessionEvents.Writer.TryWrite(new LlmAgentProviderTurnStartedEvent(ThreadId, _provider.Id, _tasks.TakeLastReported()));
                return _provider;
            }
        }

        private void Emit(LlmAgentEvent item)
        {
            lock (_route)
            {
                if (_host is { } host)
                {
                    host.Events.Writer.TryWrite(item);
                }
                else
                {
                    _sessionEvents.Writer.TryWrite(item);
                }
            }
        }

        private void EndTurn(JsonElement root)
        {
            TurnTarget? target = CurrentTarget();
            if (target is null)
            {
                return;
            }
            string text = OptionalString(root, "result") ?? string.Empty;
            if (text.Length > 0 && target.Completed.Count == 0)
            {
                Emit(new LlmAgentMessageCompletedEvent(ThreadId, target.Id, $"result-{target.Id}", text));
            }
            bool isError = OptionalBoolean(root, "is_error") == true;
            string subtype = OptionalString(root, "subtype") ?? string.Empty;
            LlmAgentCompletionReason reason = subtype.Contains("interrupt", StringComparison.OrdinalIgnoreCase)
                ? LlmAgentCompletionReason.Interruption
                : isError ? LlmAgentCompletionReason.ProviderFailure : LlmAgentCompletionReason.Completed;
            Emit(new LlmAgentTurnCompletedEvent(ThreadId, target.Id, reason, isError ? "Claude CLI reported a turn error." : null));
            lock (_route)
            {
                if (target is HostTurn host)
                {
                    host.Events.Writer.TryComplete();
                    _host = null;
                }
                else
                {
                    _provider = null;
                }
                _activeTurnId = null;
            }
        }

        /// <summary>The CLI's output ended: every waiting caller fails, and unreported work ends as Unknown.</summary>
        private void Fail(Exception failure)
        {
            HostTurn[] turns;
            TaskCompletionSource<JsonElement>[] waiters;
            RunningToolCall[] calls;
            lock (_route)
            {
                _failure = failure;
                turns = [.. _written, .. _host is null ? [] : new[] { _host }];
                waiters = [.. _controlWaiters.Values];
                calls = [.. _toolCalls.Values];
                _written.Clear();
                _host = null;
                _provider = null;
            }
            foreach (HostTurn turn in turns) { turn.Events.Writer.TryComplete(failure); }
            foreach (TaskCompletionSource<JsonElement> waiter in waiters) { waiter.TrySetException(failure); }
            // No CLI remains to take a host tool's result.
            foreach (RunningToolCall call in calls) { Cancel(call); }
            _tasks.EndAll();
            _sessionEvents.Writer.TryComplete();
        }

        /// <summary>The consumer stopped reading a turn: interrupt it now, or when the CLI starts it.</summary>
        private async Task AbandonAsync(HostTurn turn)
        {
            bool running;
            lock (_route)
            {
                turn.Abandoned = true;
                running = _host == turn;
            }
            if (running)
            {
                await TryInterruptAsync();
            }
        }

        private async Task TryInterruptAsync()
        {
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

        /// <summary>A turn that receives routed events: a host turn, or one the runtime started.</summary>
        private abstract class TurnTarget(string id)
        {
            public string Id { get; } = id;

            public HashSet<string> Completed { get; } = new(StringComparer.Ordinal);
        }

        private sealed class HostTurn(string id) : TurnTarget(id)
        {
            public Channel<LlmAgentEvent> Events { get; } = Channel.CreateUnbounded<LlmAgentEvent>(
                new UnboundedChannelOptions { SingleReader = true });

            public bool InterruptOnStart { get; set; }

            public bool Abandoned { get; set; }
        }

        private sealed class ProviderTurn(string id) : TurnTarget(id);

        /// <summary>A host tool call in flight, keyed by the CLI's JSON-RPC request ID so a cancellation can find it.</summary>
        private sealed class RunningToolCall(CancellationTokenSource cancellation)
        {
            public CancellationTokenSource Cancellation { get; } = cancellation;

            public Task Completion { get; set; } = Task.CompletedTask;
        }

        private static IReadOnlyList<object> SerializeInput(
            IReadOnlyList<LlmAgentContent> content,
            IReadOnlyList<LlmAttachment> attachments) =>
        [
            .. content.Select(item => item switch
            {
                LlmAgentTextContent text => (object)new { type = "text", text = text.Text },
                LlmAgentImageContent image => SerializeImageInput(image.ImageUrl),
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
                ["description"] = tool.Description ?? string.Empty,
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
