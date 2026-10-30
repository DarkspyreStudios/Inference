using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.HostTools;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.LocalServices;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// One AGY session's host tools. The session's MCP tool list is registered with the host-tools service under a fresh
/// session ID, which the AGY process receives in <see cref="HostToolsProtocol.SessionVariable"/>, beside the data folder
/// the service runs in, in <see cref="HostToolsProtocol.FolderVariable"/>. Each call AGY makes
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

    // The process's one connection to the host-tools service, with the data folder the service runs in.
    private static (string Folder, HostToolsClient Client)? s_client;

    private readonly McpToolBridge _bridge;
    private readonly ConcurrentDictionary<int, LlmAgentToolEvent> _completed = new();
    private HostToolsClient? _client;
    private IAsyncDisposable? _registration;
    private Turn? _turn;

    private AntigravityHostTools(McpToolBridge bridge) => _bridge = bridge;

    /// <summary>The ID the AGY process's environment carries to the MCP program.</summary>
    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// True once the host-tools client this session's tools are registered with has closed, as the stop request in every
    /// data folder move's release makes it.
    /// </summary>
    internal bool Closed => _client is not { } client || client.Closed.IsCompleted;

    /// <summary>
    /// Registers <paramref name="tools"/> for a new AGY session, after pointing AGY's global configuration at the
    /// host-tools MCP program. Returns null when the session has no host tools.
    /// </summary>
    public static async Task<AntigravityHostTools?> OpenAsync(LlmConnection connection, string? modelId,
        IReadOnlyList<LlmAgentClientToolDefinition> tools, LlmAgentToolHandler? handler, IInferenceResourceAccess? resources,
        IAntigravityCliRunner runner, string executable, string workingDirectory, string dataFolder, CancellationToken cancellationToken)
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
            AntigravityNativeAgent.Capabilities.RichContentSupported.For(connection, null),
            AntigravityNativeAgent.Capabilities.RichContentSupported.For(connection, modelId),
            () => connection, () => resources, scope);
        var hostTools = new AntigravityHostTools(bridge);
        try
        {
            await AntigravityHostToolsSetup.EnsureAsync(runner, executable, workingDirectory, cancellationToken).ConfigureAwait(false);
            await hostTools.RegisterAsync(dataFolder, cancellationToken).ConfigureAwait(false);
            return hostTools;
        }
        catch
        {
            await hostTools.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Registers this session's tool list again, under the same session ID, with the client of <paramref name="dataFolder"/>.</summary>
    internal async Task ReopenAsync(string dataFolder, CancellationToken cancellationToken)
    {
        await DisposeRegistrationAsync().ConfigureAwait(false);
        await RegisterAsync(dataFolder, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the host-tools service of this process's client, closing every connection to it, the AGY MCP programs' too, and
    /// drops the client. The next session or reopen connects again with the data folder it reads.
    /// </summary>
    internal static async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        await Connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (s_client is not { } current) return;
            await HostToolsRuntime.StopServiceAsync(ServicePackage, current.Folder, cancellationToken).ConfigureAwait(false);
            await current.Client.DisposeAsync().ConfigureAwait(false);
            s_client = null;
        }
        finally
        {
            Connecting.Release();
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
            await DisposeRegistrationAsync().ConfigureAwait(false);
        }
        finally
        {
            _bridge.Dispose();
        }
    }

    private async Task RegisterAsync(string dataFolder, CancellationToken cancellationToken)
    {
        HostToolsClient client = await ClientAsync(dataFolder, cancellationToken).ConfigureAwait(false);
        _registration = await client.RegisterAsync(SessionId, JsonSerializer.SerializeToElement(_bridge.ListResult()), CallAsync,
            cancellationToken).ConfigureAwait(false);
        _client = client;
    }

    private async Task DisposeRegistrationAsync()
    {
        IAsyncDisposable? registration = _registration;
        _registration = null;
        _client = null;
        try
        {
            if (registration is not null) await registration.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The service connection is gone, and the session's registration with it.
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

    /// <summary>
    /// The process's one connection to the host-tools service in <paramref name="dataFolder"/>, reopened when the service
    /// closed it or the folder changed. The app owns the service, so the connection creates the folder's service files.
    /// </summary>
    private static async Task<HostToolsClient> ClientAsync(string dataFolder, CancellationToken cancellationToken)
    {
        await Connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (s_client is { } open && string.Equals(open.Folder, dataFolder, StringComparison.Ordinal)
                && !open.Client.Closed.IsCompleted) return open.Client;
            if (s_client is { } previous) await previous.Client.DisposeAsync().ConfigureAwait(false);
            HostToolsClient client = await HostToolsRuntime.ConnectAsync(ServicePackage,
                new ServiceFolders(dataFolder, null, CreateIfMissing: true), cancellationToken).ConfigureAwait(false);
            s_client = (dataFolder, client);
            return client;
        }
        finally
        {
            Connecting.Release();
        }
    }

    private sealed record Turn(string ThreadId, string TurnId, int Generation);
}
