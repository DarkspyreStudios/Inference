using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM;

public sealed record LlmAgentCallerServices
{
    public ILlmAgentPermissionSelector? Permissions { get; init; }
    public ILlmAgentTextFileReader? TextFileReader { get; init; }
    public ILlmAgentTextFileWriter? TextFileWriter { get; init; }
    public ILlmAgentTerminalFactory? Terminals { get; init; }
    public ILlmAgentShellExecutor? ShellExecutor { get; init; }
    public IInferenceResourceAccess? Resources { get; init; }
    public override string ToString() => "LlmAgentCallerServices { <borrowed> }";
}

public enum LlmAgentCallerServiceKind
{
    Permission = 0, ReadTextFile = 1, WriteTextFile = 2, Terminal = 3, ShellExecution = 4
}
public enum LlmAgentPermissionChoiceKind
{
    AllowOnce = 0, AllowAlways = 1, RejectOnce = 2, RejectAlways = 3
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmAgentPermissionCancelled), "cancelled")]
[JsonDerivedType(typeof(LlmAgentPermissionSelected), "selected")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record LlmAgentPermissionResult
{
    private protected LlmAgentPermissionResult() { }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmAgentToolPresentedContent), "content")]
[JsonDerivedType(typeof(LlmAgentToolPresentedDiff), "diff")]
[JsonDerivedType(typeof(LlmAgentToolPresentedTerminal), "terminal")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record LlmAgentToolPresentationContent
{
    private protected LlmAgentToolPresentationContent() { }
}

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
    public LlmAgentContentAnnotations? Annotations { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmAgentPresentedResourceText), "text")]
