using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.Image.Providers;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ComfyOutputImage))]
internal sealed partial class ComfyOutputJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ComfyPromptRequest))]
[JsonSerializable(typeof(ComfyDeleteRequest))]
[JsonSerializable(typeof(ComfyWorkflowDiagnostics))]
[JsonSerializable(typeof(ComfyHistoryDiagnostics))]
[JsonSerializable(typeof(ComfyQueueDiagnostics))]
[JsonSerializable(typeof(ComfyResponseDiagnostics))]
internal sealed partial class ComfyProtocolJsonContext : JsonSerializerContext;

internal sealed record ComfyOutputImage(string FileName, string Subfolder, string Type);
internal sealed record ComfyPromptRequest(JsonObject Prompt, [property: JsonPropertyName("client_id")] string ClientId);
internal sealed record ComfyDeleteRequest(string[] Delete);
internal sealed record ComfyWorkflowDiagnostics(string WorkflowId, string WorkflowVersion, string[] RequiredNodeTypes, string[] MissingNodeTypes);
internal sealed record ComfyHistoryDiagnostics(string Source, string Status);
internal sealed record ComfyQueueDiagnostics(string Source, bool Running, bool Pending);
internal sealed record ComfyResponseDiagnostics(string Status, string Response);
