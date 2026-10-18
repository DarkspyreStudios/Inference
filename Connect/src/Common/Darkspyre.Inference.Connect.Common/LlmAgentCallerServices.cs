using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Services the caller lends to an agent session so the agent can ask permission, touch files, run commands and reach resources through the caller. A null service means the caller does not offer it.</summary>
public sealed record LlmAgentCallerServices
{
    /// <summary>Asks the caller to choose an answer when the agent requests permission to run a tool.</summary>
    public ILlmAgentPermissionSelector? Permissions { get; init; }
    /// <summary>Reads text files on the agent's behalf through the caller.</summary>
    public ILlmAgentTextFileReader? TextFileReader { get; init; }
    /// <summary>Writes text files on the agent's behalf through the caller.</summary>
    public ILlmAgentTextFileWriter? TextFileWriter { get; init; }
    /// <summary>Starts caller-owned terminals that run commands for the agent.</summary>
    public ILlmAgentTerminalFactory? Terminals { get; init; }
    /// <summary>Runs the agent's shell calls through the caller.</summary>
    public ILlmAgentShellExecutor? ShellExecutor { get; init; }
    /// <summary>Gives the agent access to the caller's workspace resources.</summary>
    public IInferenceResourceAccess? Resources { get; init; }
    /// <summary>
    /// The Host's full transcript of this conversation. Compaction reads it only to summarize the turns that an omitted
    /// provider compaction state left out of the context.
    /// </summary>
    public ILlmConversationTranscript? Transcript { get; init; }
    /// <inheritdoc />
    public override string ToString() => "LlmAgentCallerServices { <borrowed> }";
}

/// <summary>The effect a permission choice has when the caller selects it.</summary>
public enum LlmAgentPermissionChoiceKind
{
    /// <summary>Allows this one tool call.</summary>
    AllowOnce = 0,
    /// <summary>Allows this tool call and later calls of the same kind.</summary>
    AllowAlways = 1,
    /// <summary>Rejects this one tool call.</summary>
    RejectOnce = 2,
    /// <summary>Rejects this tool call and later calls of the same kind.</summary>
    RejectAlways = 3
}

