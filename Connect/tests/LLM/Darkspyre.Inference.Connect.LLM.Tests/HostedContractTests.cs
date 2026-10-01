using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HostedContractTests
{
    [Fact]
    public void PortableAndConnectOwnedMetadataIncludeOnlyTheFixedXaiExtension()
    {
        var search = new XaiXSearchTool([], null, new(2026, 1, 2), new(2026, 1, 3), true, true);
        var portable = new LlmInferenceRequest { ModelId = "model", HostedTools = [search], MaxHostedTurns = 2 };
        string json = JsonSerializer.Serialize(portable, ConnectLlmJson.PortableRequest);
        var restored = JsonSerializer.Deserialize(json, ConnectLlmJson.PortableRequest)!;
        Assert.Equal(search, Assert.IsType<XaiXSearchTool>(Assert.Single(restored.HostedTools)) with { AllowedHandles = search.AllowedHandles });
        Assert.Equal(2, restored.MaxHostedTurns);
        Assert.Contains("\"type\":\"xai-x-search\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Kind\"", json, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(portable, InferenceJson.LlmRequest));
        var chat = new LlmChatRequest { ModelId = "model", HostedTools = [search], MaxHostedTurns = 2 };
        var chatCopy = JsonSerializer.Deserialize(JsonSerializer.Serialize(chat, ConnectLlmJson.ChatRequest), ConnectLlmJson.ChatRequest)!;
        Assert.IsType<XaiXSearchTool>(Assert.Single(chatCopy.HostedTools));
        Assert.Equal(2, chatCopy.MaxHostedTurns);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"ModelId\":\"model\",\"HostedTools\":[{\"type\":\"host-unknown\"}]}", ConnectLlmJson.PortableRequest));
    }

    [Fact]
    public void LiveAuthorizationRefusesBothDirectionsWithoutCallbacksOrDiagnostics()
    {
        var authorization = new Authorization();
        var mcp = new LlmRemoteMcpTool("https://example.test", "server", Authorization: authorization,
            Headers: new Dictionary<string, string> { ["Authorization"] = "secret-header" });
        Assert.Equal("LlmRemoteMcpTool { <redacted> }", mcp.ToString());
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(mcp, InferenceJson.LlmRemoteMcpTool));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new LlmChatRequest { ModelId = "model", HostedTools = [mcp] }, ConnectLlmJson.ChatRequest));
        const string data = "{\"ModelId\":\"model\",\"HostedTools\":[{\"type\":\"remote-mcp\",\"ServerUrl\":\"https://example.test\",\"ServerLabel\":\"server\",\"Authorization\":{}}]}";
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize(data, InferenceJson.LlmRequest));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize(data, ConnectLlmJson.ChatRequest));
        Assert.Equal(0, authorization.Calls);
        Assert.False(authorization.Disposed);
        string nullAuthority = JsonSerializer.Serialize(mcp with { Authorization = null }, InferenceJson.LlmRemoteMcpTool);
        Assert.Contains("secret-header", nullAuthority, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize(nullAuthority, InferenceJson.LlmRemoteMcpTool)!.Authorization);
    }

    [Fact]
    public void UnknownDerivedDefinitionsRefuseAtMetadataAndOperationBoundaries()
    {
        LlmHostedTool unknown = new ForeignTool();
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(unknown, InferenceJson.LlmHostedTool));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new LlmChatRequest { ModelId = "model", HostedTools = [unknown] }, ConnectLlmJson.ChatRequest));
        Assert.Throws<NotSupportedException>(() => HostedTools.Validate([unknown], null, new HashSet<LlmHostedToolKind> { unknown.Kind }));
    }

    [Fact]
    public void CapabilitiesFreezeCallerSetsAndStayUnavailableByDefault()
    {
        var kinds = new HashSet<LlmHostedToolKind> { LlmHostedToolKind.WebSearch };
        var common = new LlmCapabilities { HostedTools = kinds };
        var connect = new LlmProviderCapabilities { HostedTools = kinds };
        var agent = new LlmAgentCapabilities { HostedTools = kinds };
        kinds.Clear();
        Assert.Single(common.HostedTools);
        Assert.Single(connect.HostedTools);
        Assert.Single(agent.HostedTools);
        Assert.Empty(new LlmCapabilities().HostedTools);
        Assert.Empty(new LlmProviderCapabilities().HostedTools);
        Assert.True(new LlmAgentCapabilities { HostedTools = new HashSet<LlmHostedToolKind>() }.IsNone);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidLimitsRefuseWithoutCapabilities(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HostedTools.Validate([], limit, new HashSet<LlmHostedToolKind>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => HostedTools.Validate([new LlmDocumentSearchTool([], limit)], null,
            new HashSet<LlmHostedToolKind> { LlmHostedToolKind.DocumentSearch }));
    }

    [Theory]
    [InlineData(LlmHostedToolKind.ImageUnderstanding)]
    [InlineData(LlmHostedToolKind.VideoUnderstanding)]
    public void UnderstandingObservationsAllowUnknownOutputButNeverInventAnImage(LlmHostedToolKind kind)
    {
        var call = new LlmHostedToolCall("call", kind, "actual", "future-status", null, null);
        Assert.Null(HostedTools.Snapshot(call).Output);
        Assert.Equal("future-status", HostedTools.Snapshot(call).Status);
        Assert.Throws<ArgumentException>(() => HostedTools.Snapshot(call with { Output = new LlmHostedImageOutput(ReadOnlyMemory<byte>.Empty, "image/png") }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ObservedOutputListsRejectNullEntriesBeforePublication(int shape)
    {
        (LlmHostedToolKind kind, LlmHostedToolOutput output) = shape switch
        {
            0 => (LlmHostedToolKind.WebSearch, (LlmHostedToolOutput)new LlmHostedSearchOutput([null!])),
            1 => (LlmHostedToolKind.CodeExecution, new LlmHostedCodeOutput(null, [null!], null)),
            2 => (LlmHostedToolKind.CodeExecution, new LlmHostedCodeOutput(null, null, [null!])),
            3 => (LlmHostedToolKind.DocumentSearch, new LlmHostedDocumentSearchOutput([null!])),
            _ => (LlmHostedToolKind.RemoteMcp, new LlmHostedMcpOutput(null, null, null) { Resources = [null!] })
        };
        var call = new LlmHostedToolCall("call", kind, "actual", "actual-status", null, output);
        Assert.Throws<ArgumentException>(() => HostedTools.Snapshot(call));
        Assert.Same(output, call.Output);
    }

    [Fact]
    public void ManagedSnapshotsPreserveEmptyFiltersAndBorrowAuthority()
    {
        List<string> tools = ["first"];
        var headers = new Dictionary<string, string> { ["private"] = "original" };
        var authorization = new Authorization();
        var original = new LlmRemoteMcpTool("https://example.test", "server", AllowedTools: tools, Headers: headers, Authorization: authorization)
        { AllowedTools = tools, Headers = headers };
        var snapshot = Assert.IsType<LlmRemoteMcpTool>(Assert.Single(HostedTools.Snapshot([original])));
        tools.Add("late"); headers["private"] = "late";
        Assert.Equal("first", Assert.Single(snapshot.AllowedTools!));
        Assert.Equal("original", snapshot.Headers!["private"]);
        Assert.Same(authorization, snapshot.Authorization);
        var empty = Assert.IsType<XaiXSearchTool>(Assert.Single(HostedTools.Snapshot([new XaiXSearchTool([])])));
        Assert.NotNull(empty.AllowedHandles); Assert.Empty(empty.AllowedHandles);
        Assert.Null(empty.ExcludedHandles);
    }

    public static TheoryData<string, string> ForeignCases => new()
    {
        { "PortableRequest", "{\"ModelId\":\"model\",\"HostedTools\":[{\"type\":\"xai-x-search\",\"AllowedHandles\":[],\"FromDate\":\"2026-01-02\"}],\"MaxHostedTurns\":1}" },
        { "ChatRequest", "{\"ModelId\":\"model\",\"HostedTools\":[{\"type\":\"remote-mcp\",\"ServerUrl\":\"https://example.test\",\"ServerLabel\":\"server\",\"Authorization\":null,\"Headers\":{\"x-secret\":\"actual\"}}]}" },
        { "XaiXSearchTool", "{\"AllowedHandles\":[],\"ExcludedHandles\":null,\"FromDate\":null,\"ToDate\":null,\"IncludeImageUnderstanding\":false,\"IncludeVideoUnderstanding\":false}" },
        { "ChatResult", "{\"Text\":\"answer\",\"Status\":0,\"Diagnostics\":{\"HttpStatusCode\":200},\"Citations\":[{\"Url\":\"https://example.test\",\"StartIndex\":0,\"EndIndex\":null},{\"Url\":\"https://example.test\",\"StartIndex\":null,\"EndIndex\":2}]}" },
        { "LlmAgentHostedToolCallStartedEvent", "{\"ThreadId\":\"thread\",\"TurnId\":\"turn\",\"RequestId\":\"request\",\"CallId\":\"call\",\"Kind\":0,\"Name\":\"search\"}" },
        { "LlmAgentHostedToolCallProgressEvent", "{\"ThreadId\":\"thread\",\"TurnId\":\"turn\",\"RequestId\":\"request\",\"CallId\":\"call\",\"Phase\":0}" },
        { "LlmAgentHostedToolCallCompletedEvent", "{\"ThreadId\":\"thread\",\"TurnId\":\"turn\",\"RequestId\":\"request\",\"Call\":{\"CallId\":\"call\",\"Kind\":2,\"Name\":\"code\",\"Status\":\"actual\",\"Input\":{},\"Output\":{\"type\":\"code\",\"Code\":null,\"Logs\":[\"first\",\"error\"],\"ImageUrls\":null}}}" },
        { "LlmAgentCitationEvent", "{\"ThreadId\":\"thread\",\"TurnId\":\"turn\",\"RequestId\":\"request\",\"Citation\":{\"Url\":\"https://example.test\",\"StartIndex\":null,\"EndIndex\":2}}" },
        { "LlmAgentCompactedEvent", "{\"ThreadId\":\"thread\",\"TurnId\":\"turn\",\"RequestId\":\"request\",\"ContextRevision\":2,\"DroppedMessageCount\":0,\"Usage\":null}" }
    };

    [Theory]
    [MemberData(nameof(ForeignCases))]
    public void ActualForeignOwnedMetadataExecutesThenCollects(string getter, string json)
    {
        WeakReference generation = ExerciseGeneration(getter, json);
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(generation.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseGeneration(string getter, string json)
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(ConnectLlmJson).Assembly.Location)!);
        var weak = new WeakReference(generation);
        try
        {
            var connect = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            var common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assert.Equal(typeof(ConnectLlmJson).Assembly.ManifestModule.ModuleVersionId, connect.ManifestModule.ModuleVersionId);
            Assert.Equal(typeof(InferenceJson).Assembly.ManifestModule.ModuleVersionId, common.ManifestModule.ModuleVersionId);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(connect));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(common));
            var metadata = (JsonTypeInfo)connect.GetType(typeof(ConnectLlmJson).FullName!)!.GetProperty(getter)!.GetValue(null)!;
            Assert.Same(connect, metadata.Options.TypeInfoResolver!.GetType().Assembly);
            object value = JsonSerializer.Deserialize(json, metadata)!;
            Assert.Same(getter == "PortableRequest" ? common : connect, value.GetType().Assembly);
            string written = JsonSerializer.Serialize(value, metadata);
            Assert.NotNull(JsonSerializer.Deserialize(written, metadata));
            if (getter is "PortableRequest" or "ChatRequest")
            {
                object hosted = Assert.Single(((System.Collections.IEnumerable)value.GetType().GetProperty("HostedTools")!.GetValue(value)!).Cast<object>());
                Assert.Same(getter == "PortableRequest" ? connect : common, hosted.GetType().Assembly);
            }
            using JsonDocument expected = JsonDocument.Parse(json);
            using JsonDocument actual = JsonDocument.Parse(written);
            foreach (var property in expected.RootElement.EnumerateObject())
                Assert.True(Contains(property.Value, actual.RootElement.GetProperty(property.Name)));
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static bool Contains(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind == JsonValueKind.Object)
            return expected.EnumerateObject().All(property => actual.TryGetProperty(property.Name, out var value) && Contains(property.Value, value));
        if (expected.ValueKind == JsonValueKind.Array)
            return expected.GetArrayLength() == actual.GetArrayLength() && expected.EnumerateArray().Zip(actual.EnumerateArray()).All(pair => Contains(pair.First, pair.Second));
        return JsonElement.DeepEquals(expected, actual);
    }

    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private sealed record ForeignTool : LlmHostedTool { public override LlmHostedToolKind Kind => LlmHostedToolKind.WebSearch; }
    private sealed class Authorization : IProviderAuthorization, IDisposable
    {
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        public ValueTask<ProviderAuthorizationHeader> GetAsync(CancellationToken cancellationToken = default)
        { Calls++; return ValueTask.FromResult(new ProviderAuthorizationHeader("Bearer", "secret")); }
        public void Dispose() => Disposed = true;
    }
}
