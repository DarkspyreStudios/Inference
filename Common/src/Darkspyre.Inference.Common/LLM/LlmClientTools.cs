using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Common.LLM;

/// <summary>Base type for a tool the client declares to the model, serialized with a <c>type</c> discriminator of <c>function</c> or <c>shell</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmToolDefinition), "function")]
[JsonDerivedType(typeof(LlmShellTool), "shell")]
public abstract record LlmClientToolDefinition
{
    private protected LlmClientToolDefinition() { }
}

/// <summary>A skill the shell tool exposes to the model, described by name, description and location.</summary>
/// <param name="Name">The skill name.</param>
/// <param name="Description">What the skill does, as shown to the model.</param>
/// <param name="Path">The path of the skill in the client environment.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmShellSkill(string Name, string Description, string Path)
{
    private readonly string _name = Name ?? throw new ArgumentNullException(nameof(Name));
    private readonly string _description = Description ?? throw new ArgumentNullException(nameof(Description));
    private readonly string _path = Path ?? throw new ArgumentNullException(nameof(Path));
    /// <summary>The skill name.</summary>
    [JsonRequired] public string Name { get => _name; init => _name = value ?? throw new ArgumentNullException(nameof(value)); }
    /// <summary>What the skill does, as shown to the model.</summary>
    [JsonRequired] public string Description { get => _description; init => _description = value ?? throw new ArgumentNullException(nameof(value)); }
    /// <summary>The path of the skill in the client environment.</summary>
    [JsonRequired] public string Path { get => _path; init => _path = value ?? throw new ArgumentNullException(nameof(value)); }
}

/// <summary>A shell tool the client runs on the model's behalf, with the skills it makes available.</summary>
/// <param name="Skills">The skills available to the shell.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmShellTool(IReadOnlyList<LlmShellSkill> Skills) : LlmClientToolDefinition
{
    private readonly ReadOnlyCollection<LlmShellSkill> _skills = ClientToolData.Copy(Skills);
    /// <summary>The skills available to the shell. The list is copied and must not contain null entries.</summary>
    [JsonRequired] public IReadOnlyList<LlmShellSkill> Skills { get => _skills; init => _skills = ClientToolData.Copy(value); }
}

/// <summary>Base type for a client tool call the model makes, serialized with a <c>type</c> discriminator of <c>function</c> or <c>shell</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmToolCall), "function")]
[JsonDerivedType(typeof(LlmShellCall), "shell")]
public abstract record LlmClientToolCall
{
    private string _id;
    private protected LlmClientToolCall(string id) { _id = ClientToolData.Text(id); }
    /// <summary>The call identifier the matching result refers to. It must not be blank.</summary>
    [JsonRequired, JsonPropertyOrder(-1)] public string Id { get => _id; init => _id = ClientToolData.Text(value); }
}

/// <summary>A request from the model to run shell commands in the client environment.</summary>
/// <param name="Id">The call identifier.</param>
/// <param name="Commands">The commands to run, in order.</param>
/// <param name="TimeoutMs">The time limit for the commands in milliseconds, or null for no limit from the model.</param>
/// <param name="MaxOutputLength">The maximum output length the model wants back, or null for no limit from the model.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmShellCall(string Id, IReadOnlyList<string> Commands, long? TimeoutMs, int? MaxOutputLength)
    : LlmClientToolCall(Id)
{
    private readonly ReadOnlyCollection<string> _commands = ClientToolData.Copy(Commands);
    /// <summary>The commands to run, in order. The list is copied and must not contain null entries.</summary>
    [JsonRequired] public IReadOnlyList<string> Commands { get => _commands; init => _commands = ClientToolData.Copy(value); }
}

/// <summary>Base type for the result of a client tool call, serialized with a <c>type</c> discriminator of <c>function</c> or <c>shell</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmToolResult), "function")]
[JsonDerivedType(typeof(LlmShellCallResult), "shell")]
public abstract record LlmClientToolResult
{
    private string _toolCallId;
    private protected LlmClientToolResult(string toolCallId) { _toolCallId = ClientToolData.Text(toolCallId); }
    /// <summary>The identifier of the call this result answers. It must not be blank.</summary>
    [JsonRequired, JsonPropertyOrder(-1)] public string ToolCallId { get => _toolCallId; init => _toolCallId = ClientToolData.Text(value); }
}

/// <summary>The output and outcome of one shell command.</summary>
/// <param name="Stdout">The standard output, or null when none was captured.</param>
/// <param name="Stderr">The standard error, or null when none was captured.</param>
/// <param name="Outcome">How the command ended.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmShellCommandResult(string? Stdout, string? Stderr, LlmShellOutcome Outcome)
{
    private readonly LlmShellOutcome _outcome = Outcome ?? throw new ArgumentNullException(nameof(Outcome));
    /// <summary>How the command ended.</summary>
    [JsonRequired] public LlmShellOutcome Outcome { get => _outcome; init => _outcome = value ?? throw new ArgumentNullException(nameof(value)); }
}

/// <summary>The result of a shell call, with one entry per command.</summary>
/// <param name="ToolCallId">The identifier of the shell call this result answers.</param>
/// <param name="Output">The result of each command, in command order.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmShellCallResult(string ToolCallId, IReadOnlyList<LlmShellCommandResult> Output)
    : LlmClientToolResult(ToolCallId)
{
    private readonly ReadOnlyCollection<LlmShellCommandResult> _output = ClientToolData.Copy(Output);
    /// <summary>The result of each command, in command order. The list is copied and must not contain null entries.</summary>
    [JsonRequired] public IReadOnlyList<LlmShellCommandResult> Output { get => _output; init => _output = ClientToolData.Copy(value); }
}

/// <summary>Base type for how a shell command ended, serialized with a <c>type</c> discriminator of <c>exit</c> or <c>timeout</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmShellExitedOutcome), "exit")]
[JsonDerivedType(typeof(LlmShellTimedOutOutcome), "timeout")]
public abstract record LlmShellOutcome
{
    private protected LlmShellOutcome() { }
}

/// <summary>A shell command that exited.</summary>
/// <param name="ExitCode">The process exit code.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmShellExitedOutcome([property: JsonRequired] int ExitCode) : LlmShellOutcome;

/// <summary>A shell command that hit its time limit.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmShellTimedOutOutcome() : LlmShellOutcome;

/// <summary>A stream event carrying a shell call the model made.</summary>
/// <param name="Call">The shell call.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmShellCallEvent(LlmShellCall Call) : LlmStreamEvent
{
    private readonly LlmShellCall _call = Call ?? throw new ArgumentNullException(nameof(Call));
    /// <summary>The shell call.</summary>
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
