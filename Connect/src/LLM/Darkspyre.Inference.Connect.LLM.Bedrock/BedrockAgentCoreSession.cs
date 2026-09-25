using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Amazon.BedrockAgentCore;
using Amazon.BedrockAgentCore.Model;
using Amazon.Runtime.EventStreams;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

internal sealed class BedrockAgentCoreSession : ILlmAgentSession
{
    private const int MaximumInlineToolRounds = 32;

    private readonly IBedrockAgentCoreInvoker _invoker;
    private readonly LlmAwsCredentials _aws;
    private readonly string _harnessArn;
    private readonly string _initialModelId;
    private readonly string? _instructions;
    private readonly IReadOnlyList<LlmAgentToolDefinition> _tools;
    private readonly Dictionary<string, LlmAgentToolDefinition> _toolsByName;
    private readonly LlmAgentSandboxMode _sandbox;
    private readonly LlmAgentApprovalPolicy _approvalPolicy;
    private readonly LlmAgentToolHandler? _toolHandler;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    // Sent with the first turn, then cleared once the harness streams a response to it.
    private AgentOpeningContext? _opening;
    // The runtime's tool-output support after the connection's overrides, and after the model's too.
    private readonly LlmRichContentSupported _richRoute;
    private readonly LlmRichContentSupported _richContent;
    private bool _disposed;

    // AgentCore's own background work is not measured; nothing is reported and stop is Unknown.
    private readonly NoAgentResources _resources = new(LlmAgentResourceStopStatus.Unknown);

    public BedrockAgentCoreSession(
        IBedrockAgentCoreInvoker invoker,
        LlmAwsCredentials aws,
        string harnessArn,
        string sessionId,
        string initialModelId,
        string? instructions,
        IReadOnlyList<LlmAgentToolDefinition> tools,
        LlmAgentSandboxMode sandbox,
        LlmAgentApprovalPolicy approvalPolicy,
        LlmAgentToolHandler? toolHandler,
        LlmRichContentSupported richRoute,
        LlmRichContentSupported richContent,
        AgentOpeningContext? opening = null)
    {
        _invoker = invoker;
        _opening = opening;
        _richRoute = richRoute;
        _richContent = richContent;
        _aws = aws;
        _harnessArn = harnessArn;
        _initialModelId = initialModelId;
        _instructions = instructions;
        _tools = tools;
        _toolsByName = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        _sandbox = sandbox;
        _approvalPolicy = approvalPolicy;
        _toolHandler = toolHandler;
        ThreadId = sessionId;
        SessionId = sessionId;
    }

    public string ThreadId { get; }

    public string SessionId { get; }

