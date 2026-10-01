using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Media;

namespace Darkspyre.Inference.Connect.Image.Providers;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ComfyOutputImage))]
[JsonSerializable(typeof(ComfyPreparedEnvelope))]
[JsonSerializable(typeof(ComfyUploadImage))]
internal sealed partial class ComfyOutputJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ComfyPromptRequest))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ComfyDeleteRequest))]
[JsonSerializable(typeof(ComfyWorkflowDiagnostics))]
[JsonSerializable(typeof(ComfyHistoryDiagnostics))]
[JsonSerializable(typeof(ComfyQueueDiagnostics))]
[JsonSerializable(typeof(ComfyResponseDiagnostics))]
internal sealed partial class ComfyProtocolJsonContext : JsonSerializerContext;

internal sealed record ComfyOutputImage(string FileName, string Subfolder, string Type);
internal sealed record ComfyUploadImage(
    [property: JsonPropertyName("name"), JsonRequired] string Name,
    [property: JsonPropertyName("subfolder"), JsonRequired] string Subfolder,
    [property: JsonPropertyName("type"), JsonRequired] string Type);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ComfyPreparedEnvelope(
    [property: JsonRequired] JsonElement Workflow,
    [property: JsonRequired] IReadOnlyList<InferenceResourceScope> ReferenceScopes);
internal sealed record ComfyPromptRequest(JsonObject Prompt, [property: JsonPropertyName("client_id")] string ClientId);
internal sealed record ComfyDeleteRequest(string[] Delete);
internal sealed record ComfyWorkflowDiagnostics(string WorkflowId, string WorkflowVersion, string[] RequiredNodeTypes, string[] MissingNodeTypes);
internal sealed record ComfyHistoryDiagnostics(string Source, string Status);
internal sealed record ComfyQueueDiagnostics(string Source, bool Running, bool Pending);
internal sealed record ComfyResponseDiagnostics(string Status, string Response);
