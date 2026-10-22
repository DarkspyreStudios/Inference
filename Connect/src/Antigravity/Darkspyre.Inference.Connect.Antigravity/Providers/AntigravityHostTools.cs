using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.HostTools;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// One AGY session's host tools. The session's MCP tool list is registered with the host-tools service under a fresh
/// session ID, which the AGY process receives in <see cref="HostToolsProtocol.SessionVariable"/>. Each call AGY makes
/// through the host-tools server runs on the shared <see cref="McpToolBridge"/>, and its tool event is kept for the
/// turn's mapping of AGY's call_mcp_tool step.
/// </summary>
internal sealed class AntigravityHostTools : IAsyncDisposable
{
    /// <summary>AGY's native tool that calls a configured MCP server's tool.</summary>
    internal const string CallMcpTool = "call_mcp_tool";

    private static readonly SemaphoreSlim Connecting = new(1, 1);

    /// <summary>The host-tools service package beside the host-tools library.</summary>
    private static readonly string ServicePackage = Path.Combine(
        Path.GetDirectoryName(typeof(HostToolsRuntime).Assembly.Location)
            ?? throw new InvalidDataException("The host-tools client has no physical location."),
        HostToolsRuntime.PackageFolder);

    private static HostToolsClient? s_client;

    private readonly McpToolBridge _bridge;
    private readonly ConcurrentDictionary<int, LlmAgentToolEvent> _completed = new();
    private IAsyncDisposable? _registration;
    private Turn? _turn;

    private AntigravityHostTools(McpToolBridge bridge) => _bridge = bridge;

    /// <summary>The ID the AGY process's environment carries to the MCP program.</summary>
    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Registers <paramref name="tools"/> for a new AGY session, after pointing AGY's global configuration at the
    /// host-tools MCP program. Returns null when the session has no host tools.
    /// </summary>
    public static async Task<AntigravityHostTools?> OpenAsync(LlmConnection connection, string? modelId,
        IReadOnlyList<LlmAgentClientToolDefinition> tools, LlmAgentToolHandler? handler, IInferenceResourceAccess? resources,
        IAntigravityCliRunner runner, string executable, string workingDirectory, CancellationToken cancellationToken)
    {
        if (tools.Count == 0) return null;
        // The first definition of a duplicated name is the one used.
        IReadOnlyList<LlmAgentToolDefinition> definitions = [.. ClientTools.Functions(tools).DistinctBy(tool => tool.Name, StringComparer.Ordinal)];
        InferenceResourceSelectionVersion? scope = null;
        if (resources is not null)
        {
            InferenceResourceSelection selection = await LlmResourcePreparation.ReadSelectionAsync(resources, static () => { }, cancellationToken)
                .ConfigureAwait(false);
            scope = new(selection.WorkspaceId, selection.SessionId, selection.Revision);
        }
        var bridge = new McpToolBridge(HostToolsProtocol.ServerName, definitions,
            LlmToolNames.Create(definitions.Select(tool => tool.Name), McpToolBridge.IsValidToolName), handler,
            NativeAgentContract.Antigravity.RichContentSupported.For(connection, null),
            NativeAgentContract.Antigravity.RichContentSupported.For(connection, modelId),
            () => connection, () => resources, scope);
        var hostTools = new AntigravityHostTools(bridge);
        try
        {
            await AntigravityHostToolsSetup.EnsureAsync(runner, executable, workingDirectory, cancellationToken).ConfigureAwait(false);
            HostToolsClient client = await ClientAsync(cancellationToken).ConfigureAwait(false);
            hostTools._registration = await client.RegisterAsync(hostTools.SessionId,
                JsonSerializer.SerializeToElement(bridge.ListResult()), hostTools.CallAsync, cancellationToken).ConfigureAwait(false);
            return hostTools;
        }
        catch
        {
            await hostTools.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Sets the turn that host tool calls belong to, and clears the previous turn's results.</summary>
    public void BeginTurn(string threadId, string turnId, int generation)
    {
        _completed.Clear();
        Volatile.Write(ref _turn, new Turn(threadId, turnId, generation));
    }

    /// <summary>Whether an AGY tool step is a call to a host tool.</summary>
    public static bool IsHostCall(AntigravityToolStep tool) =>
        string.Equals(tool.Name, CallMcpTool, StringComparison.Ordinal)
        && tool.Parameters.ValueKind == JsonValueKind.Object
        && tool.Parameters.TryGetProperty("ServerName", out JsonElement server)
        && server.ValueKind == JsonValueKind.String
        && string.Equals(server.GetString(), HostToolsProtocol.ServerName, StringComparison.Ordinal);

    /// <summary>The running event for a host call step, under the host tool's own name and arguments.</summary>
    public LlmAgentToolEvent Started(string threadId, string turnId, string itemId, AntigravityToolStep tool)
    {
        string native = tool.Parameters.TryGetProperty("ToolName", out JsonElement name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()! : string.Empty;
        JsonElement arguments = tool.Parameters.TryGetProperty("Arguments", out JsonElement supplied) ? supplied.Clone() : JsonElement.Parse("{}");
        return new LlmAgentToolEvent(threadId, turnId, itemId, HostToolsProtocol.ServerName, _bridge.Names.Logical(native) ?? native,
            arguments, null, "inProgress");
    }

    /// <summary>The finished host call's event for step <paramref name="stepIndex"/>, when the host ran it.</summary>
    public LlmAgentToolEvent? Completed(int stepIndex) => _completed.TryRemove(stepIndex, out LlmAgentToolEvent? toolEvent) ? toolEvent : null;

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_registration is { } registration) await registration.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The service connection is gone, and the session's registration with it.
        }
        finally
        {
            _bridge.Dispose();
        }
    }

