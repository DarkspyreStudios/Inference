using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>The kind of conversation content an agent observation carries.</summary>
public enum LlmAgentContentObservationKind
{
    /// <summary>Content of a user message.</summary>
    UserMessage = 0,
    /// <summary>Content of an assistant message.</summary>
    AssistantMessage = 1,
    /// <summary>Reasoning content.</summary>
    Reasoning = 2
}

/// <summary>How observed content combines with earlier content for the same item.</summary>
public enum LlmAgentContentObservationMode
{
    /// <summary>The content is appended to the item.</summary>
    Append = 0,
    /// <summary>The content replaces the item. Requires an item identifier.</summary>
    Replace = 1
}

/// <summary>Presents a tool call made by an agent so a host can show it.</summary>
/// <param name="ThreadId">The agent thread the tool call belongs to.</param>
/// <param name="TurnId">The turn the tool call belongs to, when known.</param>
/// <param name="Tool">The tool call presentation.</param>
/// <param name="IsReplay">Whether the event replays earlier session history.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentToolPresentationEvent(
    string ThreadId,
    string? TurnId,
    LlmAgentToolPresentation Tool,
    bool IsReplay) : LlmAgentEvent(CallerData.Id(ThreadId), CallerData.OptionalId(TurnId))
{
    /// <inheritdoc />
    [JsonRequired] public override string ThreadId { get => base.ThreadId; init => base.ThreadId = CallerData.Id(value); }
    /// <inheritdoc />
    [JsonRequired] public override string? TurnId { get => base.TurnId; init => base.TurnId = CallerData.OptionalId(value); }
    /// <summary>A validated copy of the tool call presentation.</summary>
    [JsonRequired] public LlmAgentToolPresentation Tool { get; init => field = AgentObservationData.Tool(value); } = AgentObservationData.Tool(Tool);
    /// <summary>Whether the event replays earlier session history.</summary>
    [JsonRequired] public bool IsReplay { get; init; } = IsReplay;
    /// <summary>Returns a placeholder that hides the event content.</summary>
    public override string ToString() => "LlmAgentToolPresentationEvent { <redacted> }";
}

/// <summary>Reports conversation content an agent produced or received, so a host can show it.</summary>
/// <param name="ThreadId">The agent thread the content belongs to.</param>
/// <param name="TurnId">The turn the content belongs to, when known.</param>
/// <param name="ItemId">The item the content belongs to. Required when <paramref name="Mode"/> is <see cref="LlmAgentContentObservationMode.Replace"/>.</param>
/// <param name="Kind">The kind of content.</param>
/// <param name="Mode">How the content combines with earlier content for the same item.</param>
/// <param name="IsReplay">Whether the event replays earlier session history.</param>
/// <param name="Content">The content blocks.</param>
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
    /// <inheritdoc />
    [JsonRequired] public override string ThreadId { get => base.ThreadId; init => base.ThreadId = CallerData.Id(value); }
    /// <inheritdoc />
    [JsonRequired] public override string? TurnId { get => base.TurnId; init => base.TurnId = CallerData.OptionalId(value); }
    /// <summary>The item the content belongs to, when known. Throws when it is null and the mode replaces content.</summary>
    [JsonRequired] public string? ItemId { get; init => field = AgentObservationData.Item(value, this.Mode); } = CallerData.OptionalId(ItemId);
    /// <summary>The kind of content. Throws when the value is undefined.</summary>
    [JsonRequired] public LlmAgentContentObservationKind Kind { get; init => field = AgentObservationData.Kind(value); } = AgentObservationData.Kind(Kind);
    /// <summary>How the content combines with earlier content for the same item. Throws when it replaces content without an item identifier.</summary>
    [JsonRequired] public LlmAgentContentObservationMode Mode { get; init => field = AgentObservationData.Mode(value, this.ItemId); } = AgentObservationData.Mode(Mode, ItemId);
    /// <summary>Whether the event replays earlier session history.</summary>
    [JsonRequired] public bool IsReplay { get; init; } = IsReplay;
    /// <summary>A validated copy of the content blocks.</summary>
    [JsonRequired] public IReadOnlyList<LlmAgentPresentedContentBlock> Content { get; init => field = AgentObservationData.Content(value); } = AgentObservationData.Content(Content);
    /// <summary>Returns a placeholder that hides the event content.</summary>
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
