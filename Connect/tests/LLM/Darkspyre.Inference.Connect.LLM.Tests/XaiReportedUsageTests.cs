using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiReportedUsageTests
{
    private const string Facts = """
        {"input_tokens":11,"output_tokens":3,"total_tokens":14,"input_tokens_details":{"cached_tokens":7},"output_tokens_details":{"reasoning_tokens":2},"cost_in_usd_ticks":37756001,"num_server_side_tools_used":23,"num_sources_used":29,"server_side_tool_usage_details":{"web_search_calls":2,"x_search_calls":3,"code_interpreter_calls":5,"mcp_calls":7,"image_generation_calls":0,"file_search_calls":11,"document_search_calls":13,"x_posts_fetched":44,"x_users_fetched":8,"future_calls":19}}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualTerminalUsagePreservesIndependentCostCountsAndExistingTokens(bool streaming)
    {
        LlmUsage usage = Assert.IsType<LlmUsage>((await Complete(Facts, streaming)).Usage);
        Assert.Equal(0.0037756001m, usage.ProviderReportedCostUsd);
        Assert.Equal(23, usage.HostedToolCallCount);
        Assert.Equal(29, usage.HostedSourcesUsed);
        Assert.Equal(11, usage.InputTokens);
        Assert.Equal(3, usage.OutputTokens);
        Assert.Equal(14, usage.TotalTokens);
        Assert.Equal(7, usage.CachedInputTokens);
        Assert.Equal(2, usage.ReasoningOutputTokens);
        Assert.Equal(5, usage.HostedToolCalls!.Count);
        Assert.Equal(2, usage.HostedToolCalls[LlmHostedToolKind.WebSearch]);
        Assert.Equal(3, usage.HostedToolCalls[LlmHostedToolKind.SocialSearch]);
        Assert.Equal(5, usage.HostedToolCalls[LlmHostedToolKind.CodeExecution]);
        Assert.Equal(7, usage.HostedToolCalls[LlmHostedToolKind.RemoteMcp]);
        Assert.Equal(0, usage.HostedToolCalls[LlmHostedToolKind.ImageGeneration]);
        Assert.False(usage.HostedToolCalls.ContainsKey(LlmHostedToolKind.DocumentSearch));
        Assert.False(usage.HostedToolCalls.ContainsKey(LlmHostedToolKind.ImageUnderstanding));
        Assert.False(usage.HostedToolCalls.ContainsKey(LlmHostedToolKind.VideoUnderstanding));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitZeroAndEmptyDetailRemainObserved(bool streaming)
    {
        LlmUsage usage = Assert.IsType<LlmUsage>((await Complete("""{"cost_in_usd_ticks":0,"num_server_side_tools_used":0,"num_sources_used":0,"server_side_tool_usage_details":{}}""", streaming)).Usage);
        Assert.Equal(0m, usage.ProviderReportedCostUsd);
        Assert.Equal(0, usage.HostedToolCallCount);
        Assert.Equal(0, usage.HostedSourcesUsed);
        Assert.Empty(usage.HostedToolCalls!);
        Assert.Null(usage.InputTokens);
        Assert.Null(usage.OutputTokens);
        Assert.Null(usage.TotalTokens);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    public async Task AbsentOrNonObjectUsageRemainsUnknown(string usage)
    {
        Assert.Null((await Complete(null, true)).Usage);
        Assert.Null((await Complete(usage, false)).Usage);
        LlmUsage absent = Assert.IsType<LlmUsage>((await Complete("{}", true)).Usage);
        Assert.Null(absent.ProviderReportedCostUsd);
        Assert.Null(absent.HostedToolCallCount);
        Assert.Null(absent.HostedSourcesUsed);
        Assert.Null(absent.HostedToolCalls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"1\"")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("999999999999999999999999999999999999999999999999999999")]
    public async Task InvalidIndependentObservationsDoNotFailGenerationOrFabricateFacts(string invalid)
    {
        string usage = $$"""
            {"cost_in_usd_ticks":{{invalid}},"num_server_side_tools_used":{{invalid}},"num_sources_used":{{invalid}},"server_side_tool_usage_details":{"web_search_calls":{{invalid}},"mcp_calls":0},"output_tokens":3}
            """;
        foreach (bool streaming in new[] { false, true })
        {
            LlmChatResult result = await Complete(usage, streaming);
            Assert.Equal("answer", result.Text);
            Assert.Equal(LlmCompletionStatus.Completed, result.Status);
            Assert.Null(result.Usage!.ProviderReportedCostUsd);
            Assert.Null(result.Usage.HostedToolCallCount);
            Assert.Null(result.Usage.HostedSourcesUsed);
            Assert.Equal(3, result.Usage.OutputTokens);
            Assert.Equal(new KeyValuePair<LlmHostedToolKind, int>(LlmHostedToolKind.RemoteMcp, 0), Assert.Single(result.Usage.HostedToolCalls!));
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("false")]
    public async Task InvalidDetailIsNotAnObservedEmptyMap(string invalid)
    {
        LlmUsage usage = Assert.IsType<LlmUsage>((await Complete("{\"server_side_tool_usage_details\":" + invalid + ",\"num_sources_used\":0}", true)).Usage);
        Assert.Null(usage.HostedToolCalls);
        Assert.Equal(0, usage.HostedSourcesUsed);
    }

    [Fact]
    public async Task CountOverflowDoesNotEraseValidCostOrInferATotalFromPartialDetail()
    {
        LlmUsage usage = Assert.IsType<LlmUsage>((await Complete("""{"cost_in_usd_ticks":1,"num_server_side_tools_used":2147483648,"num_sources_used":2147483648,"server_side_tool_usage_details":{"web_search_calls":2147483648,"x_search_calls":4}}""", true)).Usage);
        Assert.Equal(0.0000000001m, usage.ProviderReportedCostUsd);
        Assert.Null(usage.HostedToolCallCount);
        Assert.Null(usage.HostedSourcesUsed);
        Assert.Equal(new KeyValuePair<LlmHostedToolKind, int>(LlmHostedToolKind.SocialSearch, 4), Assert.Single(usage.HostedToolCalls!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalUsageAloneIsAuthoritativeAndDuplicateTerminalNeverAddsCost(bool absentFinal)
    {
        string final = Answer(absentFinal ? null : Facts);
        string body = "data: {\"type\":\"response.in_progress\",\"response\":" + Answer(Facts) + "}\n\n"
            + "data: {\"type\":\"response.completed\",\"response\":" + final + "}\n\n"
            + "data: {\"type\":\"response.completed\",\"response\":" + Answer(Facts) + "}\n\n";
        using var http = new HttpClient(new Handler(body, true));
        List<LlmStreamEvent> events = await Read(new XaiResponsesLlmClient(http).StreamAsync(Connection(true), Request()));
        Assert.IsType<LlmChatCompletionEvent>(events[^1]);
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Empty(events.OfType<LlmUsageDeltaEvent>());
        if (absentFinal) Assert.Null(result.Usage);
        else Assert.Equal(0.0037756001m, result.Usage!.ProviderReportedCostUsd);
    }

    [Fact]
    public async Task OtherResponsesProvidersDoNotAcquireXaiMetrics()
    {
        using var http = new HttpClient(new Handler(Answer(Facts), false));
        var connection = new LlmConnection
        {
            Version = "1", Transport = LlmTransport.OpenAiResponses, Endpoint = "https://provider.test/v1",
            Credential = new LlmCredential { ApiKey = "controlled" }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = false }
        };
        LlmChatResult result = Assert.Single((await Read(new OpenAiResponsesLlmClient(http).StreamAsync(connection, Request()))).OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(11, result.Usage!.InputTokens);
        Assert.Null(result.Usage.ProviderReportedCostUsd);
        Assert.Null(result.Usage.HostedToolCalls);
        Assert.Null(result.Usage.HostedToolCallCount);
        Assert.Null(result.Usage.HostedSourcesUsed);
    }

    [Fact]
    public async Task ActualForeignAdapterAndOwnedResultMetadataRetireOutsideCallingFrame()
    {
        (WeakReference[] roots, string json) = await ExerciseForeign();
        for (int attempt = 0; attempt < 20 && roots.Any(root => root.IsAlive); attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(10);
        }
        Assert.All(roots, root => Assert.False(root.IsAlive));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement usage = document.RootElement.GetProperty("Usage");
        Assert.Equal(0.0037756001m, usage.GetProperty("ProviderReportedCostUsd").GetDecimal());
        Assert.Equal(23, usage.GetProperty("HostedToolCallCount").GetInt32());
        Assert.Equal(29, usage.GetProperty("HostedSourcesUsed").GetInt32());
        Assert.Equal(5, usage.GetProperty("HostedToolCalls").EnumerateObject().Count());
    }

    public static async Task<string> ExecuteForeignAdapterAsync()
    {
        LlmChatResult result = await Complete(Facts, true);
        string json = JsonSerializer.Serialize(result, ConnectLlmJson.ChatResult);
        LlmChatResult restored = JsonSerializer.Deserialize(json, ConnectLlmJson.ChatResult)!;
        return JsonSerializer.Serialize(restored, ConnectLlmJson.ChatResult);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference[], string)> ExerciseForeign()
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(XaiReportedUsageTests).Assembly.Location)!);
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(XaiReportedUsageTests).Assembly.Location);
            Assembly connect = generation.LoadFromAssemblyPath(typeof(LlmProviderService).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(connect));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(common));
            Assert.Equal(typeof(LlmProviderService).Module.ModuleVersionId, connect.ManifestModule.ModuleVersionId);
            Assert.Equal(typeof(InferenceJson).Module.ModuleVersionId, common.ManifestModule.ModuleVersionId);
            Type caller = fixture.GetType(typeof(XaiReportedUsageTests).FullName!, true)!;
            MethodInfo method = caller.GetMethod(nameof(ExecuteForeignAdapterAsync))!;
            string json = await (Task<string>)method.Invoke(null, null)!;
            var metadata = (System.Text.Json.Serialization.Metadata.JsonTypeInfo)connect.GetType(typeof(ConnectLlmJson).FullName!, true)!
                .GetProperty(nameof(ConnectLlmJson.ChatResult))!.GetValue(null)!;
            Assert.Same(connect, metadata.Type.Assembly);
            Type adapter = connect.GetType(typeof(XaiResponsesLlmClient).FullName!, true)!;
            return ([new(generation), new(fixture), new(connect), new(common), new(caller), new(method), new(adapter), new(metadata), new(metadata.Options.TypeInfoResolver!)], json);
        }
        finally { generation.Unload(); }
    }

    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is not null && (name.Name.StartsWith("Darkspyre.Inference", StringComparison.Ordinal)))
            {
                string path = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(path)) return LoadFromAssemblyPath(path);
            }
            return null;
        }
    }

    private static async Task<LlmChatResult> Complete(string? usage, bool streaming)
    {
        string answer = Answer(usage);
        using var http = new HttpClient(new Handler(streaming ? "data: {\"type\":\"response.completed\",\"response\":" + answer + "}\n\n" : answer, streaming));
        return Assert.Single((await Read(new XaiResponsesLlmClient(http).StreamAsync(Connection(streaming), Request()))).OfType<LlmChatCompletionEvent>()).Result;
    }

    private static string Answer(string? usage) => "{\"id\":\"actual-response\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"answer\"}]}]" + (usage is null ? "}" : ",\"usage\":" + usage + "}");
    private static LlmChatRequest Request() => new() { ModelId = "grok-4.7", Messages = [new("user", "question")] };
    private static LlmConnection Connection(bool streaming) => new()
    {
        Version = "1", Transport = LlmTransport.XaiResponses, Endpoint = "https://api.x.ai/v1",
        Credential = new LlmCredential { OAuth = new() { AccessToken = "controlled-oauth" } }.Serialize(),
        CapabilityOverrides = new() { SupportsStreaming = streaming }
    };
    private static async Task<List<LlmStreamEvent>> Read(IAsyncEnumerable<LlmStreamEvent> source)
    {
        var events = new List<LlmStreamEvent>(); await foreach (LlmStreamEvent item in source) events.Add(item); return events;
    }
    private sealed class Handler(string body, bool streaming) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") });
    }
}