    public Task<LlmAgentCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NativeAgentContract.AgentCore);
    }

    public Task<LlmRequestAccounting> MeasureContextAsync(LlmAgentContext proposedContext,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(proposedContext);
        return Task.FromResult(new LlmRequestAccounting
        {
            ModelId = null, // The harness does not expose the effective context being measured.
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


    public async IAsyncEnumerable<LlmAgentEvent> RunTurnAsync(
        LlmAgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        NativeAgentContract.ValidateTurn(request);
        ValidateTurn(request);
        await _turnGate.WaitAsync(cancellationToken);
        string turnId = $"turn-{Guid.NewGuid():N}";
        try
        {
            yield return new LlmAgentTurnStartedEvent(ThreadId, turnId);
            AgentOpeningContext? opening = _opening;
            if (opening is not null)
            {
                yield return new LlmAgentWarningEvent(ThreadId, turnId, opening.Notice);
            }
            List<HarnessMessage> messages = BuildInitialMessages(opening is null
                ? request.Input
                : [new LlmAgentTextContent(opening.Text), .. request.Input]);
            string modelId = request.ModelId ?? _initialModelId;

            for (int round = 0; round < MaximumInlineToolRounds; round++)
            {
                InvokeHarnessRequest invokeRequest = BuildRequest(messages, modelId);
                var text = new StringBuilder();
                var pendingTools = new Dictionary<int, PendingToolUse>();
                string? stopReason = null;

                await foreach (IEventStreamEvent streamEvent in _invoker.InvokeAsync(
                                   _aws,
                                   invokeRequest,
                                   cancellationToken))
                {
                    _opening = null;
                    switch (streamEvent)
                    {
                        case HarnessContentBlockStartEvent
                        {
                            ContentBlockIndex: int index,
                            Start.ToolUse: { } toolUse
                        }:
                            pendingTools[index] = new PendingToolUse(
                                toolUse.ToolUseId,
                                toolUse.Name,
                                toolUse.ServerName,
                                toolUse.Type);
                            break;
                        case HarnessContentBlockDeltaEvent
                        {
                            ContentBlockIndex: int index,
                            Delta.ToolUse.Input: { Length: > 0 } input
                        } when pendingTools.TryGetValue(index, out PendingToolUse? pending):
                            pending.Input.Append(input);
                            break;
                        case HarnessContentBlockDeltaEvent
                        {
                            ContentBlockIndex: int index,
                            Delta.Text: { Length: > 0 } delta
                        }:
                            text.Append(delta);
                            yield return new LlmAgentTextDeltaEvent(
                                ThreadId,
                                turnId,
                                $"content-{round}-{index}",
                                delta);
                            break;
                        case HarnessContentBlockDeltaEvent
                        {
                            ContentBlockIndex: int index,
                            Delta.ReasoningContent.Text: { Length: > 0 } reasoning
                        }:
                            yield return new LlmAgentReasoningDeltaEvent(
                                ThreadId,
                                turnId,
                                $"reasoning-{round}-{index}",
                                reasoning,
                                IsSummary: false);
                            break;
                        case HarnessMessageStopEvent stopped:
                            stopReason = stopped.StopReason?.Value;
                            break;
                    }
                }

                if (text.Length > 0)
                {
                    yield return new LlmAgentMessageCompletedEvent(
                        ThreadId,
                        turnId,
                        $"message-{round}-{turnId}",
                        text.ToString());
                }

                if (stopReason is null)
                {
                    yield return new LlmAgentTurnCompletedEvent(
                        ThreadId,
                        turnId,
                        LlmAgentTurnStatus.Failed,
                        "AgentCore ended the Harness stream without a message-stop event.");
                    yield break;
                }

                if (!string.Equals(stopReason, "tool_use", StringComparison.Ordinal))
                {
                    (LlmAgentTurnStatus status, string? error) = Completion(stopReason);
                    yield return new LlmAgentTurnCompletedEvent(
                        ThreadId,
                        turnId,
                        status,
                        error)
                    {
                        Reason = stopReason is "refusal" or "guardrail_intervened" or "content_filtered"
                            ? LlmAgentCompletionReason.Refusal
                            : status == LlmAgentTurnStatus.Completed
                                ? LlmAgentCompletionReason.Completed
                                : LlmAgentCompletionReason.ProviderFailure
                    };
                    yield break;
                }

                if (pendingTools.Count == 0)
                {
                    yield return new LlmAgentTurnCompletedEvent(
                        ThreadId,
                        turnId,
                        LlmAgentTurnStatus.Failed,
                        "AgentCore stopped for tool use without returning a tool call.");
                    yield break;
                }

                List<CompletedToolUse> results = [];
                foreach (PendingToolUse pending in pendingTools
                             .OrderBy(entry => entry.Key)
                             .Select(entry => entry.Value))
                {
                    JsonElement? parsedArguments = TryParseArguments(pending.Input);
                    if (parsedArguments is null)
                    {
                        yield return new LlmAgentTurnCompletedEvent(
                            ThreadId,
                            turnId,
                            LlmAgentTurnStatus.Failed,
                            $"AgentCore returned invalid JSON for tool '{pending.Name}'.");
                        yield break;
                    }
                    JsonElement arguments = parsedArguments.Value;

                    string callId = string.IsNullOrWhiteSpace(pending.Id)
                        ? $"tool-{Guid.NewGuid():N}"
                        : pending.Id;
                    yield return new LlmAgentToolEvent(
                        ThreadId,
                        turnId,
                        callId,
                        pending.ServerName,
                        pending.Name,
                        arguments,
                        null,
                        "inProgress");

                    LlmToolOutput result = await ExecuteToolAsync(
                        turnId,
                        callId,
                        pending,
                        arguments,
                        cancellationToken);
                    yield return new LlmAgentToolEvent(
                        ThreadId,
                        turnId,
                        callId,
                        pending.ServerName,
                        pending.Name,
                        arguments,
                        !result.IsError,
                        result.IsError ? "failed" : "completed");
                    results.Add(new CompletedToolUse(
                        callId,
                        pending.Name,
                        pending.ServerName,
                        pending.Type,
                        arguments,
                        result));
                }

                messages = BuildToolResultMessages(results);
            }

            yield return new LlmAgentTurnCompletedEvent(
                ThreadId,
                turnId,
                LlmAgentTurnStatus.Failed,
                $"AgentCore exceeded {MaximumInlineToolRounds} host tool rounds.")
            {
                Reason = LlmAgentCompletionReason.IterationLimit
            };
        }
        finally
        {
            _turnGate.Release();
        }
    }

    public Task SteerAsync(
        string turnId,
        IReadOnlyList<LlmAgentContent> input,
        CancellationToken cancellationToken = default)
    {
        _ = turnId;
        _ = input;
        _ = cancellationToken;
        ObjectDisposedException.ThrowIf(_disposed, this);
        throw new NotSupportedException("AgentCore Harness does not support steering an active turn.");
    }

    public Task InterruptAsync(
        string turnId,
        CancellationToken cancellationToken = default)
    {
        _ = turnId;
        _ = cancellationToken;
        ObjectDisposedException.ThrowIf(_disposed, this);
        throw new NotSupportedException(
            "AgentCore Harness does not expose a remote turn-interrupt operation. Cancel RunTurnAsync instead.");
    }

    public IAsyncEnumerable<LlmAgentEvent> ObserveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _resources.ObserveAsync(cancellationToken);
    }

    public Task<IReadOnlyList<LlmAgentResource>> GetResourcesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return NoAgentResources.GetAsync();
    }

    public Task<LlmAgentResourceOutput> ReadResourceOutputAsync(string resourceId, string? cursor,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        cancellationToken.ThrowIfCancellationRequested();
        return NoAgentResources.ReadAsync();
    }

    public Task<LlmAgentResourceStopStatus> StopResourceAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        cancellationToken.ThrowIfCancellationRequested();
        return _resources.StopAsync();
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _resources.Dispose();
        _turnGate.Dispose();
        return ValueTask.CompletedTask;
    }

    internal InvokeHarnessRequest BuildRequest(
        List<HarnessMessage> messages,
        string modelId)
    {
        bool restrictRemoteTools = _sandbox != LlmAgentSandboxMode.DangerFullAccess
            || _approvalPolicy != LlmAgentApprovalPolicy.Never;
        var request = new InvokeHarnessRequest
        {
            HarnessArn = _harnessArn,
            RuntimeSessionId = SessionId,
            Messages = messages,
            Model = BedrockAgentCoreLlmClient.BuildModelConfiguration(modelId),
            Tools = BedrockAgentCoreLlmClient.BuildTools(_tools)
        };
        if (restrictRemoteTools)
        {
            request.AllowedTools = _tools.Select(tool => tool.Name).ToList();
        }
        if (!string.IsNullOrWhiteSpace(_instructions))
        {
            request.SystemPrompt =
            [
                new HarnessSystemContentBlock { Text = _instructions }
            ];
        }
        return request;
    }

    private static List<HarnessMessage> BuildInitialMessages(
        IReadOnlyList<LlmAgentContent> input)
    {
        var content = new List<HarnessContentBlock>();
        foreach (LlmAgentContent item in input)
        {
            switch (item)
            {
                case LlmAgentTextContent text when text.Text.Length > 0:
                    content.Add(new HarnessContentBlock { Text = text.Text });
                    break;
                case LlmAgentTextContent:
                    break;
                case LlmAgentImageContent:
                    throw new NotSupportedException(
                        "AgentCore Harness does not accept image content blocks.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(input));
            }
        }
        if (content.Count == 0)
        {
            throw new ArgumentException("An AgentCore turn requires text input.", nameof(input));
        }
        return
        [
            new HarnessMessage
            {
                Role = HarnessConversationRole.User,
                Content = content
            }
        ];
    }

    private async Task<LlmToolOutput> ExecuteToolAsync(
        string turnId,
        string callId,
        PendingToolUse pending,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!_toolsByName.ContainsKey(pending.Name) || _toolHandler is null)
        {
            return FailedToolResult("AgentCore requested a tool the host did not register.");
        }
        try
        {
            // Content the harness cannot carry is omitted when the result is built; it never fails the tool.
            return await _toolHandler(
                new LlmAgentToolCall(
                    ThreadId,
                    turnId,
                    callId,
                    pending.ServerName,
                    pending.Name,
                    arguments),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return FailedToolResult("The host tool failed.");
        }
    }

    private List<HarnessMessage> BuildToolResultMessages(
        IReadOnlyList<CompletedToolUse> results)
    {
        var assistantContent = new List<HarnessContentBlock>();
        var userContent = new List<HarnessContentBlock>();
        foreach (CompletedToolUse completed in results)
        {
            assistantContent.Add(new HarnessContentBlock
            {
                ToolUse = new HarnessToolUseBlock
                {
                    ToolUseId = completed.Id,
                    Name = completed.Name,
                    ServerName = completed.ServerName,
                    Type = completed.Type ?? HarnessToolUseType.Tool_use,
                    Input = BedrockAgentCoreLlmClient.DeserializeDocument(
                        completed.Arguments.GetRawText())
                }
            });
            userContent.Add(new HarnessContentBlock
            {
                ToolResult = new HarnessToolResultBlock
                {
                    ToolUseId = completed.Id,
                    Type = completed.Type ?? HarnessToolUseType.Tool_use,
                    Status = completed.Result.IsError
                        ? HarnessToolUseStatus.Error
                        : HarnessToolUseStatus.Success,
                    Content = BuildToolResultContent(ToolOutputProjector.Project(
                        completed.Result, _richRoute, _richContent))
                }
            });
        }
        return
        [
            new HarnessMessage
            {
                Role = HarnessConversationRole.Assistant,
                Content = assistantContent
            },
            new HarnessMessage
            {
                Role = HarnessConversationRole.User,
                Content = userContent
            }
        ];
    }

    /// <summary>The harness carries text blocks; an empty result still sends one empty block.</summary>
    private static List<HarnessToolResultContentBlock> BuildToolResultContent(ProjectedToolOutput projected)
    {
        List<HarnessToolResultContentBlock> content = [.. projected.Parts.Select(part => part is SentText text
            ? new HarnessToolResultContentBlock { Text = text.Text }
            : throw new InvalidOperationException("AgentCore tool results carry text only."))];
        if (content.Count == 0)
        {
            content.Add(new HarnessToolResultContentBlock { Text = string.Empty });
        }
        return content;
    }

    private static JsonElement? TryParseArguments(StringBuilder input)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                input.Length == 0 ? "{}" : input.ToString());
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ValidateTurn(LlmAgentTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Input);
        LlmAttachments.Validate(
            request.Attachments,
            supportsImages: false,
            supportsDocuments: false,
            nameof(request));
        // AgentCore Harness exposes no reasoning control, so it accepts no level.
        Darkspyre.Inference.Connect.LLM.Providers.LlmReasoningEffort.Validate(
            request.ReasoningLevel,
            System.Collections.Frozen.FrozenSet<Darkspyre.Inference.Common.LLM.LlmReasoningLevel>.Empty);
        if (request.OutputSchema is not null)
        {
            throw new NotSupportedException(
                "AgentCore Harness does not expose provider-neutral structured output control.");
        }
    }

    private static LlmToolOutput FailedToolResult(string message) => LlmToolOutput.FromText(message, isError: true);

    private static (LlmAgentTurnStatus Status, string? Error) Completion(string? stopReason) =>
        stopReason switch
        {
            "interrupted" => (LlmAgentTurnStatus.Interrupted, "AgentCore interrupted the turn."),
            "content_filtered"
                or "malformed_model_output"
                or "malformed_tool_use"
                or "max_iterations_exceeded"
                or "max_output_tokens_exceeded"
                or "max_tokens"
                or "model_context_window_exceeded"
                or "partial_turn"
                or "timeout_exceeded" =>
                (LlmAgentTurnStatus.Failed, $"AgentCore stopped with '{stopReason}'."),
            _ => (LlmAgentTurnStatus.Completed, null)
        };

    private sealed record PendingToolUse(
        string Id,
        string Name,
        string? ServerName,
        HarnessToolUseType? Type)
    {
        public StringBuilder Input { get; } = new();
    }

    private sealed record CompletedToolUse(
        string Id,
        string Name,
        string? ServerName,
        HarnessToolUseType? Type,
        JsonElement Arguments,
        LlmToolOutput Result);
}
