using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;

using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Chat-style adapter over the separately authenticated official Claude CLI.</summary>
internal sealed class ClaudeCliLlmClient(HttpClient http, IClaudeCliRunner runner)
    : LlmClientBase(http),
        ILlmAccountUsageClient,
        IExternalLlmAuthenticationProbe,
        IStreamingCredentialRefreshPolicy
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

    /// <summary>The effort levels the Claude CLI accepts, for its chat and agent lanes.</summary>
    internal static readonly IReadOnlySet<LlmReasoningLevel> ReasoningLevels = LlmReasoningEffort.Levels(
        LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High,
        LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum);

    protected override LlmProviderCapabilities DefaultCapabilities { get; } = new()
    {
        SupportsStreaming = true,
        SupportsStreamingUsage = true,
        SupportsModelListing = false,
        SupportsAccountUsage = true,
        SupportsJsonSchema = true,
        SupportsJsonObject = false,
        ReasoningLevels = ReasoningLevels,
        SupportsTemperature = false,
        SupportsToolUse = false,
        SupportsAgentSessions = true,
        Agent = NativeAgentContract.Claude,
        SupportsAgentToolUse = true,
        SupportsPromptImages = true,
        SupportsPromptDocuments = true,
        SupportsToolResultImages = true,
        SupportsToolResultDocuments = true
    };

    public override LlmProviderCapabilities GetCapabilities(
        LlmConnection connection,
        string? modelId = null)
    {
        LlmProviderCapabilities capabilities = base.GetCapabilities(connection, modelId);
        LlmCredential? credential = ClaudeCliConfiguration.ValidateConnection(connection);
        return capabilities with
        {
            SupportsModelListing = HasCatalogCredential(credential),
            SupportsJsonObject = false,
            SupportsTemperature = false,
            SupportsToolUse = false,
            SupportsPromptImages = true,
            SupportsPromptDocuments = true,
            SupportsToolResultImages = true,
            SupportsToolResultDocuments = true
        };
    }

    public override async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConnection connection,
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        ValidateRequest(connection, request, capabilities);
        if (request.Messages.Any(message => message.ResponseState is not null))
        {
            throw new NotSupportedException("Claude CLI chat cannot import adapter response state.");
        }
        ValidateClaudeCliRequest(connection, request);
        LlmStructuredOutputEnforcement? enforcement = request.StructuredOutput is null
            ? null
            : StructuredOutput.ResolveEnforcement(request.StructuredOutput, capabilities);
        string prompt = BuildClaudePrompt(request);
        string standardInput = BuildClaudeStandardInput(request, prompt);
        var arguments = new List<string>
        {
            "-p",
            "--verbose",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--include-partial-messages",
            "--no-session-persistence",
            "--safe-mode",
            "--disable-slash-commands",
            "--tools", string.Empty,
            "--permission-prompts", "none",
            "--system-prompt", ClaudeSystemPrompt,
            "--model", request.ModelId
        };
        if (request.ReasoningLevel is { } level)
        {
            arguments.AddRange(["--effort", LlmReasoningEffort.Word(level)]);
        }
        if (enforcement == LlmStructuredOutputEnforcement.ProviderJsonSchema)
        {
            arguments.AddRange(["--json-schema", request.StructuredOutput!.Schema.GetRawText()]);
        }

        ClaudeTerminalResult? terminal = null;
        ClaudeCliProcessResult? processResult = null;
        LlmProviderException? parseException = null;
        bool emittedText = false;
        await foreach (ClaudeCliProcessEvent processEvent in InvokeClaudeStreamingAsync(
                           ClaudeCliConfiguration.ResolveExecutable(connection.Endpoint),
                           arguments,
                           standardInput,
                           cancellationToken))
        {
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
            try
            {
                parsed = ParseClaudeLine(
                    outputLine.Line,
                    preferStructuredOutput: enforcement == LlmStructuredOutputEnforcement.ProviderJsonSchema);
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
            throw CreateClaudeExitException(processResult);
        }
        if (parseException is not null)
        {
            throw parseException;
        }
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
                "Claude CLI reported a provider error. Run claude in a terminal to verify sign-in and model access.",
                Transport,
                providerCode: "claude_cli_error");
        }

        bool refused = string.Equals(terminal.StopReason, "refusal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(terminal.TerminalReason, "refusal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(terminal.Subtype, "refusal", StringComparison.OrdinalIgnoreCase);
        bool incomplete = string.Equals(terminal.StopReason, "max_tokens", StringComparison.OrdinalIgnoreCase)
            || string.Equals(terminal.TerminalReason, "max_tokens", StringComparison.OrdinalIgnoreCase);
        if (!emittedText && terminal.Text.Length > 0)
        {
            yield return refused
                ? new LlmRefusalDeltaEvent(terminal.Text)
                : new LlmTextDeltaEvent(terminal.Text);
        }

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

        yield return new LlmCompletionEvent(new LlmChatResult
        {
            Text = refused ? string.Empty : terminal.Text,
            Refusal = refused ? terminal.Text : null,
            Status = refused
                ? LlmCompletionStatus.Refused
                : incomplete
                    ? LlmCompletionStatus.Incomplete
                    : LlmCompletionStatus.Completed,
            FinishReason = terminal.StopReason ?? terminal.TerminalReason,
            IncompleteReason = incomplete ? terminal.StopReason ?? terminal.TerminalReason : null,
            Usage = terminal.Usage,
            ProviderResponseId = terminal.SessionId,
            StructuredOutput = validation,
            Diagnostics = new LlmProviderDiagnostics(200, null, null)
        });
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
                cancellationToken);
        }
        catch (ClaudeCliUnavailableException exception)
        {
            throw ClaudeCliConfiguration.Unavailable(exception);
        }

        if (result.ExitCode != 0 || result.StandardInputError is not null)
        {
            throw CreateClaudeExitException(result);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("is_error", out JsonElement error)
                && error.ValueKind == JsonValueKind.True)
            {
                throw new LlmProviderException(
                    "Claude CLI reported an account-usage error. Run claude in a terminal to verify sign-in.",
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
            throw new NotSupportedException(
                "Claude CLI does not expose model discovery. Configure the optional catalog OAuth credential or enter a model id manually.");
        }

        LlmOAuthTokens catalogCredential = credential!.OAuth!;

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
            await ProviderHttp.ThrowIfUnsuccessfulAsync(
                response,
                Transport,
                credential,
                "Anthropic model discovery",
                cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
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
                    exception);
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

        return [.. models.OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    private static void ValidateClaudeCliRequest(
        LlmConnection connection,
        LlmChatRequest request)
    {
        _ = ClaudeCliConfiguration.ValidateConnection(connection);
        if (request.MaxOutputTokens is not null)
        {
            throw new NotSupportedException("Claude CLI does not expose a maximum-output-token option.");
        }
        if (request.ProviderOptions.Count > 0)
        {
            throw new NotSupportedException("Claude CLI request provider options are not supported.");
        }
    }

    private static string BuildClaudePrompt(LlmChatRequest request)
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
            foreach (LlmChatMessage message in request.Messages)
            {
                prompt.Append('[').Append(message.Role.Trim().ToUpperInvariant()).AppendLine("]");
                prompt.AppendLine(message.Content);
                if (message.Attachments.Count > 0)
                {
                    prompt.Append("[ATTACHMENTS: ")
                        .Append(string.Join(", ", message.Attachments.Select(item => item.FileName)))
                        .AppendLine("]");
                }
            }
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

    private static string BuildClaudeStandardInput(LlmChatRequest request, string prompt)
    {
        var content = new List<object> { new { type = "text", text = prompt } };
        foreach (LlmAttachment attachment in request.Messages.SelectMany(item => item.Attachments))
        {
            content.Add(AnthropicAttachments.BuildContentBlock(attachment));
        }

        return JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content }
        }) + "\n";
    }

    private static ParsedClaudeLine ParseClaudeLine(string line, bool preferStructuredOutput)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
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
            && LlmJson.GetObject(streamEvent, "delta") is { } delta
            && string.Equals(LlmJson.GetString(delta, "type"), "text_delta", StringComparison.Ordinal))
        {
            return new ParsedClaudeLine(LlmJson.GetString(delta, "text"), null, null);
        }
        if (!string.Equals(type, "result", StringComparison.Ordinal))
        {
            return new ParsedClaudeLine(null, null, null);
        }

        string text = LlmJson.GetString(root, "result") ?? string.Empty;
        if (preferStructuredOutput
            && root.TryGetProperty("structured_output", out JsonElement structuredOutput)
            && structuredOutput.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            text = structuredOutput.GetRawText();
        }
        bool isError = root.TryGetProperty("is_error", out JsonElement error)
            && error.ValueKind == JsonValueKind.True;
        LlmChatUsage? usage = root.TryGetProperty("usage", out JsonElement usageElement)
            ? ReadClaudeUsage(usageElement)
            : null;
        return new ParsedClaudeLine(
            null,
            new ClaudeTerminalResult(
                text,
                isError,
                LlmJson.GetString(root, "subtype"),
                LlmJson.GetString(root, "stop_reason"),
                LlmJson.GetString(root, "terminal_reason"),
                LlmJson.GetString(root, "session_id"),
                usage),
            "Claude CLI response received; validating output…");
    }

    private static LlmChatUsage? ReadClaudeUsage(JsonElement usage)
    {
        int? input = LlmJson.GetInt(usage, "input_tokens");
        int? output = LlmJson.GetInt(usage, "output_tokens");
        int? total = input is null && output is null ? null : (input ?? 0) + (output ?? 0);
        return total is null ? null : new LlmChatUsage(input, output, total);
    }

    private async Task<ClaudeCliProcessResult> InvokeClaudeAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        string workingDirectory = CliInfrastructure.CreateWorkingDirectory("claude");
        try
        {
            return await runner.RunAsync(
                new ClaudeCliInvocation(executable, arguments, standardInput, workingDirectory),
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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string workingDirectory = CliInfrastructure.CreateWorkingDirectory("claude");
        try
        {
            IAsyncEnumerable<ClaudeCliProcessEvent> events = runner.StreamAsync(
                new ClaudeCliInvocation(executable, arguments, standardInput, workingDirectory),
                cancellationToken);
            await using IAsyncEnumerator<ClaudeCliProcessEvent> enumerator =
                events.GetAsyncEnumerator(cancellationToken);
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

    private static LlmProviderException CreateClaudeExitException(ClaudeCliProcessResult result)
    {
        string combined = $"{result.StandardError}\n{result.StandardOutput}";
        bool authenticationFailure = combined.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("login", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("credential", StringComparison.OrdinalIgnoreCase);
        string guidance = authenticationFailure
            ? " Run claude in a terminal and complete Claude sign-in."
            : string.Empty;
        string inputFailure = result.StandardInputError is null
            ? string.Empty
            : $" {result.StandardInputError}";
        string providerCode = result.StandardInputError is null
            ? $"claude_cli_exit_{result.ExitCode}"
            : "claude_cli_input_closed";
        return new LlmProviderException(
            $"Claude CLI exited with code {result.ExitCode}.{inputFailure}{guidance}",
            LlmTransport.AnthropicClaudeCli,
            providerCode: providerCode);
    }

    private sealed record ClaudeTerminalResult(
        string Text,
        bool IsError,
        string? Subtype,
        string? StopReason,
        string? TerminalReason,
        string? SessionId,
        LlmChatUsage? Usage);

    private sealed record ParsedClaudeLine(
        string? TextDelta,
        ClaudeTerminalResult? Terminal,
        string? Activity);
}
