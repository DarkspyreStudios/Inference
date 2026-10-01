using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Common.LLM;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmToolDefinition), "function")]
[JsonDerivedType(typeof(LlmShellTool), "shell")]
public abstract record LlmClientToolDefinition
{
    private protected LlmClientToolDefinition() { }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmShellSkill(string Name, string Description, string Path)
{
    private readonly string _name = Name ?? throw new ArgumentNullException(nameof(Name));
    private readonly string _description = Description ?? throw new ArgumentNullException(nameof(Description));
    private readonly string _path = Path ?? throw new ArgumentNullException(nameof(Path));
    [JsonRequired] public string Name { get => _name; init => _name = value ?? throw new ArgumentNullException(nameof(value)); }
    [JsonRequired] public string Description { get => _description; init => _description = value ?? throw new ArgumentNullException(nameof(value)); }
    [JsonRequired] public string Path { get => _path; init => _path = value ?? throw new ArgumentNullException(nameof(value)); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmShellTool(IReadOnlyList<LlmShellSkill> Skills) : LlmClientToolDefinition
{
    private readonly ReadOnlyCollection<LlmShellSkill> _skills = ClientToolData.Copy(Skills);
    [JsonRequired] public IReadOnlyList<LlmShellSkill> Skills { get => _skills; init => _skills = ClientToolData.Copy(value); }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmToolCall), "function")]
[JsonDerivedType(typeof(LlmShellCall), "shell")]
public abstract record LlmClientToolCall
{
    private string _id;
    private protected LlmClientToolCall(string id) { _id = ClientToolData.Text(id); }
    [JsonRequired, JsonPropertyOrder(-1)] public string Id { get => _id; init => _id = ClientToolData.Text(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmShellCall(string Id, IReadOnlyList<string> Commands, long? TimeoutMs, int? MaxOutputLength)
    : LlmClientToolCall(Id)
{
    private readonly ReadOnlyCollection<string> _commands = ClientToolData.Copy(Commands);
    [JsonRequired] public IReadOnlyList<string> Commands { get => _commands; init => _commands = ClientToolData.Copy(value); }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmToolResult), "function")]
[JsonDerivedType(typeof(LlmShellCallResult), "shell")]
public abstract record LlmClientToolResult
{
    private string _toolCallId;
    private protected LlmClientToolResult(string toolCallId) { _toolCallId = ClientToolData.Text(toolCallId); }
    [JsonRequired, JsonPropertyOrder(-1)] public string ToolCallId { get => _toolCallId; init => _toolCallId = ClientToolData.Text(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmShellCommandResult(string? Stdout, string? Stderr, LlmShellOutcome Outcome)
{
    private readonly LlmShellOutcome _outcome = Outcome ?? throw new ArgumentNullException(nameof(Outcome));
    [JsonRequired] public LlmShellOutcome Outcome { get => _outcome; init => _outcome = value ?? throw new ArgumentNullException(nameof(value)); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmShellCallResult(string ToolCallId, IReadOnlyList<LlmShellCommandResult> Output)
    : LlmClientToolResult(ToolCallId)
{
    private readonly ReadOnlyCollection<LlmShellCommandResult> _output = ClientToolData.Copy(Output);
    [JsonRequired] public IReadOnlyList<LlmShellCommandResult> Output { get => _output; init => _output = ClientToolData.Copy(value); }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmShellExitedOutcome), "exit")]
[JsonDerivedType(typeof(LlmShellTimedOutOutcome), "timeout")]
public abstract record LlmShellOutcome
{
    private protected LlmShellOutcome() { }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmShellExitedOutcome([property: JsonRequired] int ExitCode) : LlmShellOutcome;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmShellTimedOutOutcome() : LlmShellOutcome;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmShellCallEvent(LlmShellCall Call) : LlmStreamEvent
{
    private readonly LlmShellCall _call = Call ?? throw new ArgumentNullException(nameof(Call));
    [JsonRequired] public LlmShellCall Call { get => _call; init => _call = value ?? throw new ArgumentNullException(nameof(value)); }
}

internal static class ClientToolData
{
    internal static string Text(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }

    internal static ReadOnlyCollection<T> Copy<T>(IReadOnlyList<T> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        T[] copy = value.ToArray();
        if (copy.Any(item => item is null)) throw new ArgumentException("Tool collections cannot contain null entries.", nameof(value));
        return Array.AsReadOnly(copy);
    }
}
