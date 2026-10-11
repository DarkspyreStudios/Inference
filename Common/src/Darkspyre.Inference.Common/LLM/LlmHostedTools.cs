using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.LLM;

/// <summary>The stage a running hosted tool call reports in a progress event. Members: Searching, Executing, Generating, Interpreting.</summary>
public enum LlmHostedToolPhase
{
    /// <summary>The hosted tool is running a search.</summary>
    Searching = 0,
    /// <summary>The hosted tool is running code in the provider sandbox.</summary>
    Executing = 1,
    /// <summary>The hosted tool is producing an image.</summary>
    Generating = 2,
    /// <summary>The hosted code tool is processing the output of the code it ran.</summary>
    Interpreting = 3
}
/// <summary>Whether the hosted image tool creates a new image, edits an input image, or lets the provider choose (Auto).</summary>
public enum LlmImageGenerationAction
{
    /// <summary>The provider decides whether to create a new image or edit an input image.</summary>
    Auto = 0,
    /// <summary>The tool creates a new image.</summary>
    Generate = 1,
    /// <summary>The tool changes an input image.</summary>
    Edit = 2
}

/// <summary>A tool the provider runs on its own side during a request, serialized with a <c>type</c> discriminator.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmWebSearchTool), "web-search")]
[JsonDerivedType(typeof(LlmCodeExecutionTool), "code-execution")]
[JsonDerivedType(typeof(LlmImageGenerationTool), "image-generation")]
[JsonDerivedType(typeof(LlmDocumentSearchTool), "document-search")]
[JsonDerivedType(typeof(LlmRemoteMcpTool), "remote-mcp")]
public abstract record LlmHostedTool
{
    /// <summary>Initializes a hosted tool for a derived record.</summary>
    protected LlmHostedTool() { }
    /// <summary>The kind of hosted tool this record describes.</summary>
    [JsonIgnore] public abstract LlmHostedToolKind Kind { get; }
}

/// <summary>An approximate user location the provider uses to localize web search results.</summary>
/// <param name="Country">The country of the location.</param>
/// <param name="City">The city of the location.</param>
/// <param name="Region">The region or state of the location.</param>
/// <param name="Timezone">The time zone of the location.</param>
public sealed record LlmWebSearchLocation(string? Country = null,
    string? City = null, string? Region = null, string? Timezone = null);

/// <summary>The provider-hosted web search tool.</summary>
/// <param name="AllowedDomains">The domains search is limited to, or null for no limit.</param>
/// <param name="ExcludedDomains">The domains search leaves out, or null for none.</param>
/// <param name="IncludeImageUnderstanding">True lets the provider read images found on searched pages.</param>
/// <param name="IncludeImageSearch">True lets the provider search for images.</param>
public sealed record LlmWebSearchTool(
    IReadOnlyList<string>? AllowedDomains = null, IReadOnlyList<string>? ExcludedDomains = null,
    bool IncludeImageUnderstanding = false, bool IncludeImageSearch = false) : LlmHostedTool
{
    /// <summary>The domains search is limited to, as a read-only copy, or null for no limit.</summary>
    public IReadOnlyList<string>? AllowedDomains { get; init; } = HostedSnapshot.List(AllowedDomains);
    /// <summary>The domains search leaves out, as a read-only copy, or null for none.</summary>
    public IReadOnlyList<string>? ExcludedDomains { get; init; } = HostedSnapshot.List(ExcludedDomains);
    /// <summary>The approximate user location used to localize results, or null for none.</summary>
    public LlmWebSearchLocation? Location { get; init; }
    /// <inheritdoc />
    [JsonIgnore] public override LlmHostedToolKind Kind => LlmHostedToolKind.WebSearch;
}

/// <summary>The provider-hosted code execution tool.</summary>
public sealed record LlmCodeExecutionTool() : LlmHostedTool
{
    /// <inheritdoc />
    [JsonIgnore] public override LlmHostedToolKind Kind => LlmHostedToolKind.CodeExecution;
}

/// <summary>The provider-hosted image generation tool.</summary>
/// <param name="Action">Whether the tool generates a new image, edits an input image, or lets the provider choose.</param>
public sealed record LlmImageGenerationTool(LlmImageGenerationAction Action = LlmImageGenerationAction.Auto) : LlmHostedTool
{
    /// <inheritdoc />
    [JsonIgnore] public override LlmHostedToolKind Kind => LlmHostedToolKind.ImageGeneration;
}

