using Darkspyre.Inference.Common;
using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// Session-oriented agent adapter over the official Claude CLI SDK protocol. The Host holds a
/// <see cref="ClaudeConversation"/>, which owns the saved history and runs each CLI process as an inner session.
/// </summary>
internal sealed partial class ClaudeAgentClient(IClaudeCliSessionProcessFactory processFactory,
    ClaudeCliLlmClient chat, LearnedModelContext learned) : ILlmAgentClient
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
        options = options with { HostedTools = HostedTools.WithoutUnofferedImages(options.HostedTools, NativeAgentContract.Claude.HostedTools) };
        HostedTools.Validate(options.HostedTools, options.MaxHostedTurns, NativeAgentContract.Claude.HostedTools);
        // The CLI owns its id, so ThreadId is ignored. The runtime policy is accepted and not read: no provider tool runs.
        string workingDirectory = options.Runtime?.WorkingDirectory ?? Environment.CurrentDirectory;
        // Returned tool calls are never answered, so the CLI keeps no record of the interrupted session.
        bool ephemeral = (options.Runtime?.Ephemeral ?? false) || options.ToolHandling == LlmAgentToolHandling.ReturnRequests;
        NativeAgentContract.ValidateConnection(connection, NativeAgentContract.Claude, supportsAgentToolUse: true,
            options.ModelId, options.Tools, options.ToolHandler);
        ClaudeCliConfiguration.ValidateConnection(connection);
        ValidateOptions(options.ModelId, workingDirectory);
        options = AgentAbilityTools.Apply(options);
        LlmAgentContext history = options.Context is null ? new()
            : ManagedAgentContext.WithoutContinuationState(options.Context, supportsResources: true).Context;
        var conversation = new ClaudeConversation(this, connectionId, connection, options, workingDirectory, ephemeral, history);
        await conversation.OpenAsync(cancellationToken);
        return conversation;
    }

    /// <summary>The opening context with the caller's current selection (or none without access), and the resource scope.</summary>
    private static async Task<(LlmAgentContext? Context, InferenceResourceSelectionVersion? Scope)> ReadResourceScopeAsync(
        IInferenceResourceAccess? access, LlmAgentContext? context, CancellationToken token)
    {
        if (access is null)
        {
            if (context is null) return (null, null);
            LlmAgentContext cleared = LlmResourcePreparation.Adopt(context, null);
            ManagedAgentContext.ValidateRuntimeResources(cleared);
            return (cleared, null);
        }
        InferenceResourceSelection selection = await LlmResourcePreparation.ReadSelectionAsync(access, static () => { }, token);
        LlmAgentContext adopted = LlmResourcePreparation.Adopt(context ?? new(), selection);
        await LlmResourcePreparation.ValidateContextAsync(adopted, selection, access, static () => { }, token);
        LlmAttachments.ValidateReferences(new() { ModelId = string.Empty, Messages = adopted.Entries.Select(entry => entry.Message).ToArray() },
            supportsResources: true);
        foreach (LlmAgentContextEntry entry in adopted.Entries)
            LlmAttachments.Validate(entry.Message.Attachments, true, true, nameof(context), supportsReferences: true);
        return (context is null ? null : adopted, new(selection.WorkspaceId, selection.SessionId, selection.Revision));
    }

    /// <summary>Starts one CLI process and its session, with <paramref name="opening"/> sent before the first input.</summary>
    [SuppressMessage("Design", "CA1031", Justification = "Failed opening preserves both the original failure and a genuine retirement failure.")]
    private async Task<ClaudeAgentSession> OpenInnerAsync(string connectionId, LlmConnection connection,
        LlmAgentSessionOptions options, string workingDirectory, bool ephemeral, LlmAgentContext opening,
        Action<LlmAgentEvent> record, Func<CancellationToken, ValueTask<bool>> beforeResults,
        CancellationToken cancellationToken)
    {
        string threadId = Guid.NewGuid().ToString();
        string? modelId = options.ModelId;
        // The first definition of a duplicated name is the one used.
        IReadOnlyList<LlmAgentToolDefinition> tools = [.. ClientTools.Functions(options.Tools).DistinctBy(tool => tool.Name, StringComparer.Ordinal)];
        // An empty history with no resource selection opens like a start without context.
        bool empty = opening.Entries.Count == 0 && opening.ResourceSelectionVersion is null && opening.SelectedResources.Count == 0;
        (LlmAgentContext? Context, int Omitted) openingContext = AgentOpeningContext.Validate(
            options with { Context = empty ? null : opening }, supportsResources: true);
        if (options.CallerServices?.Resources is null && openingContext.Context is { } context)
            _ = AgentOpeningContext.From(context, openingContext.Omitted, carriesAttachments: true);
        IInferenceResourceAccess? resourceAccess = options.CallerServices?.Resources;
        string normalizedDirectory = CliInfrastructure.NormalizeWorkingDirectory(workingDirectory);
        LlmProviderCapabilities capabilities = ClaudeCliLlmClient.ResolveCapabilities(connection, modelId);
        LlmToolNames names = LlmToolNames.Create(tools.Select(tool => tool.Name), IsValidMcpToolName);
        IReadOnlyList<string> arguments = BuildArguments(
            threadId,
            modelId,
            tools,
            names,
            ephemeral,
            (capabilities.SupportsReasoningText != InferenceSupport.Unsupported),
            options.FallbackModelIds,
            // A non-positive iteration limit uses the record default, as on every route.
            options.ExecutionOptions is { } execution ? ManagedAgentPolicy.Normalize(execution, null) : null);
        (LlmAgentContext? adoptedContext, InferenceResourceSelectionVersion? resourceScope) =
            await ReadResourceScopeAsync(resourceAccess, openingContext.Context, cancellationToken);
        openingContext = (adoptedContext, openingContext.Omitted);
        IClaudeCliSessionProcess process;
        try
        {
            process = await processFactory.StartAsync(
                new ClaudeCliSessionInvocation(
                    ClaudeCliConfiguration.ResolveExecutable(connection.Endpoint),
                    arguments,
                    normalizedDirectory)
                { AccountToken = ClaudeCliEnvironment.AccountToken(LlmCredential.Parse(connection.Credential)) },
                cancellationToken);
        }
        catch (ClaudeCliUnavailableException exception)
        {
            throw ClaudeCliConfiguration.Unavailable(exception);
        }

        var session = new ClaudeAgentSession(
            process,
            threadId,
            threadId,
            modelId,
            tools,
            names,
            options.ToolHandler,
            options.ToolHandling == LlmAgentToolHandling.ReturnRequests,
            NativeAgentContract.Claude.RichContentSupported.For(connection, null),
            NativeAgentContract.Claude.RichContentSupported.For(connection, modelId),
            openingContext,
            resourceAccess,
            resourceScope,
            connection,
            record,
            beforeResults);
        try
        {
            session.Start();
            await session.InitializeAsync(options.Instructions, (capabilities.SupportsReasoningText != InferenceSupport.Unsupported), cancellationToken);
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
        string? modelId,
        IReadOnlyList<LlmAgentToolDefinition> tools,
        LlmToolNames names,
        bool ephemeral,
        bool requestReasoningText,
        IReadOnlyList<string> fallbackModelIds,
        LlmAgentExecutionOptions? execution)
    {
        List<string> arguments =
        [
            "--verbose",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            // The echo marks where each host turn starts; output with no echo before it is the runtime's own turn.
            "--replay-user-messages",
            "--include-partial-messages",
            .. ClaudeCliConfiguration.IsolationArguments,
            // Nothing outside --allowedTools may run.
            "--permission-mode", "dontAsk",
            "--settings", new JsonObject { ["showThinkingSummaries"] = requestReasoningText }.ToJsonString()
        ];
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            arguments.AddRange(["--model", modelId]);
        }

        if (tools.Count > 0)
        {
            arguments.Add("--allowedTools");
            arguments.Add(string.Join(',', tools.Select(tool => McpToolName(names.Native(tool.Name)))));
        }

        arguments.Add($"--session-id={threadId}");

        if (ephemeral)
        {
            arguments.Add("--no-session-persistence");
        }

        // The CLI stops at launch when a fallback model equals the main model.
        string[] fallbacks = [.. fallbackModelIds.Where(id => !string.Equals(id, modelId, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal)];
        if (fallbacks.Length > 0)
        {
            arguments.AddRange(["--fallback-model", string.Join(',', fallbacks)]);
        }

        if (execution is not null)
        {
            arguments.AddRange(["--max-turns", execution.MaxModelIterations.ToString(CultureInfo.InvariantCulture)]);
            // The CLI stops at launch on a budget of 0 or less, which sets no cap.
            if (execution.MaxSpendUsd > 0)
            {
                arguments.AddRange(["--max-budget-usd", execution.MaxSpendUsd.Value.ToString(CultureInfo.InvariantCulture)]);
            }
        }

        return arguments;
    }

    private static void ValidateOptions(string? modelId, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException("A model id is required.", nameof(modelId));
        }

        _ = CliInfrastructure.NormalizeWorkingDirectory(workingDirectory);
    }

    /// <summary>
    /// The request size and limit a Claude CLI too-long refusal reports, such as
    /// <c>Prompt is too long · the request is ~1053843 tokens (limit 1000000)</c>; null for a value the text lacks.
    /// </summary>
    internal static (long? Tokens, long? Limit) ReadRefusalSizes(string? detail)
    {
        Match sizes = RefusalSizes().Match(detail ?? string.Empty);
        return sizes.Success
            ? (long.Parse(sizes.Groups[1].Value, CultureInfo.InvariantCulture), long.Parse(sizes.Groups[2].Value, CultureInfo.InvariantCulture))
            : (null, null);
    }

    [GeneratedRegex(@"~?(\d+) tokens \(limit (\d+)\)")]
    private static partial Regex RefusalSizes();

    /// <summary>The MCP tool-name rule. A name outside it is sent as its <see cref="LlmToolNames.Alias"/>.</summary>
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
        private readonly LlmToolNames _names;
        private readonly LlmAgentToolHandler? _toolHandler;
        // Held around each call to a tool that requires sequential execution, so two such calls never overlap.
        private readonly SemaphoreSlim _sequential = new(1, 1);
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
        // The CLI runs no background work of its own: its Task and Monitor tools are off.
        private readonly NoAgentResources _resources = new(LlmAgentResourceStopStatus.Unknown);
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
        // Called with every event of a host turn before it is queued, so the conversation records it in order.
        private readonly Action<LlmAgentEvent> _record;
        // Decision point (b): asked when a model step's last bridge call settles, before its response is written.
        private readonly Func<CancellationToken, ValueTask<bool>> _beforeResults;
        // LlmAgentToolHandling.ReturnRequests: the first bridge call ends the turn with the step's calls unrun.
        private readonly bool _returnRequests;
        // The running model message has ended (its message_stop was read); _route guards it.
        private bool _messageStopped;
        private LlmUsage? _lastStepUsage;

        public ClaudeAgentSession(
            IClaudeCliSessionProcess process,
            string threadId,
            string sessionId,
            string? initialModelId,
            IReadOnlyList<LlmAgentToolDefinition> tools,
            LlmToolNames names,
            LlmAgentToolHandler? toolHandler,
            bool returnRequests,
            LlmRichContentSupported richRoute,
            LlmRichContentSupported richContent,
            (LlmAgentContext? Context, int Omitted) opening,
            IInferenceResourceAccess? resourceAccess,
            InferenceResourceSelectionVersion? resourceScope,
            LlmConnection connection,
            Action<LlmAgentEvent> record,
            Func<CancellationToken, ValueTask<bool>> beforeResults)
        {
            _process = process;
            _record = record;
            _beforeResults = beforeResults;
            _returnRequests = returnRequests;
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
            _names = names;
            _toolHandler = toolHandler;
        }

        public string ThreadId { get; }

        public string SessionId { get; }

        /// <summary>The connection with the catalog this session's <c>initialize</c> attached.</summary>
        internal LlmConnection Connection => _connection;

        /// <summary>The usage the CLI last reported for one model step; null until it reports one.</summary>
        internal LlmUsage? LastStepUsage => Volatile.Read(ref _lastStepUsage);

        /// <summary>The refusal of mid-turn steering.</summary>
        internal const string SteeringUnsupported = "Claude CLI does not expose mid-turn steering through this adapter.";

        public Task<LlmAgentCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
            return Task.FromResult(NativeAgentContract.Claude);
        }

        public LlmCompactionResponsibility CompactionResponsibility => LlmCompactionResponsibility.Provider;

        public Task<LlmAgentCompactedEvent?> CompactNowAsync(CancellationToken cancellationToken = default)
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
            throw NativeAgentContract.UnsupportedContext(operation.Token);
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
                        ["toolsListResult"] = McpToolsListResult(_tools, _names)
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
            _ = SerializeInput(request.Input, []);
            if (!_turnGate.Wait(0, cancellationToken))
            {
                throw new InvalidOperationException("The session is running or paused.");
            }
            HostTurn? turn = null;
            bool completed = false;
            try
            {
                if (request.Input.Count == 0 && request.Attachments.Count == 0 && _openingContext is null)
                {
                    // Nothing to send: the turn completes at once.
                    string emptyTurnId = Guid.NewGuid().ToString();
                    yield return new LlmAgentTurnStartedEvent(ThreadId, emptyTurnId);
                    completed = true;
                    yield return new LlmAgentTurnCompletedEvent(ThreadId, emptyTurnId, LlmAgentCompletionReason.Completed, null);
                    yield break;
                }
                string? selectedModelId;
                lock (_route) { selectedModelId = _selectedModelId; }
                LlmProviderCapabilities capabilities = ClaudeCliLlmClient.ResolveCapabilities(
                    _connection, request.ModelId ?? selectedModelId);
                LlmReasoningLevel? nativeEffort = ClaudeCliLlmClient.MapReasoning(
                    _connection, request.ModelId ?? selectedModelId, request.ReasoningLevel);
                (JsonArray input, string? openingNotice) = await PrepareInputAsync(request, selectedModelId, capabilities, cancellationToken);
                var settings = new JsonObject { ["showThinkingSummaries"] = (capabilities.SupportsReasoningText != InferenceSupport.Unsupported) };
                if (nativeEffort is { } level)
                {
                    settings["effortLevel"] = LlmReasoningEffort.Word(level);
                }
                string turnId = Guid.NewGuid().ToString();
                turn = new HostTurn(turnId, new ClaudeStreamUsage((capabilities.SupportsUsageDeltas != InferenceSupport.Unsupported), _accountedCalls), (capabilities.SupportsReasoningText != InferenceSupport.Unsupported))
                {
                    OutputSchema = request.OutputSchema
                };
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
                if (turn.Suspended)
                {
                    // Decision point (b) ended the turn's stream; the conversation disposes this session.
                    completed = true;
                    yield break;
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

        /// <summary>
        /// The turn's content: the opening context, then the turn instructions, the input, a notice for each attachment
        /// left out, and the answer schema's guidance, followed by the attachments that are sent.
        /// </summary>
        private async Task<(JsonArray Content, string? Notice)> PrepareInputAsync(LlmAgentTurnRequest turn, string? selectedModelId,
            LlmProviderCapabilities capabilities, CancellationToken token)
        {
            LlmAgentContext? openingContext = _openingContext;
            string modelId = turn.ModelId ?? selectedModelId ?? string.Empty;
            // An attachment the model or route cannot take is left out of this request, with a notice in its place.
            LlmChatMessage turnMessage = LlmRequestNormalizer.Normalize(_connection, new LlmChatRequest
            {
                ModelId = modelId,
                Resources = _resourceAccess,
                Messages = [new("user", string.Empty) { Attachments = turn.Attachments }]
            }, capabilities).Messages[0];
            LlmAgentContent[] input =
            [
                .. string.IsNullOrEmpty(turn.Instructions) ? [] : new LlmAgentContent[]
                {
                    new LlmAgentTextContent($"Instructions for this turn:\n{turn.Instructions}\n\n")
                },
                .. turn.Input,
                .. turnMessage.Content.Length == 0 ? [] : new LlmAgentContent[] { new LlmAgentTextContent(turnMessage.Content) },
                .. turn.OutputSchema is { } schema ? new LlmAgentContent[] { new LlmAgentTextContent(StructuredOutput.Guidance(schema)) } : []
            ];
            var request = new LlmChatRequest
            {
                ModelId = modelId,
                Resources = _resourceAccess,
                Messages = [.. openingContext?.Entries.Select(entry => entry.Message) ?? [],
                    new("user", string.Empty) { Attachments = turnMessage.Attachments }]
            };
            LlmAttachments.ValidateReferences(request, supportsResources: _resourceAccess is not null);
            (JsonArray Content, string? Notice) Project(LlmChatRequest prepared)
            {
                AgentOpeningContext? opening = openingContext is null ? null : AgentOpeningContext.From(openingContext with
                {
                    Entries = openingContext.Entries.Select((entry, index) => entry with { Message = prepared.Messages[index] }).ToArray()
                }, _openingOmitted, carriesAttachments: true);
                return (opening is null ? SerializeInput(input, prepared.Messages[^1].Attachments)
                    : SerializeInput([new LlmAgentTextContent(opening.Text), .. input],
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
            throw new NotSupportedException(SteeringUnsupported);
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
            return NoAgentResources.GetAsync();
        }

        public Task<LlmAgentResourceOutput> ReadResourceOutputAsync(
            string resourceId,
            string? cursor,
            CancellationToken cancellationToken = default)
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
            return NoAgentResources.ReadAsync();
        }

        public Task<LlmAgentResourceStopStatus> StopResourceAsync(
            string resourceId,
            CancellationToken cancellationToken = default)
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
            return _resources.StopAsync();
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
            _resources.Dispose();
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
            _sequential.Dispose();
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
                if (_returnRequests && CurrentTarget() is HostTurn host)
                {
                    // The call is not answered: the turn ends with the step's calls, and the CLI is interrupted.
                    await ReturnRequestsAsync(host);
                    return null;
                }
                if (PrepareToolCall(message, callId, requestId, out LlmAgentToolCall? call) is { } refusal)
                {
                    await WriteMcpResponseAsync(requestId, refusal.Message, cancellationToken);
                    return refusal.ToolEvent;
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
                    return new McpResponse(McpResult(id, McpToolsListResult(_tools, _names)), null);
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

            string? name = OptionalString(parameters, "name") is { } native ? _names.Logical(native) : null;
            if (name is null || !_toolsByName.TryGetValue(name, out LlmAgentToolDefinition? definition))
            {
                return new McpResponse(McpError(id, -32602, "Unknown tool."), null);
            }

            JsonElement arguments = parameters.TryGetProperty("arguments", out JsonElement suppliedArguments)
                ? suppliedArguments.Clone()
                : JsonElement.Parse("{}");
            PendingToolCall? pending = TakePendingToolCall(name, arguments);
            string callId = pending?.Id ?? ReadJsonRpcId(id) ?? controlRequestId;
            // A wrapped schema's value is checked against the tool's own schema and is what the handler receives.
            StructuredOutput.Evaluation evaluation = ToolSchemas.EvaluateArguments(definition.InputSchema, arguments.GetRawText());
            if (ClientTools.ArgumentRejection(name, evaluation) is { } rejection)
            {
                // The handler does not run; the model receives the errors and the turn continues.
                return new McpResponse(
                    McpResult(id, new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = rejection }),
                        ["isError"] = true
                    }),
                    new LlmAgentToolEvent(ThreadId, _activeTurnId ?? string.Empty, callId, McpServerName, name, arguments,
                        false, "failed", LlmToolOutput.FromText(rejection, isError: true)));
            }
            call = new LlmAgentToolCall(ThreadId, _activeTurnId ?? string.Empty, callId, McpServerName, name, evaluation.Instance);
            return null;
        }

        /// <summary>
        /// Ends the host turn with every tool use of the model's step returned unrun, each with its argument check, and
        /// interrupts the CLI. No result reaches the CLI or the host.
        /// </summary>
        private async Task ReturnRequestsAsync(HostTurn host)
        {
            PendingToolCall[] calls;
            lock (_route)
            {
                calls = [.. _pendingToolCalls];
                _pendingToolCalls.Clear();
            }
            foreach (PendingToolCall call in calls)
            {
                string? rejection = _toolsByName.TryGetValue(call.Name, out LlmAgentToolDefinition? definition)
                    ? ClientTools.ArgumentRejection(call.Name, ToolSchemas.EvaluateArguments(definition.InputSchema, call.Arguments.GetRawText()))
                    : "Unknown tool.";
                Emit(rejection is null
                    ? new LlmAgentToolEvent(ThreadId, host.Id, call.Id, McpServerName, call.Name, call.Arguments, null, "returned")
                    : new LlmAgentToolEvent(ThreadId, host.Id, call.Id, McpServerName, call.Name, call.Arguments, false, "failed",
                        LlmToolOutput.FromText(rejection, isError: true)));
            }
            await TryInterruptAsync();
            Emit(new LlmAgentTurnCompletedEvent(ThreadId, host.Id, LlmAgentCompletionReason.ToolRequests, null));
            lock (_route)
            {
                host.Events.Writer.TryComplete();
                if (_host == host) _host = null;
                _activeTurnId = null;
            }
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
                if (origin is HostTurn host && LastCallOfStep(running)
                    && await _beforeResults(operation.Token))
                {
                    // The conversation compacts and reopens: this result stays unwritten and the turn's stream ends.
                    lock (_route)
                    {
                        host.Suspended = true;
                        host.Events.Writer.TryComplete();
                    }
                    return;
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

        /// <summary>
        /// The running call settles the step: the model message has ended, every tool use of it has its call, and no
        /// other bridge call is running.
        /// </summary>
        private bool LastCallOfStep(RunningToolCall running)
        {
            lock (_route)
            {
                return _messageStopped && _pendingToolCalls.Count == 0 && _toolCalls.Count == 1 && _toolCalls[0] == running;
            }
        }

        private async Task<McpResponse> RunToolCallAsync(LlmAgentToolCall call, JsonElement id, CancellationToken cancellationToken)
        {
            LlmToolOutput output;
            bool sequential = _toolsByName[call.Name].RequiresSequentialExecution;
            bool entered = false;
            try
            {
                if (_toolHandler is not { } handler)
                {
                    output = LlmToolOutput.FromText(ClientTools.NoHandler(call.Name), isError: true);
                }
                else
                {
                    if (sequential)
                    {
                        await _sequential.WaitAsync(cancellationToken);
                        entered = true;
                    }
                    output = await handler(call, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                output = LlmToolOutput.FromText(ClientTools.HandlerFailure(error, _connection), isError: true);
            }
            finally
            {
                if (entered) _sequential.Release();
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
                // A resource part the MCP route cannot carry is left out with the projection's notice.
                return await LlmResourcePreparation.ProjectInlineAsync(request, prepared => ToolResponse(call, id, output,
                    ToolOutputProjector.Project(ClientTools.Functions(prepared.Messages[0].ToolResults)[0].Output, _richRoute, _richContent)),
                    static () => { }, cancellationToken, includeSelection: false);
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
                    _record(item);
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

            if (OptionalString(message, "model") is { } model) ObserveModel(model);
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

                string name = LogicalToolName(OptionalString(block, "name") ?? string.Empty);
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

        private LlmAgentEvent? MapSystemMessage(JsonElement root, string turnId)
        {
            string subtype = OptionalString(root, "subtype") ?? string.Empty;
            if (subtype == "api_retry")
            {
                // The CLI's attempt is the attempt about to run; error is its failure category.
                int? attempt = OptionalInt32(root, "attempt");
                int? status = OptionalInt32(root, "error_status");
                string? category = OptionalString(root, "error");
                return new LlmAgentResponseRecoveryEvent(ThreadId, turnId, $"retry-{turnId}-{attempt}", attempt)
                {
                    Delay = OptionalInt32(root, "retry_delay_ms") is { } delay ? TimeSpan.FromMilliseconds(delay) : null,
                    Error = status is null ? category : $"{status}: {category}"
                };
            }

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
                            // The step's model is known from message start, before the first usage event of the step.
                            if (root.TryGetProperty("event", out JsonElement started)
                                && OptionalString(started, "type") == "message_start"
                                && started.TryGetProperty("message", out JsonElement startedMessage)
                                && OptionalString(startedMessage, "model") is { } startedModel)
                            {
                                target.ModelId = startedModel;
                            }
                            if (root.TryGetProperty("event", out JsonElement messageEvent)
                                && OptionalString(messageEvent, "type") is ("message_start" or "message_stop") and { } boundary)
                            {
                                lock (_route) { _messageStopped = boundary == "message_stop"; }
                            }
                            LlmUsageDeltaEvent? observation = target.Usage.Observe(root);
                            if (target.Usage.Last is { } stepUsage) Volatile.Write(ref _lastStepUsage, stepUsage);
                            if (observation is not null)
                            {
                                Emit(new LlmAgentUsageEvent(ThreadId, target.Id, $"usage-{target.Id}",
                                    observation.Cumulative, observation.Cumulative) { ModelId = target.ModelId });
                            }
                            foreach (LlmAgentEvent item in MapStreamEvent(root, target.Id, target.ReasoningText))
                            {
                                Emit(item);
                            }
                            break;
                        }
                    case "system":
                        if (MapSystemMessage(root, CurrentTarget()?.Id ?? string.Empty) is { } systemEvent)
                        {
                            Emit(systemEvent);
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
                _sessionEvents.Writer.TryWrite(new LlmAgentProviderTurnStartedEvent(ThreadId, _provider.Id, null));
                return _provider;
            }
        }

        private void Emit(LlmAgentEvent item)
        {
            lock (_route)
            {
                if (_host is { } host)
                {
                    _record(item);
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
            Emit(new LlmAgentUsageEvent(ThreadId, target.Id, $"usage-{target.Id}", usage, usage) { ModelId = target.ModelId });
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
                : subtype == "error_max_turns" ? LlmAgentCompletionReason.IterationLimit
                : subtype == "error_max_budget_usd" ? LlmAgentCompletionReason.SpendLimit
                : isError ? LlmAgentCompletionReason.ProviderFailure
                : stopReason == "refusal" ? LlmAgentCompletionReason.Refusal
                : stopReason is "max_tokens" or "model_context_window_exceeded" ? LlmAgentCompletionReason.Truncation
                : subtype == "success" && stopReason is null or "end_turn" or "stop_sequence"
                    ? LlmAgentCompletionReason.Completed : LlmAgentCompletionReason.Unknown;
            string? error = reason is LlmAgentCompletionReason.IterationLimit or LlmAgentCompletionReason.SpendLimit
                ? FirstError(root)
                : isError ? "Claude CLI reported a turn error."
                + ClaudeCliConfiguration.ErrorDetail(root, LlmCredential.Parse(_connection.Credential))
                : reason == LlmAgentCompletionReason.Unknown
                    ? $"Claude CLI returned result subtype '{subtype}' with stop reason '{stopReason}'." : null;
            Emit(new LlmAgentTurnCompletedEvent(ThreadId, target.Id, reason, error)
            {
                IsContextOverflow = isError && LlmContextOverflow.ClaudeCli(error),
                StructuredOutput = (target as HostTurn)?.OutputSchema is { } schema
                    ? ClientTools.AnswerCheck(schema, text, reason, LlmStructuredOutputEnforcement.LocalValidationOnly) : null
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

        /// <summary>Records the model the CLI reports for the running turn's step.</summary>
        private void ObserveModel(string model)
        {
            if (CurrentTarget() is { } target) target.ModelId = model;
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

            /// <summary>The model the CLI last reported for this turn; null until it reports one.</summary>
            public string? ModelId { get; set; }

            public HashSet<string> Completed { get; } = new(StringComparer.Ordinal);
        }

        private sealed class HostTurn(string id, ClaudeStreamUsage usage, bool reasoningText) : TurnTarget(id, usage, reasoningText)
        {
            public Channel<LlmAgentEvent> Events { get; } = Channel.CreateUnbounded<LlmAgentEvent>(
                new UnboundedChannelOptions { SingleReader = true });

            public bool InterruptOnStart { get; set; }

            /// <summary>The turn's answer schema; the final answer is checked against it.</summary>
            public JsonElement? OutputSchema { get; init; }

            public bool Abandoned { get; set; }

            /// <summary>Decision point (b) ended the stream with a result unwritten; no completion follows.</summary>
            public bool Suspended { get; set; }
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

        /// <summary>The bridge's MCP tool list: the text the CLI receives for the tools, so the conversation measure counts it too.</summary>
        internal static JsonObject McpToolsListResult(IReadOnlyList<LlmAgentToolDefinition> tools, LlmToolNames names) => new()
        {
            ["tools"] = new JsonArray([.. tools.Select(tool => new JsonObject
            {
                ["name"] = names.Native(tool.Name),
                ["description"] = tool.Description ?? string.Empty,
                ["inputSchema"] = JsonNode.Parse(ToolSchemas.Wire(tool.InputSchema).GetRawText()),
                ["_meta"] = new JsonObject
                {
                    ["anthropic/alwaysLoad"] = !tool.DeferLoading,
                    // The CLI files a result over a tool's size cap, which the model cannot read back with Read off.
                    // 4,000,000 characters is the largest Claude window, 1,000,000 tokens, at four characters a token.
                    ["anthropic/maxResultSizeChars"] = 4_000_000
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

        /// <summary>A bridge tool's logical name; any other tool keeps the name the CLI reported.</summary>
        private string LogicalToolName(string name)
        {
            string prefix = $"mcp__{McpServerName}__";
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) return name;
            string native = name[prefix.Length..];
            return _names.Logical(native) ?? native;
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

        private static int? OptionalInt32(JsonElement element, string propertyName) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
                ? number
                : null;

        /// <summary>The CLI's first error text in a result message; null when it reports none.</summary>
        private static string? FirstError(JsonElement result) =>
            result.TryGetProperty("errors", out JsonElement errors)
            && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0
            && errors[0].ValueKind == JsonValueKind.String
                ? errors[0].GetString()
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
