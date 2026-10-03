using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.LLM;

public enum LlmHostedToolPhase { Searching = 0, Executing = 1, Generating = 2, Interpreting = 3 }
public enum LlmImageGenerationAction { Auto = 0, Generate = 1, Edit = 2 }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmWebSearchTool), "web-search")]
[JsonDerivedType(typeof(LlmCodeExecutionTool), "code-execution")]
[JsonDerivedType(typeof(LlmImageGenerationTool), "image-generation")]
[JsonDerivedType(typeof(LlmDocumentSearchTool), "document-search")]
[JsonDerivedType(typeof(LlmRemoteMcpTool), "remote-mcp")]
public abstract record LlmHostedTool
{
    protected LlmHostedTool() { }
    [JsonIgnore] public abstract LlmHostedToolKind Kind { get; }
}

public sealed record LlmWebSearchLocation(string? Country = null,
    string? City = null, string? Region = null, string? Timezone = null);

public sealed record LlmWebSearchTool(
    IReadOnlyList<string>? AllowedDomains = null, IReadOnlyList<string>? ExcludedDomains = null,
    bool IncludeImageUnderstanding = false, bool IncludeImageSearch = false) : LlmHostedTool
{
    public IReadOnlyList<string>? AllowedDomains { get; init; } = HostedSnapshot.List(AllowedDomains);
    public IReadOnlyList<string>? ExcludedDomains { get; init; } = HostedSnapshot.List(ExcludedDomains);
    public LlmWebSearchLocation? Location { get; init; }
    [JsonIgnore] public override LlmHostedToolKind Kind => LlmHostedToolKind.WebSearch;
}

public sealed record LlmCodeExecutionTool() : LlmHostedTool
{
    [JsonIgnore] public override LlmHostedToolKind Kind => LlmHostedToolKind.CodeExecution;
}

public sealed record LlmImageGenerationTool(LlmImageGenerationAction Action = LlmImageGenerationAction.Auto) : LlmHostedTool
{
    [JsonIgnore] public override LlmHostedToolKind Kind => LlmHostedToolKind.ImageGeneration;
}

public sealed record LlmDocumentSearchTool(IReadOnlyList<string> CollectionIds, int? MaxResults = null) : LlmHostedTool
{
    [JsonRequired]
    public IReadOnlyList<string> CollectionIds { get; init => field = HostedSnapshot.List(HostedSnapshot.Required(value))!; }
        = HostedSnapshot.List(HostedSnapshot.Required(CollectionIds))!;
    [JsonIgnore] public override LlmHostedToolKind Kind => LlmHostedToolKind.DocumentSearch;
}

public sealed record LlmRemoteMcpTool(
    string ServerUrl, string ServerLabel, string? ServerDescription = null,
    IReadOnlyList<string>? AllowedTools = null, IProviderAuthorization? Authorization = null,
    IReadOnlyDictionary<string, string>? Headers = null, bool DeferLoading = false) : LlmHostedTool
{
    [JsonRequired]
    public string ServerUrl { get; init => field = HostedSnapshot.Identity(value); } = HostedSnapshot.Identity(ServerUrl);
    [JsonRequired]
    public string ServerLabel { get; init => field = HostedSnapshot.Identity(value); } = HostedSnapshot.Identity(ServerLabel);
    public IReadOnlyList<string>? AllowedTools { get; init; } = HostedSnapshot.List(AllowedTools);
    public IReadOnlyDictionary<string, string>? Headers { get; init; } = Headers is null ? null
        : new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(Headers, StringComparer.Ordinal));
    [JsonIgnore] public override LlmHostedToolKind Kind => LlmHostedToolKind.RemoteMcp;
    public override string ToString() => "LlmRemoteMcpTool { <redacted> }";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmHostedSearchOutput), "search")]
[JsonDerivedType(typeof(LlmHostedCodeOutput), "code")]
[JsonDerivedType(typeof(LlmHostedDocumentSearchOutput), "document-search")]
[JsonDerivedType(typeof(LlmHostedMcpOutput), "mcp")]
[JsonDerivedType(typeof(LlmHostedImageOutput), "image")]
public abstract record LlmHostedToolOutput
{
    private protected LlmHostedToolOutput() { }
    public IReadOnlyList<InferenceResourceDescriptor>? Resources { get; init; }
}

