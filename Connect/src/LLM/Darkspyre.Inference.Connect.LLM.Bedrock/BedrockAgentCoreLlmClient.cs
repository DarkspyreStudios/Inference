using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Amazon.BedrockAgentCore;
using Amazon.BedrockAgentCore.Model;
using Amazon.Runtime.Documents;
using Amazon.Runtime.EventStreams;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

/// <summary>Amazon Bedrock AgentCore Harness adapter.</summary>
internal sealed class BedrockAgentCoreLlmClient(IBedrockAgentCoreInvoker invoker)
    : ILlmClient, ILlmAgentResumeClient
{
    internal const string DefaultModelId = "default";

    private static readonly LlmProviderCapabilities Defaults = new()
    {
        SupportsStreaming = true,
        SupportsStreamingUsage = true,
        SupportsModelListing = false,
        SupportsJsonSchema = false,
        SupportsJsonObject = false,
        SupportsTemperature = false,
        SupportsToolUse = false,
        SupportsAgentSessions = true,
        Agent = NativeAgentContract.AgentCore,
        SupportsAgentToolUse = true,
        SupportsPromptImages = false,
        SupportsPromptDocuments = false,
        RichContentSupported = RichContentRoutes.AgentCore
    };

    public LlmTransport Transport => LlmTransport.AwsBedrockAgentCore;

    public LlmProviderDefinition ProviderDefinition =>
        BedrockProviderDefinitions.AgentCoreHarness;

    public LlmProviderCapabilities GetCapabilities(
        LlmConnection connection,
        string? modelId = null)
    {
        ValidateConnection(connection);
        LlmProviderCapabilities capabilities = Defaults.Apply(connection.CapabilityOverrides);
        return modelId is not null
            && connection.ModelCapabilityOverrides.TryGetValue(
                modelId,
                out LlmCapabilityOverrides? modelOverrides)
                    ? capabilities.Apply(modelOverrides)
                    : capabilities;
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConnection connection,
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        ValidateChatRequest(request, capabilities);
        if (request.Messages.Any(message => message.ContinuationState is not null))
        {
            throw new NotSupportedException("AgentCore cannot import adapter continuation state.");
        }
        LlmStructuredOutputEnforcement? enforcement = request.StructuredOutput is null
            ? null
            : StructuredOutput.ResolveEnforcement(request.StructuredOutput, capabilities);
        LlmAwsCredentials aws = RequireAws(connection);
        InvokeHarnessRequest invokeRequest = BuildChatRequest(connection, request);
        var text = new StringBuilder();
        LlmUsage? usage = null;
        string? stopReason = null;

        await foreach (IEventStreamEvent streamEvent in invoker.InvokeAsync(
                           aws,
                           invokeRequest,
                           cancellationToken))
        {
            switch (streamEvent)
            {
                case HarnessContentBlockDeltaEvent { Delta.Text: { Length: > 0 } delta }:
                    text.Append(delta);
                    yield return new LlmTextDeltaEvent(delta);
                    break;
                case HarnessContentBlockDeltaEvent
                {
                    Delta.ReasoningContent.Text: { Length: > 0 } reasoning
                }:
                    yield return new LlmActivityEvent(reasoning);
                    break;
                case HarnessContentBlockStartEvent { Start.ToolUse: not null }:
                    throw new InvalidOperationException(
                        "AgentCore returned a tool call on the chat lane even though tools were disabled. Use ILlmAgentClient for Harness tools.");
                case HarnessMessageStopEvent stopped:
                    stopReason = stopped.StopReason?.Value;
                    break;
                case HarnessMetadataEvent { Usage: not null } metadata:
                    usage = new LlmUsage(
                        metadata.Usage.InputTokens,
                        metadata.Usage.OutputTokens,
                        metadata.Usage.TotalTokens);
                    break;
            }
        }

        if (stopReason is null)
        {
            throw new InvalidOperationException(
                "AgentCore ended the Harness stream without a message-stop event.");
        }

        string finalText = text.ToString();
        bool refused = string.Equals(stopReason, "content_filtered", StringComparison.Ordinal);
        bool incomplete = stopReason is "max_tokens"
            or "max_output_tokens_exceeded"
            or "max_iterations_exceeded"
            or "model_context_window_exceeded"
            or "timeout_exceeded";
        LlmStructuredOutputValidation? validation = request.StructuredOutput is { } structured
            && enforcement is not null
                ? refused
                    ? new LlmStructuredOutputValidation(
                        enforcement.Value,
                        false,
                        false,
                        ["Structured output was not evaluated because the provider refused the request."])
                    : StructuredOutput.Validate(finalText, structured, enforcement.Value)
                : null;
        yield return new LlmChatCompletionEvent(new LlmChatResult
        {
            Text = finalText,
            Refusal = refused ? finalText : null,
            Status = refused
                ? LlmCompletionStatus.Refused
                : incomplete
                    ? LlmCompletionStatus.Incomplete
                    : LlmCompletionStatus.Completed,
            FinishReason = MapFinishReason(stopReason),
            IncompleteReason = incomplete ? stopReason : null,
            Usage = usage,
            StructuredOutput = validation,
            Diagnostics = new LlmProviderDiagnostics(200, null, null)
        });
    }

    public Task<IReadOnlyList<LlmModel>> ListModelsAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConnection(connection);
        return Task.FromResult<IReadOnlyList<LlmModel>>([]);
    }

    public Task<ILlmAgentSession> StartSessionAsync(
        string connectionId,
        LlmConnection connection,
        LlmAgentSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        LlmAgentNativeRuntimeOptions runtime = NativeAgentContract.ValidateStart(options, NativeAgentContract.AgentCore);
        LlmAgentHostRuntimePolicy policy = NativeAgentContract.HostPolicy(runtime);
        NativeAgentContract.ValidateConnection(connection, NativeAgentContract.AgentCore, supportsAgentToolUse: true,
            options.ModelId, options.Tools, options.ToolHandler);
        ValidateConnection(connection);
        ValidateSessionOptions(
            options.ModelId,
            runtime.WorkingDirectory,
            options.Tools,
            options.ToolHandler,
            runtime.Ephemeral,
            policy.Sandbox,
            policy.NetworkAllowed);
        string sessionId = $"darkspyre-inference-connect-{Guid.NewGuid():N}";
        return Task.FromResult<ILlmAgentSession>(new BedrockAgentCoreSession(
            invoker,
            RequireAws(connection),
            connection.Endpoint,
            sessionId,
            options.ModelId,
            options.Instructions,
            options.Tools,
            policy.Sandbox,
            policy.ApprovalPolicy,
            options.ToolHandler,
            NativeAgentContract.AgentCore.RichContentSupported.For(connection, null),
            NativeAgentContract.AgentCore.RichContentSupported.For(connection, options.ModelId),
            AgentOpeningContext.For(options, carriesAttachments: false)));
    }

    public Task<ILlmAgentSession> ResumeSessionAsync(
        string connectionId,
        LlmConnection connection,
        string threadId,
        LlmAgentResumeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConnection(connection);
        ValidateRuntimeSessionId(threadId);
        options ??= new LlmAgentResumeOptions();
        NativeAgentContract.ValidateConnection(connection, NativeAgentContract.AgentCore, supportsAgentToolUse: true,
            options.ModelId, options.Tools, options.ToolHandler);
        string modelId = options.ModelId ?? DefaultModelId;
        LlmAgentNativeRuntimeOptions runtime = NativeAgentContract.ResumeRuntime(options, NativeAgentContract.AgentCore);
        LlmAgentHostRuntimePolicy policy = NativeAgentContract.HostPolicy(runtime);
        ValidateSessionOptions(
            modelId,
            runtime.WorkingDirectory,
            options.Tools,
            options.ToolHandler,
            runtime.Ephemeral,
            policy.Sandbox,
            policy.NetworkAllowed);
        return Task.FromResult<ILlmAgentSession>(new BedrockAgentCoreSession(
            invoker,
            RequireAws(connection),
            connection.Endpoint,
            threadId,
            modelId,
            options.Instructions,
            options.Tools,
            policy.Sandbox,
            policy.ApprovalPolicy,
            options.ToolHandler,
            NativeAgentContract.AgentCore.RichContentSupported.For(connection, null),
            NativeAgentContract.AgentCore.RichContentSupported.For(connection, modelId)));
    }

    internal static InvokeHarnessRequest BuildChatRequest(
        LlmConnection connection,
        LlmChatRequest request)
    {
        var system = new List<HarnessSystemContentBlock>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            system.Add(new HarnessSystemContentBlock { Text = request.Instructions });
        }

        var messages = new List<HarnessMessage>();
        foreach (LlmChatMessage message in request.Messages)
        {
            if (message.Role is "system" or "developer")
            {
                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    system.Add(new HarnessSystemContentBlock { Text = message.Content });
                }
                continue;
            }

            if (message.Role is not ("user" or "assistant"))
            {
                throw new ArgumentException(
                    $"AgentCore does not support the chat role '{message.Role}'.",
                    nameof(request));
            }

            if (message.Content.Length == 0)
            {
                continue;
            }

            messages.Add(new HarnessMessage
            {
                Role = message.Role == "assistant"
                    ? HarnessConversationRole.Assistant
                    : HarnessConversationRole.User,
                Content = [new HarnessContentBlock { Text = message.Content }]
            });
        }

        if (messages.Count == 0)
        {
            throw new ArgumentException(
                "AgentCore requires at least one user or assistant message.",
                nameof(request));
        }

        var result = new InvokeHarnessRequest
        {
            HarnessArn = connection.Endpoint,
            RuntimeSessionId = $"darkspyre-inference-connect-{Guid.NewGuid():N}",
            Messages = messages,
            AllowedTools = []
        };
        if (system.Count > 0)
        {
            result.SystemPrompt = system;
        }
        if (request.MaxOutputTokens is int maxTokens)
        {
            result.MaxTokens = maxTokens;
        }
        result.Model = BuildModelConfiguration(request.ModelId);
        return result;
    }

    internal static HarnessModelConfiguration? BuildModelConfiguration(string? modelId) =>
        string.IsNullOrWhiteSpace(modelId)
        || string.Equals(modelId, DefaultModelId, StringComparison.OrdinalIgnoreCase)
            ? null
            : new HarnessModelConfiguration
            {
                BedrockModelConfig = new HarnessBedrockModelConfig { ModelId = modelId }
            };

    internal static List<HarnessTool>? BuildTools(
        IReadOnlyList<LlmAgentToolDefinition> tools) => tools.Count == 0
            ? null
            : tools.Select(tool => new HarnessTool
            {
                Type = HarnessToolType.Inline_function,
                Name = tool.Name,
                Config = new HarnessToolConfiguration
                {
                    InlineFunction = new HarnessInlineFunctionConfig
                    {
                        Description = tool.Description,
                        InputSchema = DeserializeDocument(tool.InputSchema.GetRawText())
                    }
                }
            }).ToList();

    internal static Document DeserializeDocument(string json) =>
        JsonSerializer.Deserialize<Document>(json);

    internal static string MapFinishReason(string? reason) => reason switch
    {
        "end_turn" or "stop_sequence" => "stop",
        "max_tokens" or "max_output_tokens_exceeded" => "length",
        "content_filtered" => "content_filter",
        "tool_use" => "tool_calls",
        _ => reason ?? "stop"
    };

    internal static void ValidateRuntimeSessionId(string sessionId)
    {
        if (sessionId.Length is < 33 or > 100
            || !sessionId.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_'))
        {
            throw new ArgumentException(
                "An AgentCore runtime session id must contain 33-100 letters, numbers, hyphens, or underscores.",
                nameof(sessionId));
        }
    }

    private static void ValidateChatRequest(
        LlmChatRequest request,
        LlmProviderCapabilities capabilities)
    {
        if (string.IsNullOrWhiteSpace(request.ModelId))
        {
            throw new ArgumentException("A model id is required.", nameof(request));
        }
        if (!capabilities.SupportsStreaming)
        {
            throw new NotSupportedException(
                "AgentCore Harness has no non-streaming request, so streaming cannot be turned off for this connection.");
        }
        if (request.Temperature is not null)
        {
            throw new NotSupportedException(
                "AgentCore Harness does not expose provider-neutral temperature control.");
        }
        if (request.MaxOutputTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Maximum output tokens must be positive.");
        }
        // AgentCore Harness exposes no reasoning control, so it accepts no level.
        Darkspyre.Inference.Connect.LLM.Providers.LlmReasoningEffort.Validate(
            request.ReasoningLevel,
            System.Collections.Frozen.FrozenSet<Darkspyre.Inference.Common.LLM.LlmReasoningLevel>.Empty);
        if (request.ProviderOptions.Count > 0)
        {
            throw new NotSupportedException(
                "AgentCore request provider options are not currently supported.");
        }
        LlmClientBase.ValidatePromptAttachments(request, capabilities);
        LlmClientBase.ValidateToolUse(request, capabilities);
    }

    private static void ValidateSessionOptions(
        string modelId,
        string workingDirectory,
        IReadOnlyList<LlmAgentToolDefinition> tools,
        LlmAgentToolHandler? toolHandler,
        bool ephemeral,
        LlmAgentSandboxMode sandbox,
        bool networkAllowed)
    {
        ArgumentNullException.ThrowIfNull(tools);
        NativeAgentContract.ValidateTools(tools);
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException("A model id is required.", nameof(modelId));
        }
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException(
                "A working directory is required by the agent contract.",
                nameof(workingDirectory));
        }
        if (ephemeral)
        {
            throw new NotSupportedException(
                "AgentCore Harness does not provide a non-persistent session mode.");
        }
        if (sandbox == LlmAgentSandboxMode.DangerFullAccess && !networkAllowed)
        {
            throw new NotSupportedException(
                "AgentCore cannot disable remote network access in danger-full-access mode.");
        }
        if (tools.Count > 0 && toolHandler is null)
        {
            throw new ArgumentException(
                "AgentCore host tools require a tool handler.",
                nameof(toolHandler));
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (LlmAgentToolDefinition tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name)
                || tool.Name.Length > 64
                || !tool.Name.All(character => char.IsAsciiLetterOrDigit(character)
                    || character is '-' or '_')
                || string.IsNullOrWhiteSpace(tool.Description)
                || tool.InputSchema.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "Every AgentCore tool requires a 1-64 character name containing only letters, numbers, hyphens, or underscores, a description, and a JSON object input schema.",
                    nameof(tools));
            }
            if (tool.DeferLoading)
            {
                throw new NotSupportedException(
                    "AgentCore inline functions do not support deferred tool loading.");
            }
            if (!names.Add(tool.Name))
            {
                throw new ArgumentException(
                    $"AgentCore tool names must be unique; '{tool.Name}' is duplicated.",
                    nameof(tools));
            }
        }
    }

    private void ValidateConnection(LlmConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Transport != Transport)
        {
            throw new ArgumentException(
                $"A {connection.Transport} connection cannot use the AgentCore client.",
                nameof(connection));
        }
        if (!connection.Endpoint.StartsWith("arn:", StringComparison.Ordinal)
            || !connection.Endpoint.Contains(":bedrock-agentcore:", StringComparison.Ordinal)
            || !connection.Endpoint.Contains(":harness/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The AgentCore connection endpoint must be a Harness ARN.",
                nameof(connection));
        }
        if (connection.AdditionalHeaders.Count > 0)
        {
            throw new NotSupportedException("AgentCore does not accept additional HTTP headers.");
        }
        if (connection.ProviderOptions.Count > 0)
        {
            throw new NotSupportedException(
                "AgentCore connection provider options are not currently supported.");
        }
        LlmAwsCredentials aws = RequireAws(connection);
        if (string.IsNullOrWhiteSpace(aws.Region))
        {
            throw new InvalidOperationException("AgentCore requires an AWS region.");
        }
        string[] arnParts = connection.Endpoint.Split(':');
        if (arnParts.Length > 3
            && !string.Equals(arnParts[3], aws.Region, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The Harness ARN region '{arnParts[3]}' does not match the AWS connection region '{aws.Region}'.",
                nameof(connection));
        }
    }

    private static LlmAwsCredentials RequireAws(LlmConnection connection) =>
        LlmCredential.Parse(connection.Credential)?.Aws
        ?? throw new InvalidOperationException(
            "AgentCore requires AWS connection settings containing a region and optional profile.");
}
