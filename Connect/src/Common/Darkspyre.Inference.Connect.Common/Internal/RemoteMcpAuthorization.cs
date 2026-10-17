using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// Resolves the live authorization of each remote MCP server in a Responses payload and writes it on the server's
/// <c>mcp</c> tool entry before the payload is sent. The OpenAI API and Grok share it.
/// </summary>
internal static class RemoteMcpAuthorization
{
    /// <summary>
    /// Writes <c>authorization</c> on each <c>mcp</c> entry of <paramref name="payload"/> whose
    /// <see cref="LlmRemoteMcpTool"/> carries an <see cref="LlmRemoteMcpTool.Authorization"/>. The entries match the
    /// tools in order. Each authority is asked once per payload. Returns the secret values the payload now holds,
    /// the static MCP header values included, for redaction from diagnostics.
    /// </summary>
    /// <exception cref="InvalidOperationException">An authority returned no header or a header that is not a usable bearer token.</exception>
    internal static async Task<IReadOnlyList<string>> ApplyAsync(JsonObject payload, IReadOnlyList<LlmHostedTool> tools,
        CancellationToken cancellationToken)
    {
        LlmRemoteMcpTool[] servers = [.. tools.OfType<LlmRemoteMcpTool>()];
        if (servers.Length == 0) return [];
        var secrets = new List<string>();
        foreach (LlmRemoteMcpTool server in servers)
            if (server.Headers is { } headers) secrets.AddRange(headers.Values);
        JsonObject[] entries = [.. (payload["tools"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(entry => entry["type"]?.GetValue<string>() == "mcp")];
        if (entries.Length != servers.Length)
            throw new InvalidOperationException("The payload's MCP entries do not match the request's MCP servers.");
        var resolved = new Dictionary<IProviderAuthorization, string>(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < servers.Length; index++)
        {
            if (servers[index].Authorization is not { } authority) continue;
            if (!resolved.TryGetValue(authority, out string? token))
            {
                ProviderAuthorizationHeader header = await authority.GetAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("MCP authorization returned no header.");
                // Both services take the bearer token itself in the authorization field.
                if (!string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(header.Value)
                    || header.Value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
                    throw new InvalidOperationException("MCP authorization must be a bearer token.");
                token = header.Value;
                resolved.Add(authority, token);
                secrets.Add(token);
            }
            entries[index]["authorization"] = token;
        }
        return secrets;
    }
}
