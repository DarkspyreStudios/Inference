using Darkspyre.Inference.Common;
using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
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
        options = ClientTools.Snapshot(options);
        LlmAgentNativeRuntimeOptions runtime = NativeAgentContract.ValidateStart(options, NativeAgentContract.Claude, supportsResources: true);
        LlmAgentHostRuntimePolicy policy = NativeAgentContract.HostPolicy(runtime);
        NativeAgentContract.ValidateConnection(connection, NativeAgentContract.Claude, supportsAgentToolUse: true,
            options.ModelId, options.Tools, options.ToolHandler);
        ClaudeCliConfiguration.ValidateConnection(connection);
        ValidateOptions(options.ModelId, runtime.WorkingDirectory, ClientTools.Functions(options.Tools), options.ToolHandler);
        (LlmAgentContext? Context, int Omitted) opening = AgentOpeningContext.Validate(options, supportsResources: true);
        if (options.CallerServices?.Resources is null && opening.Context is { } context)
            _ = AgentOpeningContext.From(context, opening.Omitted, carriesAttachments: true);
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
            ClientTools.Functions(options.Tools),
            options.ToolHandler,
            runtime.Ephemeral,
            connection,
            NativeAgentContract.Claude.RichContentSupported.For(connection, options.ModelId),
            opening,
            options.CallerServices?.Resources,
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
        options = ClientTools.Snapshot(options ?? new LlmAgentResumeOptions());
        NativeAgentContract.ValidateConnection(connection, NativeAgentContract.Claude, supportsAgentToolUse: true,
            options.ModelId, options.Tools, options.ToolHandler);
        LlmAgentNativeRuntimeOptions runtime = NativeAgentContract.ResumeRuntime(options, NativeAgentContract.Claude, supportsResources: true);
        LlmAgentHostRuntimePolicy policy = NativeAgentContract.HostPolicy(runtime);
        ValidateOptions(options.ModelId, runtime.WorkingDirectory, ClientTools.Functions(options.Tools), options.ToolHandler, modelOptional: true);
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
            ClientTools.Functions(options.Tools),
            options.ToolHandler,
            runtime.Ephemeral,
            connection,
            NativeAgentContract.Claude.RichContentSupported.For(connection, options.ModelId),
            opening: (null, 0),
            options.CallerServices?.Resources,
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

        options = ClientTools.Snapshot(options ?? new LlmAgentResumeOptions());
        NativeAgentContract.ValidateConnection(connection, NativeAgentContract.Claude, supportsAgentToolUse: true,
            options.ModelId, options.Tools, options.ToolHandler);
        LlmAgentNativeRuntimeOptions runtime = NativeAgentContract.ResumeRuntime(options, NativeAgentContract.Claude, supportsResources: true);
        LlmAgentHostRuntimePolicy policy = NativeAgentContract.HostPolicy(runtime);
        ValidateOptions(options.ModelId, runtime.WorkingDirectory, ClientTools.Functions(options.Tools), options.ToolHandler, modelOptional: true);
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
            ClientTools.Functions(options.Tools),
            options.ToolHandler,
            runtime.Ephemeral,
            connection,
            NativeAgentContract.Claude.RichContentSupported.For(connection, options.ModelId),
            opening: (null, 0),
            options.CallerServices?.Resources,
            policy.AllowRuntimeBackgroundWork,
            options.ResourceOutputRetentionBytes,
            cancellationToken);
    }

    private static async Task<InferenceResourceSelectionVersion?> ReadResourceScopeAsync(
        IInferenceResourceAccess? access, LlmAgentContext? context, CancellationToken token)
    {
        if (access is null)
        {
            if (context is not null) ManagedAgentContext.ValidateRuntimeResources(context);
            return null;
        }
        InferenceResourceSelection selection = await LlmResourcePreparation.ReadSelectionAsync(access, static () => { }, token);
        context ??= new()
        {
            ResourceSelectionVersion = new(selection.WorkspaceId, selection.SessionId, selection.Revision),
            SelectedResources = selection.SelectedResources
        };
        await LlmResourcePreparation.ValidateContextAsync(context, selection, access, static () => { }, token);
        LlmAttachments.ValidateReferences(new() { ModelId = string.Empty, Messages = context.Entries.Select(entry => entry.Message).ToArray() },
            supportsResources: true);
        foreach (LlmAgentContextEntry entry in context.Entries)
            LlmAttachments.Validate(entry.Message.Attachments, true, true, nameof(context), supportsReferences: true);
        return new(selection.WorkspaceId, selection.SessionId, selection.Revision);
    }

    [SuppressMessage("Design", "CA1031", Justification = "Failed opening preserves both the original failure and a genuine retirement failure.")]
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
        (LlmAgentContext? Context, int Omitted) opening,
        IInferenceResourceAccess? resourceAccess,
        bool allowRuntimeBackgroundWork,
        long retentionBytes,
        CancellationToken cancellationToken)
    {
        string normalizedDirectory = CliInfrastructure.NormalizeWorkingDirectory(workingDirectory);
        LlmProviderCapabilities capabilities = ClaudeCliLlmClient.ResolveCapabilities(connection, modelId);
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
            allowRuntimeBackgroundWork,
            (capabilities.SupportsReasoningText != InferenceSupport.Unsupported));
        InferenceResourceSelectionVersion? resourceScope = await ReadResourceScopeAsync(resourceAccess, opening.Context, cancellationToken);
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
            resourceAccess,
            resourceScope,
            retentionBytes,
            connection);
        try
        {
            session.Start();
            await session.InitializeAsync(instructions, (capabilities.SupportsReasoningText != InferenceSupport.Unsupported), cancellationToken);
            return session;
        }
        catch (Exception original)
        {
            try { await session.DisposeAsync(); }
            catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
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
        bool allowRuntimeBackgroundWork,
        bool requestReasoningText)
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
            "--permission-prompts", "none",
            "--settings", new JsonObject { ["showThinkingSummaries"] = requestReasoningText }.ToJsonString()
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
        LlmAgentSandboxMode.ReadOnly => "dontAsk",
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
        // The last model acknowledged by the CLI; _route guards reads and changes.
        private string? _selectedModelId;
        private LlmConnection _connection;
        private readonly HashSet<string> _accountedCalls = new(StringComparer.Ordinal);
        private readonly HashSet<string> _completedResults = new(StringComparer.Ordinal);
        private readonly IReadOnlyList<LlmAgentToolDefinition> _tools;
        private readonly Dictionary<string, LlmAgentToolDefinition> _toolsByName;
        private readonly LlmAgentToolHandler? _toolHandler;
        // The agent lane's tool-output support after the connection's overrides, and after the model's too.
        private readonly LlmRichContentSupported _richRoute;
        private readonly LlmRichContentSupported _richContent;
        private readonly SemaphoreSlim _turnGate = new(1, 1);
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly List<PendingToolCall> _pendingToolCalls = [];
        private readonly List<RunningToolCall> _toolCalls = [];
        private readonly List<Exception> _toolFailures = [];
        // Everything below is read for the session's lifetime by one reader; _route guards turn routing.
        private readonly Lock _route = new();
        private readonly Channel<LlmAgentEvent> _sessionEvents = Channel.CreateUnbounded<LlmAgentEvent>(
            new UnboundedChannelOptions { SingleReader = true });
        private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _controlWaiters = new(StringComparer.Ordinal);
        // Host turns written to the CLI and not yet echoed back, in the order written.
        private readonly List<HostTurn> _written = [];
        private readonly ClaudeBackgroundTasks _tasks;
        private readonly CancellationTokenSource _readerStop = new();
        private readonly LlmOperationLifetime _lifetime = new();
        private Task _reader = Task.CompletedTask;
        private HostTurn? _host;
        private ProviderTurn? _provider;
        private Exception? _failure;
        private int _observing;
        // Sent with the first turn, then cleared once the provider has received it.
        private LlmAgentContext? _openingContext;
        private readonly int _openingOmitted;
        private IInferenceResourceAccess? _resourceAccess;
        private readonly InferenceResourceSelectionVersion? _resourceScope;
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
            (LlmAgentContext? Context, int Omitted) opening,
            IInferenceResourceAccess? resourceAccess,
            InferenceResourceSelectionVersion? resourceScope,
            long retentionBytes,
            LlmConnection connection)
        {
            _process = process;
            _tasks = new ClaudeBackgroundTasks(threadId, retentionBytes, item => _sessionEvents.Writer.TryWrite(item),
                TimeSpan.FromMilliseconds(500));
            _richRoute = richRoute;
            _richContent = richContent;
            (_openingContext, _openingOmitted) = opening;
            _resourceAccess = resourceAccess;
            _resourceScope = resourceScope;
            ThreadId = threadId;
            SessionId = sessionId;
            _selectedModelId = initialModelId;
            _connection = connection;
            _tools = tools;
            _toolsByName = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
            _toolHandler = toolHandler;
        }

        public string ThreadId { get; }

        public string SessionId { get; }

        public Task<LlmAgentCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
            return Task.FromResult(NativeAgentContract.Claude);
        }

        public Task<LlmRequestAccounting> MeasureContextAsync(LlmAgentContext proposedContext,
            CancellationToken cancellationToken = default)
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
            ArgumentNullException.ThrowIfNull(proposedContext);
            return Task.FromResult(new LlmRequestAccounting
            {
                ModelId = null, // The provider owns effective model/run selection; this is not a measurement.
                Diagnostic = "Provider-owned context does not support host replacement accounting."
            });
        }

        public Task<LlmAgentContextStatus> GetContextStatusAsync(
            LlmAgentTurnRequest? prospectiveTurn = null,
            CancellationToken cancellationToken = default)
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
            throw NativeAgentContract.UnsupportedContext(operation.Token);
        }

        public Task<LlmAgentContext> CaptureContextAsync(
            LlmAgentContextCaptureOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
            throw NativeAgentContract.UnsupportedContext(operation.Token);
        }

        public Task<LlmAgentContext> CompactWithProviderAsync(
            LlmProviderCompactionRequest request, CancellationToken cancellationToken = default)
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
            throw NativeAgentContract.UnsupportedContext(operation.Token);
        }

        public Task ReplaceContextAsync(
            LlmAgentContext context,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
            throw NativeAgentContract.UnsupportedContext(operation.Token);
        }

        public IAsyncEnumerable<LlmAgentEvent> ContinueTurnAsync(
            LlmAgentTurnContinuation continuation,
            CancellationToken cancellationToken = default)
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
            return NativeAgentContract.UnsupportedContinuation(operation.Token);
        }


        /// <summary>Starts reading the CLI for the session's lifetime; control requests need it running.</summary>
        public void Start() => _reader = Task.Run(() => ReadAsync(_readerStop.Token));

        public async Task InitializeAsync(
            string? instructions,
            bool requestReasoningText,
            CancellationToken cancellationToken)
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
            cancellationToken = operation.Token;
            var request = new JsonObject
            {
                ["subtype"] = "initialize",
                ["systemPrompt"] = string.IsNullOrWhiteSpace(instructions)
                    ? null
                    : new JsonArray(instructions),
                ["systemPromptSnapshot"] = false
            };
            if (_tools.Count > 0)
            {
                request["sdkMcpServers"] = new JsonArray(McpServerName);
                request["sdkMcpServerManifests"] = new JsonObject
                {
                    [McpServerName] = new JsonObject
                    {
                        ["initializeResult"] = McpInitializeResult("2025-06-18"),
                        ["toolsListResult"] = McpToolsListResult(_tools)
                    }
                };
            }

            JsonElement initialization = await SendControlRequestAsync(request, cancellationToken);
            if (initialization.ValueKind == JsonValueKind.Object && initialization.TryGetProperty("models", out _))
                _connection = _connection with { Catalog = ClaudeCliModelCatalog.Project(initialization) };
            await SetThinkingDisplayAsync(requestReasoningText, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public IAsyncEnumerable<LlmAgentEvent> RunTurnAsync(
            LlmAgentTurnRequest request, CancellationToken cancellationToken = default) =>
            _lifetime.StreamAsync(token => ProduceTurnAsync(request, token), cancellationToken);

        private async IAsyncEnumerable<LlmAgentEvent> ProduceTurnAsync(
            LlmAgentTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            request = ClientTools.Snapshot(request);
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
                parameterName: nameof(request),
                supportsReferences: _resourceAccess is not null);
            _ = SerializeInput(request.Input, []);
            if (!_turnGate.Wait(0, cancellationToken))
            {
                throw new InvalidOperationException("The session is running or paused.");
            }
            HostTurn? turn = null;
            bool completed = false;
            try
            {
                string? selectedModelId;
                lock (_route) { selectedModelId = _selectedModelId; }
                LlmProviderCapabilities capabilities = ClaudeCliLlmClient.ResolveCapabilities(
                    _connection, request.ModelId ?? selectedModelId);
                LlmReasoningLevel? nativeEffort = ClaudeCliLlmClient.MapReasoning(
                    _connection, request.ModelId ?? selectedModelId, request.ReasoningLevel);
                (JsonArray input, string? openingNotice) = await PrepareInputAsync(request, selectedModelId, cancellationToken);
                var settings = new JsonObject { ["showThinkingSummaries"] = (capabilities.SupportsReasoningText != InferenceSupport.Unsupported) };
                if (nativeEffort is { } level)
                {
                    settings["effortLevel"] = LlmReasoningEffort.Word(level);
                }
                string turnId = Guid.NewGuid().ToString();
                turn = new HostTurn(turnId, new ClaudeStreamUsage((capabilities.SupportsUsageDeltas != InferenceSupport.Unsupported), _accountedCalls), (capabilities.SupportsReasoningText != InferenceSupport.Unsupported));
                if (!string.IsNullOrWhiteSpace(request.ModelId)
                    && !string.Equals(request.ModelId, selectedModelId, StringComparison.Ordinal))
                {
                    await SendControlRequestAsync(
                        new JsonObject { ["subtype"] = "set_model", ["model"] = request.ModelId },
                        cancellationToken);
                    lock (_route) { _selectedModelId = request.ModelId; }
                }
                await SendControlRequestAsync(
                    new JsonObject { ["subtype"] = "apply_flag_settings", ["settings"] = settings },
                    cancellationToken);
                await SetThinkingDisplayAsync((capabilities.SupportsReasoningText != InferenceSupport.Unsupported), cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                yield return new LlmAgentTurnStartedEvent(ThreadId, turnId);
                cancellationToken.ThrowIfCancellationRequested();
                if (openingNotice is not null)
                {
                    yield return new LlmAgentWarningEvent(ThreadId, turnId, openingNotice);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                lock (_route)
                {
                    if (_failure is { } failure) { throw failure; }
                    _written.Add(turn);
                }
                await WriteJsonLineAsync(
                    new JsonObject
                    {
                        ["type"] = "user",
                        ["session_id"] = string.Empty,
                        ["uuid"] = turnId,
                        ["message"] = new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = input
                        },
                        ["parent_tool_use_id"] = null
                    },
                    cancellationToken);
                _openingContext = null;

                // The reader routes this turn's events here once the CLI echoes the message back.
                await foreach (LlmAgentEvent item in turn.Events.Reader.ReadAllAsync(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return item;
                    cancellationToken.ThrowIfCancellationRequested();
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
                if (turn is not null && !completed && !_disposed)
                {
                    await AbandonAsync(turn);
                }
                _turnGate.Release();
            }
        }

        private async Task<(JsonArray Content, string? Notice)> PrepareInputAsync(LlmAgentTurnRequest turn, string? selectedModelId, CancellationToken token)
        {
            LlmAgentContext? openingContext = _openingContext;
            var request = new LlmChatRequest
            {
                ModelId = turn.ModelId ?? selectedModelId ?? string.Empty,
                Resources = _resourceAccess,
                Messages = [.. openingContext?.Entries.Select(entry => entry.Message) ?? [],
                    new("user", string.Empty) { Attachments = turn.Attachments }]
            };
            LlmAttachments.ValidateReferences(request, supportsResources: _resourceAccess is not null);
            (JsonArray Content, string? Notice) Project(LlmChatRequest prepared)
            {
                AgentOpeningContext? opening = openingContext is null ? null : AgentOpeningContext.From(openingContext with
                {
                    Entries = openingContext.Entries.Select((entry, index) => entry with { Message = prepared.Messages[index] }).ToArray()
                }, _openingOmitted, carriesAttachments: true);
                return (opening is null ? SerializeInput(turn.Input, prepared.Messages[^1].Attachments)
                    : SerializeInput([new LlmAgentTextContent(opening.Text), .. turn.Input],
                        [.. opening.Attachments, .. prepared.Messages[^1].Attachments]), opening?.Notice);
            }
            if (_resourceAccess is not { } access) return Project(request);
            try
            {
                LlmResourcePreparation.ValidateScope(access, _resourceScope!, static () => { }, token);
                return await LlmResourcePreparation.ProjectInlineAsync(request, Project, static () => { }, token);
            }
            catch (Exception error) when (LlmResourcePreparation.IsCleanupFailure(error))
            {
                lock (_route) { _toolFailures.Add(error); }
                throw;
            }
        }

        public Task SteerAsync(
            string turnId,
            IReadOnlyList<LlmAgentContent> input,
            CancellationToken cancellationToken = default)
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
            throw new NotSupportedException(
                "Claude CLI does not expose mid-turn steering through this adapter.");
        }

        public async Task InterruptAsync(
            string turnId,
            CancellationToken cancellationToken = default)
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
            cancellationToken = operation.Token;
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
                new JsonObject { ["subtype"] = "interrupt", ["cancel_queued"] = false },
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public IAsyncEnumerable<LlmAgentEvent> ObserveAsync(CancellationToken cancellationToken = default) =>
            _lifetime.StreamAsync(ProduceObservationsAsync, cancellationToken);

        private async IAsyncEnumerable<LlmAgentEvent> ProduceObservationsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _observing, 1) == 1)
            {
                throw new InvalidOperationException("The session feed has one reader at a time.");
            }
            try
            {
                await foreach (LlmAgentEvent item in _sessionEvents.Reader.ReadAllAsync(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return item;
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            finally
            {
                Volatile.Write(ref _observing, 0);
            }
        }

        public Task<IReadOnlyList<LlmAgentResource>> GetResourcesAsync(CancellationToken cancellationToken = default)
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
            return Task.FromResult(_tasks.Snapshot());
        }

        public async Task<LlmAgentResourceOutput> ReadResourceOutputAsync(
            string resourceId,
            string? cursor,
            CancellationToken cancellationToken = default)
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
            LlmAgentResourceOutput output = await _tasks.ReadAsync(resourceId, cursor, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            return output;
        }

        public async Task<LlmAgentResourceStopStatus> StopResourceAsync(
            string resourceId,
            CancellationToken cancellationToken = default)
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
            cancellationToken = operation.Token;
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
                await SendControlRequestAsync(new JsonObject { ["subtype"] = "stop_task", ["task_id"] = resourceId }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                // The request is not the exit; the resource reports Stopped when the CLI confirms.
                return LlmAgentResourceStopStatus.Requested;
            }
            catch (LlmProviderException)
            {
                return LlmAgentResourceStopStatus.Failed;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_route) { _disposed = true; }
            return new(_lifetime.RetireAsync(RetireSessionAsync));
        }

        [SuppressMessage("Design", "CA1031", Justification = "Retirement drains every owned stage and preserves its failures for all disposal callers.")]
        private async Task RetireSessionAsync()
        {
            var failures = new List<Exception>();
            try { await _readerStop.CancelAsync(); }
            catch (Exception error) { failures.Add(error); }
            try { await _process.DisposeAsync(); }
            catch (Exception error) { failures.Add(error); }
            try { await _reader; }
            catch (OperationCanceledException) when (_readerStop.IsCancellationRequested) { }
            catch (Exception error) { failures.Add(error); }
            try { await _tasks.DisposeAsync(); }
            catch (Exception error) { failures.Add(error); }
            // The stopped reader admits no new calls. Retain each call through its response write.
            Task[] calls;
            lock (_route)
            {
                calls = [.. _toolCalls.Select(call => call.Completion)];
                failures.AddRange(_toolFailures);
                _toolFailures.Clear();
            }
            Task allCalls = Task.WhenAll(calls);
            try { await allCalls; }
            catch (Exception error)
            {
                if (allCalls.Exception is { } aggregate) failures.AddRange(aggregate.InnerExceptions);
                else failures.Add(error);
            }
            _sessionEvents.Writer.TryComplete();
            _resourceAccess = null;
            _openingContext = null;
            _readerStop.Dispose();
            _turnGate.Dispose();
            _writeLock.Dispose();
            if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
            if (failures.Count > 1) throw new AggregateException("The Claude CLI session did not retire cleanly.", failures);
        }

        private Task<JsonElement> SetThinkingDisplayAsync(bool requestReasoningText, CancellationToken cancellationToken) =>
            SendControlRequestAsync(new JsonObject
            {
                ["subtype"] = "set_max_thinking_tokens",
                // Omitting the budget preserves the headless session's current thinking configuration.
                ["thinking_display"] = requestReasoningText ? "summarized" : "omitted"
            }, cancellationToken);

        private async Task<JsonElement> SendControlRequestAsync(
            JsonNode request,
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
                    ClaudeCliControlProtocol.Request(requestId, request),
                    cancellationToken);
                return await waiter.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                lock (_route) { _controlWaiters.Remove(requestId); }
            }
        }

        private async Task WriteControlRequestWithoutWaitingAsync(
            JsonNode request,
            CancellationToken cancellationToken)
        {
            await WriteJsonLineAsync(
                ClaudeCliControlProtocol.Request(Guid.NewGuid().ToString("N"), request),
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
                    : JsonElement.Parse("0");
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
                new JsonObject
                {
                    ["type"] = "control_response",
                    ["response"] = new JsonObject
                    {
                        ["subtype"] = "success",
                        ["request_id"] = requestId,
                        ["response"] = new JsonObject { ["mcp_response"] = JsonNode.Parse(message.GetRawText()) }
                    }
                },
                cancellationToken);
        }

        private McpResponse HandleMcpMessage(JsonElement message)
        {
            JsonElement id = message.TryGetProperty("id", out JsonElement messageId)
                ? messageId.Clone()
                : JsonElement.Parse("0");
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
                    return new McpResponse(McpResult(id, new JsonObject()), null);
                case "notifications/cancelled":
                    CancelToolCall(message);
                    return new McpResponse(McpResult(id, new JsonObject()), null);
                case "ping":
                    return new McpResponse(McpResult(id, new JsonObject()), null);
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
                    : JsonElement.Parse("{}");
            PendingToolCall? pending = TakePendingToolCall(name, arguments);
            string callId = pending?.Id ?? ReadJsonRpcId(id) ?? controlRequestId;
            call = new LlmAgentToolCall(ThreadId, _activeTurnId ?? string.Empty, callId, McpServerName, name, arguments);
            return null;
        }

        /// <summary>Runs a checked call on its own task, so the reader keeps reading while the host tool works.</summary>
        private void StartToolCall(LlmAgentToolCall call, JsonElement id, string requestId, CancellationToken cancellationToken)
        {
            TurnTarget? origin = CurrentTarget();
            LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
            JsonElement identity = id.ValueKind is JsonValueKind.Number or JsonValueKind.String ? id.Clone()
                : JsonElement.Parse(JsonValue.Create(requestId)!.ToJsonString());
            var running = new RunningToolCall(identity, CancellationTokenSource.CreateLinkedTokenSource(operation.Token));
            try
            {
                lock (_route)
                {
                    if (_toolCalls.Any(active => JsonElement.DeepEquals(active.Id, identity)))
                        throw ProtocolFailure("Claude CLI repeated an active host tool request ID.");
                    _toolCalls.Add(running);
                    running.Completion = Task.Run(() => CompleteToolCallAsync(call, id, requestId, running, origin, operation), CancellationToken.None);
                }
            }
            catch
            {
                running.Cancellation.Dispose();
                operation.Dispose();
                throw;
            }
        }

        [SuppressMessage("Design", "CA1031", Justification = "Background failures are observed and retained for session retirement, not abandoned as task faults.")]
        private async Task CompleteToolCallAsync(LlmAgentToolCall call, JsonElement id, string requestId,
            RunningToolCall running, TurnTarget? origin, LlmOperationLifetime.Lease operation)
        {
            try
            {
                McpResponse response;
                try
                {
                    response = await RunToolCallAsync(call, id, running.Cancellation.Token);
                }
                catch (OperationCanceledException error) when (running.Cancellation.IsCancellationRequested && !LlmResourcePreparation.IsCleanupFailure(error))
                {
                    // The CLI cancelled the call, or the session ended. The call's control request is still answered.
                    response = new McpResponse(McpResult(id, new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "The tool call was cancelled." }),
                        ["isError"] = true
                    }), null);
                }
                operation.Token.ThrowIfCancellationRequested();
                if (response.ToolEvent is { } toolEvent)
                {
                    // Emitted before the answer: the CLI continues only after it, so the event precedes the turn's later output.
                    EmitFor(origin, toolEvent);
                }
                await WriteMcpResponseAsync(requestId, response.Message, operation.Token);
            }
            catch (OperationCanceledException error) when (operation.Token.IsCancellationRequested && !LlmResourcePreparation.IsCleanupFailure(error)) { }
            catch (Exception failure)
            {
                bool failSession;
                lock (_route)
                {
                    _toolFailures.Add(failure);
                    failSession = !_disposed && _failure is null;
                }
                if (!failSession) return;
                try { Fail(ProtocolFailure("Claude CLI did not accept a host tool result.", failure)); }
                catch (Exception cleanup)
                {
                    lock (_route) { _toolFailures.Add(cleanup); }
                }
            }
            finally
            {
                lock (_route) { _toolCalls.Remove(running); }
                running.Cancellation.Dispose();
                operation.Dispose();
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

            cancellationToken.ThrowIfCancellationRequested();
            output = ManagedAgentContext.CopyOutput(output);
            if (output.Parts.Any(part => part is LlmToolOutputSessionResource))
            {
                IInferenceResourceAccess access = _resourceAccess ?? throw new NotSupportedException("Tool resources require caller-owned access.");
                LlmResourcePreparation.ValidateScope(access, _resourceScope!, static () => { }, cancellationToken);
                var request = new LlmChatRequest
                {
                    ModelId = string.Empty,
                    Resources = access,
                    Messages = [new("user", string.Empty) { ToolResults = [new LlmToolResult(call.CallId, output)] }]
                };
                return await LlmResourcePreparation.ProjectInlineAsync(request, prepared =>
                {
                    LlmToolOutput binary = ClientTools.Functions(prepared.Messages[0].ToolResults)[0].Output;
                    ProjectedToolOutput resourceProjection = ToolOutputProjector.Project(binary, _richRoute, _richContent);
                    for (int index = 0; index < output.Parts.Count; index++)
                        if (output.Parts[index] is LlmToolOutputSessionResource
                            && resourceProjection.Report.Parts[index].Delivery == LlmToolPartDelivery.Omitted)
                            throw new NotSupportedException("The Claude MCP route cannot carry this logical resource content.");
                    return ToolResponse(call, id, output, resourceProjection);
                }, static () => { }, cancellationToken, includeSelection: false);
            }
            // Unsupported parts are omitted by the projection; they never fail the tool or the turn.
            ProjectedToolOutput projected = ToolOutputProjector.Project(output, _richRoute, _richContent);
            return ToolResponse(call, id, output, projected);
        }

        private McpResponse ToolResponse(LlmAgentToolCall call, JsonElement id, LlmToolOutput output, ProjectedToolOutput projected)
        {
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
                    new JsonObject
                    {
                        ["content"] = SerializeToolResult(projected),
                        ["isError"] = projected.IsError
                    }),
                toolEvent);
        }

        /// <summary>The CLI cancels a call it no longer waits for, as when its turn is interrupted.</summary>
        private void CancelToolCall(JsonElement message)
        {
            if (!message.TryGetProperty("params", out JsonElement parameters)
                || !parameters.TryGetProperty("requestId", out JsonElement requested)
                || requested.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            {
                return;
            }
            RunningToolCall? running;
            lock (_route) { running = _toolCalls.FirstOrDefault(active => JsonElement.DeepEquals(active.Id, requested)); }
            Cancel(running);
        }

        private void Cancel(RunningToolCall? running)
        {
            try { running?.Cancellation.Cancel(); }
            catch (ObjectDisposedException)
            {
                // The call finished as the cancellation arrived.
            }
            catch (AggregateException failure)
            {
                // A throwing callback cannot prevent cancellation of other calls or closing the feed.
                lock (_route) { _toolFailures.Add(failure); }
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
            // The CLI writes a failed request as an assistant message marked with an error. The model said nothing: the
            // turn's error result reports the failure.
            if (!root.TryGetProperty("message", out JsonElement message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out JsonElement content)
                || content.ValueKind != JsonValueKind.Array
                || OptionalString(root, "error") is not null)
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
                    : JsonElement.Parse("{}");
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

        private IEnumerable<LlmAgentEvent> MapStreamEvent(JsonElement root, string turnId, bool reasoningText)
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
            else if (reasoningText && string.Equals(deltaType, "thinking_delta", StringComparison.Ordinal))
            {
                string value = OptionalString(delta, "thinking") ?? string.Empty;
                if (value.Length > 0)
                {
                    yield return new LlmAgentReasoningDeltaEvent(
                        ThreadId,
                        turnId,
                        itemId,
                        value,
                        IsSummary: true);
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
                new JsonObject
                {
                    ["type"] = "control_response",
                    ["response"] = new JsonObject
                    {
                        ["subtype"] = "error",
                        ["request_id"] = requestId,
                        ["error"] = "Unsupported control request."
                    }
                },
                cancellationToken);
        }

        private async Task WriteJsonLineAsync(JsonNode value, CancellationToken cancellationToken)
        {
            string line = value.ToJsonString();
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await _process.WriteLineAsync(line, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
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
                    case "rate_limit_event":
                        if (root.TryGetProperty("rate_limit_info", out JsonElement rateLimit)
                            && ClaudeRateLimitEvent.Parse(rateLimit, DateTimeOffset.UtcNow) is { } observed)
                        { _connection.AccountUsageObservationBinding?.Record(observed); }
                        break;
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
                            if (target.Usage.Observe(root) is { } observation)
                            {
                                Emit(new LlmAgentUsageEvent(ThreadId, target.Id, $"usage-{target.Id}",
                                    observation.Cumulative, observation.Cumulative));
                            }
                            foreach (LlmAgentEvent item in MapStreamEvent(root, target.Id, target.ReasoningText))
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
            if (!ClaudeCliControlProtocol.TryReadResponse(root, out string requestId, out JsonElement response))
            {
                return;
            }
            TaskCompletionSource<JsonElement>? waiter;
            lock (_route) { _controlWaiters.TryGetValue(requestId, out waiter); }
            if (waiter is null)
            {
                return;
            }
            if (ClaudeCliControlProtocol.IsRejected(response))
            {
                waiter.TrySetException(ProtocolFailure("Claude CLI rejected an agent control request."));
                return;
            }
            waiter.TrySetResult(ClaudeCliControlProtocol.Payload(response));
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
                LlmProviderCapabilities capabilities = ClaudeCliLlmClient.ResolveCapabilities(_connection, _selectedModelId);
                _provider = new ProviderTurn(Guid.NewGuid().ToString(), new ClaudeStreamUsage(
                    (capabilities.SupportsUsageDeltas != InferenceSupport.Unsupported), _accountedCalls), (capabilities.SupportsReasoningText != InferenceSupport.Unsupported));
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
            if (OptionalString(root, "uuid") is { } resultId && !_completedResults.Add(resultId)) { return; }
            TurnTarget? target = CurrentTarget();
            if (target is null)
            {
                return;
            }
            target.Usage.Finish();
            LlmUsage? usage = ClaudeStreamUsage.ReadFinal(root);
            Emit(new LlmAgentUsageEvent(ThreadId, target.Id, $"usage-{target.Id}", usage, usage));
            bool isError = OptionalBoolean(root, "is_error") == true;
            string text = OptionalString(root, "result") ?? string.Empty;
            if (!isError && text.Length > 0 && target.Completed.Count == 0)
            {
                Emit(new LlmAgentMessageCompletedEvent(ThreadId, target.Id, $"result-{target.Id}", text));
            }
            string subtype = OptionalString(root, "subtype") ?? string.Empty;
            string? stopReason = OptionalString(root, "stop_reason") ?? OptionalString(root, "terminal_reason");
            LlmAgentCompletionReason reason = subtype.Contains("interrupt", StringComparison.OrdinalIgnoreCase)
                ? LlmAgentCompletionReason.Interruption
                : isError ? LlmAgentCompletionReason.ProviderFailure
                : stopReason == "refusal" ? LlmAgentCompletionReason.Refusal
                : stopReason is "max_tokens" or "model_context_window_exceeded" ? LlmAgentCompletionReason.Truncation
                : subtype == "success" && stopReason is null or "end_turn" or "stop_sequence"
                    ? LlmAgentCompletionReason.Completed : LlmAgentCompletionReason.Unknown;
            string? error = isError ? "Claude CLI reported a turn error."
                + ClaudeCliConfiguration.ErrorDetail(root, LlmCredential.Parse(_connection.Credential))
                : reason == LlmAgentCompletionReason.Unknown
                    ? $"Claude CLI returned result subtype '{subtype}' with stop reason '{stopReason}'." : null;
            Emit(new LlmAgentTurnCompletedEvent(ThreadId, target.Id, reason, error)
            {
                IsContextOverflow = isError && LlmContextOverflow.ClaudeCli(error)
            });
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
                calls = [.. _toolCalls];
                _written.Clear();
                _host = null;
                _provider = null;
            }
            foreach (HostTurn turn in turns) { turn.Events.Writer.TryComplete(failure); }
            foreach (TaskCompletionSource<JsonElement> waiter in waiters) { waiter.TrySetException(failure); }
            // No CLI remains to take a host tool's result.
            foreach (RunningToolCall call in calls) { Cancel(call); }
            _tasks.EndAll();
            _sessionEvents.Writer.TryComplete(failure);
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
                    new JsonObject { ["subtype"] = "interrupt", ["cancel_queued"] = false },
                    timeout.Token);
            }
            catch (Exception exception) when (exception is
                OperationCanceledException or InvalidOperationException or ObjectDisposedException)
            {
                // Best effort when enumeration ends before the CLI emits a terminal result.
            }
        }

        /// <summary>A turn that receives routed events: a host turn, or one the runtime started.</summary>
        private abstract class TurnTarget(string id, ClaudeStreamUsage usage, bool reasoningText)
        {
            public string Id { get; } = id;
            public ClaudeStreamUsage Usage { get; } = usage;
            public bool ReasoningText { get; } = reasoningText;

            public HashSet<string> Completed { get; } = new(StringComparer.Ordinal);
        }

        private sealed class HostTurn(string id, ClaudeStreamUsage usage, bool reasoningText) : TurnTarget(id, usage, reasoningText)
        {
            public Channel<LlmAgentEvent> Events { get; } = Channel.CreateUnbounded<LlmAgentEvent>(
                new UnboundedChannelOptions { SingleReader = true });

            public bool InterruptOnStart { get; set; }

            public bool Abandoned { get; set; }
        }

        private sealed class ProviderTurn(string id, ClaudeStreamUsage usage, bool reasoningText) : TurnTarget(id, usage, reasoningText);

        /// <summary>A host tool call in flight, matched by its typed JSON-RPC ID so cancellation reaches only that call.</summary>
        private sealed class RunningToolCall(JsonElement id, CancellationTokenSource cancellation)
        {
            public JsonElement Id { get; } = id;
            public CancellationTokenSource Cancellation { get; } = cancellation;

            public Task Completion { get; set; } = Task.CompletedTask;
        }

        private static JsonArray SerializeInput(
            IReadOnlyList<LlmAgentContent> content,
            IReadOnlyList<LlmMessageAttachment> attachments) => new(
        [
            .. content.Select(item => item switch
            {
                LlmAgentTextContent text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
                LlmAgentImageContent image => SerializeImageInput(image.ImageUrl),
                _ => throw new ArgumentException(
                    $"Unsupported agent content type '{item.GetType().Name}'.",
                    nameof(content))
            }),
            .. attachments.Select(LlmAttachments.RequireBinary).Select(AnthropicAttachments.BuildContentBlock)
        ]);

        private static JsonObject SerializeImageInput(string imageUrl)
        {
            if (TryParseDataUrl(imageUrl, out string mediaType, out string data))
            {
                return new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = data }
                };
            }

            return new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "url", ["url"] = imageUrl }
            };
        }

        private static JsonArray SerializeToolResult(ProjectedToolOutput projected) => new(
        [
            .. projected.Parts.Select(part => part switch
            {
                SentText text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
                SentImage image => new JsonObject
                {
                    ["type"] = "image",
                    ["mimeType"] = image.MediaType,
                    ["data"] = Convert.ToBase64String(image.Data.Span)
                },
                SentResourceLink { MediaType: null } link => new JsonObject
                {
                    ["type"] = "resource_link",
                    ["uri"] = link.Uri,
                    ["name"] = link.Name ?? link.Uri
                },
                SentResourceLink link => new JsonObject
                {
                    ["type"] = "resource_link",
                    ["uri"] = link.Uri,
                    ["name"] = link.Name ?? link.Uri,
                    ["mimeType"] = link.MediaType
                },
                SentEmbeddedText embedded => new JsonObject
                {
                    ["type"] = "resource",
                    ["resource"] = new JsonObject
                    {
                        ["uri"] = embedded.Uri,
                        ["mimeType"] = embedded.MediaType ?? "text/plain",
                        ["text"] = embedded.Text
                    }
                },
                _ => throw new InvalidOperationException("The Claude MCP route does not carry this part natively.")
            })
        ]);

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

        private static JsonObject McpInitializeResult(string protocolVersion) => new()
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = McpServerName,
                ["version"] = typeof(ClaudeAgentClient).Assembly.GetName().Version?.ToString() ?? "0.0.0"
            }
        };

        private static JsonObject McpToolsListResult(IReadOnlyList<LlmAgentToolDefinition> tools) => new()
        {
            ["tools"] = new JsonArray([.. tools.Select(tool => new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description ?? string.Empty,
                ["inputSchema"] = JsonNode.Parse(tool.InputSchema.GetRawText()),
                ["_meta"] = new JsonObject
                {
                    ["anthropic/alwaysLoad"] = !tool.DeferLoading
                }
            })])
        };

        private static JsonElement McpResult(JsonElement id, JsonNode result) => JsonElement.Parse(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["result"] = result
        }.ToJsonString());

        private static JsonElement McpError(JsonElement id, int code, string message) =>
            JsonElement.Parse(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(id.GetRawText()),
                ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
            }.ToJsonString());

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
