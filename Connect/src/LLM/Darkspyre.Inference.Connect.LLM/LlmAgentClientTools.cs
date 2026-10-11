using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Base type for a client tool offered to an agent runtime, serialized with a <c>type</c> discriminator of <c>function</c> or <c>shell</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmAgentToolDefinition), "function")]
[JsonDerivedType(typeof(LlmAgentShellTool), "shell")]
public abstract record LlmAgentClientToolDefinition
{
    private protected LlmAgentClientToolDefinition() { }
}

/// <summary>A shell tool offered to an agent runtime.</summary>
/// <param name="Tool">The shell tool definition.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentShellTool(LlmShellTool Tool) : LlmAgentClientToolDefinition
{
    private readonly LlmShellTool _tool = Tool ?? throw new ArgumentNullException(nameof(Tool));
    /// <summary>The shell tool definition.</summary>
    [JsonRequired] public LlmShellTool Tool { get => _tool; init => _tool = value ?? throw new ArgumentNullException(nameof(value)); }
}
