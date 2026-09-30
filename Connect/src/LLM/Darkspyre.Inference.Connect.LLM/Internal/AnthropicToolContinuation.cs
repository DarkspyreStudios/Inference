using System.Text.Json.Nodes;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Checks scoped, projected Claude history without executing or rewriting native content.</summary>
internal static class AnthropicToolContinuation
{
    internal static void Validate(JsonObject payload)
    {
        JsonArray messages = payload["messages"]!.AsArray();
        var invocations = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonNode[] native = payload["tools"] is JsonArray tools
            ? tools.Where(tool => Text(tool!, "type") is not null).Select(tool => tool!).ToArray() : [];
        var declared = native.Select(tool => Text(tool, "name")).ToHashSet(StringComparer.Ordinal);
        bool hasProgrammaticRuntime = native.Any(tool => Text(tool, "type") is "code_execution_20260120" or "code_execution_20260521");
        bool hasImplicitExecution = native.Any(tool => Text(tool, "type") is
            "web_search_20260209" or "web_search_20260318" or "web_fetch_20260209" or "web_fetch_20260309" or "web_fetch_20260318");
        bool hasExecutionSubtools = hasProgrammaticRuntime || hasImplicitExecution
            || native.Any(tool => Text(tool, "type") == "code_execution_20250825");
        bool ProvidesInvocation(string name) => name switch
        {
            "bash_code_execution" or "text_editor_code_execution" => hasExecutionSubtools,
            "code_execution" => declared.Contains(name) || hasImplicitExecution,
            _ => declared.Contains(name)
        };
        for (int index = 0; index < messages.Count; index++)
        {
            JsonNode message = messages[index]!;
            if (Text(message, "role") != "assistant") continue;
            JsonArray content = message["content"]!.AsArray();
            JsonNode[] server = content.Where(block => Text(block!, "type") == "server_tool_use").Select(block => block!).ToArray();
            foreach (JsonNode invocation in server) invocations[Text(invocation, "id")!] = Text(invocation, "name")!;
            JsonNode[] clients = content.Where(block => Text(block!, "type") == "tool_use").Select(block => block!).ToArray();
            var answered = content.Select(block => Text(block!, "tool_use_id")).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
            string[] pendingNative = server.Where(invocation => !answered.Contains(Text(invocation, "id")))
                .Select(invocation => Text(invocation, "name")!).ToArray();
            if (clients.Length == 0)
            {
                if (index == messages.Count - 1 && pendingNative.Any(name => !ProvidesInvocation(name)))
                    throw Invalid("Active Claude continuation requires its native tool definitions.");
                continue;
            }
            var programmatic = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonNode client in clients)
            {
                if (client["caller"] is not JsonObject caller || Text(caller, "type") is not
                    ("code_execution_20260120" or "code_execution_20260521")) continue;
                string? invocationId = Text(caller, "tool_id");
                if (string.IsNullOrWhiteSpace(invocationId) || !invocations.TryGetValue(invocationId, out string? name) || name != "code_execution")
                    throw Invalid("Programmatic Claude callers require a retained code-execution invocation.");
                programmatic.Add(Text(client, "id")!);
            }
            if (programmatic.Count == 0 && pendingNative.Length == 0) continue;

            if (index + 1 >= messages.Count || Text(messages[index + 1]!, "role") != "user"
                || messages[index + 1]!["content"] is not JsonArray results
                || results.Any(result => Text(result!, "type") != "tool_result"))
                throw Invalid("Mixed or programmatic Claude calls require an immediate tool-result-only reply.");
            var waiting = clients.Select(client => Text(client, "id")).ToHashSet(StringComparer.Ordinal);
            if (waiting.Count != clients.Length)
                throw Invalid("Claude client call identities must be unique.");
            var returned = new HashSet<string?>(StringComparer.Ordinal);
            foreach (JsonNode? result in results)
            {
                string? id = Text(result!, "tool_use_id");
                if (!waiting.Contains(id) || !returned.Add(id))
                    throw Invalid("Claude tool results must answer every waiting client exactly once.");
                if (programmatic.Contains(id!))
                {
                    JsonNode? value = result!["content"];
                    if (value is JsonValue text && text.TryGetValue<string>(out _)) continue;
                    if (value is JsonArray blocks && blocks.All(block => Text(block!, "type") == "text"
                        && block!["text"] is JsonValue part && part.TryGetValue<string>(out _))) continue;
                    throw Invalid("Programmatic Claude tool results accept only text.");
                }
            }
            if (!waiting.SetEquals(returned))
                throw Invalid("Claude tool results must answer every waiting client exactly once.");
            if (index + 2 < messages.Count)
            {
                if (Text(messages[index + 2]!, "role") != "assistant")
                    throw Invalid("The mixed or programmatic Claude turn must finish before new user input.");
                continue;
            }
            if (programmatic.Count > 0 && string.IsNullOrWhiteSpace(Text(payload, "container")))
                throw Invalid("Programmatic Claude continuation requires a retained container identity.");
            if (pendingNative.Any(name => !ProvidesInvocation(name)) || programmatic.Count > 0 && !hasProgrammaticRuntime)
                throw Invalid("Active Claude continuation requires its native tool definitions.");
        }
    }

    private static string? Text(JsonNode node, string name) => node[name] is JsonValue value
        && value.TryGetValue<string>(out string? text) ? text : null;
    private static ArgumentException Invalid(string message) => new(message);
}