/// <summary>The caller's answer to a permission request: either a selected choice or a cancellation.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmAgentPermissionCancelled), "cancelled")]
[JsonDerivedType(typeof(LlmAgentPermissionSelected), "selected")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record LlmAgentPermissionResult
{
    private protected LlmAgentPermissionResult() { }
}

/// <summary>One item a tool call shows the caller: a content block, a file diff or a terminal.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmAgentToolPresentedContent), "content")]
[JsonDerivedType(typeof(LlmAgentToolPresentedDiff), "diff")]
[JsonDerivedType(typeof(LlmAgentToolPresentedTerminal), "terminal")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record LlmAgentToolPresentationContent
{
    private protected LlmAgentToolPresentationContent() { }
}

/// <summary>A block of content shown to the caller: text, an image, audio, a resource link or an embedded resource.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmAgentPresentedText), "text")]
[JsonDerivedType(typeof(LlmAgentPresentedImage), "image")]
[JsonDerivedType(typeof(LlmAgentPresentedAudio), "audio")]
[JsonDerivedType(typeof(LlmAgentPresentedResourceLink), "resource-link")]
[JsonDerivedType(typeof(LlmAgentPresentedEmbeddedResource), "embedded-resource")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record LlmAgentPresentedContentBlock
{
    private protected LlmAgentPresentedContentBlock() { }
    /// <summary>Optional hints about the block's audience, priority and modification time.</summary>
    public LlmAgentContentAnnotations? Annotations { get; init; }
}

/// <summary>The body of an embedded resource: either text or binary data.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmAgentPresentedResourceText), "text")]
[JsonDerivedType(typeof(LlmAgentPresentedResourceBytes), "binary")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record LlmAgentPresentedResourceBody
{
    private protected LlmAgentPresentedResourceBody() { }
}

/// <summary>A permission result that reports the caller cancelled the request without choosing.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPermissionCancelled() : LlmAgentPermissionResult
{
    /// <inheritdoc />
    public override string ToString() => "LlmAgentPermissionCancelled { <data> }";
}

/// <summary>Identifies the thread, session and turn a caller callback belongs to, with the working directory and runtime policy in force.</summary>
/// <param name="ThreadId">The conversation thread id.</param>
/// <param name="SessionId">The agent session id.</param>
/// <param name="TurnId">The turn id, or null outside a turn.</param>
/// <param name="WorkingDirectory">The absolute working directory, or null when none applies.</param>
/// <param name="RuntimePolicy">The runtime policy that governs the callback, or null when none applies.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentCallbackScope(string ThreadId, string SessionId, string? TurnId, string? WorkingDirectory, LlmAgentRuntimePolicy? RuntimePolicy)
{
    /// <summary>The conversation thread id; it must not be blank.</summary>
    [JsonRequired] public string ThreadId { get; init => field = CallerData.Id(value); } = CallerData.Id(ThreadId);
    /// <summary>The agent session id; it must not be blank.</summary>
    [JsonRequired] public string SessionId { get; init => field = CallerData.Id(value); } = CallerData.Id(SessionId);
    /// <summary>The turn id, or null outside a turn; when set it must not be blank.</summary>
    public string? TurnId { get; init => field = CallerData.OptionalId(value); } = CallerData.OptionalId(TurnId);
    /// <summary>The absolute working directory, or null when none applies.</summary>
    public string? WorkingDirectory { get; init => field = CallerData.OptionalPath(value); } = CallerData.OptionalPath(WorkingDirectory);
    /// <summary>The runtime policy that governs the callback, or null when none applies.</summary>
    public LlmAgentRuntimePolicy? RuntimePolicy { get; init => field = CallerData.Policy(value); } = CallerData.Policy(RuntimePolicy);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentCallbackScope { <redacted> }";
}

/// <summary>Asks the caller to run one shell call for the agent.</summary>
/// <param name="Scope">The thread, session and turn the call belongs to.</param>
/// <param name="Call">The shell call to run.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentShellRequest(LlmAgentCallbackScope Scope, LlmShellCall Call)
{
    /// <summary>The thread, session and turn the call belongs to.</summary>
    [JsonRequired] public LlmAgentCallbackScope Scope { get; init => field = CallerData.Required(value); } = CallerData.Required(Scope);
    /// <summary>The shell call to run.</summary>
    [JsonRequired] public LlmShellCall Call { get; init => field = CallerData.Required(value); } = CallerData.Required(Call);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentShellRequest { <redacted> }";
}

/// <summary>One answer the caller can pick in a permission request.</summary>
/// <param name="Id">The choice id the caller returns when it selects this choice.</param>
/// <param name="Label">The text shown for the choice.</param>
/// <param name="Kind">The effect of the choice.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPermissionChoice(string Id, string Label, LlmAgentPermissionChoiceKind Kind)
{
    /// <summary>The choice id the caller returns when it selects this choice; it must not be blank.</summary>
    [JsonRequired] public string Id { get; init => field = CallerData.Id(value); } = CallerData.Id(Id);
    /// <summary>The text shown for the choice.</summary>
    [JsonRequired] public string Label { get; init => field = CallerData.Required(value); } = CallerData.Required(Label);
    /// <summary>The effect of the choice; it must be a defined value.</summary>
    [JsonRequired] public LlmAgentPermissionChoiceKind Kind { get; init => field = CallerData.ChoiceKind(value); } = CallerData.ChoiceKind(Kind);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentPermissionChoice { <redacted> }";
}

/// <summary>Asks the caller to pick one of several choices before the agent runs a tool call.</summary>
/// <param name="Scope">The thread, session and turn the request belongs to.</param>
/// <param name="Tool">The tool call that needs permission.</param>
/// <param name="Choices">The choices offered, with distinct ids.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPermissionRequest(LlmAgentCallbackScope Scope, LlmAgentToolPresentation Tool, IReadOnlyList<LlmAgentPermissionChoice> Choices)
{
    /// <summary>The thread, session and turn the request belongs to.</summary>
    [JsonRequired] public LlmAgentCallbackScope Scope { get; init => field = CallerData.Required(value); } = CallerData.Required(Scope);
    /// <summary>The tool call that needs permission.</summary>
    [JsonRequired] public LlmAgentToolPresentation Tool { get; init => field = CallerData.Required(value); } = CallerData.Required(Tool);
    /// <summary>A read-only copy of the choices offered; their ids are distinct.</summary>
    [JsonRequired] public IReadOnlyList<LlmAgentPermissionChoice> Choices { get; init => field = CallerData.Choices(value); } = CallerData.Choices(Choices);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentPermissionRequest { <redacted> }";
}

/// <summary>A permission result that names the choice the caller selected.</summary>
/// <param name="ChoiceId">The id of the selected <see cref="LlmAgentPermissionChoice"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPermissionSelected(string ChoiceId) : LlmAgentPermissionResult
{
    /// <summary>The id of the selected <see cref="LlmAgentPermissionChoice"/>; it must not be blank.</summary>
    [JsonRequired] public string ChoiceId { get; init => field = CallerData.Id(value); } = CallerData.Id(ChoiceId);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentPermissionSelected { <redacted> }";
}

/// <summary>A file location a tool call touches.</summary>
/// <param name="Path">The absolute file path.</param>
/// <param name="Line">The line number in the file, or null for the whole file.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentToolLocation(string Path, uint? Line)
{
    /// <summary>The absolute file path; file URLs are rejected.</summary>
    [JsonRequired] public string Path { get; init => field = CallerData.Path(value); } = CallerData.Path(Path);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentToolLocation { <redacted> }";
}

/// <summary>Tool presentation content that shows a content block.</summary>
/// <param name="Content">The content block to show.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentToolPresentedContent(LlmAgentPresentedContentBlock Content) : LlmAgentToolPresentationContent
{
    /// <summary>The content block to show.</summary>
    [JsonRequired] public LlmAgentPresentedContentBlock Content { get; init => field = CallerData.Required(value); } = CallerData.Required(Content);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentToolPresentedContent { <redacted> }";
}

/// <summary>Tool presentation content that shows a change to one file.</summary>
/// <param name="Path">The absolute path of the changed file.</param>
/// <param name="OldText">The file text before the change, or null for a new file.</param>
/// <param name="NewText">The file text after the change.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentToolPresentedDiff(string Path, string? OldText, string NewText) : LlmAgentToolPresentationContent
{
    /// <summary>The absolute path of the changed file; file URLs are rejected.</summary>
    [JsonRequired] public string Path { get; init => field = CallerData.Path(value); } = CallerData.Path(Path);
    /// <summary>The file text after the change.</summary>
    [JsonRequired] public string NewText { get; init => field = CallerData.Required(value); } = CallerData.Required(NewText);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentToolPresentedDiff { <redacted> }";
}

/// <summary>Tool presentation content that shows a caller-owned terminal.</summary>
/// <param name="TerminalId">The id of the terminal to show.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentToolPresentedTerminal(string TerminalId) : LlmAgentToolPresentationContent
{
    /// <summary>The id of the terminal to show; it must not be blank.</summary>
    [JsonRequired] public string TerminalId { get; init => field = CallerData.Id(value); } = CallerData.Id(TerminalId);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentToolPresentedTerminal { <redacted> }";
}

/// <summary>Optional hints attached to a presented content block.</summary>
/// <param name="Audience">The roles the content is meant for, or null when unspecified.</param>
/// <param name="Priority">The relative importance of the content, or null when unspecified.</param>
/// <param name="LastModified">When the content last changed, or null when unknown.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentContentAnnotations(IReadOnlyList<string>? Audience, double? Priority, DateTimeOffset? LastModified)
{
    /// <summary>A read-only copy of the roles the content is meant for, or null when unspecified.</summary>
    public IReadOnlyList<string>? Audience { get; init => field = value is null ? null : CallerData.List(value); } = Audience is null ? null : CallerData.List(Audience);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentContentAnnotations { <redacted> }";
}

/// <summary>A content block of plain text.</summary>
/// <param name="Text">The text to show.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPresentedText(string Text) : LlmAgentPresentedContentBlock
{
    /// <summary>The text to show.</summary>
    [JsonRequired] public string Text { get; init => field = CallerData.Required(value); } = CallerData.Required(Text);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentPresentedText { <redacted> }";
}

/// <summary>A content block holding an image.</summary>
/// <param name="Data">The image bytes.</param>
/// <param name="MediaType">The image media type, such as image/png.</param>
/// <param name="Uri">The image's source URI, or null when it has none.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPresentedImage(ReadOnlyMemory<byte> Data, string MediaType, string? Uri = null) : LlmAgentPresentedContentBlock
{
    /// <summary>A private copy of the image bytes.</summary>
    [JsonRequired] public ReadOnlyMemory<byte> Data { get; init => field = value.ToArray(); } = Data.ToArray();
    /// <summary>The image media type, such as image/png.</summary>
    [JsonRequired] public string MediaType { get; init => field = CallerData.Required(value); } = CallerData.Required(MediaType);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentPresentedImage { <redacted> }";
}

/// <summary>A content block holding audio.</summary>
/// <param name="Data">The audio bytes.</param>
/// <param name="MediaType">The audio media type, such as audio/wav.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPresentedAudio(ReadOnlyMemory<byte> Data, string MediaType) : LlmAgentPresentedContentBlock
{
    /// <summary>A private copy of the audio bytes.</summary>
    [JsonRequired] public ReadOnlyMemory<byte> Data { get; init => field = value.ToArray(); } = Data.ToArray();
    /// <summary>The audio media type, such as audio/wav.</summary>
    [JsonRequired] public string MediaType { get; init => field = CallerData.Required(value); } = CallerData.Required(MediaType);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentPresentedAudio { <redacted> }";
}

/// <summary>A content block that links to a resource without carrying its contents.</summary>
/// <param name="Uri">The resource URI.</param>
/// <param name="Name">The resource name.</param>
/// <param name="Title">A display title, or null when none is given.</param>
/// <param name="Description">A description of the resource, or null when none is given.</param>
/// <param name="MediaType">The resource media type, or null when unknown.</param>
/// <param name="SizeBytes">The resource size in bytes, or null when unknown.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPresentedResourceLink(string Uri, string Name, string? Title, string? Description, string? MediaType, long? SizeBytes) : LlmAgentPresentedContentBlock
{
    /// <summary>The resource URI.</summary>
    [JsonRequired] public string Uri { get; init => field = CallerData.Required(value); } = CallerData.Required(Uri);
    /// <summary>The resource name.</summary>
    [JsonRequired] public string Name { get; init => field = CallerData.Required(value); } = CallerData.Required(Name);
    /// <summary>The resource size in bytes, or null when unknown; negative sizes are rejected.</summary>
    public long? SizeBytes { get; init => field = CallerData.Size(value); } = CallerData.Size(SizeBytes);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentPresentedResourceLink { <redacted> }";
}

/// <summary>An embedded resource body that carries text.</summary>
/// <param name="Uri">The resource URI.</param>
/// <param name="MediaType">The resource media type, or null when unknown.</param>
/// <param name="Text">The resource text.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPresentedResourceText(string Uri, string? MediaType, string Text) : LlmAgentPresentedResourceBody
{
    /// <summary>The resource URI.</summary>
    [JsonRequired] public string Uri { get; init => field = CallerData.Required(value); } = CallerData.Required(Uri);
    /// <summary>The resource text.</summary>
    [JsonRequired] public string Text { get; init => field = CallerData.Required(value); } = CallerData.Required(Text);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentPresentedResourceText { <redacted> }";
}

/// <summary>An embedded resource body that carries binary data.</summary>
/// <param name="Uri">The resource URI.</param>
/// <param name="MediaType">The resource media type, or null when unknown.</param>
/// <param name="Data">The resource bytes.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPresentedResourceBytes(string Uri, string? MediaType, ReadOnlyMemory<byte> Data) : LlmAgentPresentedResourceBody
{
    /// <summary>The resource URI.</summary>
    [JsonRequired] public string Uri { get; init => field = CallerData.Required(value); } = CallerData.Required(Uri);
    /// <summary>A private copy of the resource bytes.</summary>
    [JsonRequired] public ReadOnlyMemory<byte> Data { get; init => field = value.ToArray(); } = Data.ToArray();
    /// <inheritdoc />
    public override string ToString() => "LlmAgentPresentedResourceBytes { <redacted> }";
}

/// <summary>A content block that carries a resource's contents inline.</summary>
/// <param name="Resource">The embedded resource body.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPresentedEmbeddedResource(LlmAgentPresentedResourceBody Resource) : LlmAgentPresentedContentBlock
{
    /// <summary>The embedded resource body.</summary>
    [JsonRequired] public LlmAgentPresentedResourceBody Resource { get; init => field = CallerData.Required(value); } = CallerData.Required(Resource);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentPresentedEmbeddedResource { <redacted> }";
}

/// <summary>Asks the caller to read a text file, optionally limited to a range of lines.</summary>
/// <param name="Scope">The thread, session and turn the read belongs to.</param>
/// <param name="Path">The absolute path of the file to read.</param>
/// <param name="StartLine">The 1-based line to start reading at, or null for the first line.</param>
/// <param name="MaxLines">The most lines to return, or null for no limit.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentTextFileReadRequest(LlmAgentCallbackScope Scope, string Path, uint? StartLine, uint? MaxLines)
{
    /// <summary>The thread, session and turn the read belongs to.</summary>
    [JsonRequired] public LlmAgentCallbackScope Scope { get; init => field = CallerData.Required(value); } = CallerData.Required(Scope);
    /// <summary>The absolute path of the file to read; file URLs are rejected.</summary>
    [JsonRequired] public string Path { get; init => field = CallerData.Path(value); } = CallerData.Path(Path);
    /// <summary>The 1-based line to start reading at, or null for the first line; zero is rejected.</summary>
    public uint? StartLine { get; init => field = CallerData.StartLine(value); } = CallerData.StartLine(StartLine);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentTextFileReadRequest { <redacted> }";
}

/// <summary>Asks the caller to write text to a file.</summary>
/// <param name="Scope">The thread, session and turn the write belongs to.</param>
/// <param name="Path">The absolute path of the file to write.</param>
/// <param name="Text">The text to write.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentTextFileWriteRequest(LlmAgentCallbackScope Scope, string Path, string Text)
{
    /// <summary>The thread, session and turn the write belongs to.</summary>
    [JsonRequired] public LlmAgentCallbackScope Scope { get; init => field = CallerData.Required(value); } = CallerData.Required(Scope);
    /// <summary>The absolute path of the file to write; file URLs are rejected.</summary>
    [JsonRequired] public string Path { get; init => field = CallerData.Path(value); } = CallerData.Path(Path);
    /// <summary>The text to write.</summary>
    [JsonRequired] public string Text { get; init => field = CallerData.Required(value); } = CallerData.Required(Text);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentTextFileWriteRequest { <redacted> }";
}

/// <summary>The outcome of a text file write.</summary>
/// <param name="RegisteredResources">The resources the caller registered for the written file.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentTextFileWriteResult(IReadOnlyList<InferenceResourceDescriptor> RegisteredResources)
{
    /// <summary>A read-only copy of the resources the caller registered for the written file.</summary>
    [JsonRequired] public IReadOnlyList<InferenceResourceDescriptor> RegisteredResources { get; init => field = CallerData.List(value); } = CallerData.List(RegisteredResources);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentTextFileWriteResult { <redacted> }";
}

/// <summary>One environment variable set for a terminal command.</summary>
/// <param name="Name">The variable name.</param>
/// <param name="Value">The variable value.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentEnvironmentVariable(string Name, string Value)
{
    /// <summary>The variable name.</summary>
    [JsonRequired] public string Name { get; init => field = CallerData.Required(value); } = CallerData.Required(Name);
    /// <summary>The variable value.</summary>
    [JsonRequired] public string Value { get; init => field = CallerData.Required(value); } = CallerData.Required(Value);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentEnvironmentVariable { <redacted> }";
}

/// <summary>Asks the caller to start a terminal that runs one command.</summary>
/// <param name="Scope">The thread, session and turn the terminal belongs to.</param>
/// <param name="Command">The command to run.</param>
/// <param name="Arguments">The command arguments.</param>
/// <param name="Environment">The environment variables to set.</param>
/// <param name="WorkingDirectory">The absolute working directory, or null for the caller's default.</param>
/// <param name="OutputByteLimit">The most output bytes the terminal keeps, or null for the caller's default.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentTerminalCreateRequest(LlmAgentCallbackScope Scope, string Command, IReadOnlyList<string> Arguments, IReadOnlyList<LlmAgentEnvironmentVariable> Environment, string? WorkingDirectory, ulong? OutputByteLimit)
{
    /// <summary>The thread, session and turn the terminal belongs to.</summary>
    [JsonRequired] public LlmAgentCallbackScope Scope { get; init => field = CallerData.Required(value); } = CallerData.Required(Scope);
    /// <summary>The command to run.</summary>
    [JsonRequired] public string Command { get; init => field = CallerData.Required(value); } = CallerData.Required(Command);
    /// <summary>A read-only copy of the command arguments.</summary>
    [JsonRequired] public IReadOnlyList<string> Arguments { get; init => field = CallerData.List(value); } = CallerData.List(Arguments);
    /// <summary>A read-only copy of the environment variables to set.</summary>
    [JsonRequired] public IReadOnlyList<LlmAgentEnvironmentVariable> Environment { get; init => field = CallerData.List(value); } = CallerData.List(Environment);
    /// <summary>The absolute working directory, or null for the caller's default.</summary>
    public string? WorkingDirectory { get; init => field = CallerData.OptionalPath(value); } = CallerData.OptionalPath(WorkingDirectory);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentTerminalCreateRequest { <redacted> }";
}

/// <summary>How a terminal command ended.</summary>
/// <param name="ExitCode">The process exit code, or null when the process ended by signal.</param>
/// <param name="Signal">The signal that ended the process, or null when it exited normally.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentTerminalExit(int? ExitCode, string? Signal)
{
    /// <inheritdoc />
    public override string ToString() => "LlmAgentTerminalExit { <redacted> }";
}

/// <summary>The output a terminal has captured so far, and its exit status once the command ends.</summary>
/// <param name="Output">The captured output text.</param>
/// <param name="Truncated">True when output beyond the byte limit was dropped.</param>
/// <param name="Exit">The exit status, or null while the command runs.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentTerminalOutput(string Output, bool Truncated, LlmAgentTerminalExit? Exit)
{
    /// <summary>The captured output text.</summary>
    [JsonRequired] public string Output { get; init => field = CallerData.Required(value); } = CallerData.Required(Output);
    /// <summary>True when output beyond the byte limit was dropped.</summary>
    [JsonRequired] public bool Truncated { get; init => field = value; } = Truncated;
    /// <inheritdoc />
    public override string ToString() => "LlmAgentTerminalOutput { <redacted> }";
}

/// <summary>Describes a tool call to the caller, for permission requests and display.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentToolPresentation
{
    /// <summary>The tool call id; it must not be blank.</summary>
    public required string CallId { get; init => field = CallerData.Id(value); }
    /// <summary>The tool name, or null when unknown.</summary>
    public string? Name { get; init; }
    /// <summary>A human-readable title for the call, or null when none is given.</summary>
    public string? Title { get; init; }
    /// <summary>The category of tool, such as read or edit, or null when unknown.</summary>
    public string? Kind { get; init; }
    /// <summary>The call's progress status, or null when unknown.</summary>
    public string? Status { get; init; }
    /// <summary>A private copy of the raw tool input, or null when none is given.</summary>
    public JsonElement? RawInput { get; init => field = value?.Clone(); }
    /// <summary>A private copy of the raw tool output, or null when none is available.</summary>
    public JsonElement? RawOutput { get; init => field = value?.Clone(); }
    /// <summary>A read-only copy of the content the call shows; empty by default.</summary>
    public IReadOnlyList<LlmAgentToolPresentationContent> Content { get; init => field = CallerData.List(value); } = [];
    /// <summary>A read-only copy of the file locations the call touches; empty by default.</summary>
    public IReadOnlyList<LlmAgentToolLocation> Locations { get; init => field = CallerData.List(value); } = [];
    /// <inheritdoc />
    public override string ToString() => "LlmAgentToolPresentation { <redacted> }";
}

/// <summary>Caller service that answers the agent's permission requests.</summary>
public interface ILlmAgentPermissionSelector
{
    /// <summary>Presents the request to the caller and returns the selected choice or a cancellation.</summary>
    Task<LlmAgentPermissionResult> RequestAsync(LlmAgentPermissionRequest request, CancellationToken cancellationToken = default);
}
/// <summary>Caller service that reads text files for the agent.</summary>
public interface ILlmAgentTextFileReader
{
    /// <summary>Reads the requested file, or the requested range of its lines, and returns the text.</summary>
    Task<string> ReadAsync(LlmAgentTextFileReadRequest request, CancellationToken cancellationToken = default);
}
/// <summary>Caller service that writes text files for the agent.</summary>
public interface ILlmAgentTextFileWriter
{
    /// <summary>Writes the requested text to the file and returns the resources registered for it.</summary>
    Task<LlmAgentTextFileWriteResult> WriteAsync(LlmAgentTextFileWriteRequest request, CancellationToken cancellationToken = default);
}
/// <summary>Caller service that starts terminals for the agent.</summary>
public interface ILlmAgentTerminalFactory
{
    /// <summary>Starts the requested command in a new terminal and returns a handle to it.</summary>
    Task<ILlmAgentTerminal> CreateAsync(LlmAgentTerminalCreateRequest request, CancellationToken cancellationToken = default);
}
/// <summary>A caller-owned terminal running one command; disposing it releases the terminal.</summary>
public interface ILlmAgentTerminal : IAsyncDisposable
{
    /// <summary>Returns the output captured so far and the exit status if the command has ended.</summary>
    Task<LlmAgentTerminalOutput> GetOutputAsync(CancellationToken cancellationToken = default);
    /// <summary>Waits for the command to end and returns its exit status.</summary>
    Task<LlmAgentTerminalExit> WaitForExitAsync(CancellationToken cancellationToken = default);
    /// <summary>Stops the running command.</summary>
    Task KillAsync(CancellationToken cancellationToken = default);
}
/// <summary>Caller service that runs shell calls for the agent.</summary>
public interface ILlmAgentShellExecutor
{
    /// <summary>Runs the requested shell call and returns its result.</summary>
    Task<LlmShellCallResult> ExecuteAsync(LlmAgentShellRequest request, CancellationToken cancellationToken = default);
}

internal static class CallerData
{
    internal static T Required<T>(T value) where T : class => value ?? throw new ArgumentNullException(nameof(value));
    internal static string Id(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); return value; }
    internal static string? OptionalId(string? value) => value is null ? null : Id(value);
    internal static string Path(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Caller paths cannot be file URLs.", nameof(value));
        bool driveRooted = value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':'
            && value[2] is '\\' or '/';
        bool unc = false;
        if (value.StartsWith("\\\\", StringComparison.Ordinal))
        {
            int serverEnd = value.IndexOf('\\', 2);
            unc = serverEnd > 2 && serverEnd + 1 < value.Length && value[serverEnd + 1] != '\\'
                && Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed) && parsed.IsFile && parsed.IsUnc;
        }
        bool nativeAbsolute = !value.StartsWith("\\\\", StringComparison.Ordinal) && System.IO.Path.IsPathFullyQualified(value);
        if (!(value.StartsWith('/') || driveRooted || unc || nativeAbsolute))
            throw new ArgumentException("Caller paths must be absolute.", nameof(value));
        return value;
    }
    internal static string? OptionalPath(string? value) => value is null ? null : Path(value);
    internal static long? Size(long? value)
    {
        if (value is < 0) throw new ArgumentOutOfRangeException(nameof(value));
        return value;
    }
    internal static uint? StartLine(uint? value)
    {
        if (value == 0) throw new ArgumentOutOfRangeException(nameof(value));
        return value;
    }
    internal static LlmAgentPermissionChoiceKind ChoiceKind(LlmAgentPermissionChoiceKind value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        return value;
    }
    internal static ReadOnlyCollection<T> List<T>(IReadOnlyList<T> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        T[] copy = value.ToArray();
        if (copy.Any(item => item is null)) throw new ArgumentException("Caller data lists cannot contain null entries.", nameof(value));
        return Array.AsReadOnly(copy);
    }
    internal static ReadOnlyCollection<LlmAgentPermissionChoice> Choices(IReadOnlyList<LlmAgentPermissionChoice> value)
    {
        var copy = List(value);
        if (copy.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != copy.Count)
            throw new ArgumentException("Permission choice IDs must be distinct.", nameof(value));
        return copy;
    }
    internal static LlmAgentRuntimePolicy? Policy(LlmAgentRuntimePolicy? value) => value switch
    {
        null => null,
        LlmAgentHostRuntimePolicy host => host with { WritableRoots = List(host.WritableRoots) },
        LlmAgentProviderRuntimePolicy provider => provider,
        _ => throw new ArgumentException("Unsupported runtime policy.", nameof(value))
    };
}
