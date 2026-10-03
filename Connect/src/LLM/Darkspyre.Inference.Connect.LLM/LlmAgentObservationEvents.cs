using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.LLM;

public enum LlmAgentContentObservationKind
{
    UserMessage = 0,
    AssistantMessage = 1,
    Reasoning = 2
}

public enum LlmAgentContentObservationMode
{
    Append = 0,
    Replace = 1
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentToolPresentationEvent(
    string ThreadId,
    string? TurnId,
    LlmAgentToolPresentation Tool,
    bool IsReplay) : LlmAgentEvent(CallerData.Id(ThreadId), CallerData.OptionalId(TurnId))
{
    [JsonRequired] public override string ThreadId { get => base.ThreadId; init => base.ThreadId = CallerData.Id(value); }
    [JsonRequired] public override string? TurnId { get => base.TurnId; init => base.TurnId = CallerData.OptionalId(value); }
    [JsonRequired] public LlmAgentToolPresentation Tool { get; init => field = AgentObservationData.Tool(value); } = AgentObservationData.Tool(Tool);
    [JsonRequired] public bool IsReplay { get; init; } = IsReplay;
    public override string ToString() => "LlmAgentToolPresentationEvent { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentContentObservationEvent(
    string ThreadId,
    string? TurnId,
    string? ItemId,
    LlmAgentContentObservationKind Kind,
    LlmAgentContentObservationMode Mode,
    bool IsReplay,
    IReadOnlyList<LlmAgentPresentedContentBlock> Content)
    : LlmAgentEvent(AgentObservationData.Thread(ThreadId, ItemId, Mode), CallerData.OptionalId(TurnId))
{
    [JsonRequired] public override string ThreadId { get => base.ThreadId; init => base.ThreadId = CallerData.Id(value); }
    [JsonRequired] public override string? TurnId { get => base.TurnId; init => base.TurnId = CallerData.OptionalId(value); }
    [JsonRequired] public string? ItemId { get; init => field = AgentObservationData.Item(value, this.Mode); } = CallerData.OptionalId(ItemId);
    [JsonRequired] public LlmAgentContentObservationKind Kind { get; init => field = AgentObservationData.Kind(value); } = AgentObservationData.Kind(Kind);
    [JsonRequired] public LlmAgentContentObservationMode Mode { get; init => field = AgentObservationData.Mode(value, this.ItemId); } = AgentObservationData.Mode(Mode, ItemId);
    [JsonRequired] public bool IsReplay { get; init; } = IsReplay;
    [JsonRequired] public IReadOnlyList<LlmAgentPresentedContentBlock> Content { get; init => field = AgentObservationData.Content(value); } = AgentObservationData.Content(Content);
    public override string ToString() => "LlmAgentContentObservationEvent { <redacted> }";
}

internal static class AgentObservationData
{
    internal static string Thread(string threadId, string? itemId, LlmAgentContentObservationMode mode)
    {
        Mode(mode, CallerData.OptionalId(itemId));
        return CallerData.Id(threadId);
    }

    internal static string? Item(string? itemId, LlmAgentContentObservationMode mode)
    {
        var value = CallerData.OptionalId(itemId);
        Mode(mode, value);
        return value;
    }

    internal static LlmAgentContentObservationKind Kind(LlmAgentContentObservationKind value) => Enum.IsDefined(value)
        ? value : throw new ArgumentOutOfRangeException(nameof(value));

    internal static LlmAgentContentObservationMode Mode(LlmAgentContentObservationMode value, string? itemId)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (value == LlmAgentContentObservationMode.Replace && itemId is null)
            throw new ArgumentException("Replacing observed content requires an actual item identity.", nameof(itemId));
        return value;
    }

    internal static void Validate(object value)
    {
        var item = (LlmAgentEvent)value;
        CallerData.Id(item.ThreadId);
        CallerData.OptionalId(item.TurnId);
        if (item is LlmAgentContentObservationEvent content)
        {
            Kind(content.Kind);
            Mode(content.Mode, CallerData.OptionalId(content.ItemId));
        }
    }

    internal static LlmAgentToolPresentation Tool(LlmAgentToolPresentation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value with
        {
            RawInput = value.RawInput,
            RawOutput = value.RawOutput,
            Content = CallerData.List(value.Content.Select(ToolContent).ToArray()),
            Locations = CallerData.List(value.Locations.Select(location => location with { }).ToArray())
        };
    }

    internal static IReadOnlyList<LlmAgentPresentedContentBlock> Content(IReadOnlyList<LlmAgentPresentedContentBlock> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return CallerData.List(value.Select(Block).ToArray());
    }

    private static LlmAgentToolPresentationContent ToolContent(LlmAgentToolPresentationContent value) => value switch
    {
        LlmAgentToolPresentedContent content => content with { Content = Block(content.Content) },
        LlmAgentToolPresentedDiff diff => diff with { },
        LlmAgentToolPresentedTerminal terminal => terminal with { },
        _ => throw new ArgumentException("Unknown tool presentation content.", nameof(value))
    };

    private static LlmAgentPresentedContentBlock Block(LlmAgentPresentedContentBlock value)
    {
        LlmAgentPresentedContentBlock result = value switch
        {
            LlmAgentPresentedText text => text with { },
            LlmAgentPresentedImage image => image with { Data = image.Data },
            LlmAgentPresentedAudio audio => audio with { Data = audio.Data },
            LlmAgentPresentedResourceLink link => link with { },
            LlmAgentPresentedEmbeddedResource embedded => embedded with
            {
                Resource = embedded.Resource switch
                {
                    LlmAgentPresentedResourceText text => text with { },
                    LlmAgentPresentedResourceBytes bytes => bytes with { Data = bytes.Data },
                    _ => throw new ArgumentException("Unknown presented resource body.", nameof(value))
                }
            },
            _ => throw new ArgumentException("Unknown presented content block.", nameof(value))
        };
        return result with { Annotations = value.Annotations is { } annotations ? annotations with { Audience = annotations.Audience } : null };
    }
}
