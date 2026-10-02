using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Common.LLM;

/// <summary>Provider-owned typed controls. Metadata and entry validators admit only their fixed variants.</summary>
public abstract record LlmProviderControls
{
    protected LlmProviderControls() { }
}

internal sealed class CommonProviderControlsJsonConverter : JsonConverter<LlmProviderControls>
{
    public override bool HandleNull => true;
    public override LlmProviderControls? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null
            : throw new NotSupportedException("Common metadata does not admit connector-owned provider controls.");
    public override void Write(Utf8JsonWriter writer, LlmProviderControls value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else throw new NotSupportedException("Common metadata does not admit connector-owned provider controls.");
    }
}