public sealed record LlmHostedSearchSource(string? Url, string? Title = null)
{
    [JsonRequired]
    public string? Url { get; init; } = Url;
}
public sealed record LlmHostedSearchOutput(IReadOnlyList<LlmHostedSearchSource>? Sources) : LlmHostedToolOutput
{
    public IReadOnlyList<LlmHostedSearchSource>? Sources { get; init; } = HostedSnapshot.List(Sources);
}
public sealed record LlmHostedCodeOutput(string? Code, IReadOnlyList<string>? Logs, IReadOnlyList<string>? ImageUrls) : LlmHostedToolOutput
{
    public IReadOnlyList<string>? Logs { get; init; } = HostedSnapshot.List(Logs);
    public IReadOnlyList<string>? ImageUrls { get; init; } = HostedSnapshot.List(ImageUrls);
}
public sealed record LlmHostedDocumentMatch(string FileId, string? FileName, double? Score, string? Text)
{
    [JsonRequired]
    public string FileId { get; init => field = HostedSnapshot.Required(value); } = HostedSnapshot.Required(FileId);
}
public sealed record LlmHostedDocumentSearchOutput(IReadOnlyList<LlmHostedDocumentMatch>? Results) : LlmHostedToolOutput
{
    public IReadOnlyList<LlmHostedDocumentMatch>? Results { get; init; } = HostedSnapshot.List(Results);
}
public sealed record LlmHostedMcpOutput(JsonElement? Arguments, JsonElement? Output, JsonElement? Error) : LlmHostedToolOutput
{
    public JsonElement? Arguments { get; init; } = Arguments?.Clone();
    public JsonElement? Output { get; init; } = Output?.Clone();
    public JsonElement? Error { get; init; } = Error?.Clone();
}
public sealed record LlmHostedImageOutput(ReadOnlyMemory<byte> Data, string MediaType) : LlmHostedToolOutput
{
    [JsonRequired]
    public ReadOnlyMemory<byte> Data { get; init; } = Data.ToArray();
    [JsonRequired]
    public string MediaType { get; init => field = HostedSnapshot.Required(value); } = HostedSnapshot.Required(MediaType);
}
public sealed record LlmHostedToolCall(string? CallId, LlmHostedToolKind Kind,
    string Name, string? Status,
    JsonElement? Input, LlmHostedToolOutput? Output)
{
    [JsonRequired]
    public string? CallId { get; init; } = CallId;
    [JsonRequired]
    public LlmHostedToolKind Kind { get; init => field = HostedSnapshot.Defined(value); } = HostedSnapshot.Defined(Kind);
    [JsonRequired]
    public string Name { get; init => field = HostedSnapshot.Identity(value); } = HostedSnapshot.Identity(Name);
    [JsonRequired]
    public string? Status { get; init; } = Status;
    public JsonElement? Input { get; init; } = Input?.Clone();
}
public sealed record LlmHostedToolCallStartedEvent(string CallId,
    LlmHostedToolKind Kind, string Name) : LlmStreamEvent
{
    [JsonRequired]
    public string CallId { get; init => field = HostedSnapshot.Identity(value); } = HostedSnapshot.Identity(CallId);
    [JsonRequired]
    public LlmHostedToolKind Kind { get; init => field = HostedSnapshot.Defined(value); } = HostedSnapshot.Defined(Kind);
    [JsonRequired]
    public string Name { get; init => field = HostedSnapshot.Identity(value); } = HostedSnapshot.Identity(Name);
}
public sealed record LlmHostedToolCallProgressEvent(string CallId,
    LlmHostedToolPhase Phase) : LlmStreamEvent
{
    [JsonRequired]
    public string CallId { get; init => field = HostedSnapshot.Identity(value); } = HostedSnapshot.Identity(CallId);
    [JsonRequired]
    public LlmHostedToolPhase Phase { get; init => field = HostedSnapshot.Defined(value); } = HostedSnapshot.Defined(Phase);
}
public sealed record LlmHostedToolCallCompletedEvent(LlmHostedToolCall Call) : LlmStreamEvent
{
    [JsonRequired]
    public LlmHostedToolCall Call { get; init => field = HostedSnapshot.Required(value); } = HostedSnapshot.Required(Call);
}

/// <summary>Offsets, when reported, are UTF-16 positions in emitted text; EndIndex is exclusive.</summary>
public sealed record LlmCitation(string Url, string? Title = null, int? StartIndex = null, int? EndIndex = null)
{
    [JsonRequired]
    public string Url { get; init => field = HostedSnapshot.Required(value); } = HostedSnapshot.Required(Url);
}
public sealed record LlmCitationEvent(LlmCitation Citation) : LlmStreamEvent
{
    [JsonRequired]
    public LlmCitation Citation { get; init => field = HostedSnapshot.Required(value); } = HostedSnapshot.Required(Citation);
}

internal static class HostedSnapshot
{
    internal static IReadOnlyList<T>? List<T>(IReadOnlyList<T>? value) => value is null ? null : Array.AsReadOnly(value.ToArray());
    internal static T Required<T>(T? value) where T : class { ArgumentNullException.ThrowIfNull(value); return value; }
    internal static string Identity(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); return value; }
    internal static T Defined<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
}

internal sealed class ProviderAuthorizationJsonConverter : JsonConverter<IProviderAuthorization>
{
    public override bool HandleNull => true;
    public override IProviderAuthorization? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : throw new NotSupportedException("Live provider authorization cannot be deserialized.");
    public override void Write(Utf8JsonWriter writer, IProviderAuthorization value, JsonSerializerOptions options)
    {
        if (value is not null) { throw new NotSupportedException("Live provider authorization cannot be serialized."); }
        writer.WriteNullValue();
    }
}
