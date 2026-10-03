using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Response-local hosted observations from the shared Responses parser.</summary>
internal abstract class ResponsesHostedToolObserver
{
    internal abstract IEnumerable<LlmStreamEvent> ObserveAdded(JsonElement root);
    internal abstract IEnumerable<LlmStreamEvent> Complete(JsonElement root);
}
