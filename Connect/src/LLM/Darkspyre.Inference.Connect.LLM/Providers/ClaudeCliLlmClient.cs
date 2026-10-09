using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Chat-style adapter over the separately authenticated official Claude CLI.</summary>
internal sealed class ClaudeCliLlmClient(HttpClient http, IClaudeCliRunner runner, IClaudeCliSessionProcessFactory processFactory)
    : LlmClientBase(http),
        ILlmContextEstimator,
        ILlmAccountUsageClient,
        IExternalLlmAuthenticationProbe,
        IStreamingCredentialRefreshPolicy,
        ILlmCatalogCapabilities
{
    private const string CatalogEndpoint = "https://api.anthropic.com/v1";
    private const string AnthropicVersion = "2023-06-01";
    private const string ClaudeCodeVersion = "2.1.278";
    private const string OAuthBetas = "claude-code-20250219,oauth-2025-04-20";
    private const int ModelPageSize = 1000;
    private const string ClaudeSystemPrompt =
        "You are a general-purpose language model. Follow the supplied instructions and conversation. "
        + "Do not use tools or access files, applications, or external resources.";
    private readonly string _catalogSessionId = Guid.NewGuid().ToString();

    public override LlmTransport Transport => LlmTransport.AnthropicClaudeCli;

    public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.ClaudeSubscription;

    protected override bool OffersNonStreamingRequests => true;

    protected override bool SupportsResourcePreparation => true;

    /// <summary>The effort levels the Claude CLI accepts, for its chat and agent lanes.</summary>
    internal static readonly IReadOnlySet<LlmReasoningLevel> ReasoningLevels = LlmReasoningEffort.Levels(
        LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High,
        LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum);

    private static readonly IReadOnlySet<LlmReasoningLevel> Claude46ReasoningLevels = LlmReasoningEffort.Levels(
        LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.Maximum);

    protected override LlmProviderCapabilities DefaultCapabilities => Capabilities;

    private static readonly LlmProviderCapabilities Capabilities = new()
    {
        OutputLimitEnforcement = Common.LLM.LlmOutputLimitEnforcement.Unknown,
        ToolHistory = LlmToolHistory.Text,
        SupportsStreaming = InferenceSupport.Supported,
        SupportsReasoningText = InferenceSupport.Supported,
        SupportsStreamingUsage = InferenceSupport.Supported,
        SupportsUsageDeltas = InferenceSupport.Supported,
        SupportsModelListing = InferenceSupport.Supported,
        SupportsAccountUsage = InferenceSupport.Supported,
        PromptCache = PromptCaching.Automatic,
        SupportsJsonSchema = InferenceSupport.Supported,
        SupportsJsonObject = InferenceSupport.Unsupported,
        ReasoningLevels = ReasoningLevels,
        SupportsTemperature = InferenceSupport.Unsupported,
        SupportsToolUse = InferenceSupport.Unsupported,
        SupportsAgentSessions = InferenceSupport.Supported,
        Agent = NativeAgentContract.Claude,
        SupportsAgentToolUse = InferenceSupport.Supported,
        SupportsPromptImages = InferenceSupport.Supported,
        SupportsPromptDocuments = InferenceSupport.Supported,
        RichContentSupported = RichContentRoutes.ClaudePrompt
    };

    public override LlmProviderCapabilities GetCapabilities(
        LlmConnection connection,
        string? modelId = null)
    {
        return ResolveCapabilities(connection, modelId);
    }

    internal static LlmProviderCapabilities ResolveCapabilities(LlmConnection connection, string? modelId)
    {
        _ = ClaudeCliConfiguration.ValidateConnection(connection);
        IReadOnlySet<LlmReasoningLevel>? offered = CatalogReasoningLevels(connection, modelId)
            ?? (modelId is "claude-sonnet-4-6" or "claude-opus-4-6"
                or "claude-sonnet-4-6[1m]" or "claude-opus-4-6[1m]" ? Claude46ReasoningLevels : null);
        LlmProviderCapabilities capabilities = LlmCapabilityOverrideLookup.Apply(
            Capabilities with { ReasoningLevels = offered ?? ReasoningLevels }, connection, modelId);
        return capabilities with
        {
            ReasoningLevels = offered is not null
                ? capabilities.ReasoningLevels.Intersect(offered).ToFrozenSet()
                : capabilities.ReasoningLevels,
            SupportsModelListing = InferenceSupport.Supported,
            SupportsJsonObject = InferenceSupport.Unsupported,
            SupportsTemperature = InferenceSupport.Unsupported,
            SupportsTopP = InferenceSupport.Unsupported,
            SupportsTopK = InferenceSupport.Unsupported,
            SupportsSeed = InferenceSupport.Unsupported,
            SupportsPresencePenalty = InferenceSupport.Unsupported,
            SupportsFrequencyPenalty = InferenceSupport.Unsupported,
            MaxStopSequences = 0,
            SupportsToolUse = InferenceSupport.Unsupported,
            // Attachments travel only as stream-json content blocks; the non-streaming request reads plain text.
            SupportsPromptImages = capabilities.SupportsStreaming,
            SupportsPromptDocuments = capabilities.SupportsStreaming,
            RichContentSupported = (capabilities.SupportsStreaming != InferenceSupport.Unsupported) ? capabilities.RichContentSupported
                : capabilities.RichContentSupported with
                {
                    Images = LlmRichContentCarriage.Unsupported,
                    PdfDocuments = LlmRichContentCarriage.Unsupported,
                    TextDocuments = capabilities.RichContentSupported.TextDocuments == LlmRichContentCarriage.Unsupported
                        ? LlmRichContentCarriage.Unsupported : LlmRichContentCarriage.Text,
                    PreservesOrder = InferenceSupport.Supported
                }
        };
    }

    private static FrozenSet<LlmReasoningLevel>? CatalogReasoningLevels(LlmConnection connection, string? modelId)
    {
        if (ILlmCatalogCapabilities.EntryFor(connection, modelId) is not { ValueKind: JsonValueKind.Object } entry)
            return null;
        if (entry.TryGetProperty("supportsEffort", out JsonElement support) && support.ValueKind == JsonValueKind.False)
            return FrozenSet<LlmReasoningLevel>.Empty;
        if (!entry.TryGetProperty("supportedEffortLevels", out JsonElement levels) || levels.ValueKind != JsonValueKind.Array)
            return null;
        return ReasoningLevels.Where(level => levels.EnumerateArray().Any(value =>
            value.ValueKind == JsonValueKind.String && value.GetString() == LlmReasoningEffort.Word(level))).ToFrozenSet();
    }

    protected override void ValidateReasoningRequest(LlmConnection connection, LlmChatRequest request,
        LlmProviderCapabilities capabilities) => _ = MapReasoning(connection, request.ModelId, request.ReasoningLevel);

    /// <summary>Maps caller effort to the CLI's native effort vocabulary for both chat and agent turns.</summary>
    internal static LlmReasoningLevel? MapReasoning(LlmConnection connection, string? modelId, LlmReasoningLevel? requested)
    {
        if (requested is null) return null;
        if (ILlmCatalogCapabilities.EntryFor(connection, modelId) is { ValueKind: JsonValueKind.Object } entry
            && entry.TryGetProperty("supportsEffort", out JsonElement support) && support.ValueKind == JsonValueKind.False)
            throw new InferenceException(InferenceFailureCode.InvalidRequest,
                "The selected Claude CLI model reports no effort control.");
        LlmReasoningLevel[] row = requested.Value switch
        {
            LlmReasoningLevel.None or LlmReasoningLevel.Minimal or LlmReasoningLevel.Low =>
                [LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum],
            LlmReasoningLevel.Medium =>
                [LlmReasoningLevel.Medium, LlmReasoningLevel.Low, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum],
            LlmReasoningLevel.High =>
                [LlmReasoningLevel.High, LlmReasoningLevel.Medium, LlmReasoningLevel.Low, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum],
            LlmReasoningLevel.ExtraHigh =>
                [LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.High, LlmReasoningLevel.Medium, LlmReasoningLevel.Low, LlmReasoningLevel.Maximum],
            LlmReasoningLevel.Maximum =>
                [LlmReasoningLevel.Maximum, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.High, LlmReasoningLevel.Medium, LlmReasoningLevel.Low],
            _ => throw new ArgumentOutOfRangeException(nameof(requested), requested, "Unknown reasoning level.")
        };
        IReadOnlySet<LlmReasoningLevel> offered = ResolveCapabilities(connection, modelId).ReasoningLevels;
        foreach (LlmReasoningLevel native in row)
            if (offered.Count == 0 || offered.Contains(native)) return native;
        throw new NotSupportedException("The selected Claude CLI effort choices contain no mapped native effort.");
    }

    public override IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConnection connection,
        LlmChatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Resources is null
            ? StreamPreparedAsync(connection, request, cancellationToken)
            : LlmResourcePreparation.StreamInlineAsync(request, input => _ = ValidateChatRequest(connection, input),
                (input, token) => StreamPreparedAsync(connection, input, token), cancellationToken);
    }

    private (LlmProviderCapabilities Capabilities, LlmStructuredOutputEnforcement? Enforcement) ValidateChatRequest(
        LlmConnection connection, LlmChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        ValidateRequest(connection, request, capabilities);
        if (request.Resources is not null && (capabilities.SupportsPromptDocuments == InferenceSupport.Unsupported))
            throw new NotSupportedException("Claude CLI requires stream-json input to materialize caller-owned resources.");
        if (request.Messages.Any(message => message.ContinuationState is not null))
            throw new NotSupportedException("Claude CLI chat cannot import adapter continuation state.");
        ValidateClaudeCliRequest(connection, request);
        LlmStructuredOutputEnforcement? enforcement = request.StructuredOutput is null
            ? null : StructuredOutput.ResolveEnforcement(request.StructuredOutput, capabilities);
        return (capabilities, enforcement);
    }

    private async IAsyncEnumerable<LlmStreamEvent> StreamPreparedAsync(
        LlmConnection connection,
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        (LlmProviderCapabilities capabilities, LlmStructuredOutputEnforcement? enforcement) = ValidateChatRequest(connection, request);
        request = request with { ReasoningLevel = MapReasoning(connection, request.ModelId, request.ReasoningLevel) };
        string prompt = BuildClaudePrompt(connection, request);
        string executable = ClaudeCliConfiguration.ResolveExecutable(connection.Endpoint);
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        if ((capabilities.SupportsStreaming == InferenceSupport.Unsupported))
        {
            LlmChatResult completeResult = await CompleteAsync(executable, prompt, request, enforcement, credential, cancellationToken);
            foreach (LlmStreamEvent completeEvent in CompleteResponseEvents.From(completeResult, []))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return completeEvent;
            }

            yield break;
        }

        string standardInput = BuildClaudeStandardInput(connection, request, prompt);
        var arguments = new List<string>
        {
            "-p",
            "--verbose",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--include-partial-messages"
        };
        AddChatArguments(arguments, request, enforcement);

        ClaudeTerminalResult? terminal = null;
        string? observedModel = null;
        ClaudeCliProcessResult? processResult = null;
        LlmProviderException? parseException = null;
        bool emittedText = false;
        var usage = new ClaudeStreamUsage((capabilities.SupportsUsageDeltas != InferenceSupport.Unsupported));
        await foreach (ClaudeCliProcessEvent processEvent in InvokeClaudeStreamingAsync(
                           executable,
                           arguments,
                           standardInput,
                           (capabilities.SupportsReasoningText != InferenceSupport.Unsupported),
                           request.MaxOutputTokens,
                           cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processEvent is ClaudeCliProcessCompleted completion)
            {
                processResult = completion.Result;
                continue;
            }
            if (processEvent is not ClaudeCliOutputLine outputLine
                || string.IsNullOrWhiteSpace(outputLine.Line))
            {
                continue;
            }

            ParsedClaudeLine parsed;
            LlmUsageDeltaEvent? observation;
            LlmAccountUsageSnapshot? accountObservation;
            try
            {
                using JsonDocument document = JsonDocument.Parse(outputLine.Line);
                observedModel = ReadObservedModel(document.RootElement, connection, credential) ?? observedModel;
                accountObservation = LlmJson.GetString(document.RootElement, "type") == "rate_limit_event"
                    && document.RootElement.TryGetProperty("rate_limit_info", out JsonElement info)
                    ? ClaudeRateLimitEvent.Parse(info, DateTimeOffset.UtcNow) : null;
                observation = usage.Observe(document.RootElement);
                parsed = ParseClaudeLine(
                    document.RootElement,
                    enforcement == LlmStructuredOutputEnforcement.ProviderJsonSchema,
                    credential);
            }
            catch (JsonException exception)
            {
                parseException ??= new LlmProviderException(
                    "Claude CLI returned malformed streaming JSON.",
                    Transport,
                    providerCode: "claude_cli_invalid_json",
                    innerException: exception);
                continue;
            }

            if (accountObservation is not null) { yield return new AccountUsageObservedEvent(accountObservation); }
            if (observation is not null) { yield return observation; }
            if ((capabilities.SupportsReasoningText != InferenceSupport.Unsupported) && parsed.ReasoningDelta is { Length: > 0 } reasoning)
            { yield return new LlmReasoningDeltaEvent(reasoning, IsSummary: true); }
            if (!string.IsNullOrEmpty(parsed.Activity))
            {
                yield return new LlmActivityEvent(parsed.Activity);
            }
            if (!string.IsNullOrEmpty(parsed.TextDelta))
            {
                emittedText = true;
                yield return new LlmTextDeltaEvent(parsed.TextDelta);
            }
            if (parsed.Terminal is not null)
            {
                terminal = parsed.Terminal;
                usage.Finish();
            }
        }

        if (processResult is null)
        {
            throw new LlmProviderException(
                "Claude CLI returned no process completion event.",
                Transport,
                providerCode: "claude_cli_invalid_response");
        }
        if (processResult.ExitCode != 0 || processResult.StandardInputError is not null)
        {
            throw CreateClaudeExitException(processResult, credential, terminal);
        }
        if (parseException is not null)
        {
            throw parseException;
        }
        LlmChatResult result = BuildResult(terminal, request, enforcement, observedModel);
        if (!emittedText && terminal!.Text.Length > 0)
        {
            yield return result.Status == LlmCompletionStatus.Refused
                ? new LlmRefusalDeltaEvent(terminal.Text)
                : new LlmTextDeltaEvent(terminal.Text);
        }

        yield return new LlmChatCompletionEvent(result);
    }

    /// <summary>
    /// Runs one non-streaming turn with <c>--output-format json</c>. The CLI accepts stream-json input only with
    /// stream-json output, so the prompt travels as plain text on standard input.
    /// </summary>
    private async Task<LlmChatResult> CompleteAsync(
        string executable,
        string prompt,
        LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement,
        LlmCredential? credential,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-p", "--output-format", "json" };
        AddChatArguments(arguments, request, enforcement);
        ClaudeCliProcessResult processResult;
        try
        {
            processResult = await InvokeClaudeAsync(executable, arguments, prompt, request.MaxOutputTokens, cancellationToken);
        }
        catch (ClaudeCliUnavailableException exception)
        {
            throw ClaudeCliConfiguration.Unavailable(exception);
        }

        if (processResult.ExitCode != 0 || processResult.StandardInputError is not null)
        {
            throw CreateClaudeExitException(processResult, credential);
        }

        ParsedClaudeLine parsed;
        try
        {
            parsed = ParseClaudeLine(
                processResult.StandardOutput,
                enforcement == LlmStructuredOutputEnforcement.ProviderJsonSchema,
                credential);
        }
        catch (JsonException exception)
        {
            throw new LlmProviderException(
                "Claude CLI returned malformed JSON.",
                Transport,
                providerCode: "claude_cli_invalid_json",
                innerException: exception);
        }

        return BuildResult(parsed.Terminal, request, enforcement, null);
    }

    /// <summary>The chat-lane arguments both output formats share.</summary>
    private static void AddChatArguments(
        List<string> arguments,
        LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement)
    {
        arguments.AddRange(
        [
            "--no-session-persistence",
            "--safe-mode",
            "--disable-slash-commands",
            "--tools", string.Empty,
            "--permission-prompts", "none",
            "--system-prompt", ClaudeSystemPrompt,
            "--model", request.ModelId
        ]);
        if (request.ReasoningLevel is { } level)
        {
            arguments.AddRange(["--effort", LlmReasoningEffort.Word(level)]);
        }
        if (enforcement == LlmStructuredOutputEnforcement.ProviderJsonSchema)
        {
            arguments.AddRange(["--json-schema", request.StructuredOutput!.Schema.GetRawText()]);
        }
    }

    private LlmChatResult BuildResult(
        ClaudeTerminalResult? terminal,
        LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement,
        string? observedModel)
    {
        if (terminal is null)
        {
            throw new LlmProviderException(
                "Claude CLI returned no terminal result event.",
                Transport,
                providerCode: "claude_cli_invalid_response");
        }
        if (terminal.IsError)
        {
            throw new LlmProviderException(
                "Claude CLI reported a provider error. Run claude in a terminal to verify sign-in and model access."
                    + terminal.ErrorDetail,
                Transport,
                providerCode: "claude_cli_error");
        }

        bool refused = string.Equals(terminal.StopReason, "refusal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(terminal.TerminalReason, "refusal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(terminal.Subtype, "refusal", StringComparison.OrdinalIgnoreCase);
        bool incomplete = string.Equals(terminal.StopReason, "max_tokens", StringComparison.OrdinalIgnoreCase)
            || string.Equals(terminal.TerminalReason, "max_tokens", StringComparison.OrdinalIgnoreCase)
            || string.Equals(terminal.StopReason, "model_context_window_exceeded", StringComparison.OrdinalIgnoreCase)
            || string.Equals(terminal.TerminalReason, "model_context_window_exceeded", StringComparison.OrdinalIgnoreCase);
        string? finishReason = terminal.StopReason ?? terminal.TerminalReason;
        bool completed = string.Equals(terminal.Subtype, "success", StringComparison.Ordinal)
            && (finishReason is null
                || string.Equals(finishReason, "end_turn", StringComparison.OrdinalIgnoreCase)
                || string.Equals(finishReason, "stop_sequence", StringComparison.OrdinalIgnoreCase)
                || terminal.HasStructuredOutput && string.Equals(finishReason, "tool_use", StringComparison.OrdinalIgnoreCase));
        LlmStructuredOutputValidation? validation = request.StructuredOutput is null
            ? null
            : refused
                ? new LlmStructuredOutputValidation(
                    enforcement!.Value,
                    false,
                    false,
                    ["Structured output was not evaluated because the provider refused the request."])
                : StructuredOutput.Validate(
                    terminal.Text,
                    request.StructuredOutput,
                    enforcement!.Value);

        return new LlmChatResult
        {
            Text = refused ? string.Empty : terminal.Text,
            ModelId = observedModel,
            Refusal = refused ? terminal.Text : null,
            Status = refused
                ? LlmCompletionStatus.Refused
                : incomplete
                    ? LlmCompletionStatus.Incomplete
                    : completed ? LlmCompletionStatus.Completed : LlmCompletionStatus.Unknown,
            FinishReason = finishReason,
            IncompleteReason = incomplete ? finishReason : null,
            Usage = terminal.Usage,
            ProviderResponseId = terminal.SessionId,
            StructuredOutput = validation,
            Diagnostics = new LlmProviderDiagnostics(200, null, null)
        };
    }

    public bool RequiresFreshOAuthCredentialForStreaming(LlmConnection connection)
    {
        _ = ClaudeCliConfiguration.ValidateConnection(connection);
        return false;
    }

    public async Task<bool> IsAuthenticatedAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default)
    {
        _ = ClaudeCliConfiguration.ValidateConnection(connection);
        try
        {
            ClaudeCliProcessResult result = await InvokeClaudeAsync(
                ClaudeCliConfiguration.ResolveExecutable(connection.Endpoint),
                ["auth", "status", "--json"],
                standardInput: null,
                maxOutputTokens: null,
                cancellationToken);
            if (result.ExitCode != 0 || result.StandardInputError is not null)
            {
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement root = document.RootElement;
            return root.TryGetProperty("loggedIn", out JsonElement loggedIn)
                && loggedIn.ValueKind == JsonValueKind.True
                && root.TryGetProperty("apiProvider", out JsonElement provider)
                && string.Equals(provider.GetString(), "firstParty", StringComparison.OrdinalIgnoreCase);
        }
        catch (ClaudeCliUnavailableException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<LlmAccountUsageSnapshot> GetAccountUsageAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default)
    {
        LlmCredential? credential = ClaudeCliConfiguration.ValidateConnection(connection);
        ClaudeCliProcessResult result;
        try
        {
            result = await InvokeClaudeAsync(
                ClaudeCliConfiguration.ResolveExecutable(connection.Endpoint),
                [
                    "-p",
                    "/usage",
                    "--output-format", "json",
                    "--no-session-persistence",
                    "--setting-sources", "user"
                ],
                standardInput: null,
                maxOutputTokens: null,
                cancellationToken);
        }
        catch (ClaudeCliUnavailableException exception)
        {
            throw ClaudeCliConfiguration.Unavailable(exception);
        }

        if (result.ExitCode != 0 || result.StandardInputError is not null)
        {
            throw CreateClaudeExitException(result, credential);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("is_error", out JsonElement error)
                && error.ValueKind == JsonValueKind.True)
            {
                throw new LlmProviderException(
                    "Claude CLI reported an account-usage error. Run claude in a terminal to verify sign-in."
                        + ClaudeCliConfiguration.ErrorDetail(root, credential),
                    Transport,
                    providerCode: "claude_cli_usage_error");
            }

            string summary = LlmJson.GetString(root, "result")
                ?? throw new JsonException("The result property is missing.");
            return ClaudeAccountUsageParser.Parse(
                AccountUsageSanitizer.Sanitize(summary, credential),
                DateTimeOffset.UtcNow);
        }
        catch (JsonException exception)
        {
            throw new LlmProviderException(
                "Claude CLI returned invalid account-usage JSON.",
                Transport,
                providerCode: "claude_cli_invalid_account_usage",
                innerException: exception);
        }
        catch (FormatException exception)
        {
            throw new LlmProviderException(
                "Claude CLI returned an unrecognized account-usage report.",
                Transport,
                providerCode: "claude_cli_invalid_account_usage",
                innerException: exception);
        }
    }

    public override async Task<IReadOnlyList<LlmModel>> ListModelsAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default)
    {
        LlmCredential? credential = ClaudeCliConfiguration.ValidateConnection(connection);
        if (!HasCatalogCredential(credential))
        {
            using var cliConsumption = SdkHttpConsumption.Enter(Http, cancellationToken);
            void Fence() { cliConsumption.Token.ThrowIfCancellationRequested(); cliConsumption.Fence(); }
            IReadOnlyList<LlmModel> options = await ClaudeCliModelCatalog.ReadAsync(processFactory,
                ClaudeCliConfiguration.ResolveExecutable(connection.Endpoint), Fence, cliConsumption.Token).ConfigureAwait(false);
            return cliConsumption.Publish(options);
        }

        LlmOAuthTokens catalogCredential = credential!.OAuth!;

        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken); cancellationToken = consumption.Token;
        var models = new List<LlmModel>();
        string? afterId = null;
        while (true)
        {
            string query = $"?limit={ModelPageSize}"
                + (afterId is null ? string.Empty : $"&after_id={Uri.EscapeDataString(afterId)}");
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{CatalogEndpoint}/models{query}");
            ApplyCatalogHeaders(request, catalogCredential);
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
            string[] secrets = request.Headers.SelectMany(header => header.Value).ToArray();
            await ProviderHttp.ThrowIfUnsuccessfulAsync(
                response,
                Transport,
                credential,
                "Anthropic model discovery",
                secrets,
                cancellationToken);
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken);
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException exception)
            {
                throw ProtocolException(
                    "Anthropic model discovery returned invalid JSON.",
                    response,
                    body,
                    credential,
                    exception, secrets);
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("data", out JsonElement data)
                    || data.ValueKind != JsonValueKind.Array)
                {
                    break;
                }

                models.AddRange(data.EnumerateArray()
                    .Select(model => new LlmModel(
                        LlmJson.GetString(model, "id") ?? string.Empty,
                        LlmJson.GetString(model, "display_name")
                            ?? LlmJson.GetString(model, "id")
                            ?? string.Empty))
                    .Where(model => model.Id.Length > 0));

                bool hasMore = root.TryGetProperty("has_more", out JsonElement more)
                    && more.ValueKind == JsonValueKind.True;
                afterId = LlmJson.GetString(root, "last_id");
                if (!hasMore || string.IsNullOrEmpty(afterId))
                {
                    break;
                }
            }
        }

        return consumption.Publish<IReadOnlyList<LlmModel>>([.. models.OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)]);
    }

    private static void ValidateClaudeCliRequest(
        LlmConnection connection,
        LlmChatRequest request)
    {
        _ = ClaudeCliConfiguration.ValidateConnection(connection);
        if (request.ProviderOptions.Count > 0)
        {
            throw new NotSupportedException("Claude CLI request provider options are not supported.");
        }
    }

    public string? EstimateDiagnostic => LocalTokenCount.CliDiagnostic;

    /// <summary>Counts the prompt this client would send. Attachment content is not countable here.</summary>
    // Claude's tokenizer counts more tokens than the local one. On a real 321-entry session history, Haiku 5.5, Sonnet 5.5
    // and Opus 5.5 each charged 1.50 input tokens per locally counted token (measured through this CLI, 2026-10-09).
    private const double ClaudeTokensPerLocalToken = 1.5;

    public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request)
    {
        // Another provider's continuation state is never sent to the CLI: an agent opening drops it, and chat refuses it
        // when sending. The estimate counts what the CLI receives.
        request = request with { Messages = [.. request.Messages.Select(message => message with { ContinuationState = null })] };
        _ = ValidateChatRequest(connection, request);
        // Selected caller-owned content is unknown to local accounting.
        if (request.Resources is not null) return null;
        long attachments = 0;
        foreach (var message in request.Messages)
        {
            foreach (LlmMessageAttachment attachment in message.Attachments)
            {
                if (attachment is not LlmAttachment inline) return null;
                attachments += ClaudeAttachmentTokens.Estimate(inline.Kind, inline.MediaType, inline.Data.Span);
            }
            foreach (var result in ClientTools.Functions(message.ToolResults))
                foreach (SentToolPart part in ProjectToolOutput(connection, request.ModelId, result.Output).Parts)
                    attachments += part switch
                    {
                        SentImage image => ClaudeAttachmentTokens.Estimate(LlmBinaryKind.Image, image.MediaType, image.Data.Span),
                        SentFile file => ClaudeAttachmentTokens.Estimate(LlmBinaryKind.Document, file.MediaType, file.Data.Span),
                        _ => 0,
                    };
        }
        long text = (long)Math.Ceiling(LocalTokenCount.Count(BuildClaudePrompt(connection, request)) * ClaudeTokensPerLocalToken);
        return new ContextEstimate(text + attachments);
    }

    private string BuildClaudePrompt(LlmConnection connection, LlmChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Instructions) && request.Messages.Count == 0)
        {
            throw new ArgumentException(
                "A prompt requires instructions or at least one message.",
                nameof(request));
        }

        var prompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            prompt.AppendLine("INSTRUCTIONS");
            prompt.AppendLine(request.Instructions.Trim());
            prompt.AppendLine();
        }
        if (request.Messages.Count > 0)
        {
            prompt.AppendLine("CONVERSATION");
            TranscriptText.Append(prompt, request.Messages, output => ProjectToolOutput(connection, request.ModelId, output));
        }
        if (request.StructuredOutput is { } structured)
        {
            prompt.AppendLine();
            prompt.AppendLine("OUTPUT CONTRACT");
            prompt.Append("Return only one JSON value named '")
                .Append(structured.Name)
                .AppendLine("'. Do not use Markdown fences or include explanatory text.");
            if (!string.IsNullOrWhiteSpace(structured.Description))
            {
                prompt.AppendLine(structured.Description.Trim());
            }
        }

        return prompt.ToString();
    }

    private string BuildClaudeStandardInput(LlmConnection connection, LlmChatRequest request, string prompt)
    {
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt });
        foreach (LlmAttachment attachment in request.Messages.SelectMany(item => item.Attachments).Select(LlmAttachments.RequireBinary))
        {
            content.Add(item: AnthropicAttachments.BuildContentBlock(attachment));
        }
        foreach (LlmToolResult result in request.Messages.SelectMany(message => ClientTools.Functions(message.ToolResults)))
            foreach (SentToolPart part in ProjectToolOutput(connection, request.ModelId, result.Output).Parts)
                if (part is SentImage or SentFile) content.Add(item: AnthropicAttachments.BuildToolPart(part));

        return new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content }
        }.ToJsonString() + "\n";
    }

    private string? ReadObservedModel(JsonElement root, LlmConnection connection, LlmCredential? credential)
    {
        if (root.TryGetProperty("parent_tool_use_id", out JsonElement parent) && parent.ValueKind != JsonValueKind.Null)
            return null;
        string? type = LlmJson.GetString(root, "type");
        JsonElement? message = type == "assistant" ? LlmJson.GetObject(root, "message")
            : type == "stream_event" && LlmJson.GetObject(root, "event") is { } streamEvent
                && LlmJson.GetString(streamEvent, "type") == "message_start"
                ? LlmJson.GetObject(streamEvent, "message") : null;
        return message is { } observed
            ? ObservedModel.Read(observed, "model", Transport, connection, credential, null) : null;
    }

    private static ParsedClaudeLine ParseClaudeLine(string line, bool preferStructuredOutput, LlmCredential? credential)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return ParseClaudeLine(document.RootElement, preferStructuredOutput, credential);
    }

    private static ParsedClaudeLine ParseClaudeLine(JsonElement root, bool preferStructuredOutput, LlmCredential? credential)
    {
        string type = LlmJson.GetString(root, "type") ?? string.Empty;
        if (string.Equals(type, "system", StringComparison.Ordinal))
        {
            string subtype = LlmJson.GetString(root, "subtype") ?? string.Empty;
            string? activity = string.Equals(subtype, "init", StringComparison.Ordinal)
                ? "Claude CLI connected; waiting for model output…"
                : subtype.Contains("fallback", StringComparison.OrdinalIgnoreCase)
                    ? "Claude CLI is switching to an available model…"
                    : null;
            return new ParsedClaudeLine(null, null, activity);
        }
        if (string.Equals(type, "stream_event", StringComparison.Ordinal)
            && LlmJson.GetObject(root, "event") is { } streamEvent
            && string.Equals(LlmJson.GetString(streamEvent, "type"), "content_block_delta", StringComparison.Ordinal)
            && LlmJson.GetObject(streamEvent, "delta") is { } delta)
        {
            return LlmJson.GetString(delta, "type") switch
            {
                "text_delta" => new ParsedClaudeLine(LlmJson.GetString(delta, "text"), null, null),
                "thinking_delta" => new ParsedClaudeLine(null, null, null) { ReasoningDelta = LlmJson.GetString(delta, "thinking") },
                _ => new ParsedClaudeLine(null, null, null)
            };
        }
        if (!string.Equals(type, "result", StringComparison.Ordinal))
        {
            return new ParsedClaudeLine(null, null, null);
        }

        string text = LlmJson.GetString(root, "result") ?? string.Empty;
        bool hasStructuredOutput = false;
        if (preferStructuredOutput
            && root.TryGetProperty("structured_output", out JsonElement structuredOutput)
            && structuredOutput.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            text = structuredOutput.GetRawText();
            hasStructuredOutput = true;
        }
        bool isError = root.TryGetProperty("is_error", out JsonElement error)
            && error.ValueKind == JsonValueKind.True;
        LlmUsage? usage = ClaudeStreamUsage.ReadFinal(root);
        return new ParsedClaudeLine(
            null,
            new ClaudeTerminalResult(
                text,
                hasStructuredOutput,
                isError,
                LlmJson.GetString(root, "subtype"),
                LlmJson.GetString(root, "stop_reason"),
                LlmJson.GetString(root, "terminal_reason"),
                LlmJson.GetString(root, "session_id"),
                usage,
                isError ? ClaudeCliConfiguration.ErrorDetail(root, credential) : string.Empty),
            "Claude CLI response received; validating output…");
    }

    private async Task<ClaudeCliProcessResult> InvokeClaudeAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? standardInput,
        int? maxOutputTokens,
        CancellationToken cancellationToken)
    {
        string workingDirectory = CliInfrastructure.CreateWorkingDirectory("claude");
        try
        {
            return await runner.RunAsync(
                new ClaudeCliInvocation(executable, arguments, standardInput, workingDirectory)
                { MaxOutputTokens = maxOutputTokens },
                cancellationToken);
        }
        finally
        {
            CliInfrastructure.DeleteWorkingDirectory(workingDirectory);
        }
    }

    private async IAsyncEnumerable<ClaudeCliProcessEvent> InvokeClaudeStreamingAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string standardInput,
        bool requestReasoningText,
        int? maxOutputTokens,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string workingDirectory = CliInfrastructure.CreateWorkingDirectory("claude");
        try
        {
            arguments = [.. arguments, "--thinking-display", requestReasoningText ? "summarized" : "omitted"];
            if (requestReasoningText)
            {
                string settingsPath = Path.Combine(workingDirectory, "claude-settings.json");
                var settings = new JsonObject { ["showThinkingSummaries"] = true };
                await File.WriteAllTextAsync(settingsPath, settings.ToJsonString(), cancellationToken).ConfigureAwait(false);
                arguments = [.. arguments, "--settings", settingsPath];
            }
            IAsyncEnumerable<ClaudeCliProcessEvent> events = runner.StreamAsync(
                new ClaudeCliInvocation(executable, arguments, standardInput, workingDirectory)
                { MaxOutputTokens = maxOutputTokens },
                cancellationToken);
            IAsyncEnumerator<ClaudeCliProcessEvent> enumerator = events.GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    ClaudeCliProcessEvent processEvent;
                    try
                    {
                        if (!await enumerator.MoveNextAsync())
                        {
                            break;
                        }
                        processEvent = enumerator.Current;
                    }
                    catch (ClaudeCliUnavailableException exception)
                    {
                        throw ClaudeCliConfiguration.Unavailable(exception);
                    }

                    yield return processEvent;
                }
            }
            finally
            {
                // A process that is still running continues to its exit in the background.
                await enumerator.DisposeAsync();
            }
        }
        finally
        {
            CliInfrastructure.DeleteWorkingDirectory(workingDirectory);
        }
    }

    private void ApplyCatalogHeaders(HttpRequestMessage request, LlmOAuthTokens oauth)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauth.AccessToken);
        request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBetas);
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            $"claude-cli/{ClaudeCodeVersion} (external, cli)");
        request.Headers.TryAddWithoutValidation("x-app", "cli");
        request.Headers.TryAddWithoutValidation(
            "X-Claude-Code-Session-Id",
            _catalogSessionId);
        request.Headers.TryAddWithoutValidation("anthropic-dangerous-direct-browser-access", "true");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static bool HasCatalogCredential(LlmCredential? credential) =>
        !string.IsNullOrWhiteSpace(credential?.OAuth?.AccessToken);

    private static LlmProviderException CreateClaudeExitException(ClaudeCliProcessResult result, LlmCredential? credential,
        ClaudeTerminalResult? terminal = null)
    {
        string detail = terminal?.IsError == true ? terminal.ErrorDetail : string.Empty;
        if (terminal is null && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
                JsonElement root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("is_error", out JsonElement error)
                    && error.ValueKind == JsonValueKind.True)
                    detail = ClaudeCliConfiguration.ErrorDetail(root, credential);
            }
            catch (JsonException)
            {
                detail = " CLI exit output could not be read as a result envelope.";
            }
        }
        string combined = $"{result.StandardError}\n{result.StandardOutput}";
        bool authenticationFailure = combined.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("login", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("credential", StringComparison.OrdinalIgnoreCase);
        string guidance = authenticationFailure
            ? " Run claude in a terminal and complete Claude sign-in."
            : string.Empty;
        string inputFailure = result.StandardInputError is null
            ? string.Empty
            : $" {SecretRedactor.Sanitize(result.StandardInputError, credential)}";
        string providerCode = result.StandardInputError is null
            ? $"claude_cli_exit_{result.ExitCode}"
            : "claude_cli_input_closed";
        return new LlmProviderException(
            $"Claude CLI exited with code {result.ExitCode}.{inputFailure}{guidance}"
                + detail,
            LlmTransport.AnthropicClaudeCli,
            providerCode: providerCode);
    }

    private sealed record ClaudeTerminalResult(
        string Text,
        bool HasStructuredOutput,
        bool IsError,
        string? Subtype,
        string? StopReason,
        string? TerminalReason,
        string? SessionId,
        LlmUsage? Usage,
        string ErrorDetail);

    private sealed record ParsedClaudeLine(
        string? TextDelta,
        ClaudeTerminalResult? Terminal,
        string? Activity)
    {
        public string? ReasoningDelta { get; init; }
    }
}
