using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.ConsoleTester;

internal sealed record ConsoleTesterProvider(LlmProviderDefinition Definition, string Id)
{
    public string DisplayName => Definition.DisplayName;
    public string ConnectionId => "console-tester-" + Id;
    public LlmTransport Transport => Definition.Transport;
    public string Endpoint => Definition.DefaultEndpoint;

    public LlmConnection CreateConnection(string version, string? credential) => new()
    {
        Version = version,
        Transport = Transport,
        Endpoint = Endpoint,
        Credential = credential
    };
}

// Adapters own discovery and defaults. Short command aliases also keep the existing vault keys stable.
internal sealed class ConsoleTesterProviders(IEnumerable<ILlmClient> clients)
{
    public IReadOnlyList<ConsoleTesterProvider> All { get; } = clients
        .Select(client => client.ProviderDefinition)
        .OrderBy(definition => definition.DisplayOrder)
        .ThenBy(definition => definition.DisplayName, StringComparer.Ordinal)
        .Select(definition => new ConsoleTesterProvider(definition, Alias(definition.Id)))
        .ToArray();

    public ConsoleTesterProvider Resolve(string id) => All.FirstOrDefault(provider =>
        string.Equals(provider.Id, Normalize(id), StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider.Definition.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException(
            $"Unknown provider '{id}'. Choose one of: {string.Join(", ", All.Select(provider => provider.Id))}.", nameof(id));

    public ConsoleTesterProvider? FindByConnectionId(string connectionId) =>
        All.FirstOrDefault(provider => provider.ConnectionId == connectionId);

    private static string Alias(string id) => id switch
    {
        "chatgpt-subscription" => "codex",
        "claude-subscription" => "claude",
        "gemini-subscription" => "antigravity",
        "bedrock-aws-profile" => "bedrock",
        "bedrock-agentcore-harness" => "agentcore",
        "apple-foundation-models" => "apple",
        "google-vertex-ai" => "vertex",
        "gemini-api" => "gemini",
        _ => id
    };

    private static string Normalize(string id) => id.ToLowerInvariant() switch
    {
        "chatgpt" => "codex",
        "agy" => "antigravity",
        "aws-bedrock" => "bedrock",
        "bedrock-agentcore" => "agentcore",
        _ => id
    };
}
