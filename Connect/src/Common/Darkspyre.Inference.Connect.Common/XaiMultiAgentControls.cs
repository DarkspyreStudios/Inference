using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

// xAI batch and deferred chat use this; they are out of the build (Connect/src/Resources/Darkspyre.Inference.Connect.Resources,
// outside the solution). It moves into that provider's own library when the provider returns to the build.
/// <summary>Null requests the provider default and suppresses a saved agent count.</summary>
/// <param name="AgentCount">The number of agents to run, or null for the provider default.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record XaiMultiAgentControls(int? AgentCount = null) : LlmProviderControls;