    /// <summary>Runs one tools/call from AGY's MCP program and returns its MCP CallToolResult.</summary>
    private async Task<JsonElement> CallAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _turn) is not { } turn)
            return JsonSerializer.SerializeToElement(McpToolBridge.TextResult("No turn is running for this host tool call.", isError: true));
        int? step = StepIndex(parameters);
        // The call shares the item ID the turn's mapping gives AGY's step: generation and step index.
        string callId = step is { } index ? $"{turn.Generation}:{index}" : Guid.NewGuid().ToString("N");
        McpToolRequest request = _bridge.Prepare(parameters, turn.ThreadId, turn.TurnId, (_, _) => callId);
        if (request.InvalidParameters is { } invalid)
            return JsonSerializer.SerializeToElement(McpToolBridge.TextResult(invalid, isError: true));
        McpToolOutcome outcome;
        try
        {
            outcome = request.Rejected ?? await _bridge.RunAsync(request.Call!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // AGY cancelled the call, or the session ended.
            return JsonSerializer.SerializeToElement(McpToolBridge.TextResult("The tool call was cancelled.", isError: true));
        }
        if (step is { } completed) _completed[completed] = outcome.Event;
        return JsonSerializer.SerializeToElement(outcome.Result);
    }

    /// <summary>
    /// The AGY step a call belongs to. AGY's progress token is "&lt;trajectory&gt;:&lt;step index&gt;" (AGY 1.3.2).
    /// </summary>
    private static int? StepIndex(JsonElement parameters) =>
        parameters.ValueKind == JsonValueKind.Object
        && parameters.TryGetProperty("_meta", out JsonElement meta) && meta.ValueKind == JsonValueKind.Object
        && meta.TryGetProperty("progressToken", out JsonElement token) && token.ValueKind == JsonValueKind.String
        && token.GetString() is { } text && text.LastIndexOf(':') is var colon and >= 0
        && int.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            ? index : null;

    /// <summary>The process's one connection to the host-tools service, reopened when the service closed it.</summary>
    private static async Task<HostToolsClient> ClientAsync(CancellationToken cancellationToken)
    {
        await Connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (s_client is { } open && !open.Closed.IsCompleted) return open;
            if (s_client is { } closed) await closed.DisposeAsync().ConfigureAwait(false);
            s_client = await HostToolsRuntime.ConnectAsync(ServicePackage, cancellationToken).ConfigureAwait(false);
            return s_client;
        }
        finally
        {
            Connecting.Release();
        }
    }

    private sealed record Turn(string ThreadId, string TurnId, int Generation);
}
