using Darkspyre.Inference.Common;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed class AntigravityCliLlmClient(IAntigravityCliRunner runner)
    : ILlmClient, ILlmAccountUsageClient, IExternalLlmAuthenticationProbe, ILlmContextEstimator
{
    /// <summary>The longest process argument Linux accepts (MAX_ARG_STRLEN less its terminator), in UTF-8 bytes.</summary>
    internal const int MaximumUnixArgumentBytes = 131_071;

    /// <summary>The longest non-streaming prompt on Windows, in UTF-16 characters, within the 32,767-character command line.</summary>
    internal const int MaximumWindowsArgumentCharacters = 30_000;

    private static readonly LlmProviderCapabilities Capabilities = new()
    {
        OutputLimitEnforcement = Common.LLM.LlmOutputLimitEnforcement.Unsupported,
        ToolHistory = LlmToolHistory.Text,
        SupportsStreaming = InferenceSupport.Supported,
        SupportsStreamingUsage = InferenceSupport.Supported,
        SupportsUsageDeltas = InferenceSupport.Supported,
        SupportsModelListing = InferenceSupport.Supported,
        SupportsAccountUsage = InferenceSupport.Supported,
        SupportsJsonSchema = InferenceSupport.Supported,
        SupportsJsonObject = InferenceSupport.Unsupported,
        ReasoningLevels = LlmReasoningEffort.Levels(
            LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum),
        SupportsTemperature = InferenceSupport.Unsupported,
        SupportsToolUse = InferenceSupport.Unsupported,
        SupportsAgentSessions = InferenceSupport.Supported,
        SupportsAgentToolUse = InferenceSupport.Supported,
        Agent = NativeAgentContract.Antigravity
    };

    public LlmTransport Transport => LlmTransport.GoogleAntigravityCli;

    public LlmProviderDefinition ProviderDefinition => AntigravityDefinition.Value;

    public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
        => ResolveCapabilities(connection, modelId);

    internal static LlmProviderCapabilities ResolveCapabilities(LlmConnection connection, string? modelId)
    {
        ValidateConnection(connection);
        LlmProviderCapabilities capabilities = LlmCapabilityOverrideLookup.Apply(Capabilities, connection, modelId);
        // These published slugs pin the effort; a different flag must not select another model.
        LlmReasoningLevel? pinnedEffort = modelId switch
        {
            "gemini-3.8-flash-low" or "gemini-3.7-flash-low" or "gemini-3.6-flash-low" or "gemini-3.1-pro-low"
                => LlmReasoningLevel.Low,
            "gemini-3.8-flash-medium" or "gemini-3.7-flash-medium" or "gemini-3.6-flash-medium" or "gpt-oss-120b-medium"
                => LlmReasoningLevel.Medium,
            "gemini-3.8-flash-high" or "gemini-3.7-flash-high" or "gemini-3.6-flash-high" or "gemini-3.1-pro-high"
                => LlmReasoningLevel.High,
            _ => null
        };
        return capabilities with
        {
            ReasoningLevels = modelId is "claude-sonnet-4-6" or "claude-opus-4-6-thinking"
                ? LlmReasoningEffort.Levels()
                : pinnedEffort is { } pinned
                    ? LlmReasoningEffort.Levels(pinned)
                    : capabilities.ReasoningLevels,
            SupportsJsonObject = InferenceSupport.Unsupported,
            SupportsTemperature = InferenceSupport.Unsupported,
            SupportsTopP = InferenceSupport.Unsupported,
            SupportsTopK = InferenceSupport.Unsupported,
            SupportsSeed = InferenceSupport.Unsupported,
            SupportsPresencePenalty = InferenceSupport.Unsupported,
            SupportsFrequencyPenalty = InferenceSupport.Unsupported,
            MaxStopSequences = 0,
            SupportsToolUse = InferenceSupport.Unsupported,
            SupportsPromptImages = InferenceSupport.Unsupported,
            SupportsPromptDocuments = InferenceSupport.Unsupported,
            RichContentSupported = RichContentRoutes.TextOnly
        };
    }

    /// <summary>
    /// Maps desired effort to the selected Antigravity model's native CLI vocabulary. A model with no effort control gets
    /// none: the effort is dropped.
    /// </summary>
    internal static LlmReasoningLevel? MapReasoning(LlmConnection connection, string? modelId, LlmReasoningLevel? requested)
    {
        if (requested is null || modelId is "claude-sonnet-4-6" or "claude-opus-4-6-thinking") return null;
        LlmProviderCapabilities capabilities = ResolveCapabilities(connection, modelId);
        IReadOnlySet<LlmReasoningLevel> choices = capabilities.ReasoningLevels.Count == 0
            ? Capabilities.ReasoningLevels : capabilities.ReasoningLevels;
        LlmReasoningLevel[] row = requested switch
        {
            LlmReasoningLevel.None or LlmReasoningLevel.Minimal or LlmReasoningLevel.Low =>
                [LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum],
            LlmReasoningLevel.Medium => [LlmReasoningLevel.Medium, LlmReasoningLevel.Low, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum],
            LlmReasoningLevel.High => [LlmReasoningLevel.High, LlmReasoningLevel.Medium, LlmReasoningLevel.Low, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum],
            LlmReasoningLevel.ExtraHigh => [LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.High, LlmReasoningLevel.Medium, LlmReasoningLevel.Low, LlmReasoningLevel.Maximum],
            LlmReasoningLevel.Maximum => [LlmReasoningLevel.Maximum, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.High, LlmReasoningLevel.Medium, LlmReasoningLevel.Low],
            _ => throw new ArgumentOutOfRangeException(nameof(requested))
        };
        foreach (LlmReasoningLevel choice in row)
            if (choices.Contains(choice)) return choice;
        throw new NotSupportedException("The selected Antigravity native choices contain no mapped CLI effort.");
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConnection connection,
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        request = Prepare(connection, request, capabilities);
        LlmStructuredOutputEnforcement? enforcement = ValidateRequest(request, capabilities);
        string prompt = BuildPrompt(request, enforcement);

        JsonElement? providerSchema = enforcement == LlmStructuredOutputEnforcement.ProviderJsonSchema
            ? request.StructuredOutput?.Schema
            : null;
        string executable = ResolveExecutable(connection.Endpoint);
        if ((capabilities.SupportsStreaming == InferenceSupport.Unsupported))
        {
            LlmChatResult completeResult = await CompleteAsync(
                executable, prompt, request, enforcement, providerSchema, cancellationToken);
            foreach (LlmStreamEvent completeEvent in CompleteResponseEvents.From(completeResult, []))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return completeEvent;
            }

            yield break;
        }

        string standardInput = BuildStandardInput(prompt);
        var arguments = new List<string>
        {
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--sandbox",
            "--disable-slash-commands"
        };
        AddModelArguments(arguments, request);
        ParsedResponse? response = null;
        AntigravityCliProcessResult? processResult = null;
        LlmProviderException? parseException = null;
        bool emittedText = false;
        var ledger = new AntigravityStepLedger();
        var usageDeltas = new UsageDeltas((capabilities.SupportsUsageDeltas != InferenceSupport.Unsupported));
        await foreach (AntigravityCliProcessEvent processEvent in InvokeStreamingAsync(
                           executable,
                           arguments,
                           standardInput,
                           providerSchema,
                           cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processEvent is AntigravityCliProcessCompleted completion)
            {
                processResult = completion.Result;
                continue;
            }
            if (response is not null
                || processEvent is not AntigravityCliOutputLine outputLine
                || string.IsNullOrWhiteSpace(outputLine.Line))
            {
                continue;
            }

            ParsedStreamLine parsed;
            LlmUsageDeltaEvent? observation = null;
            bool projectContent = true;
            try
            {
                parsed = ParseStreamLine(
                    outputLine.Line,
                    preferStructuredOutput: providerSchema is not null);
                if (response is null && parsed.Step is { } step)
                {
                    projectContent = ledger.Record(step);
                    observation = usageDeltas.Next(ledger.Usage);
                }
            }
            catch (InvalidDataException exception)
            {
                throw new LlmProviderException("Antigravity CLI reported conflicting step data.", Transport,
                    providerCode: "antigravity_cli_invalid_response", innerException: exception);
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
            if (observation is not null) { yield return observation; }
            if (projectContent && !string.IsNullOrEmpty(parsed.Activity))
            {
                yield return new LlmActivityEvent(parsed.Activity);
            }
            if (projectContent && !string.IsNullOrEmpty(parsed.TextDelta))
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
            throw CreateExitException(processResult, response?.Error);
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

        LlmChatResult result = BuildResult(response, request, enforcement);
        if (!emittedText && response.Text.Length > 0)
        {
            yield return new LlmTextDeltaEvent(response.Text);
        }

        yield return new LlmChatCompletionEvent(result);
    }

    /// <summary>
    /// Runs one non-streaming turn. The CLI's only non-streaming form takes the prompt as a process argument, and
    /// standard input stays empty.
    /// </summary>
    private async Task<LlmChatResult> CompleteAsync(
        string executable,
        string prompt,
        LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement,
        JsonElement? providerSchema,
        CancellationToken cancellationToken)
    {
        EnsurePromptFitsArgument(executable, prompt);
        var arguments = new List<string>
        {
            "-p", prompt,
            "--output-format", "json",
            "--sandbox",
            "--disable-slash-commands"
        };
        AddModelArguments(arguments, request);
        AntigravityCliProcessResult processResult = await InvokeAsync(
            executable,
            arguments,
            null,
            providerSchema,
            cancellationToken);
        if (processResult.ExitCode != 0 || processResult.StandardInputError is not null)
        {
            throw CreateExitException(processResult);
        }

        AntigravityResult envelope = AntigravityProtocol.ParseResultEnvelope(processResult.StandardOutput);
        return BuildResult(ToParsedResponse(envelope, preferStructuredOutput: providerSchema is not null), request, enforcement);
    }

    /// <summary>Rejects a prompt that the platform cannot carry as one process argument.</summary>
    internal static void EnsurePromptFitsArgument(string executable, string prompt)
    {
        string extension = Path.GetExtension(executable);
        bool fits = OperatingSystem.IsWindows()
            ? !string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase)
                && prompt.Length <= MaximumWindowsArgumentCharacters
            : Encoding.UTF8.GetByteCount(prompt) <= MaximumUnixArgumentBytes;
        if (!fits)
        {
            throw new NotSupportedException(
                "The Antigravity CLI takes a non-streaming prompt as a process argument, and this prompt is too long for one. "
                + "Turn streaming on for this connection.");
        }
    }

    private static void AddModelArguments(List<string> arguments, LlmChatRequest request)
    {
        if (!string.Equals(request.ModelId, "auto", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(["--model", request.ModelId]);
        }
        if (request.ReasoningLevel is { } level)
        {
            arguments.AddRange(["--effort", LlmReasoningEffort.Word(level)]);
        }
    }

    private LlmChatResult BuildResult(
        ParsedResponse response,
        LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement)
    {
        bool completed = string.Equals(response.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(response.Status, "ERROR", StringComparison.OrdinalIgnoreCase)
            || string.Equals(response.Status, "INVALID", StringComparison.OrdinalIgnoreCase)
            || string.Equals(response.Status, "CANCELED", StringComparison.OrdinalIgnoreCase)
            || string.Equals(response.Status, "INTERRUPTED", StringComparison.OrdinalIgnoreCase))
        {
            throw new LlmProviderException(
                "Antigravity CLI reported a provider error."
                    + (response.Error is null ? string.Empty : $" CLI detail: {response.Error}"),
                Transport,
                providerCode: "antigravity_cli_error");
        }

        return new LlmChatResult
        {
            Text = response.Text,
            Status = completed ? LlmCompletionStatus.Completed : LlmCompletionStatus.Unknown,
            FinishReason = completed ? "completed" : response.Status.Length == 0 ? null : response.Status,
            Usage = response.Usage,
            ProviderResponseId = response.ConversationId,
            StructuredOutput = !completed || request.StructuredOutput is null
                ? null
                : StructuredOutput.Validate(response.Text, request.StructuredOutput, enforcement!.Value),
            Diagnostics = new LlmProviderDiagnostics(200, null, null)
        };
    }

    public async Task<IReadOnlyList<LlmModel>> ListModelsAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default)
    {
        LlmProviderCapabilities capabilities = GetCapabilities(connection);
        if ((capabilities.SupportsModelListing == InferenceSupport.Unsupported))
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

        List<LlmModel> models = ParseModels(result.StandardOutput, connection);
        if (models.Count == 0)
        {
            throw new LlmProviderException(
                "Antigravity CLI returned no available models. Complete sign-in by running agy in a terminal.",
                Transport,
                providerCode: "antigravity_cli_no_models");
        }

        return [new LlmModel("auto", "Auto (Antigravity CLI)", "Google"), .. models];
    }

    public async Task<ExternalAuthenticationState> CheckAuthenticationAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default)
    {
        ValidateConnection(connection);
        try
        {
            AntigravityCliProcessResult result = await InvokeAsync(
                ResolveExecutable(connection.Endpoint),
                ["models"],
                null,
                null,
                cancellationToken);
            return result.ExitCode == 0 && ParseModels(result.StandardOutput).Count > 0
                ? ExternalAuthenticationState.SignedIn
                : ExternalAuthenticationState.SignedOut;
        }
        catch (LlmProviderException exception)
            when (string.Equals(
                exception.ProviderCode,
                "antigravity_cli_not_found",
                StringComparison.Ordinal))
        {
            return ExternalAuthenticationState.NotInstalled;
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
                string? error = AntigravityProtocol.ReadError(root);
                throw new LlmProviderException(
                    "Antigravity CLI reported an account-usage error. Run agy in a terminal to verify sign-in."
                        + (error is null ? string.Empty : $" CLI detail: {error}"),
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
            List<string> effectiveArguments = await WithSchemaAsync(arguments, schema, workingDirectory, cancellationToken);
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
        catch (CliProcessTerminationException exception)
        {
            throw ExitTimeout(exception);
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
            List<string> effectiveArguments = await WithSchemaAsync(arguments, schema, workingDirectory, cancellationToken);
            IAsyncEnumerable<AntigravityCliProcessEvent> events = runner.StreamAsync(
                new AntigravityCliInvocation(
                    executable,
                    effectiveArguments,
                    standardInput,
                    workingDirectory),
                cancellationToken);
            IAsyncEnumerator<AntigravityCliProcessEvent> enumerator = events.GetAsyncEnumerator(cancellationToken);
            try
            {
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
                    catch (CliProcessTerminationException exception)
                    {
                        throw ExitTimeout(exception);
                    }

                    yield return processEvent;
                }
            }
            finally
            {
                await DisposeRunnerAsync(enumerator);
            }
        }
        finally
        {
            CliInfrastructure.DeleteWorkingDirectory(workingDirectory);
        }
    }

    /// <summary>
    /// The arguments with <c>--json-schema</c> appended when a schema is given. The schema file is written to the
    /// invocation's working directory.
    /// </summary>
    private static async Task<List<string>> WithSchemaAsync(
        IReadOnlyList<string> arguments,
        JsonElement? schema,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var effectiveArguments = new List<string>(arguments);
        if (schema is not null)
        {
            string schemaPath = Path.Combine(workingDirectory, "output-schema.json");
            await File.WriteAllTextAsync(schemaPath, schema.Value.GetRawText(), cancellationToken);
            effectiveArguments.AddRange(["--json-schema", schemaPath]);
        }

        return effectiveArguments;
    }

    /// <summary>Disposes the runner's enumerator, which ends the process when the enumeration stops early.</summary>
    private async ValueTask DisposeRunnerAsync(IAsyncEnumerator<AntigravityCliProcessEvent> enumerator)
    {
        try
        {
            await enumerator.DisposeAsync();
        }
        catch (CliProcessTerminationException exception)
        {
            throw ExitTimeout(exception);
        }
    }

    private LlmProviderException ExitTimeout(CliProcessTerminationException exception) => new(
        $"The Antigravity CLI did not exit after it was stopped. {exception.Message}",
        Transport,
        providerCode: "antigravity_cli_exit_timeout",
        innerException: exception);

    /// <summary>
    /// The request as the CLI receives it: another provider's continuation state is not sent, values the model cannot use
    /// are mapped or dropped, attachments the CLI cannot take become notices, and effort takes the model's CLI vocabulary.
    /// </summary>
    private static LlmChatRequest Prepare(LlmConnection connection, LlmChatRequest request, LlmProviderCapabilities capabilities)
    {
        request = request with { Messages = [.. request.Messages.Select(message => message with { ContinuationState = null })] };
        request = LlmRequestNormalizer.Normalize(connection, request, capabilities);
        return request with { ReasoningLevel = MapReasoning(connection, request.ModelId, request.ReasoningLevel) };
    }

    /// <summary>Checks the operations a prepared request invokes. Host tool calls are refused: the CLI runs only its own.</summary>
    private static LlmStructuredOutputEnforcement? ValidateRequest(LlmChatRequest request, LlmProviderCapabilities capabilities)
    {
        LlmClientBase.ValidateOperations(request, capabilities);
        if (request.ProviderOptions.Count > 0)
        {
            throw new NotSupportedException("Antigravity CLI request provider options are not supported.");
        }

        return request.StructuredOutput is { } structured ? StructuredOutput.ResolveEnforcement(structured, capabilities) : null;
    }

    public string? EstimateDiagnostic => LocalTokenCount.CliDiagnostic;

    public ValueTask<ContextEstimate?> EstimateAsync(LlmConnection connection, LlmChatRequest request,
        IReadOnlyDictionary<InferenceResourceReference, InferenceResourceDescriptor?> resources,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Estimate(connection, request, resources));

    /// <summary>Counts the prompt this client would send. The CLI takes text only, so no caller-owned resource is counted.</summary>
    private ContextEstimate? Estimate(LlmConnection connection, LlmChatRequest request,
        IReadOnlyDictionary<InferenceResourceReference, InferenceResourceDescriptor?> resources)
    {
        ArgumentNullException.ThrowIfNull(request);
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        request = Prepare(connection, request, capabilities);
        LlmStructuredOutputEnforcement? enforcement = ValidateRequest(request, capabilities);
        return new ContextEstimate(LocalTokenCount.Count(BuildPrompt(request, enforcement)));
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
            TranscriptText.Append(prompt, request.Messages);
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

    private static string BuildStandardInput(string prompt) => AntigravityProtocol.WriteUserInput(prompt);

    /// <summary>
    /// Maps one protocol event for chat. Only <c>agent_response</c> deltas are answer text; a schema turn
    /// streams none, because its canonical answer is the terminal <c>structured_output</c>.
    /// </summary>
    private static ParsedStreamLine ParseStreamLine(string line, bool preferStructuredOutput)
    {
        switch (AntigravityProtocol.Parse(line))
        {
            case AntigravityInit:
                return new ParsedStreamLine(null, null, "Provider connected; waiting for model output…");
            case AntigravityStep step:
                string? delta = !preferStructuredOutput
                    && string.Equals(step.StepType, "agent_response", StringComparison.Ordinal)
                        ? step.TextDelta
                        : null;
                return new ParsedStreamLine(delta, null, $"Model activity: {step.StepType.Replace('_', ' ')}", step);
            case AntigravityResult result:
                return new ParsedStreamLine(
                    null,
                    ToParsedResponse(result, preferStructuredOutput),
                    "Provider response received; validating recommendations…");
            default:
                return new ParsedStreamLine(null, null, null);
        }
    }

    /// <summary>The answer text is the structured output when a schema was sent, otherwise the response.</summary>
    private static ParsedResponse ToParsedResponse(AntigravityResult result, bool preferStructuredOutput) => new(
        preferStructuredOutput && result.StructuredOutput is { } structured ? structured.GetRawText() : result.Response,
        result.Status,
        result.ConversationId,
        AntigravityStepLedger.Normalize(result.Usage),
        result.Error);

    private static List<LlmModel> ParseModels(string output, LlmConnection? connection = null)
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
                models.Add(new LlmModel(id, displayName, owner,
                    connection is null ? null : ResolveCapabilities(connection, id)));
            }
        }

        return models;
    }

    private static LlmProviderException CreateExitException(AntigravityCliProcessResult result, string? terminalError = null)
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
        string diagnostic = terminalError ?? ReadExitError(result.StandardOutput)
            ?? (authenticationFailure ? string.Empty : BuildProcessDiagnostic(result));
        string inputFailure = result.StandardInputError is null
            ? string.Empty
            : $" {AntigravityProtocol.SanitizeDiagnostic(result.StandardInputError)}";
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

    private static string? ReadExitError(string output)
    {
        if (!output.TrimStart().StartsWith('{')) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                ? AntigravityProtocol.ReadError(LlmJson.GetObject(root, "result") ?? root) : null;
        }
        catch (JsonException)
        {
            return "CLI exit output could not be read as a result envelope.";
        }
    }

    private static string BuildProcessDiagnostic(AntigravityCliProcessResult result)
    {
        string source = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        return AntigravityProtocol.SanitizeDiagnostic(source);
    }

    internal static string ResolveExecutable(string endpoint) => CliInfrastructure.ResolveExecutable(
        endpoint,
        "agy",
        "local://antigravity-cli",
        "Antigravity CLI endpoint must be 'agy', 'local://antigravity-cli', or an absolute executable path.");

    /// <summary>Rejects a connection of another transport or one that carries configuration the CLI owns.</summary>
    internal static void ValidateConnection(LlmConnection connection)
    {
        RuntimeInputAdmission.Validate(connection);
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Transport != LlmTransport.GoogleAntigravityCli)
        {
            throw new ArgumentException(
                $"A {connection.Transport} connection cannot be used with the {LlmTransport.GoogleAntigravityCli} client.",
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
        LlmUsage? Usage,
        string? Error);

    private sealed record ParsedStreamLine(
        string? TextDelta,
        ParsedResponse? Response,
        string? Activity,
        AntigravityStep? Step = null);
}
