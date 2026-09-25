using Darkspyre.Inference.Connect.LLM.Internal;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ClaudeAgentClientTests
{
    [Fact]
    public async Task NativeContextAndInstructionRejectionsDoNotWriteToCli()
    {
        var process = new ScriptedSessionProcess();
        var factory = new RecordingSessionProcessFactory(process);
        var client = new ClaudeAgentClient(factory);
        var options = new LlmAgentSessionOptions
        {
            ModelId = "claude-test",
            WorkingDirectory = Environment.CurrentDirectory
        };
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.StartSessionAsync(Connection(), options with { Context = new() }));
        Assert.Null(factory.Invocation);
        await using var session = await client.StartSessionAsync(Connection(), options);
        int before = process.Writes.Count;
        await AgentContractTests.AssertNativeOperationsAsync(session);
        Assert.Equal(before, process.Writes.Count);
        Assert.Equal(LlmAgentNativeFork.CurrentPosition, (await session.GetCapabilitiesAsync()).NativeFork);
    }

    [Fact]
    public async Task SessionPublishesSdkMcpToolsAndExecutesHostCallback()
    {
        var process = new ScriptedSessionProcess();
        var factory = new RecordingSessionProcessFactory(process);
        var client = new ClaudeAgentClient(factory);
        LlmAgentToolCall? observedCall = null;
        using JsonDocument schema = JsonDocument.Parse(
            """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""");

        await using ILlmAgentSession session = await client.StartSessionAsync(
            Connection(),
            new LlmAgentSessionOptions
            {
                ModelId = "claude-test",
                WorkingDirectory = Environment.CurrentDirectory,
                WritableRoots = [Environment.CurrentDirectory, Path.GetTempPath()],
                NetworkAllowed = true,
                Tools =
                [
                    new LlmAgentToolDefinition
                    {
                        Name = "echo",
                        Description = "Echo text.",
                        InputSchema = schema.RootElement.Clone()
                    }
                ],
                ToolHandler = (call, _) =>
                {
                    observedCall = call;
                    return ValueTask.FromResult(new LlmToolOutput
                    {
                        Parts =
                        [
                            new LlmToolOutputText("echo: hello"),
                            new LlmToolOutputBinary(
                                LlmBinaryKind.Image,
                                "image/png",
                                new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a },
                                "chart.png"),
                            new LlmToolOutputBinary(LlmBinaryKind.Document, "application/pdf", "%PDF-"u8.ToArray(), "report.pdf"),
                            new LlmToolOutputBinary(LlmBinaryKind.Audio, "audio/wav", new byte[] { 1 }, "clip.wav"),
                            new LlmToolOutputBinary(LlmBinaryKind.Document, "text/plain", "notes"u8.ToArray(), "notes.txt"),
                            new LlmToolOutputResource("https://example.test/spec") { Name = "spec" }
                        ],
                        StructuredContent = JsonSerializer.SerializeToElement(new { n = 1 })
                    });
                }
            });

        IReadOnlyList<LlmAgentEvent> events = await ReadAllAsync(session.RunTurnAsync(
            new LlmAgentTurnRequest
            {
                Input = [new LlmAgentTextContent("Use the echo tool.")],
                Attachments =
                [
                    new LlmAttachment
                    {
                        Kind = LlmAttachmentKind.Image,
                        FileName = "prompt.png",
                        Data = new byte[]
                        {
                            0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a
                        }
                    },
                    new LlmAttachment
                    {
                        Kind = LlmAttachmentKind.Document,
                        FileName = "prompt.txt",
                        Data = "prompt document"u8.ToArray()
                    }
                ]
            }));

        Assert.NotNull(observedCall);
        Assert.Equal("echo", observedCall.Name);
        Assert.Equal("hello", observedCall.Arguments.GetProperty("text").GetString());
        Assert.Equal("darkspyre-inference-connect", observedCall.Namespace);
        Assert.Contains(events.OfType<LlmAgentToolEvent>(), item =>
            item.Tool == "echo" && item.Status == "inProgress");
        Assert.Contains(events.OfType<LlmAgentToolEvent>(), item =>
            item.Tool == "echo" && item.Status == "completed" && item.Success == true);
        Assert.Equal(
            LlmAgentTurnStatus.Completed,
            Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Status);

        ClaudeCliSessionInvocation invocation = Assert.IsType<ClaudeCliSessionInvocation>(factory.Invocation);
        Assert.Contains("--restricted", invocation.Arguments);
        Assert.Contains("--add-dir", invocation.Arguments);
        Assert.Contains(Path.GetFullPath(Path.GetTempPath()), invocation.Arguments);
        Assert.Contains("Read,Write,Edit,Bash,WebFetch,WebSearch", invocation.Arguments);
        Assert.Contains("--strict-mcp-config", invocation.Arguments);
        Assert.Contains("--tools", invocation.Arguments);
        Assert.Contains("mcp__darkspyre-inference-connect__echo,Read", invocation.Arguments);

        using JsonDocument initialize = JsonDocument.Parse(process.Writes[0]);
        JsonElement initializeRequest = initialize.RootElement.GetProperty("request");
        Assert.Equal("initialize", initializeRequest.GetProperty("subtype").GetString());
        Assert.Equal("darkspyre-inference-connect", initializeRequest.GetProperty("sdkMcpServers")[0].GetString());
        Assert.Equal(
            "echo",
            initializeRequest
                .GetProperty("sdkMcpServerManifests")
                .GetProperty("darkspyre-inference-connect")
                .GetProperty("toolsListResult")
                .GetProperty("tools")[0]
                .GetProperty("name")
                .GetString());

        JsonElement mcpResponse = process.Writes
            .Select(Parse)
            .First(root => root.GetProperty("type").GetString() == "control_response")
            .GetProperty("response")
            .GetProperty("response")
            .GetProperty("mcp_response");
        Assert.Equal(
            "echo: hello",
            mcpResponse.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        JsonElement toolContent = mcpResponse.GetProperty("result").GetProperty("content");
        Assert.False(mcpResponse.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(
            ["text", "image", "resource", "resource_link", "text"],
            toolContent.EnumerateArray().Select(item => item.GetProperty("type").GetString()));
        Assert.Equal("image/png", toolContent[1].GetProperty("mimeType").GetString());
        Assert.Equal("iVBORw0KGgo=", toolContent[1].GetProperty("data").GetString());
        JsonElement embedded = toolContent[2].GetProperty("resource");
        Assert.Equal(("file:///notes.txt", "text/plain", "notes"), (
            embedded.GetProperty("uri").GetString(),
            embedded.GetProperty("mimeType").GetString(),
            embedded.GetProperty("text").GetString()));
        Assert.Equal("https://example.test/spec", toolContent[3].GetProperty("uri").GetString());
        Assert.Equal("spec", toolContent[3].GetProperty("name").GetString());
        Assert.False(toolContent[3].TryGetProperty("mimeType", out _));
        Assert.Equal("{\"n\":1}", toolContent[4].GetProperty("text").GetString());

        JsonElement userContent = process.Writes
            .Select(Parse)
            .First(root => root.GetProperty("type").GetString() == "user")
            .GetProperty("message")
            .GetProperty("content");
        Assert.Equal("text", userContent[0].GetProperty("type").GetString());
        Assert.Equal("image", userContent[1].GetProperty("type").GetString());
        Assert.Equal(
            "image/png",
            userContent[1].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal("document", userContent[2].GetProperty("type").GetString());
        Assert.Equal("prompt.txt", userContent[2].GetProperty("title").GetString());
    }

    [Fact]
    public async Task SessionNeedsNoStoredCredentialButRequiresToolHandler()
    {
        var client = new ClaudeAgentClient(
            new RecordingSessionProcessFactory(new ScriptedSessionProcess()));
        LlmConnection apiKey = Connection() with
        {
            Credential = new LlmCredential { ApiKey = "api-key" }.Serialize()
        };

        await Assert.ThrowsAsync<NotSupportedException>(() => client.StartSessionAsync(
            apiKey,
            new LlmAgentSessionOptions
            {
                ModelId = "claude-test",
                WorkingDirectory = Environment.CurrentDirectory
            }));

        using JsonDocument schema = JsonDocument.Parse("""{"type":"object"}""");
        await Assert.ThrowsAsync<ArgumentException>(() => client.StartSessionAsync(
            Connection(),
            new LlmAgentSessionOptions
            {
                ModelId = "claude-test",
                WorkingDirectory = Environment.CurrentDirectory,
                Tools =
                [
                    new LlmAgentToolDefinition
                    {
                        Name = "echo",
                        InputSchema = schema.RootElement.Clone()
                    }
                ]
            }));
    }

    [Theory]
    [InlineData(LlmAgentSandboxMode.WorkspaceWrite)]
    [InlineData(LlmAgentSandboxMode.DangerFullAccess)]
    public async Task SessionRejectsNetworkIsolationItCannotEnforce(
        LlmAgentSandboxMode sandbox)
    {
        var client = new ClaudeAgentClient(
            new RecordingSessionProcessFactory(new ScriptedSessionProcess()));

        await Assert.ThrowsAsync<NotSupportedException>(() => client.StartSessionAsync(
            Connection(),
            new LlmAgentSessionOptions
            {
                ModelId = "claude-test",
                WorkingDirectory = Environment.CurrentDirectory,
                Sandbox = sandbox,
                NetworkAllowed = false
            }));
    }

    [Fact]
    public async Task SessionRejectsToolNamesThatCouldBroadenTheCliAllowlist()
    {
        var client = new ClaudeAgentClient(
            new RecordingSessionProcessFactory(new ScriptedSessionProcess()));
        using JsonDocument schema = JsonDocument.Parse("""{"type":"object"}""");

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.StartSessionAsync(
                Connection(),
                new LlmAgentSessionOptions
                {
                    ModelId = "claude-test",
                    WorkingDirectory = Environment.CurrentDirectory,
                    Tools =
                    [
                        new LlmAgentToolDefinition
                        {
                            Name = "echo,Read",
                            InputSchema = schema.RootElement.Clone()
                        }
                    ],
                    ToolHandler = (_, _) => ValueTask.FromResult(new LlmToolOutput())
                }));

        Assert.Contains("valid MCP name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStartContextOpensOnlyTheFirstTurnAndIsReported()
    {
        var process = new ScriptedSessionProcess();
        var client = new ClaudeAgentClient(new RecordingSessionProcessFactory(process));
        using JsonDocument schema = JsonDocument.Parse(
            """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""");
        await using ILlmAgentSession session = await client.StartSessionAsync(Connection(), new LlmAgentSessionOptions
        {
            ModelId = "claude-test",
            WorkingDirectory = Environment.CurrentDirectory,
            UnboundResponseState = LlmUnboundResponseState.Omit,
            Context = new LlmAgentContext
            {
                Entries =
                [
                    new("e1", "t1", new LlmChatMessage("user", "What is 17 times 23?")),
                    new("e2", "t1", new LlmChatMessage("assistant", "391")
                    {
                        ResponseState = new() { Transport = LlmTransport.OpenAiCodex, ModelId = "gpt", ScopeId = "scope", Payload = "[]" }
                    })
                ]
            },
            Tools = [new LlmAgentToolDefinition { Name = "echo", Description = "Echo text.", InputSchema = schema.RootElement.Clone() }],
            ToolHandler = (_, _) => ValueTask.FromResult(LlmToolOutput.FromText("echo"))
        });

        IReadOnlyList<LlmAgentEvent> first = await ReadAllAsync(session.RunTurnAsync(
            new LlmAgentTurnRequest { Input = [new LlmAgentTextContent("Add 5.")] }));
        IReadOnlyList<LlmAgentEvent> second = await ReadAllAsync(session.RunTurnAsync(
            new LlmAgentTurnRequest { Input = [new LlmAgentTextContent("Again.")] }));

        JsonElement[] turns = [.. process.Writes.Select(Parse)
            .Where(line => line.GetProperty("type").GetString() == "user" && line.TryGetProperty("uuid", out _))
            .Select(line => line.GetProperty("message").GetProperty("content"))];
        Assert.Equal(2, turns.Length);
        string opening = turns[0][0].GetProperty("text").GetString()!;
        Assert.StartsWith(AgentOpeningContext.Header, opening, StringComparison.Ordinal);
        Assert.Contains("[ASSISTANT]" + Environment.NewLine + "391", opening, StringComparison.Ordinal);
        Assert.Equal("Add 5.", turns[0][1].GetProperty("text").GetString());
        Assert.Equal(1, turns[1].GetArrayLength());
        Assert.Equal("Again.", turns[1][0].GetProperty("text").GetString());
        LlmAgentWarningEvent warning = Assert.Single(first.OfType<LlmAgentWarningEvent>());
        Assert.Contains("response state from 1 earlier messages", warning.Message, StringComparison.Ordinal);
        Assert.Empty(second.OfType<LlmAgentWarningEvent>());
    }

    private static LlmConnection Connection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.AnthropicClaudeCli,
        Endpoint = "claude",
        Credential = null
    };

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        var results = new List<T>();
        await foreach (T item in source)
        {
            results.Add(item);
        }
        return results;
    }

    private sealed class RecordingSessionProcessFactory(IClaudeCliSessionProcess process)
        : IClaudeCliSessionProcessFactory
    {
        public ClaudeCliSessionInvocation? Invocation { get; private set; }

        public Task<IClaudeCliSessionProcess> StartAsync(
            ClaudeCliSessionInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Invocation = invocation;
            return Task.FromResult(process);
        }
    }

    private sealed class ScriptedSessionProcess : IClaudeCliSessionProcess
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        private bool _disposed;

        public ConcurrentQueue<string> CapturedWrites { get; } = new();

        public IReadOnlyList<string> Writes => [.. CapturedWrites];

        public ValueTask WriteLineAsync(
            string line,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CapturedWrites.Enqueue(line);
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string type = root.GetProperty("type").GetString()!;
            if (type == "control_request")
            {
                string requestId = root.GetProperty("request_id").GetString()!;
                JsonElement request = root.GetProperty("request");
                string subtype = request.GetProperty("subtype").GetString()!;
                if (subtype is "initialize" or "set_model" or "apply_flag_settings")
                {
                    Enqueue(new
                    {
                        type = "control_response",
                        response = new
                        {
                            subtype = "success",
                            request_id = requestId,
                            response = new { }
                        }
                    });
                }
            }
            else if (type == "user")
            {
                // The CLI echoes each host message when it starts processing it (--replay-user-messages).
                Enqueue(new { type = "user", isReplay = true, uuid = root.GetProperty("uuid").GetString(), message = new { role = "user", content = "echo" } });
                Enqueue(new
                {
                    type = "assistant",
                    message = new
                    {
                        id = "message-1",
                        content = new object[]
                        {
                            new
                            {
                                type = "tool_use",
                                id = "tool-use-1",
                                name = "mcp__darkspyre-inference-connect__echo",
                                input = new { text = "hello" }
                            }
                        }
                    }
                });
                Enqueue(new
                {
                    type = "control_request",
                    request_id = "mcp-control-1",
                    request = new
                    {
                        subtype = "mcp_message",
                        server_name = "darkspyre-inference-connect",
                        message = new
                        {
                            jsonrpc = "2.0",
                            id = 9,
                            method = "tools/call",
                            @params = new
                            {
                                name = "echo",
                                arguments = new { text = "hello" }
                            }
                        }
                    }
                });
            }
            else if (type == "control_response")
            {
                Enqueue(new
                {
                    type = "stream_event",
                    @event = new
                    {
                        type = "content_block_delta",
                        index = 0,
                        delta = new { type = "text_delta", text = "echo: hello" }
                    }
                });
                Enqueue(new
                {
                    type = "result",
                    subtype = "success",
                    is_error = false,
                    result = "echo: hello",
                    session_id = "session-1"
                });
            }

            return ValueTask.CompletedTask;
        }

        public async ValueTask<string?> ReadLineAsync(
            CancellationToken cancellationToken = default) =>
            await _output.Reader.ReadAsync(cancellationToken);

        public Task<ClaudeCliProcessResult> WaitForExitAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClaudeCliProcessResult(0, string.Empty, string.Empty));

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            _output.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private void Enqueue(object value) =>
            _output.Writer.TryWrite(JsonSerializer.Serialize(value));
    }
}
