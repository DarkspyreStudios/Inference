using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmAgentToolDefinition), "function")]
[JsonDerivedType(typeof(LlmAgentShellTool), "shell")]
public abstract record LlmAgentClientToolDefinition
{
    private protected LlmAgentClientToolDefinition() { }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentShellTool(LlmShellTool Tool) : LlmAgentClientToolDefinition
{
    private readonly LlmShellTool _tool = Tool ?? throw new ArgumentNullException(nameof(Tool));
    [JsonRequired] public LlmShellTool Tool { get => _tool; init => _tool = value ?? throw new ArgumentNullException(nameof(value)); }
}
