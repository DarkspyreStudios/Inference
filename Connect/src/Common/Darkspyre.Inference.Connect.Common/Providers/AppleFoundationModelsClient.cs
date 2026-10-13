using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed class AppleFoundationModelsClient(HttpClient http, IAppleProcessRunner runner)
    : LlmClientBase(http), ILlmContextEstimator, IExternalLlmAuthenticationProbe, ILlmCatalogCapabilities
{
    public override LlmTransport Transport => LlmTransport.AppleFoundationModels;
    public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.AppleFoundationModels;
    protected override bool OffersNonStreamingRequests => true;
    public string? EstimateDiagnostic => "Apple does not report complete input occupancy. Native context overflow remains authoritative.";

    protected override LlmProviderCapabilities DefaultCapabilities { get; } = new()
    {
        SupportsStreaming = InferenceSupport.Supported,
        SupportsModelListing = InferenceSupport.Supported,
        SupportsJsonSchema = InferenceSupport.Supported,
        SupportsJsonObject = InferenceSupport.Unsupported,
        SupportsPromptImages = InferenceSupport.Unsupported,
        SupportsPromptDocuments = InferenceSupport.Unsupported,
        SupportsStreamingUsage = InferenceSupport.Unsupported,
        SupportsUsageDeltas = InferenceSupport.Unsupported,
        SupportsAccountUsage = InferenceSupport.Unsupported,
        SupportsPresencePenalty = InferenceSupport.Unsupported,
        SupportsFrequencyPenalty = InferenceSupport.Unsupported,
        SupportsTemperature = InferenceSupport.Supported,
        SupportsTopP = InferenceSupport.Supported,
        SupportsTopK = InferenceSupport.Supported,
        SupportsSeed = InferenceSupport.Supported,
        SupportsToolUse = InferenceSupport.Supported,
        Tools = new()
        {
            ChoiceModes = new[] { LlmToolChoiceMode.Auto, LlmToolChoiceMode.None }.ToFrozenSet(),
            StrictInputSchema = LlmStrictInputSchemaSupport.Unsupported,
            SupportsParallelCalls = InferenceSupport.Supported,
            SupportsParallelCallControl = InferenceSupport.Unsupported,
            SupportsArgumentStreaming = InferenceSupport.Unsupported
        },
        ToolHistory = LlmToolHistory.Structured,
        SupportsAgentSessions = InferenceSupport.Supported,
        SupportsAgentToolUse = InferenceSupport.Supported,
        SupportsAgentTurnInstructions = InferenceSupport.Supported,
        Agent = ManagedAgentPolicy.Capabilities with { RichContentSupported = RichContentRoutes.TextOnly },
        RichContentSupported = RichContentRoutes.TextOnly,
        OutputLimitEnforcement = LlmOutputLimitEnforcement.ProviderEnforced
    };

    public override LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
    {
        ValidateConnection(connection);
        if (modelId is not null && modelId != AppleProtocol.ModelId)
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, "Unknown Apple system model ID.");
        LlmProviderCapabilities result = base.GetCapabilities(connection, modelId);
        if (ILlmCatalogCapabilities.EntryFor(connection, modelId) is { ValueKind: JsonValueKind.Object } hello
            && !AppleProtocol.SupportsSampling(hello))
            result = result with { SupportsTopP = InferenceSupport.Unsupported, SupportsTopK = InferenceSupport.Unsupported, SupportsSeed = InferenceSupport.Unsupported };
        if (result.SupportsJsonObject == InferenceSupport.Supported || result.SupportsPromptImages == InferenceSupport.Supported || result.SupportsPromptDocuments == InferenceSupport.Supported
            || result.SupportsStreamingUsage == InferenceSupport.Supported || result.SupportsUsageDeltas == InferenceSupport.Supported || result.SupportsAccountUsage == InferenceSupport.Supported || result.ReasoningLevels.Count > 0
            || result.SupportsPresencePenalty == InferenceSupport.Supported || result.SupportsFrequencyPenalty == InferenceSupport.Supported || result.MaxStopSequences > 0)
            throw new NotSupportedException("Capability overrides cannot enable unsupported Apple model features.");
        return result;
    }

    public Task<bool> IsAuthenticatedAsync(LlmConnection connection, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConnection(connection);
        // This transport requires no authentication. Readiness is checked separately by probe.
        return Task.FromResult(true);
    }

    internal async Task<JsonElement> ProbeAsync(LlmConnection connection, CancellationToken cancellationToken)
    {
        ValidateConnection(connection);
        JsonElement hello = default;
        bool complete = false;
        await foreach (JsonElement frame in runner.RunAsync(connection.Endpoint,
            new("probe", Guid.NewGuid().ToString("N")), AppleProcessLimits.Read(connection), cancellationToken).ConfigureAwait(false))
        {
            switch (AppleProtocol.RequiredString(frame, "type"))
            {
                case "hello": hello = frame; break;
                case "terminal" when !complete && AppleProtocol.RequiredString(frame, "status") == "ready": complete = true; break;
                default: throw AppleProtocol.Failure("Unexpected Apple availability response.");
            }
        }
        if (!complete || hello.ValueKind != JsonValueKind.Object) throw AppleProtocol.Failure("Apple availability did not complete.");
        return hello;
    }

    public override async Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
    {
        LlmProviderCapabilities capabilities = GetCapabilities(connection, AppleProtocol.ModelId);
        if ((capabilities.SupportsModelListing == InferenceSupport.Unsupported)) return [];
        JsonElement hello = await ProbeAsync(connection, cancellationToken).ConfigureAwait(false);
        if (AppleProtocol.RequiredString(hello, "availability") != "available") return [];
        if (!AppleProtocol.SupportsSampling(hello))
            capabilities = capabilities with { SupportsTopP = InferenceSupport.Unsupported, SupportsTopK = InferenceSupport.Unsupported, SupportsSeed = InferenceSupport.Unsupported };
        return [new(AppleProtocol.ModelId, "Apple System Language Model", "Apple", capabilities)
        { MaxContextTokens = AppleProtocol.RequiredInteger(hello, "contextSize"), CatalogEntry = hello.Clone() }];
    }

    public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request)
    {
        _ = Prepare(connection, request);
        return null;
    }

    private sealed record Prepared(AppleGeneration Generation, LlmStructuredOutputRequest? Structured,
        LlmStructuredOutputEnforcement? Enforcement, IReadOnlyDictionary<string, JsonElement> ToolSchemas,
        IReadOnlySet<string> ExistingCallIds);

    private Prepared Prepare(LlmConnection connection, LlmChatRequest request)
    {
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        ValidateRequest(connection, request, capabilities);
        if (request.ProviderOptions.Count > 0) throw new NotSupportedException("Apple does not accept raw provider request properties.");
        if (request.Temperature is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(request), "Apple temperature must be between zero and one.");
        if (request.TopP is not null && request.TopK is not null)
            throw new NotSupportedException("Apple samples with top-p or top-k, not both.");
        if (request.ToolChoice is { } choice && (!Enum.IsDefined(choice.Mode) || choice.Mode is LlmToolChoiceMode.Required or LlmToolChoiceMode.Specific))
            throw new NotSupportedException("Apple supports automatic tool choice or no tools on this baseline.");
        if (request.AllowParallelToolCalls == false) throw new NotSupportedException("Apple cannot disable model-side parallel tool calls on this baseline.");
        if (request.ToolChoice is { Name: not null, Mode: not LlmToolChoiceMode.Specific })
            throw new ArgumentException("A tool name is valid only for a specific tool choice.");

        LlmStructuredOutputRequest? structured = request.StructuredOutput is { } output
            ? output with { Schema = output.Schema.Clone() } : null;
        AppleSchemaNode? outputSchema = null;
        LlmStructuredOutputEnforcement? enforcement = null;
        string? instructions = request.Instructions;
        if (structured is not null)
        {
            if (!Enum.IsDefined(structured.Fallback)) throw new ArgumentException("Unknown structured output fallback.");
            try
            {
                if ((capabilities.SupportsJsonSchema == InferenceSupport.Unsupported)) throw new NotSupportedException("Apple schema output is disabled.");
                outputSchema = AppleSchema.Convert(structured.Schema);
                string guidance = AppleSchema.Guidance(outputSchema);
                if (guidance.Length > 0) instructions = $"{instructions}\nOutput schema guidance:\n{guidance}";
                enforcement = LlmStructuredOutputEnforcement.ProviderJsonSchema;
            }
            catch (NotSupportedException) when (structured.Fallback == LlmStructuredOutputFallback.LocalValidation)
            {
                enforcement = LlmStructuredOutputEnforcement.LocalValidationOnly;
                instructions = $"{instructions}\nReturn only a JSON value matching this schema:\n{structured.Schema.GetRawText()}";
            }
        }
        LlmToolDefinition[] active = request.ToolChoice?.Mode == LlmToolChoiceMode.None ? [] : ClientTools.Functions(request.Tools).ToArray();
        AppleTool[] tools = active.Select(static tool =>
        {
            AppleSchemaNode schema = AppleSchema.Convert(tool.InputSchema);
            if (schema.Kind != "object") throw new NotSupportedException("Apple tools require an object argument schema.");
            string guidance = AppleSchema.Guidance(schema);
            return new AppleTool(tool.Name, guidance.Length == 0 ? tool.Description : $"{tool.Description}\nArgument schema guidance:\n{guidance}", schema);
        }).ToArray();
        var schemas = active.ToDictionary(static tool => tool.Name, static tool => tool.InputSchema.Clone(), StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var messages = new List<AppleMessage>();
        foreach (LlmChatMessage message in request.Messages)
        {
            if (message.ContinuationState is not null) throw new NotSupportedException("Apple uses semantic history and accepts no provider response-state payload.");
            if (message.Role is not ("user" or "assistant"))
                throw new NotSupportedException("Apple accepts Instructions and user/assistant history; this message role cannot be preserved.");
            if (pending.Count > 0 && message.ToolResults.Count == 0)
                throw new ArgumentException("Every pending tool call requires a result before another message.");
            var calls = new List<AppleCall>();
            foreach (LlmToolCall call in ClientTools.Functions(message.ToolCalls))
            {
                if (message.Role != "assistant" || !names.TryAdd(call.Id, call.Name)) throw new ArgumentException("Tool calls require unique IDs on assistant messages.");
                pending.Add(call.Id);
                calls.Add(new(call.Id, call.Name, call.ArgumentsJson));
            }
            var results = new List<AppleResult>();
            foreach (LlmToolResult result in ClientTools.Functions(message.ToolResults))
            {
                if (message.Role != "user" || !pending.Remove(result.ToolCallId)) throw new ArgumentException("A tool result must match one pending call exactly once.");
                results.Add(new(result.ToolCallId, names[result.ToolCallId], ProjectToolOutput(connection, request.ModelId, result.Output).Text));
            }
            if (results.Count > 0 && pending.Count > 0 && message.Content.Length > 0)
                throw new ArgumentException("User text cannot interrupt an incomplete group of tool results.");
            messages.Add(new(message.Role, message.Content, calls, results));
        }
        if (pending.Count > 0) throw new ArgumentException("Tool-call history is missing results.");
        return new(new(instructions, messages, tools, outputSchema, request.Temperature, request.MaxOutputTokens, (capabilities.SupportsStreaming != InferenceSupport.Unsupported),
                request.TopP, request.TopK, request.Seed),
            structured, enforcement, schemas, names.Keys.ToHashSet(StringComparer.Ordinal));
    }

    public override async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Prepared prepared = Prepare(connection, request);
        bool stream = prepared.Generation.Stream;
        string previous = string.Empty;
        LlmToolCall[]? batch = null;
        bool terminal = false;
        await foreach (JsonElement frame in runner.RunAsync(connection.Endpoint,
            new("generate", Guid.NewGuid().ToString("N"), prepared.Generation), AppleProcessLimits.Read(connection), cancellationToken).ConfigureAwait(false))
        {
            if (terminal) throw AppleProtocol.Failure("The Apple helper emitted data after completion.");
            switch (AppleProtocol.RequiredString(frame, "type"))
            {
                case "hello":
                    AppleProtocol.Ready(frame);
                    AppleProtocol.ValidateSampling(frame, prepared.Generation);
                    break;
                case "text":
                    if (prepared.Generation.Schema is not null) throw AppleProtocol.Failure("Apple returned text for structured generation.");
                    string snapshot = AppleProtocol.RequiredString(frame, "content");
                    if (!snapshot.StartsWith(previous, StringComparison.Ordinal)) throw AppleProtocol.Failure("Apple revised previously emitted text.");
                    string delta = snapshot[previous.Length..];
                    previous = snapshot;
                    if (stream && delta.Length > 0) yield return new LlmTextDeltaEvent(delta);
                    break;
                case "structured":
                    if (prepared.Generation.Schema is null) throw AppleProtocol.Failure("Apple returned an unexpected structured snapshot.");
                    previous = AppleProtocol.RequiredString(frame, "content");
                    JsonElement structuredSnapshot = ParseJson(previous);
                    if (stream) yield return new LlmStructuredOutputSnapshotEvent(structuredSnapshot);
                    break;
                case "batch":
                    if (batch is not null) throw AppleProtocol.Failure("Apple returned more than one batch in a model operation.");
                    batch = ReadCalls(frame, prepared);
                    break;
                case "terminal":
                    terminal = true;
                    string status = AppleProtocol.RequiredString(frame, "status");
                    if (status == "error") throw AppleProtocol.Error(frame);
                    string text = AppleProtocol.RequiredString(frame, "content");
                    if (text != previous) throw AppleProtocol.Failure("The Apple terminal content does not match its streamed content.");
                    LlmCompletionStatus completion = status switch
                    {
                        "unknown" when batch is null => LlmCompletionStatus.Unknown,
                        "requiresAction" when batch is { Length: > 0 } => LlmCompletionStatus.RequiresAction,
                        "refused" when batch is null => LlmCompletionStatus.Refused,
                        _ => throw AppleProtocol.Failure("Invalid Apple terminal status or tool batch.")
                    };
                    var result = new LlmChatResult
                    {
                        Text = text,
                        Status = completion,
                        ToolCalls = batch ?? [],
                        Diagnostics = new(200, null, null),
                        Refusal = completion == LlmCompletionStatus.Refused ? "Apple declined this request." : null,
                        RefusalCategory = completion == LlmCompletionStatus.Refused ? AppleProtocol.RequiredString(frame, "code") switch
                        {
                            "guardrail_violation" => "guardrail_violation",
                            "refusal" => "refusal",
                            _ => throw AppleProtocol.Failure("Unknown Apple refusal classification.")
                        } : null,
                        StructuredOutput = completion == LlmCompletionStatus.Unknown && prepared.Structured is { } structured
                            ? StructuredOutput.Validate(text, structured, prepared.Enforcement!.Value) : null
                    };
                    if (!stream)
                    {
                        foreach (LlmStreamEvent completeEvent in CompleteResponseEvents.From(result, []))
                            yield return completeEvent;
                        break;
                    }
                    if (batch is not null)
                        for (int index = 0; index < batch.Length; index++)
                        {
                            yield return new LlmToolCallStartedEvent(batch[index].Id, batch[index].Name, index);
                            yield return new LlmToolCallArgumentsDeltaEvent(batch[index].Id, batch[index].ArgumentsJson);
                            yield return new LlmToolCallCompletedEvent(batch[index]);
                        }
                    yield return new LlmChatCompletionEvent(result);
                    break;
                default: throw AppleProtocol.Failure("Unknown Apple model event.");
            }
        }
        if (!terminal) throw AppleProtocol.Failure("Apple generation ended without a terminal result.");
    }

    private static LlmToolCall[] ReadCalls(JsonElement frame, Prepared request)
    {
        if (!frame.TryGetProperty("calls", out JsonElement calls) || calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() == 0)
            throw AppleProtocol.Failure("Apple returned an empty or invalid tool batch.");
        var ids = new HashSet<string>(request.ExistingCallIds, StringComparer.Ordinal);
        return calls.EnumerateArray().Select(call =>
        {
            string id = AppleProtocol.RequiredString(call, "id"), name = AppleProtocol.RequiredString(call, "name");
            string arguments = AppleProtocol.RequiredString(call, "arguments");
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id) || !request.ToolSchemas.TryGetValue(name, out JsonElement schema)
                || ParseJson(arguments).ValueKind != JsonValueKind.Object
                || !StructuredOutput.Validate(arguments, new() { Name = name, Schema = schema }, LlmStructuredOutputEnforcement.ProviderJsonSchema).IsValid)
                throw AppleProtocol.Failure("Apple returned an unrecognized, duplicate or schema-invalid tool call.");
            return new LlmToolCall(id, name, arguments);
        }).ToArray();
    }

    private static JsonElement ParseJson(string json)
    {
        try { using JsonDocument document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
        catch (JsonException) { throw AppleProtocol.Failure("Apple returned invalid generated JSON."); }
    }

    internal static void ValidateConnection(LlmConnection connection)
    {
        RuntimeInputAdmission.Validate(connection);
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Transport != LlmTransport.AppleFoundationModels) throw new ArgumentException("Wrong Apple connection transport.");
        if (string.IsNullOrWhiteSpace(connection.Endpoint) || connection.Endpoint.IndexOfAny(['\0', '\r', '\n']) >= 0
            || (connection.Endpoint.Contains(Path.DirectorySeparatorChar) && !Path.IsPathFullyQualified(connection.Endpoint)))
            throw new ArgumentException("Apple requires an executable name or an absolute path.");
        if (!string.IsNullOrWhiteSpace(connection.Credential) || connection.AdditionalHeaders.Count > 0)
            throw new ArgumentException("Apple on-device generation accepts no credential or secret headers.");
        _ = AppleProcessLimits.Read(connection);
    }
}
