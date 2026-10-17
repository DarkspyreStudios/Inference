using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>A checked MCP tools/call: the call to run, a JSON-RPC parameter error, or a rejection to answer with.</summary>
/// <param name="Call">The call to run, when the request is valid.</param>
/// <param name="InvalidParameters">The JSON-RPC -32602 message, when the request names no known tool.</param>
/// <param name="Rejected">The answer and tool event for arguments the tool's schema rejects.</param>
internal sealed record McpToolRequest(LlmAgentToolCall? Call, string? InvalidParameters, McpToolOutcome? Rejected);

/// <summary>An MCP CallToolResult and the host tool event that records it.</summary>
/// <param name="Result">The MCP result object: content and isError.</param>
/// <param name="Event">The tool event for the session's stream.</param>
internal sealed record McpToolOutcome(JsonObject Result, LlmAgentToolEvent Event);

/// <summary>
/// Serves a session's host tools to a CLI over MCP: the tools/list entries, the tools/call argument check, and the
/// handler run that turns a tool's output into an MCP result. The Claude and Antigravity CLIs both route through it.
/// </summary>
internal sealed class McpToolBridge(
    string serverName,
    IReadOnlyList<LlmAgentToolDefinition> tools,
    LlmToolNames names,
    LlmAgentToolHandler? handler,
    LlmRichContentSupported richRoute,
    LlmRichContentSupported richContent,
    Func<LlmConnection> connection,
    Func<IInferenceResourceAccess?> resourceAccess,
    InferenceResourceSelectionVersion? resourceScope) : IDisposable
{
    private readonly Dictionary<string, LlmAgentToolDefinition> _byName = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
    private readonly SemaphoreSlim _sequential = new(1, 1);

    /// <summary>The MCP server name the CLI sees for the host tools.</summary>
    public string ServerName { get; } = serverName;

    /// <summary>The session's host tools.</summary>
    public IReadOnlyList<LlmAgentToolDefinition> Tools { get; } = tools;

    /// <summary>The logical-to-native tool name map.</summary>
    public LlmToolNames Names { get; } = names;

    /// <summary>The MCP tool-name rule. A name outside it is sent as its <see cref="LlmToolNames.Alias"/>.</summary>
    public static bool IsValidToolName(string name) =>
        name.Length is > 0 and <= 128
        && name.All(character =>
            character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_'
            or '-'
            or '.');

    /// <summary>The session's definition of <paramref name="name"/>, or null for a tool it does not offer.</summary>
    public LlmAgentToolDefinition? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>The tool's MCP tools/list entry: native name, description and wire input schema.</summary>
    public static JsonObject ToolEntry(LlmAgentToolDefinition tool, LlmToolNames names) => new()
    {
        ["name"] = names.Native(tool.Name),
        ["description"] = tool.Description ?? string.Empty,
        ["inputSchema"] = JsonNode.Parse(ToolSchemas.Wire(tool.InputSchema).GetRawText())
    };

    /// <summary>The MCP tools/list result for the session's tools.</summary>
    public JsonObject ListResult() => new() { ["tools"] = new JsonArray([.. Tools.Select(tool => ToolEntry(tool, Names))]) };

    /// <summary>An MCP CallToolResult with one text part.</summary>
    public static JsonObject TextResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError
    };

    /// <summary>
    /// Checks a tools/call's parameters. <paramref name="callId"/> receives the logical tool name and the arguments and
    /// returns the call's ID.
    /// </summary>
    public McpToolRequest Prepare(JsonElement parameters, string threadId, string turnId, Func<string, JsonElement, string> callId)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return new(null, "Invalid tool parameters.", null);
        string? name = parameters.TryGetProperty("name", out JsonElement native) && native.ValueKind == JsonValueKind.String
            ? Names.Logical(native.GetString()!) : null;
        if (name is null || !_byName.TryGetValue(name, out LlmAgentToolDefinition? definition)) return new(null, "Unknown tool.", null);
        JsonElement arguments = parameters.TryGetProperty("arguments", out JsonElement supplied) ? supplied.Clone() : JsonElement.Parse("{}");
        string id = callId(name, arguments);
        // A wrapped schema's value is checked against the tool's own schema and is what the handler receives.
        StructuredOutput.Evaluation evaluation = ToolSchemas.EvaluateArguments(definition.InputSchema, arguments.GetRawText());
        if (ClientTools.ArgumentRejection(name, evaluation) is { } rejection)
        {
            // The handler does not run; the model receives the errors and the turn continues.
            return new(null, null, new(TextResult(rejection, isError: true), new LlmAgentToolEvent(threadId, turnId, id, ServerName, name,
                arguments, false, "failed", LlmToolOutput.FromText(rejection, isError: true))));
        }
        return new(new LlmAgentToolCall(threadId, turnId, id, ServerName, name, evaluation.Instance), null, null);
    }

    /// <summary>Runs a checked call through the session's handler and returns its MCP result and tool event.</summary>
    /// <gotcha>Cancellation through <paramref name="cancellationToken"/> propagates; the caller answers the CLI.</gotcha>
    public async Task<McpToolOutcome> RunAsync(LlmAgentToolCall call, CancellationToken cancellationToken)
    {
        LlmToolOutput output;
        bool sequential = _byName[call.Name].RequiresSequentialExecution;
        bool entered = false;
        try
        {
            if (handler is null)
            {
                output = LlmToolOutput.FromText(ClientTools.NoHandler(call.Name), isError: true);
            }
            else
            {
                if (sequential)
                {
                    await _sequential.WaitAsync(cancellationToken).ConfigureAwait(false);
                    entered = true;
                }
                output = await handler(call, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A handler failure is the tool's error result; the turn continues.
        catch (Exception error)
#pragma warning restore CA1031
        {
            output = LlmToolOutput.FromText(ClientTools.HandlerFailure(error, connection()), isError: true);
        }
        finally
        {
            if (entered) _sequential.Release();
        }

        cancellationToken.ThrowIfCancellationRequested();
        output = ManagedAgentContext.CopyOutput(output);
        if (output.Parts.Any(part => part is LlmToolOutputSessionResource))
        {
            IInferenceResourceAccess access = resourceAccess() ?? throw new NotSupportedException("Tool resources require caller-owned access.");
            LlmResourcePreparation.ValidateScope(access, resourceScope!, static () => { }, cancellationToken);
            var request = new LlmChatRequest
            {
                ModelId = string.Empty,
                Resources = access,
                Messages = [new("user", string.Empty) { ToolResults = [new LlmToolResult(call.CallId, output)] }]
            };
            // A resource part the MCP route cannot carry is left out with the projection's notice.
            return await LlmResourcePreparation.ProjectInlineAsync(request, prepared => Outcome(call, output,
                ToolOutputProjector.Project(ClientTools.Functions(prepared.Messages[0].ToolResults)[0].Output, richRoute, richContent)),
                static () => { }, cancellationToken, includeSelection: false).ConfigureAwait(false);
        }
        // Unsupported parts are omitted by the projection; they never fail the tool or the turn.
        return Outcome(call, output, ToolOutputProjector.Project(output, richRoute, richContent));
    }

    public void Dispose() => _sequential.Dispose();

    private McpToolOutcome Outcome(LlmAgentToolCall call, LlmToolOutput output, ProjectedToolOutput projected) => new(
        new JsonObject { ["content"] = Content(projected), ["isError"] = projected.IsError },
        new LlmAgentToolEvent(call.ThreadId, call.TurnId, call.CallId, ServerName, call.Name, call.Arguments,
            !output.IsError, output.IsError ? "failed" : "completed", output));

    private static JsonArray Content(ProjectedToolOutput projected) => new(
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
            _ => throw new InvalidOperationException("The MCP route does not carry this tool output part natively.")
        })
    ]);
}
