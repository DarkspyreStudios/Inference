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

public sealed record LlmWebSearchTool(
    IReadOnlyList<string>? AllowedDomains = null, IReadOnlyList<string>? ExcludedDomains = null,
    bool IncludeImageUnderstanding = false, bool IncludeImageSearch = false) : LlmHostedTool
{
    public IReadOnlyList<string>? AllowedDomains { get; init; } = HostedSnapshot.List(AllowedDomains);
    public IReadOnlyList<string>? ExcludedDomains { get; init; } = HostedSnapshot.List(ExcludedDomains);
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
    public IReadOnlyList<string> CollectionIds { get; init; } = HostedSnapshot.List(CollectionIds)!;
    [JsonIgnore] public override LlmHostedToolKind Kind => LlmHostedToolKind.DocumentSearch;
}

public sealed record LlmRemoteMcpTool(
    string ServerUrl, string ServerLabel, string? ServerDescription = null,
    IReadOnlyList<string>? AllowedTools = null, IProviderAuthorization? Authorization = null,
    IReadOnlyDictionary<string, string>? Headers = null, bool DeferLoading = false) : LlmHostedTool
{
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

public sealed record LlmHostedSearchSource(string Url, string? Title = null);
public sealed record LlmHostedSearchOutput(IReadOnlyList<LlmHostedSearchSource>? Sources) : LlmHostedToolOutput
{
    public IReadOnlyList<LlmHostedSearchSource>? Sources { get; init; } = HostedSnapshot.List(Sources);
}
public sealed record LlmHostedCodeOutput(string? Code, IReadOnlyList<string>? Logs, IReadOnlyList<string>? ImageUrls) : LlmHostedToolOutput
{
    public IReadOnlyList<string>? Logs { get; init; } = HostedSnapshot.List(Logs);
    public IReadOnlyList<string>? ImageUrls { get; init; } = HostedSnapshot.List(ImageUrls);
}
public sealed record LlmHostedDocumentMatch(string FileId, string? FileName, double? Score, string? Text);
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
    public ReadOnlyMemory<byte> Data { get; init; } = Data.ToArray();
}
public sealed record LlmHostedToolCall(string CallId, LlmHostedToolKind Kind, string Name, string Status,
    JsonElement? Input, LlmHostedToolOutput? Output)
{
    public JsonElement? Input { get; init; } = Input?.Clone();
}
public sealed record LlmHostedToolCallStartedEvent(string CallId, LlmHostedToolKind Kind, string Name) : LlmStreamEvent;
public sealed record LlmHostedToolCallProgressEvent(string CallId, LlmHostedToolPhase Phase) : LlmStreamEvent;
public sealed record LlmHostedToolCallCompletedEvent(LlmHostedToolCall Call) : LlmStreamEvent;

/// <summary>Offsets, when reported, are UTF-16 positions in emitted text; EndIndex is exclusive.</summary>
public sealed record LlmCitation(string Url, string? Title = null, int? StartIndex = null, int? EndIndex = null);
public sealed record LlmCitationEvent(LlmCitation Citation) : LlmStreamEvent;

internal static class HostedSnapshot
{
    internal static IReadOnlyList<T>? List<T>(IReadOnlyList<T>? value) => value is null ? null : Array.AsReadOnly(value.ToArray());
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