[JsonDerivedType(typeof(LlmAgentPresentedResourceBytes), "binary")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record LlmAgentPresentedResourceBody
{
    private protected LlmAgentPresentedResourceBody() { }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPermissionCancelled() : LlmAgentPermissionResult
{
    public override string ToString() => "LlmAgentPermissionCancelled { <data> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentCallbackScope(string ThreadId, string SessionId, string? TurnId, string? WorkingDirectory, LlmAgentRuntimePolicy? RuntimePolicy)
{
    [JsonRequired] public string ThreadId { get; init => field = CallerData.Id(value); } = CallerData.Id(ThreadId);
    [JsonRequired] public string SessionId { get; init => field = CallerData.Id(value); } = CallerData.Id(SessionId);
    public string? TurnId { get; init => field = CallerData.OptionalId(value); } = CallerData.OptionalId(TurnId);
    public string? WorkingDirectory { get; init => field = CallerData.OptionalPath(value); } = CallerData.OptionalPath(WorkingDirectory);
    public LlmAgentRuntimePolicy? RuntimePolicy { get; init => field = CallerData.Policy(value); } = CallerData.Policy(RuntimePolicy);
    public override string ToString() => "LlmAgentCallbackScope { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentShellRequest(LlmAgentCallbackScope Scope, LlmShellCall Call)
{
    [JsonRequired] public LlmAgentCallbackScope Scope { get; init => field = CallerData.Required(value); } = CallerData.Required(Scope);
    [JsonRequired] public LlmShellCall Call { get; init => field = CallerData.Required(value); } = CallerData.Required(Call);
    public override string ToString() => "LlmAgentShellRequest { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPermissionChoice(string Id, string Label, LlmAgentPermissionChoiceKind Kind)
{
    [JsonRequired] public string Id { get; init => field = CallerData.Id(value); } = CallerData.Id(Id);
    [JsonRequired] public string Label { get; init => field = CallerData.Required(value); } = CallerData.Required(Label);
    [JsonRequired] public LlmAgentPermissionChoiceKind Kind { get; init => field = CallerData.ChoiceKind(value); } = CallerData.ChoiceKind(Kind);
    public override string ToString() => "LlmAgentPermissionChoice { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPermissionRequest(LlmAgentCallbackScope Scope, LlmAgentToolPresentation Tool, IReadOnlyList<LlmAgentPermissionChoice> Choices)
{
    [JsonRequired] public LlmAgentCallbackScope Scope { get; init => field = CallerData.Required(value); } = CallerData.Required(Scope);
    [JsonRequired] public LlmAgentToolPresentation Tool { get; init => field = CallerData.Required(value); } = CallerData.Required(Tool);
    [JsonRequired] public IReadOnlyList<LlmAgentPermissionChoice> Choices { get; init => field = CallerData.Choices(value); } = CallerData.Choices(Choices);
    public override string ToString() => "LlmAgentPermissionRequest { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPermissionSelected(string ChoiceId) : LlmAgentPermissionResult
{
    [JsonRequired] public string ChoiceId { get; init => field = CallerData.Id(value); } = CallerData.Id(ChoiceId);
    public override string ToString() => "LlmAgentPermissionSelected { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentToolLocation(string Path, uint? Line)
{
    [JsonRequired] public string Path { get; init => field = CallerData.Path(value); } = CallerData.Path(Path);
    public override string ToString() => "LlmAgentToolLocation { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentToolPresentedContent(LlmAgentPresentedContentBlock Content) : LlmAgentToolPresentationContent
{
    [JsonRequired] public LlmAgentPresentedContentBlock Content { get; init => field = CallerData.Required(value); } = CallerData.Required(Content);
    public override string ToString() => "LlmAgentToolPresentedContent { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentToolPresentedDiff(string Path, string? OldText, string NewText) : LlmAgentToolPresentationContent
{
    [JsonRequired] public string Path { get; init => field = CallerData.Path(value); } = CallerData.Path(Path);
    [JsonRequired] public string NewText { get; init => field = CallerData.Required(value); } = CallerData.Required(NewText);
    public override string ToString() => "LlmAgentToolPresentedDiff { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentToolPresentedTerminal(string TerminalId) : LlmAgentToolPresentationContent
{
    [JsonRequired] public string TerminalId { get; init => field = CallerData.Id(value); } = CallerData.Id(TerminalId);
    public override string ToString() => "LlmAgentToolPresentedTerminal { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentContentAnnotations(IReadOnlyList<string>? Audience, double? Priority, DateTimeOffset? LastModified)
{
    public IReadOnlyList<string>? Audience { get; init => field = value is null ? null : CallerData.List(value); } = Audience is null ? null : CallerData.List(Audience);
    public override string ToString() => "LlmAgentContentAnnotations { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPresentedText(string Text) : LlmAgentPresentedContentBlock
{
    [JsonRequired] public string Text { get; init => field = CallerData.Required(value); } = CallerData.Required(Text);
    public override string ToString() => "LlmAgentPresentedText { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPresentedImage(ReadOnlyMemory<byte> Data, string MediaType, string? Uri = null) : LlmAgentPresentedContentBlock
{
    [JsonRequired] public ReadOnlyMemory<byte> Data { get; init => field = value.ToArray(); } = Data.ToArray();
    [JsonRequired] public string MediaType { get; init => field = CallerData.Required(value); } = CallerData.Required(MediaType);
    public override string ToString() => "LlmAgentPresentedImage { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPresentedAudio(ReadOnlyMemory<byte> Data, string MediaType) : LlmAgentPresentedContentBlock
{
    [JsonRequired] public ReadOnlyMemory<byte> Data { get; init => field = value.ToArray(); } = Data.ToArray();
    [JsonRequired] public string MediaType { get; init => field = CallerData.Required(value); } = CallerData.Required(MediaType);
    public override string ToString() => "LlmAgentPresentedAudio { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPresentedResourceLink(string Uri, string Name, string? Title, string? Description, string? MediaType, long? SizeBytes) : LlmAgentPresentedContentBlock
{
    [JsonRequired] public string Uri { get; init => field = CallerData.Required(value); } = CallerData.Required(Uri);
    [JsonRequired] public string Name { get; init => field = CallerData.Required(value); } = CallerData.Required(Name);
    public long? SizeBytes { get; init => field = CallerData.Size(value); } = CallerData.Size(SizeBytes);
    public override string ToString() => "LlmAgentPresentedResourceLink { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPresentedResourceText(string Uri, string? MediaType, string Text) : LlmAgentPresentedResourceBody
{
    [JsonRequired] public string Uri { get; init => field = CallerData.Required(value); } = CallerData.Required(Uri);
    [JsonRequired] public string Text { get; init => field = CallerData.Required(value); } = CallerData.Required(Text);
    public override string ToString() => "LlmAgentPresentedResourceText { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPresentedResourceBytes(string Uri, string? MediaType, ReadOnlyMemory<byte> Data) : LlmAgentPresentedResourceBody
{
    [JsonRequired] public string Uri { get; init => field = CallerData.Required(value); } = CallerData.Required(Uri);
    [JsonRequired] public ReadOnlyMemory<byte> Data { get; init => field = value.ToArray(); } = Data.ToArray();
    public override string ToString() => "LlmAgentPresentedResourceBytes { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentPresentedEmbeddedResource(LlmAgentPresentedResourceBody Resource) : LlmAgentPresentedContentBlock
{
    [JsonRequired] public LlmAgentPresentedResourceBody Resource { get; init => field = CallerData.Required(value); } = CallerData.Required(Resource);
    public override string ToString() => "LlmAgentPresentedEmbeddedResource { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentTextFileReadRequest(LlmAgentCallbackScope Scope, string Path, uint? StartLine, uint? MaxLines)
{
    [JsonRequired] public LlmAgentCallbackScope Scope { get; init => field = CallerData.Required(value); } = CallerData.Required(Scope);
    [JsonRequired] public string Path { get; init => field = CallerData.Path(value); } = CallerData.Path(Path);
    public uint? StartLine { get; init => field = CallerData.StartLine(value); } = CallerData.StartLine(StartLine);
    public override string ToString() => "LlmAgentTextFileReadRequest { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentTextFileWriteRequest(LlmAgentCallbackScope Scope, string Path, string Text)
{
    [JsonRequired] public LlmAgentCallbackScope Scope { get; init => field = CallerData.Required(value); } = CallerData.Required(Scope);
    [JsonRequired] public string Path { get; init => field = CallerData.Path(value); } = CallerData.Path(Path);
    [JsonRequired] public string Text { get; init => field = CallerData.Required(value); } = CallerData.Required(Text);
    public override string ToString() => "LlmAgentTextFileWriteRequest { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentTextFileWriteResult(IReadOnlyList<InferenceResourceDescriptor> RegisteredResources)
{
    [JsonRequired] public IReadOnlyList<InferenceResourceDescriptor> RegisteredResources { get; init => field = CallerData.List(value); } = CallerData.List(RegisteredResources);
    public override string ToString() => "LlmAgentTextFileWriteResult { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentEnvironmentVariable(string Name, string Value)
{
    [JsonRequired] public string Name { get; init => field = CallerData.Required(value); } = CallerData.Required(Name);
    [JsonRequired] public string Value { get; init => field = CallerData.Required(value); } = CallerData.Required(Value);
    public override string ToString() => "LlmAgentEnvironmentVariable { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentTerminalCreateRequest(LlmAgentCallbackScope Scope, string Command, IReadOnlyList<string> Arguments, IReadOnlyList<LlmAgentEnvironmentVariable> Environment, string? WorkingDirectory, ulong? OutputByteLimit)
{
    [JsonRequired] public LlmAgentCallbackScope Scope { get; init => field = CallerData.Required(value); } = CallerData.Required(Scope);
    [JsonRequired] public string Command { get; init => field = CallerData.Required(value); } = CallerData.Required(Command);
    [JsonRequired] public IReadOnlyList<string> Arguments { get; init => field = CallerData.List(value); } = CallerData.List(Arguments);
    [JsonRequired] public IReadOnlyList<LlmAgentEnvironmentVariable> Environment { get; init => field = CallerData.List(value); } = CallerData.List(Environment);
    public string? WorkingDirectory { get; init => field = CallerData.OptionalPath(value); } = CallerData.OptionalPath(WorkingDirectory);
    public override string ToString() => "LlmAgentTerminalCreateRequest { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentTerminalExit(int? ExitCode, string? Signal)
{
    public override string ToString() => "LlmAgentTerminalExit { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentTerminalOutput(string Output, bool Truncated, LlmAgentTerminalExit? Exit)
{
    [JsonRequired] public string Output { get; init => field = CallerData.Required(value); } = CallerData.Required(Output);
    [JsonRequired] public bool Truncated { get; init => field = value; } = Truncated;
    public override string ToString() => "LlmAgentTerminalOutput { <redacted> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentToolPresentation
{
    public required string CallId { get; init => field = CallerData.Id(value); }
    public string? Name { get; init; }
    public string? Title { get; init; }
    public string? Kind { get; init; }
    public string? Status { get; init; }
    public JsonElement? RawInput { get; init => field = value?.Clone(); }
    public JsonElement? RawOutput { get; init => field = value?.Clone(); }
    public IReadOnlyList<LlmAgentToolPresentationContent> Content { get; init => field = CallerData.List(value); } = [];
    public IReadOnlyList<LlmAgentToolLocation> Locations { get; init => field = CallerData.List(value); } = [];
    public override string ToString() => "LlmAgentToolPresentation { <redacted> }";
}

public interface ILlmAgentPermissionSelector
{
    Task<LlmAgentPermissionResult> RequestAsync(LlmAgentPermissionRequest request, CancellationToken cancellationToken = default);
}
public interface ILlmAgentTextFileReader
{
    Task<string> ReadAsync(LlmAgentTextFileReadRequest request, CancellationToken cancellationToken = default);
}
public interface ILlmAgentTextFileWriter
{
    Task<LlmAgentTextFileWriteResult> WriteAsync(LlmAgentTextFileWriteRequest request, CancellationToken cancellationToken = default);
}
public interface ILlmAgentTerminalFactory
{
    Task<ILlmAgentTerminal> CreateAsync(LlmAgentTerminalCreateRequest request, CancellationToken cancellationToken = default);
}
public interface ILlmAgentTerminal : IAsyncDisposable
{
    Task<LlmAgentTerminalOutput> GetOutputAsync(CancellationToken cancellationToken = default);
    Task<LlmAgentTerminalExit> WaitForExitAsync(CancellationToken cancellationToken = default);
    Task KillAsync(CancellationToken cancellationToken = default);
}
public interface ILlmAgentShellExecutor
{
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