/// <summary>The provider-hosted search over document collections stored with the provider.</summary>
/// <param name="CollectionIds">The provider ids of the collections to search.</param>
/// <param name="MaxResults">The most matches to return, or null for the provider default.</param>
public sealed record LlmDocumentSearchTool(IReadOnlyList<string> CollectionIds, int? MaxResults = null) : LlmHostedTool
{
    /// <summary>The provider ids of the collections to search, as a read-only copy.</summary>
    [JsonRequired]
    public IReadOnlyList<string> CollectionIds { get; init => field = HostedSnapshot.List(HostedSnapshot.Required(value))!; }
        = HostedSnapshot.List(HostedSnapshot.Required(CollectionIds))!;
    /// <inheritdoc />
    [JsonIgnore] public override LlmHostedToolKind Kind => LlmHostedToolKind.DocumentSearch;
}

/// <summary>A remote MCP server the provider connects to and calls on the model's behalf.</summary>
/// <param name="ServerUrl">The URL of the MCP server.</param>
/// <param name="ServerLabel">The label that names the server in tool calls.</param>
/// <param name="ServerDescription">A description of the server sent to the provider, or null for none.</param>
/// <param name="AllowedTools">The server tools the model may call, or null for all of them.</param>
/// <param name="Authorization">The live authorization the provider sends to the server; it is never serialized.</param>
/// <param name="Headers">Extra HTTP headers the provider sends to the server, or null for none.</param>
/// <param name="DeferLoading">True asks the provider to load the server's tool definitions only when the model needs them.</param>
public sealed record LlmRemoteMcpTool(
    string ServerUrl, string ServerLabel, string? ServerDescription = null,
    IReadOnlyList<string>? AllowedTools = null, IProviderAuthorization? Authorization = null,
    IReadOnlyDictionary<string, string>? Headers = null, bool DeferLoading = false) : LlmHostedTool
{
    /// <summary>The URL of the MCP server; it must not be blank.</summary>
    [JsonRequired]
    public string ServerUrl { get; init => field = HostedSnapshot.Identity(value); } = HostedSnapshot.Identity(ServerUrl);
    /// <summary>The label that names the server in tool calls; it must not be blank.</summary>
    [JsonRequired]
    public string ServerLabel { get; init => field = HostedSnapshot.Identity(value); } = HostedSnapshot.Identity(ServerLabel);
    /// <summary>The server tools the model may call, as a read-only copy, or null for all of them.</summary>
    public IReadOnlyList<string>? AllowedTools { get; init; } = HostedSnapshot.List(AllowedTools);
    /// <summary>Extra HTTP headers for the server, as a read-only copy with ordinal keys, or null for none.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; } = Headers is null ? null
        : new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(Headers, StringComparer.Ordinal));
    /// <inheritdoc />
    [JsonIgnore] public override LlmHostedToolKind Kind => LlmHostedToolKind.RemoteMcp;
    /// <inheritdoc />
    public override string ToString() => "LlmRemoteMcpTool { <redacted> }";
}

/// <summary>The result a hosted tool call produced, serialized with a <c>type</c> discriminator.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmHostedSearchOutput), "search")]
[JsonDerivedType(typeof(LlmHostedCodeOutput), "code")]
[JsonDerivedType(typeof(LlmHostedDocumentSearchOutput), "document-search")]
[JsonDerivedType(typeof(LlmHostedMcpOutput), "mcp")]
[JsonDerivedType(typeof(LlmHostedImageOutput), "image")]
public abstract record LlmHostedToolOutput
{
    private protected LlmHostedToolOutput() { }
    /// <summary>The caller resources the output was saved to, or null when none were written.</summary>
    public IReadOnlyList<InferenceResourceDescriptor>? Resources { get; init; }
}

