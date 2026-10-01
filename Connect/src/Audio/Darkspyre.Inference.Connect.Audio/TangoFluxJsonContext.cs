using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.Audio;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(TangoFluxGeneratePayload))]
internal sealed partial class TangoFluxJsonContext : JsonSerializerContext;

internal sealed record TangoFluxGeneratePayload(string Prompt, double Duration, int Steps, double GuidanceScale,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Seed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? UseCache);
