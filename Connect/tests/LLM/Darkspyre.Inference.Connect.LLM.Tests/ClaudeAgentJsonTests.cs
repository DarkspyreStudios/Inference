using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ClaudeAgentJsonTests
{
    private const string Server = "darkspyre-inference-connect";
    private static readonly Type SessionType = typeof(ClaudeAgentClient).GetNestedType("ClaudeAgentSession", BindingFlags.NonPublic)!;

    [Theory]
    [InlineData("9.0")]
    [InlineData("\"9\"")]
    [InlineData("null")]
    public void McpEnvelopesRetainTypedIdsDefaultEscapingNullsAndFieldOrder(string rawId)
    {
        JsonElement id = JsonElement.Parse(rawId);
        JsonElement result = Invoke<JsonElement>("McpResult", id, new JsonObject { ["value"] = "<&>\u00e9", ["nullable"] = null });
        Assert.Equal("{\"jsonrpc\":\"2.0\",\"id\":" + rawId + ",\"result\":{\"value\":\"\\u003C\\u0026\\u003E\\u00E9\",\"nullable\":null}}", result.GetRawText());
        JsonElement error = Invoke<JsonElement>("McpError", id, -32601, "<&>");
        Assert.Equal("{\"jsonrpc\":\"2.0\",\"id\":" + rawId + ",\"error\":{\"code\":-32601,\"message\":\"\\u003C\\u0026\\u003E\"}}", error.GetRawText());
        Assert.Equal("{\"jsonrpc\":\"2.0\",\"id\":" + rawId + ",\"result\":{}}",
            Invoke<JsonElement>("McpResult", id, new JsonObject()).GetRawText());
    }

    [Fact]
    public void ToolManifestRetainsOrderedSchemaNullDescriptionAndBooleanLoadingMetadata()
    {
        var tools = new LlmAgentToolDefinition[]
        {
            new() { Name = "first", InputSchema = JsonElement.Parse("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}") },
            new() { Name = "second", Description = "<&>", InputSchema = JsonElement.Parse("{}"), DeferLoading = true }
        };
        Assert.Equal("""
            {"tools":[{"name":"first","description":"","inputSchema":{"type":"object","properties":{},"additionalProperties":false},"_meta":{"anthropic/alwaysLoad":true}},{"name":"second","description":"\u003C\u0026\u003E","inputSchema":{},"_meta":{"anthropic/alwaysLoad":false}}]}
            """, Invoke<JsonObject>("McpToolsListResult", (object)tools).ToJsonString());
        string version = typeof(ClaudeAgentClient).Assembly.GetName().Version!.ToString();
        Assert.Equal("{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{\"tools\":{\"listChanged\":false}},\"serverInfo\":{\"name\":\"" + Server + "\",\"version\":\"" + version + "\"}}",
            Invoke<JsonObject>("McpInitializeResult", "2025-06-18").ToJsonString());
    }

    [Theory]
    [InlineData("data:image/png;base64,AQID", "{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"AQID\"}}")]
    [InlineData("https://image.invalid/?a=1&b=2", "{\"type\":\"image\",\"source\":{\"type\":\"url\",\"url\":\"https://image.invalid/?a=1\\u0026b=2\"}}")]
    [InlineData("data:image/png;base64,", "{\"type\":\"image\",\"source\":{\"type\":\"url\",\"url\":\"data:image/png;base64,\"}}")]
    public void ImageInputRetainsDataUrlAndUrlRepresentations(string url, string expected) =>
        Assert.Equal(expected, Invoke<JsonObject>("SerializeImageInput", url).ToJsonString());

    [Fact]
    public void InputRetainsContentBeforeOrderedAttachmentsAndExplicitNullText()
    {
        LlmAgentContent[] input = [new LlmAgentTextContent(null!), new LlmAgentImageContent("data:image/png;base64,AQID"), new LlmAgentTextContent("<&>\n")];
        LlmAttachment[] attachments =
        [
            new LlmAttachment { Kind = LlmBinaryKind.Document, FileName = "notes.txt", MediaType = "text/plain", Data = "notes"u8.ToArray() },
            new LlmAttachment { Kind = LlmBinaryKind.Image, FileName = "image.png", MediaType = "image/png", Data = new byte[] { 1, 2, 3 } }
        ];
        Assert.Equal("""
            [{"type":"text","text":null},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AQID"}},{"type":"text","text":"\u003C\u0026\u003E\n"},{"type":"document","source":{"type":"text","media_type":"text/plain","data":"notes"},"title":"notes.txt"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AQID"}}]
            """, Invoke<JsonArray>("SerializeInput", input, attachments).ToJsonString());
    }

    [Fact]
    public void RichToolPartsRetainOrderMediaNamesAbsentMimeTypeAndDefaultEmbeddedType()
    {
        var projected = new ProjectedToolOutput(
            [new SentText("<&>"), new SentImage("image/png", new byte[] { 1, 2, 3 }, null),
                new SentResourceLink("urn:one", null, null), new SentResourceLink("urn:two", "named", "text/plain"),
                new SentEmbeddedText("urn:three", null, ""), new SentEmbeddedText("urn:four", "text/markdown", "notes")],
            true, true, new([], null, new(LlmToolPartDelivery.Delivered)));
        Assert.Equal("""
            [{"type":"text","text":"\u003C\u0026\u003E"},{"type":"image","mimeType":"image/png","data":"AQID"},{"type":"resource_link","uri":"urn:one","name":"urn:one"},{"type":"resource_link","uri":"urn:two","name":"named","mimeType":"text/plain"},{"type":"resource","resource":{"uri":"urn:three","mimeType":"text/plain","text":""}},{"type":"resource","resource":{"uri":"urn:four","mimeType":"text/markdown","text":"notes"}}]
            """, Invoke<JsonArray>("SerializeToolResult", projected).ToJsonString());
    }

    [Theory]
    [InlineData(false, "9.0")]
    [InlineData(true, "9.0")]
    [InlineData(true, "\"9\"")]
    public Task SettledActualClientPreservesItsCompletePrivateWriterWire(bool tools, string rawId) => ExerciseSessionAsync(tools, rawId);

    [Theory]
    [InlineData(false, "9.0")]
    [InlineData(true, "9.0")]
    [InlineData(true, "\"9\"")]
    public void ActualSettledClaudeClientAndWritersReleaseTheirConnectCommonGeneration(bool tools, string rawId)
    {
        WeakReference generation = ExerciseGeneration(tools, rawId);
        for (int attempt = 0; generation.IsAlive && attempt < 30; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive, "The settled controlled Claude client JSON caller retains its Connect/Common generation.");
    }

    private static async Task ExerciseSessionAsync(bool tools, string rawId)
    {
        Assert.Same(AssemblyLoadContext.GetLoadContext(typeof(ClaudeAgentClient).Assembly),
            AssemblyLoadContext.GetLoadContext(typeof(LlmAttachment).Assembly));
        var process = new WireProcess(tools, rawId);
        var client = new ClaudeAgentClient(new Factory(process));
        var options = new LlmAgentSessionOptions
        {
            ModelId = "initial",
            Instructions = tools ? "<&>\u00e9" : " ",
            Runtime = new() { WorkingDirectory = Environment.CurrentDirectory },
            Tools = tools ? [new() { Name = "echo", InputSchema = JsonElement.Parse("{}"), DeferLoading = true }] : [],
            ToolHandler = tools ? (_, _) => Task.FromResult(new LlmToolOutput
            {
                Parts = [new LlmToolOutputText("<&>"), new LlmToolOutputResource("urn:notes") { Text = "notes" }],
                StructuredContent = JsonElement.Parse("{\"n\":1}"),
                IsError = true
            }) : null
        };
        ILlmAgentSession session = await client.StartSessionAsync("connection", new()
        {
            Version = "1",
            Transport = LlmTransport.AnthropicClaudeCli,
            Endpoint = "claude"
        }, options);
        try
        {
            await foreach (LlmAgentEvent item in session.RunTurnAsync(new()
            {
                ModelId = "other",
                ReasoningLevel = LlmReasoningLevel.High,
                Input = [new LlmAgentTextContent("<&>\n"), new LlmAgentImageContent("data:image/png;base64,AQID")],
                Attachments = [new LlmAttachment { Kind = LlmBinaryKind.Document, FileName = "notes.txt", MediaType = "text/plain", Data = "notes"u8.ToArray() }]
            }))
            {
                if (item is LlmAgentToolEvent { Status: "inProgress" } tool) Assert.Equal("{}", tool.Arguments.GetRawText());
            }
        }
        finally
        {
            await session.DisposeAsync();
        }
        Assert.True(process.Disposed);
        Assert.All(process.Writes, line => Assert.DoesNotContain('\n', line));
        JsonObject initialization = JsonNode.Parse(process.Writes[0])!.AsObject();
        initialization["request_id"] = "id";
        string version = typeof(ClaudeAgentClient).Assembly.GetName().Version!.ToString();
        string initializeRequest = tools
            ? "{\"subtype\":\"initialize\",\"systemPrompt\":[\"\\u003C\\u0026\\u003E\\u00E9\"],\"systemPromptSnapshot\":false,\"sdkMcpServers\":[\"" + Server + "\"],\"sdkMcpServerManifests\":{\"" + Server + "\":{\"initializeResult\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{\"tools\":{\"listChanged\":false}},\"serverInfo\":{\"name\":\"" + Server + "\",\"version\":\"" + version + "\"}},\"toolsListResult\":{\"tools\":[{\"name\":\"echo\",\"description\":\"\",\"inputSchema\":{},\"_meta\":{\"anthropic/alwaysLoad\":false}}]}}}}"
            : "{\"subtype\":\"initialize\",\"systemPrompt\":null,\"systemPromptSnapshot\":false}";
        Assert.Equal("{\"type\":\"control_request\",\"request_id\":\"id\",\"request\":" + initializeRequest + "}", initialization.ToJsonString());
        AssertControl(process.Writes[1], "{\"subtype\":\"set_model\",\"model\":\"other\"}");
        AssertControl(process.Writes[2], "{\"subtype\":\"apply_flag_settings\",\"settings\":{\"effortLevel\":\"high\"}}");
        JsonObject user = JsonNode.Parse(process.Writes[3])!.AsObject();
        user["uuid"] = "turn";
        Assert.Equal("""
            {"type":"user","session_id":"","uuid":"turn","message":{"role":"user","content":[{"type":"text","text":"\u003C\u0026\u003E\n"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AQID"}},{"type":"document","source":{"type":"text","media_type":"text/plain","data":"notes"},"title":"notes.txt"}]},"parent_tool_use_id":null}
            """, user.ToJsonString());
        if (tools)
        {
            Assert.Contains("{\"type\":\"control_response\",\"response\":{\"subtype\":\"error\",\"request_id\":\"bad\",\"error\":\"Unsupported control request.\"}}", process.Writes);
            Assert.Contains("{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"ping\",\"response\":{\"mcp_response\":{\"jsonrpc\":\"2.0\",\"id\":null,\"result\":{}}}}}", process.Writes);
            Assert.Contains("{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"missing\",\"response\":{\"mcp_response\":{\"jsonrpc\":\"2.0\",\"id\":0,\"error\":{\"code\":-32601,\"message\":\"Method not found.\"}}}}}", process.Writes);
            Assert.Contains("{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"call\",\"response\":{\"mcp_response\":{\"jsonrpc\":\"2.0\",\"id\":" + rawId + ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"\\u003C\\u0026\\u003E\"},{\"type\":\"resource\",\"resource\":{\"uri\":\"urn:notes\",\"mimeType\":\"text/plain\",\"text\":\"notes\"}},{\"type\":\"text\",\"text\":\"{\\u0022n\\u0022:1}\"}],\"isError\":true}}}}}", process.Writes);
        }
    }

    private static void AssertControl(string wire, string request)
    {
        JsonObject envelope = JsonNode.Parse(wire)!.AsObject();
        envelope["request_id"] = "id";
        Assert.Equal("{\"type\":\"control_request\",\"request_id\":\"id\",\"request\":" + request + "}", envelope.ToJsonString());
    }

    private static T Invoke<T>(string name, params object[] arguments) =>
        (T)SessionType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments)!;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseGeneration(bool tools, string rawId)
    {
        string directory = Path.GetDirectoryName(typeof(ClaudeAgentJsonTests).Assembly.Location)!;
        var generation = new ConnectGeneration(directory);
        var weak = new WeakReference(generation);
        try
        {
            Assembly connect = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(ClaudeAgentJsonTests).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(connect));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(common));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            Type caller = fixture.GetType(typeof(ClaudeAgentJsonTests).FullName!, true)!;
            var operation = (Task)caller.GetMethod(nameof(ExerciseSessionAsync), BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [tools, rawId])!;
            operation.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        finally
        {
            generation.Unload();
        }
        return weak;
    }

    private sealed class ConnectGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private sealed class Factory(WireProcess process) : IClaudeCliSessionProcessFactory
    {
        public Task<IClaudeCliSessionProcess> StartAsync(ClaudeCliSessionInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult<IClaudeCliSessionProcess>(process);
    }

    private sealed class WireProcess(bool tools, string rawId) : IClaudeCliSessionProcess
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        public List<string> Writes { get; } = [];
        public bool Disposed { get; private set; }

        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default)
        {
            Writes.Add(line);
            JsonElement root = JsonElement.Parse(line);
            string? type = root.GetProperty("type").GetString();
            if (type == "control_request")
            {
                string? id = root.GetProperty("request_id").GetString();
                Push(new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = id } });
            }
            else if (type == "user")
            {
                Push(new JsonObject { ["type"] = "user", ["isReplay"] = true, ["uuid"] = root.GetProperty("uuid").GetString() });
                if (tools)
                {
                    Push(new JsonObject { ["type"] = "assistant", ["message"] = new JsonObject { ["id"] = "message", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "tool", ["name"] = "mcp__" + Server + "__echo" }) } });
                    Push(new JsonObject { ["type"] = "control_request", ["request_id"] = "bad", ["request"] = null });
                    PushMcp("ping", new JsonObject { ["jsonrpc"] = "2.0", ["id"] = null, ["method"] = "ping" });
                    PushMcp("missing", new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "unknown" });
                    PushMcp("call", new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(rawId), ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "echo" } });
                }
                else Finish();
            }
            else if (type == "control_response" && root.GetProperty("response").GetProperty("request_id").GetString() == "call") Finish();
            return ValueTask.CompletedTask;
        }

        private void PushMcp(string id, JsonObject message) => Push(new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = id,
            ["request"] = new JsonObject { ["subtype"] = "mcp_message", ["server_name"] = Server, ["message"] = message }
        });

        private void Finish() => Push(new JsonObject { ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["result"] = "done" });
        private void Push(JsonObject value) => _output.Writer.TryWrite(value.ToJsonString());
        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default) => await _output.Reader.ReadAsync(cancellationToken);
        public Task<ClaudeCliProcessResult> WaitForExitAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ClaudeCliProcessResult(0, "", ""));
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _output.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
