using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;

using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed class AntigravityCliLlmClient(IAntigravityCliRunner runner)
    : ILlmClient, ILlmAccountUsageClient, IExternalLlmAuthenticationProbe
{
    private static readonly LlmProviderCapabilities Capabilities = new()
    {
        SupportsStreaming = true,
        SupportsStreamingUsage = true,
        SupportsModelListing = true,
        SupportsAccountUsage = true,
        SupportsJsonSchema = true,
        SupportsJsonObject = false,
        ReasoningLevels = LlmReasoningEffort.Levels(
            LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High),
        SupportsTemperature = false,
        SupportsToolUse = false
    };

    public LlmTransport Transport => LlmTransport.GoogleAntigravityCli;

    public LlmProviderDefinition ProviderDefinition => ProviderDefinitions.GeminiSubscription;

    public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
    {
        EnsureTransport(connection);
        LlmProviderCapabilities capabilities = Capabilities.Apply(connection.CapabilityOverrides);
        capabilities = modelId is not null
            && connection.ModelCapabilityOverrides.TryGetValue(modelId, out LlmCapabilityOverrides? overrides)
                ? capabilities.Apply(overrides)
                : capabilities;
        return capabilities with
        {
            SupportsJsonObject = false,
            SupportsTemperature = false,
            SupportsToolUse = false,
            SupportsPromptImages = false,
            SupportsPromptDocuments = false,
            SupportsToolResultImages = false,
            SupportsToolResultDocuments = false
        };
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConnection connection,
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        LlmStructuredOutputEnforcement? enforcement = ValidateRequest(request, capabilities);
        string prompt = BuildPrompt(request, enforcement);
        string standardInput = BuildStandardInput(prompt);
        var arguments = new List<string>
        {
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--sandbox",
            "--disable-slash-commands"
        };
        if (!string.Equals(request.ModelId, "auto", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(["--model", request.ModelId]);
        }
        if (request.ReasoningLevel is { } level)
        {
            arguments.AddRange(["--effort", LlmReasoningEffort.Word(level)]);
        }

        if (request.Tools.Count > 0
            || request.ToolChoice is not null
            || request.AllowParallelToolCalls is not null
            || request.Messages.Any(message => message.ResponseState is not null)
            || request.Messages.Any(message =>
                message.ToolCalls.Count > 0 || message.ToolResults.Count > 0))
        {
            throw new NotSupportedException(
                "Antigravity CLI does not expose host-executed tool calls.");
        }

        JsonElement? providerSchema = enforcement == LlmStructuredOutputEnforcement.ProviderJsonSchema
            ? request.StructuredOutput?.Schema
            : null;
        ParsedResponse? response = null;
        AntigravityCliProcessResult? processResult = null;
        LlmProviderException? parseException = null;
        bool emittedText = false;
        await foreach (AntigravityCliProcessEvent processEvent in InvokeStreamingAsync(
                           ResolveExecutable(connection.Endpoint),
                           arguments,
                           standardInput,
                           providerSchema,
                           cancellationToken))
        {
            if (processEvent is AntigravityCliProcessCompleted completion)
            {
                processResult = completion.Result;
                continue;
            }
            if (processEvent is not AntigravityCliOutputLine outputLine
                || string.IsNullOrWhiteSpace(outputLine.Line))
            {
                continue;
            }

            ParsedStreamLine parsed;
            try
            {
                parsed = ParseStreamLine(
                    outputLine.Line,
                    preferStructuredOutput: providerSchema is not null);
            }
            catch (LlmProviderException exception)
                when (string.Equals(
                    exception.ProviderCode,
                    "antigravity_cli_invalid_json",
                    StringComparison.Ordinal))
            {
                parseException ??= exception;
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
            if (parsed.Response is not null)
            {
                response = parsed.Response;
            }
        }

        if (processResult is null)
        {
            throw new LlmProviderException(
                "Antigravity CLI returned no process completion event.",
                Transport,
                providerCode: "antigravity_cli_invalid_response");
        }
        if (processResult.ExitCode != 0 || processResult.StandardInputError is not null)
        {
            throw CreateExitException(processResult);
        }
        if (parseException is not null)
        {
            throw parseException;
        }

        if (response is null)
        {
            throw new LlmProviderException(
                "Antigravity CLI returned no terminal result event.",
                Transport,
                providerCode: "antigravity_cli_invalid_response");
        }
        if (!string.Equals(response.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            throw new LlmProviderException(
                "Antigravity CLI reported a provider error.",
                Transport,
                providerCode: "antigravity_cli_error");
        }

        if (!emittedText && response.Text.Length > 0)
        {
            yield return new LlmTextDeltaEvent(response.Text);
        }

        LlmStructuredOutputValidation? validation = request.StructuredOutput is null
            ? null
            : StructuredOutput.Validate(
                response.Text,
                request.StructuredOutput,
                enforcement!.Value);

        yield return new LlmCompletionEvent(new LlmChatResult
        {
            Text = response.Text,
            Status = LlmCompletionStatus.Completed,
            FinishReason = "completed",
            Usage = response.Usage,
            ProviderResponseId = response.ConversationId,
            StructuredOutput = validation,
            Diagnostics = new LlmProviderDiagnostics(200, null, null)
        });
    }

    public async Task<IReadOnlyList<LlmModel>> ListModelsAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default)
    {
        LlmProviderCapabilities capabilities = GetCapabilities(connection);
        if (!capabilities.SupportsModelListing)
        {
            return [];
        }

        AntigravityCliProcessResult result = await InvokeAsync(
            ResolveExecutable(connection.Endpoint),
            ["models"],
            null,
            null,
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw CreateExitException(result);
        }

        List<LlmModel> models = ParseModels(result.StandardOutput);
        if (models.Count == 0)
        {
            throw new LlmProviderException(
                "Antigravity CLI returned no available models. Complete sign-in by running agy in a terminal.",
                Transport,
                providerCode: "antigravity_cli_no_models");
        }

        return [new LlmModel("auto", "Auto (Antigravity CLI)", "Google"), .. models];
    }

    public async Task<bool> IsAuthenticatedAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default)
    {
        EnsureTransport(connection);
        try
        {
            AntigravityCliProcessResult result = await InvokeAsync(
                ResolveExecutable(connection.Endpoint),
                ["models"],
                null,
                null,
                cancellationToken);
            return result.ExitCode == 0 && ParseModels(result.StandardOutput).Count > 0;
        }
        catch (LlmProviderException exception)
            when (string.Equals(
                exception.ProviderCode,
                "antigravity_cli_not_found",
                StringComparison.Ordinal))
        {
            return false;
        }
    }

    public async Task<LlmAccountUsageSnapshot> GetAccountUsageAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default)
    {
        _ = GetCapabilities(connection);
        AntigravityCliProcessResult result = await InvokeAsync(
            ResolveExecutable(connection.Endpoint),
            ["--output-format", "json", "--print=/usage"],
            null,
            null,
            cancellationToken);
        if (result.ExitCode != 0 || result.StandardInputError is not null)
        {
            throw CreateExitException(result);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement root = document.RootElement;
            string status = root.TryGetProperty("status", out JsonElement statusElement)
                && statusElement.ValueKind == JsonValueKind.String
                    ? statusElement.GetString() ?? string.Empty
                    : string.Empty;
            if (!status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                throw new LlmProviderException(
                    "Antigravity CLI reported an account-usage error. Run agy in a terminal to verify sign-in.",
                    Transport,
                    providerCode: "antigravity_cli_usage_error");
            }

            string summary = root.TryGetProperty("response", out JsonElement response)
                && response.ValueKind == JsonValueKind.String
                    ? response.GetString() ?? string.Empty
                    : throw new JsonException("The response property is missing.");
            return AntigravityAccountUsageParser.Parse(
                AccountUsageSanitizer.Sanitize(summary, credential: null),
                DateTimeOffset.UtcNow);
        }
        catch (JsonException exception)
        {
            throw new LlmProviderException(
                "Antigravity CLI returned invalid account-usage JSON.",
                Transport,
                providerCode: "antigravity_cli_invalid_account_usage",
                innerException: exception);
        }
        catch (FormatException exception)
        {
            throw new LlmProviderException(
                "Antigravity CLI returned an unrecognized account-usage report.",
                Transport,
                providerCode: "antigravity_cli_invalid_account_usage",
                innerException: exception);
        }
    }

    private async Task<AntigravityCliProcessResult> InvokeAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? standardInput,
        JsonElement? schema,
        CancellationToken cancellationToken)
    {
        string workingDirectory = CliInfrastructure.CreateWorkingDirectory("antigravity");
        try
        {
            var effectiveArguments = new List<string>(arguments);
            if (schema is not null)
            {
                string schemaPath = Path.Combine(workingDirectory, "output-schema.json");
                await File.WriteAllTextAsync(
                    schemaPath,
                    schema.Value.GetRawText(),
                    cancellationToken);
                effectiveArguments.AddRange(["--json-schema", schemaPath]);
            }

            return await runner.RunAsync(
                new AntigravityCliInvocation(
                    executable,
                    effectiveArguments,
                    standardInput,
                    workingDirectory),
                cancellationToken);
        }
        catch (AntigravityCliUnavailableException exception)
        {
            throw new LlmProviderException(
                "Antigravity CLI was not found. Install agy, run it once, and complete Google sign-in.",
                Transport,
                providerCode: "antigravity_cli_not_found",
                innerException: exception);
        }
        finally
        {
            CliInfrastructure.DeleteWorkingDirectory(workingDirectory);
        }
    }

    private async IAsyncEnumerable<AntigravityCliProcessEvent> InvokeStreamingAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? standardInput,
        JsonElement? schema,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string workingDirectory = CliInfrastructure.CreateWorkingDirectory("antigravity");
        try
        {
            var effectiveArguments = new List<string>(arguments);
            if (schema is not null)
            {
                string schemaPath = Path.Combine(workingDirectory, "output-schema.json");
                await File.WriteAllTextAsync(
                    schemaPath,
                    schema.Value.GetRawText(),
                    cancellationToken);
                effectiveArguments.AddRange(["--json-schema", schemaPath]);
            }

            IAsyncEnumerable<AntigravityCliProcessEvent> events = runner.StreamAsync(
                new AntigravityCliInvocation(
                    executable,
                    effectiveArguments,
                    standardInput,
                    workingDirectory),
                cancellationToken);
            await using IAsyncEnumerator<AntigravityCliProcessEvent> enumerator =
                events.GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                AntigravityCliProcessEvent processEvent;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }
                    processEvent = enumerator.Current;
                }
                catch (AntigravityCliUnavailableException exception)
                {
                    throw new LlmProviderException(
                        "Antigravity CLI was not found. Install agy, run it once, and complete Google sign-in.",
                        Transport,
                        providerCode: "antigravity_cli_not_found",
                        innerException: exception);
                }

                yield return processEvent;
            }
        }
        finally
        {
            CliInfrastructure.DeleteWorkingDirectory(workingDirectory);
        }
    }

    private static LlmStructuredOutputEnforcement? ValidateRequest(
        LlmChatRequest request,
        LlmProviderCapabilities capabilities)
    {
        if (string.IsNullOrWhiteSpace(request.ModelId))
        {
            throw new ArgumentException("A model id is required.", nameof(request));
        }

        if (!capabilities.SupportsStreaming)
        {
            throw new NotSupportedException("The selected connection does not support streaming.");
        }

        if (request.Temperature is not null)
        {
            throw new NotSupportedException("Antigravity CLI does not expose a temperature option.");
        }

        if (request.MaxOutputTokens is not null)
        {
            throw new NotSupportedException("Antigravity CLI does not expose a maximum-output-token option.");
        }

        LlmReasoningEffort.Validate(request.ReasoningLevel, capabilities);

        if (request.ProviderOptions.Count > 0)
        {
            throw new NotSupportedException("Antigravity CLI request provider options are not supported.");
        }

        LlmClientBase.ValidatePromptAttachments(request, capabilities);

        if (request.StructuredOutput is not { } structured)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(structured.Name)
            || structured.Schema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Structured output requires a name and a JSON object schema.",
                nameof(request));
        }

        return StructuredOutput.ResolveEnforcement(structured, capabilities);
    }

    private static string BuildPrompt(
        LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement)
    {
        if (string.IsNullOrWhiteSpace(request.Instructions) && request.Messages.Count == 0)
        {
            throw new ArgumentException(
                "A prompt requires instructions or at least one message.",
                nameof(request));
        }

        var prompt = new StringBuilder();
        prompt.AppendLine("Use no tools. Answer only from the information in this prompt.");
        prompt.AppendLine();
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

            if (enforcement == LlmStructuredOutputEnforcement.LocalValidationOnly)
            {
                prompt.AppendLine("The response will be checked locally against this JSON Schema:");
                prompt.AppendLine(structured.Schema.GetRawText());
            }
        }

        return prompt.ToString();
    }

    private static string BuildStandardInput(string prompt) => JsonSerializer.Serialize(new
    {
        @event = "user",
        message = new { content = prompt }
    }) + "\n";

    private static ParsedStreamLine ParseStreamLine(string line, bool preferStructuredOutput)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string eventName = root.TryGetProperty("event", out JsonElement eventElement)
                ? eventElement.GetString() ?? string.Empty
                : string.Empty;
            if (string.Equals(eventName, "init", StringComparison.Ordinal))
            {
                return new ParsedStreamLine(null, null, "Provider connected; waiting for model output…");
            }
            if (string.Equals(eventName, "step_update", StringComparison.Ordinal)
                && root.TryGetProperty("step_update", out JsonElement stepUpdate)
                && stepUpdate.ValueKind == JsonValueKind.Object)
            {
                string? delta = stepUpdate.TryGetProperty("text_delta", out JsonElement textDelta)
                    && textDelta.ValueKind == JsonValueKind.String
                        ? textDelta.GetString()
                        : null;
                string? stepType = stepUpdate.TryGetProperty("step_type", out JsonElement stepTypeElement)
                    && stepTypeElement.ValueKind == JsonValueKind.String
                        ? stepTypeElement.GetString()
                        : null;
                string? activity = string.IsNullOrWhiteSpace(stepType)
                    ? "The model is working…"
                    : $"Model activity: {stepType.Replace('_', ' ')}";
                return new ParsedStreamLine(delta, null, activity);
            }
            if (!string.Equals(eventName, "result", StringComparison.Ordinal)
                || !root.TryGetProperty("result", out JsonElement result)
                || result.ValueKind != JsonValueKind.Object)
            {
                return new ParsedStreamLine(null, null, null);
            }

            string status = result.TryGetProperty("status", out JsonElement statusElement)
                ? statusElement.GetString() ?? string.Empty
                : string.Empty;
            string response = result.TryGetProperty("response", out JsonElement responseElement)
                && responseElement.ValueKind == JsonValueKind.String
                    ? responseElement.GetString() ?? string.Empty
                    : string.Empty;
            if (preferStructuredOutput
                && result.TryGetProperty("structured_output", out JsonElement structuredOutput)
                && structuredOutput.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                response = structuredOutput.GetRawText();
            }
            string? conversationId = result.TryGetProperty("conversation_id", out JsonElement idElement)
                && idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString()
                    : null;
            LlmChatUsage? usage = result.TryGetProperty("usage", out JsonElement usageElement)
                ? ReadUsage(usageElement)
                : null;
            return new ParsedStreamLine(
                null,
                new ParsedResponse(response, status, conversationId, usage),
                "Provider response received; validating recommendations…");
        }
        catch (JsonException exception)
        {
            throw new LlmProviderException(
                "Antigravity CLI returned malformed streaming JSON.",
                LlmTransport.GoogleAntigravityCli,
                providerCode: "antigravity_cli_invalid_json",
                innerException: exception);
        }
    }

    private static LlmChatUsage? ReadUsage(JsonElement usage)
    {
        int? input = ReadInteger(usage, "input_tokens");
        int? output = ReadInteger(usage, "output_tokens");
        int? total = ReadInteger(usage, "total_tokens")
            ?? (input is not null && output is not null ? input + output : null);
        return input is null && output is null && total is null
            ? null
            : new LlmChatUsage(input, output, total);
    }

    private static int? ReadInteger(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out JsonElement value)
        && value.TryGetInt32(out int parsed)
            ? parsed
            : null;

    private static List<LlmModel> ParseModels(string output)
    {
        var models = new List<LlmModel>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            string trimmed = line.Trim();
            int separator = trimmed.IndexOfAny([' ', '\t']);
            if (separator <= 0)
            {
                continue;
            }

            string id = trimmed[..separator];
            string displayName = trimmed[separator..].Trim();
            if (displayName.Length == 0
                || !id.All(character => char.IsLetterOrDigit(character)
                    || character is '-' or '_' or '.'))
            {
                continue;
            }

            if (ids.Add(id))
            {
                string owner = id.StartsWith("claude", StringComparison.OrdinalIgnoreCase)
                    ? "Anthropic via Google"
                    : "Google";
                models.Add(new LlmModel(id, displayName, owner));
            }
        }

        return models;
    }

    private static LlmProviderException CreateExitException(AntigravityCliProcessResult result)
    {
        bool authenticationFailure = result.StandardError.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || result.StandardError.Contains("login", StringComparison.OrdinalIgnoreCase)
            || result.StandardError.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || result.StandardOutput.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || result.StandardOutput.Contains("login", StringComparison.OrdinalIgnoreCase)
            || result.StandardOutput.Contains("credential", StringComparison.OrdinalIgnoreCase);
        string guidance = authenticationFailure
            ? " Run agy in a terminal and complete sign-in."
            : string.Empty;
        string diagnostic = authenticationFailure ? string.Empty : BuildProcessDiagnostic(result);
        string inputFailure = result.StandardInputError is null
            ? string.Empty
            : $" {result.StandardInputError}";
        string diagnosticSuffix = diagnostic.Length == 0
            ? string.Empty
            : $" CLI detail: {diagnostic}";
        string providerCode = result.StandardInputError is null
            ? $"antigravity_cli_exit_{result.ExitCode}"
            : "antigravity_cli_input_closed";
        return new LlmProviderException(
            $"Antigravity CLI exited with code {result.ExitCode}.{inputFailure}{guidance}{diagnosticSuffix}",
            LlmTransport.GoogleAntigravityCli,
            providerCode: providerCode,
            sanitizedResponseBody: diagnostic.Length == 0 ? null : diagnostic);
    }

    private static string BuildProcessDiagnostic(AntigravityCliProcessResult result)
    {
        string source = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        string sanitized = SecretRedactor.Sanitize(source, credential: null)
            .ReplaceLineEndings(" ")
            .Trim();
        const int maximumDiagnosticLength = 512;
        return sanitized.Length <= maximumDiagnosticLength
            ? sanitized
            : $"{sanitized[..maximumDiagnosticLength]}...";
    }

    private static string ResolveExecutable(string endpoint) => CliInfrastructure.ResolveExecutable(
        endpoint,
        "agy",
        "local://antigravity-cli",
        "Antigravity CLI endpoint must be 'agy', 'local://antigravity-cli', or an absolute executable path.");

    private void EnsureTransport(LlmConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Transport != Transport)
        {
            throw new ArgumentException(
                $"A {connection.Transport} connection cannot be used with the {Transport} client.",
                nameof(connection));
        }

        if (connection.AdditionalHeaders.Count > 0 || connection.ProviderOptions.Count > 0)
        {
            throw new NotSupportedException(
                "Antigravity CLI owns its configuration and does not accept connection headers or provider options.");
        }
    }

    private sealed record ParsedResponse(
        string Text,
        string Status,
        string? ConversationId,
        LlmChatUsage? Usage);

    private sealed record ParsedStreamLine(
        string? TextDelta,
        ParsedResponse? Response,
        string? Activity);
}
