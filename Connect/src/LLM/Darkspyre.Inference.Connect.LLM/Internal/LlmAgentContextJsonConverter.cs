using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>The owned context codec emits format 3 and admits one closed legacy format-2 graph.</summary>
internal sealed class LlmAgentContextJsonConverter : JsonConverter<LlmAgentContext>
{
    public override LlmAgentContext Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("FormatVersion", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int format))
            throw new JsonException("A captured context requires an explicit supported format version.");
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
            if (!members.Add(property.Name)) throw new JsonException("Captured context members cannot be ambiguous.");
        try
        {
            LlmAgentContext context = format switch
            {
                2 => Upgrade(JsonSerializer.Deserialize(root, options.GetTypeInfo<LegacyAgentContext>())!),
                3 => ReadCurrent(JsonSerializer.Deserialize(root, options.GetTypeInfo<AgentContextFormat3>())!),
                _ => throw new JsonException("The captured context format is unsupported.")
            };
            return Validate(context);
        }
        catch (ArgumentException error)
        {
            throw new JsonException("The captured context contains invalid portable data.", error);
        }
    }

    public override void Write(Utf8JsonWriter writer, LlmAgentContext value, JsonSerializerOptions options)
    {
        LlmAgentContext snapshot;
        try { snapshot = Validate(value); }
        catch (ArgumentException error) { throw new JsonException("The captured context contains invalid portable data.", error); }
        JsonSerializer.Serialize(writer, new AgentContextFormat3
        {
            FormatVersion = 3,
            Revision = snapshot.Revision,
            Entries = snapshot.Entries,
            SelectedResources = snapshot.SelectedResources,
            ResourceSelectionVersion = snapshot.ResourceSelectionVersion
        }, options.GetTypeInfo<AgentContextFormat3>());
    }

    private static LlmAgentContext ReadCurrent(AgentContextFormat3 value) => new()
    {
        FormatVersion = value.FormatVersion,
        Revision = value.Revision,
        Entries = Required(value.Entries),
        SelectedResources = Required(value.SelectedResources),
        ResourceSelectionVersion = value.ResourceSelectionVersion
    };

    private static LlmAgentContext Upgrade(LegacyAgentContext value) => new()
    {
        FormatVersion = 3,
        Revision = value.Revision,
        Entries = Array.AsReadOnly(Required(value.Entries).Select(static entry =>
        {
            Required(entry);
            LegacyContextMessage message = Required(entry.Message);
            return new LlmAgentContextEntry(entry.Id, entry.TurnId, new LlmChatMessage(message.Role, message.Content)
            {
                ContinuationState = message.ContinuationState,
                OmittedAttachments = Required(message.OmittedAttachments),
                Attachments = Array.AsReadOnly(Required(message.Attachments).Select(static attachment =>
                {
                    Required(attachment);
                    if (!Enum.IsDefined(attachment.Kind)) throw new JsonException("Unknown legacy binary attachment kind.");
                    return (LlmMessageAttachment)new LlmAttachment
                    {
                        Kind = attachment.Kind,
                        FileName = attachment.FileName,
                        MediaType = attachment.MediaType,
                        Data = attachment.Data.ToArray()
                    };
                }).ToArray()),
                ToolCalls = Array.AsReadOnly(Required(message.ToolCalls).Select(static call =>
                {
                    Required(call);
                    return new LlmToolCall(call.Id, call.Name, call.ArgumentsJson);
                }).ToArray()),
                ToolResults = Array.AsReadOnly(Required(message.ToolResults).Select(static result =>
                {
                    Required(result);
                    LegacyToolOutput output = Required(result.Output);
                    return new LlmToolResult(result.ToolCallId, new LlmToolOutput
                    {
                        IsError = output.IsError,
                        StructuredContent = output.StructuredContent?.Clone(),
                        Parts = Array.AsReadOnly(Required(output.Parts).Select(ConvertPart).ToArray())
                    });
                }).ToArray())
            });
        }).ToArray()),
        SelectedResources = [],
        ResourceSelectionVersion = null
    };

    private static LlmToolOutputPart ConvertPart(LegacyToolOutputPart part) => Required(part) switch
    {
        LegacyToolOutputText text => new LlmToolOutputText(text.Text),
        LegacyToolOutputBinary binary when Enum.IsDefined(binary.Kind) => new LlmToolOutputBinary(
            binary.Kind, binary.MediaType, binary.Data.ToArray(), binary.FileName),
        LegacyToolOutputResource resource => new LlmToolOutputResource(resource.Uri)
        { Name = resource.Name, MediaType = resource.MediaType, Text = resource.Text, Data = resource.Data?.ToArray() },
        _ => throw new JsonException("Unknown legacy tool output kind.")
    };

    private static LlmAgentContext Validate(LlmAgentContext context)
    {
        if (context.FormatVersion != 3) throw new JsonException("Only format 3 can be emitted.");
        return ManagedAgentContext.ValidatePortable(context);
    }

    private static T Required<T>(T? value) where T : class => value ?? throw new JsonException("Captured context data cannot be null.");
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AgentContextFormat3
{
    [JsonRequired] public int FormatVersion { get; init; } = 3;
    public long Revision { get; init; }
    public IReadOnlyList<LlmAgentContextEntry> Entries { get; init; } = [];
    [JsonRequired] public IReadOnlyList<InferenceResourceReference> SelectedResources { get; init; } = [];
    public InferenceResourceSelectionVersion? ResourceSelectionVersion { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LegacyAgentContext
{
    [JsonRequired] public int FormatVersion { get; init; }
    public long Revision { get; init; }
    public IReadOnlyList<LegacyContextEntry> Entries { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LegacyContextEntry(
    [property: JsonRequired] string Id, [property: JsonRequired] string TurnId,
    [property: JsonRequired] LegacyContextMessage Message);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LegacyContextMessage([property: JsonRequired] string Role, [property: JsonRequired] string Content)
{
    public LlmContinuationState? ContinuationState { get; init; }
    public IReadOnlyList<LlmOmittedAttachment> OmittedAttachments { get; init; } = [];
    public IReadOnlyList<LegacyContextAttachment> Attachments { get; init; } = [];
    public IReadOnlyList<LegacyContextToolCall> ToolCalls { get; init; } = [];
    public IReadOnlyList<LegacyContextToolResult> ToolResults { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LegacyContextAttachment
{
    public required LlmBinaryKind Kind { get; init; }
    public required string FileName { get; init; }
    public string? MediaType { get; init; }
    public required ReadOnlyMemory<byte> Data { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LegacyContextToolCall(
    [property: JsonRequired] string Id, [property: JsonRequired] string Name, [property: JsonRequired] string ArgumentsJson);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LegacyContextToolResult(
    [property: JsonRequired] string ToolCallId, [property: JsonRequired] LegacyToolOutput Output);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LegacyToolOutput
{
    public IReadOnlyList<LegacyToolOutputPart> Parts { get; init; } = [];
    public JsonElement? StructuredContent { get; init; }
    public bool IsError { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LegacyToolOutputText), "text")]
[JsonDerivedType(typeof(LegacyToolOutputBinary), "binary")]
[JsonDerivedType(typeof(LegacyToolOutputResource), "resource")]
internal abstract record LegacyToolOutputPart;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LegacyToolOutputText([property: JsonRequired] string Text) : LegacyToolOutputPart;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LegacyToolOutputBinary([property: JsonRequired] LlmBinaryKind Kind,
    [property: JsonRequired] string MediaType, [property: JsonRequired] ReadOnlyMemory<byte> Data,
    string? FileName = null) : LegacyToolOutputPart;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LegacyToolOutputResource([property: JsonRequired] string Uri) : LegacyToolOutputPart
{
    public string? Name { get; init; }
    public string? MediaType { get; init; }
    public string? Text { get; init; }
    public ReadOnlyMemory<byte>? Data { get; init; }
}
