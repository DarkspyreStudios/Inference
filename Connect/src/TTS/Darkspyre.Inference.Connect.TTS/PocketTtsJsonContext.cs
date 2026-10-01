using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.TTS;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PocketTtsVoiceList))]
[JsonSerializable(typeof(PocketTtsVoiceOption))]
[JsonSerializable(typeof(PocketTtsBridgePayload))]
internal sealed partial class PocketTtsJsonContext : JsonSerializerContext;

internal sealed record PocketTtsVoiceList(List<string>? Voices, List<PocketTtsVoiceOption>? VoiceOptions);
internal sealed record PocketTtsVoiceOption(string? Id, string? Name, string? Category);
internal sealed record PocketTtsBridgePayload(string Text, string? Voice, string? Speaker, double Speed,
    [property: JsonPropertyName("response_format")] string ResponseFormat);
