using System.Text.Json;

namespace Darkspyre.Inference.Connect.HostTools;

/// <summary>
/// The host-tools service's names and messages. A host registers each agent session's MCP tool list under a session ID;
/// the MCP program an agent CLI starts reads that ID from its environment, lists the session's tools and forwards each
/// call, which the service passes back to the registering host as a callback.
/// </summary>
public static class HostToolsProtocol
{
    /// <summary>The environment variable that carries the session ID into the agent CLI and the MCP program it starts.</summary>
    public const string SessionVariable = "DARKSPYRE_HOST_TOOLS_SESSION";

    /// <summary>The MCP server name the agent CLI's global configuration registers.</summary>
    public const string ServerName = "darkspyre-host-tools";

    /// <summary>The MCP program's executable name, without the Windows .exe extension.</summary>
    public const string McpExecutableName = "darkspyre-host-tools-mcp";

    /// <summary>Host operation: registers a session's tool list. Body: <see cref="HostToolsRegistration"/>.</summary>
    public const string Register = "register";

    /// <summary>Host operation: removes a session. Body: <see cref="HostToolsSession"/>.</summary>
    public const string Unregister = "unregister";

    /// <summary>MCP program operation: returns the session's MCP tools/list result. Body: <see cref="HostToolsSession"/>.</summary>
    public const string List = "list";

    /// <summary>
    /// MCP program operation and host callback: runs a tools/call and returns its MCP CallToolResult. Body:
    /// <see cref="HostToolsCall"/>.
    /// </summary>
    public const string Call = "call";

    /// <summary>The operations every subscriber may send.</summary>
    public static IReadOnlySet<string> Operations { get; } = new HashSet<string>(StringComparer.Ordinal) { Register, Unregister, List, Call };

    /// <summary>The JSON options for every host-tools message.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
}

/// <summary>A session's MCP tool list, registered by the host that runs its tools.</summary>
/// <param name="Session">The session ID the agent CLI's environment carries.</param>
/// <param name="ToolsList">The MCP tools/list result for the session.</param>
public sealed record HostToolsRegistration(string Session, JsonElement ToolsList);

/// <summary>Names a registered session.</summary>
/// <param name="Session">The session ID.</param>
public sealed record HostToolsSession(string Session);

/// <summary>A tools/call for a registered session.</summary>
/// <param name="Session">The session ID.</param>
/// <param name="Parameters">The MCP tools/call params: name, arguments and _meta.</param>
public sealed record HostToolsCall(string Session, JsonElement Parameters);