/// <summary>One source a hosted search returned.</summary>
/// <param name="Url">The URL of the source, or null when the provider gave none.</param>
/// <param name="Title">The title of the source, or null when the provider gave none.</param>
public sealed record LlmHostedSearchSource(string? Url, string? Title = null)
{
    /// <summary>The URL of the source, or null when the provider gave none.</summary>
    [JsonRequired]
    public string? Url { get; init; } = Url;
}
/// <summary>The output of a hosted web search call.</summary>
/// <param name="Sources">The sources the search returned, or null when none were reported.</param>
public sealed record LlmHostedSearchOutput(IReadOnlyList<LlmHostedSearchSource>? Sources) : LlmHostedToolOutput
{
    /// <summary>The sources the search returned, as a read-only copy, or null when none were reported.</summary>
    public IReadOnlyList<LlmHostedSearchSource>? Sources { get; init; } = HostedSnapshot.List(Sources);
}
/// <summary>The output of a hosted code execution call.</summary>
/// <param name="Code">The code the provider ran, or null when not reported.</param>
/// <param name="Logs">The log lines the run produced, or null when not reported.</param>
/// <param name="ImageUrls">The URLs of images the run produced, or null when not reported.</param>
public sealed record LlmHostedCodeOutput(string? Code, IReadOnlyList<string>? Logs, IReadOnlyList<string>? ImageUrls) : LlmHostedToolOutput
{
    /// <summary>The log lines the run produced, as a read-only copy, or null when not reported.</summary>
    public IReadOnlyList<string>? Logs { get; init; } = HostedSnapshot.List(Logs);
    /// <summary>The URLs of images the run produced, as a read-only copy, or null when not reported.</summary>
    public IReadOnlyList<string>? ImageUrls { get; init; } = HostedSnapshot.List(ImageUrls);
}
/// <summary>One document passage a hosted document search matched.</summary>
/// <param name="FileId">The provider id of the matched file.</param>
/// <param name="FileName">The name of the matched file, or null when not reported.</param>
/// <param name="Score">The relevance score the provider gave the match, or null when not reported.</param>
/// <param name="Text">The matched text, or null when not reported.</param>
public sealed record LlmHostedDocumentMatch(string FileId, string? FileName, double? Score, string? Text)
{
    /// <summary>The provider id of the matched file.</summary>
    [JsonRequired]
    public string FileId { get; init => field = HostedSnapshot.Required(value); } = HostedSnapshot.Required(FileId);
}
/// <summary>The output of a hosted document search call.</summary>
/// <param name="Results">The matches the search returned, or null when none were reported.</param>
public sealed record LlmHostedDocumentSearchOutput(IReadOnlyList<LlmHostedDocumentMatch>? Results) : LlmHostedToolOutput
{
    /// <summary>The matches the search returned, as a read-only copy, or null when none were reported.</summary>
    public IReadOnlyList<LlmHostedDocumentMatch>? Results { get; init; } = HostedSnapshot.List(Results);
}
/// <summary>The output of a call to a remote MCP server tool.</summary>
/// <param name="Arguments">The arguments the tool was called with, or null when not reported.</param>
/// <param name="Output">The result the tool returned, or null when not reported.</param>
/// <param name="Error">The error the call returned, or null when it did not fail.</param>
public sealed record LlmHostedMcpOutput(JsonElement? Arguments, JsonElement? Output, JsonElement? Error) : LlmHostedToolOutput
{
    /// <summary>The arguments the tool was called with, as a cloned element, or null when not reported.</summary>
    public JsonElement? Arguments { get; init; } = Arguments?.Clone();
    /// <summary>The result the tool returned, as a cloned element, or null when not reported.</summary>
    public JsonElement? Output { get; init; } = Output?.Clone();
    /// <summary>The error the call returned, as a cloned element, or null when it did not fail.</summary>
    public JsonElement? Error { get; init; } = Error?.Clone();
}
/// <summary>An image a hosted image generation call produced.</summary>
/// <param name="Data">The encoded image bytes.</param>
/// <param name="MediaType">The media type of the image, such as image/png.</param>
public sealed record LlmHostedImageOutput(ReadOnlyMemory<byte> Data, string MediaType) : LlmHostedToolOutput
{
    /// <summary>The encoded image bytes, held as a private copy.</summary>
    [JsonRequired]
    public ReadOnlyMemory<byte> Data { get; init; } = Data.ToArray();
    /// <summary>The media type of the image, such as image/png.</summary>
    [JsonRequired]
    public string MediaType { get; init => field = HostedSnapshot.Required(value); } = HostedSnapshot.Required(MediaType);
}
/// <summary>A finished hosted tool call, with its input and output.</summary>
/// <param name="CallId">The provider id of the call, or null when the provider gave none.</param>
/// <param name="Kind">The kind of hosted tool that ran.</param>
/// <param name="Name">The name of the tool that ran.</param>
/// <param name="Status">The final status the provider reported, or null when not reported.</param>
/// <param name="Input">The input the tool received, or null when not reported.</param>
/// <param name="Output">The output the tool produced, or null when not reported.</param>
public sealed record LlmHostedToolCall(string? CallId, LlmHostedToolKind Kind,
    string Name, string? Status,
    JsonElement? Input, LlmHostedToolOutput? Output)
{
    /// <summary>The provider id of the call, or null when the provider gave none.</summary>
    [JsonRequired]
    public string? CallId { get; init; } = CallId;
    /// <summary>The kind of hosted tool that ran.</summary>
    [JsonRequired]
    public LlmHostedToolKind Kind { get; init => field = HostedSnapshot.Defined(value); } = HostedSnapshot.Defined(Kind);
    /// <summary>The name of the tool that ran; it must not be blank.</summary>
    [JsonRequired]
    public string Name { get; init => field = HostedSnapshot.Identity(value); } = HostedSnapshot.Identity(Name);
    /// <summary>The final status the provider reported, or null when not reported.</summary>
    [JsonRequired]
    public string? Status { get; init; } = Status;
    /// <summary>The input the tool received, as a cloned element, or null when not reported.</summary>
    public JsonElement? Input { get; init; } = Input?.Clone();
}
/// <summary>A stream event raised when the provider starts a hosted tool call.</summary>
/// <param name="CallId">The provider id of the call.</param>
/// <param name="Kind">The kind of hosted tool that started.</param>
/// <param name="Name">The name of the tool that started.</param>
public sealed record LlmHostedToolCallStartedEvent(string CallId,
    LlmHostedToolKind Kind, string Name) : LlmStreamEvent
{
    /// <summary>The provider id of the call; it must not be blank.</summary>
    [JsonRequired]
    public string CallId { get; init => field = HostedSnapshot.Identity(value); } = HostedSnapshot.Identity(CallId);
    /// <summary>The kind of hosted tool that started.</summary>
    [JsonRequired]
    public LlmHostedToolKind Kind { get; init => field = HostedSnapshot.Defined(value); } = HostedSnapshot.Defined(Kind);
    /// <summary>The name of the tool that started; it must not be blank.</summary>
    [JsonRequired]
    public string Name { get; init => field = HostedSnapshot.Identity(value); } = HostedSnapshot.Identity(Name);
}
/// <summary>A stream event raised when a running hosted tool call reports its current phase.</summary>
/// <param name="CallId">The provider id of the call.</param>
/// <param name="Phase">The phase the call has reached.</param>
public sealed record LlmHostedToolCallProgressEvent(string CallId,
    LlmHostedToolPhase Phase) : LlmStreamEvent
{
    /// <summary>The provider id of the call; it must not be blank.</summary>
    [JsonRequired]
    public string CallId { get; init => field = HostedSnapshot.Identity(value); } = HostedSnapshot.Identity(CallId);
    /// <summary>The phase the call has reached.</summary>
    [JsonRequired]
    public LlmHostedToolPhase Phase { get; init => field = HostedSnapshot.Defined(value); } = HostedSnapshot.Defined(Phase);
}
/// <summary>A stream event raised when a hosted tool call finishes.</summary>
/// <param name="Call">The finished call, with its input and output.</param>
public sealed record LlmHostedToolCallCompletedEvent(LlmHostedToolCall Call) : LlmStreamEvent
{
    /// <summary>The finished call, with its input and output.</summary>
    [JsonRequired]
    public LlmHostedToolCall Call { get; init => field = HostedSnapshot.Required(value); } = HostedSnapshot.Required(Call);
}

/// <summary>Offsets, when reported, are UTF-16 positions in emitted text; EndIndex is exclusive.</summary>
public sealed record LlmCitation(string Url, string? Title = null, int? StartIndex = null, int? EndIndex = null)
{
    /// <summary>The URL of the cited source.</summary>
    [JsonRequired]
    public string Url { get; init => field = HostedSnapshot.Required(value); } = HostedSnapshot.Required(Url);
}
/// <summary>A stream event raised when the model cites a source in its output.</summary>
/// <param name="Citation">The citation the model emitted.</param>
public sealed record LlmCitationEvent(LlmCitation Citation) : LlmStreamEvent
{
    /// <summary>The citation the model emitted.</summary>
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
