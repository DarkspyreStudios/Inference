using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed partial class ClaudeAgentResourcesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("connect-claude-resources-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task UnmappedEffortIsDropped()
    {
        var process = new ManualProcess
        {
            InitializationPayload = JsonElement.Parse(
                """{"models":[{"value":"claude-test","displayName":"CLI model choice","supportsEffort":false}]}""")
        };
        LlmConnection connection = Connection() with
        { CapabilityOverrides = new() { ReasoningLevels = ClaudeCliLlmClient.ReasoningLevels } };
        await using ILlmAgentSession session = await Start(process, connection);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<LlmAgentEvent> feed = session.RunTurnAsync(new()
        {
            Input = [new LlmAgentTextContent("Reply with OK.")],
            ReasoningLevel = LlmReasoningLevel.ExtraHigh
        }, limit.Token).GetAsyncEnumerator(limit.Token);

        Assert.True(await feed.MoveNextAsync());
        string turnId = await process.WaitForUserMessage(limit.Token);
        process.Push(new { type = "user", uuid = turnId, isReplay = true });
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "OK", stop_reason = "end_turn" });
        List<LlmAgentEvent> events = await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);

        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.IsType<LlmAgentTurnCompletedEvent>(events[^1]).Reason);
        JsonElement settings = process.Requests().Last(request => request.GetProperty("subtype").GetString() == "apply_flag_settings")
            .GetProperty("settings");
        Assert.False(settings.TryGetProperty("effortLevel", out _));
    }

    [Theory]
    [InlineData(false, "low")]
    [InlineData(true, "medium")]
    public async Task NativeTurnMapsEffortForTheSelectedModelBeforeWritingInput(bool switchModel, string expected)
    {
        var process = new ManualProcess
        {
            InitializationPayload = JsonElement.Parse("""
                {"models":[{"value":"claude-test","displayName":"Initial","supportsEffort":true,"supportedEffortLevels":["low"]},
                           {"value":"claude-other","displayName":"Other","supportsEffort":true,"supportedEffortLevels":["medium"]}]}
                """)
        };
        await using ILlmAgentSession session = await Start(process);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = new LlmAgentTurnRequest
        {
            Input = [new LlmAgentTextContent("Reply with OK.")],
            ModelId = switchModel ? "claude-other" : null,
            ReasoningLevel = LlmReasoningLevel.Maximum
        };
        await using IAsyncEnumerator<LlmAgentEvent> feed = session.RunTurnAsync(request, limit.Token).GetAsyncEnumerator(limit.Token);

        Assert.True(await feed.MoveNextAsync());
        Assert.IsType<LlmAgentTurnStartedEvent>(feed.Current);
        string turnId = await process.WaitForUserMessage(limit.Token);
        process.Push(new { type = "user", uuid = turnId, isReplay = true });
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "OK", stop_reason = "end_turn" });
        await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);

        JsonElement settings = process.Writes().Last(item => item.GetProperty("type").GetString() == "control_request"
            && item.GetProperty("request").GetProperty("subtype").GetString() == "apply_flag_settings")
            .GetProperty("request").GetProperty("settings");
        Assert.Equal(expected, settings.GetProperty("effortLevel").GetString());
        Assert.Equal(LlmReasoningLevel.Maximum, request.ReasoningLevel);
        if (switchModel)
        {
            JsonElement model = Assert.Single(process.Writes(), item => item.GetProperty("type").GetString() == "control_request"
                && item.GetProperty("request").GetProperty("subtype").GetString() == "set_model");
            Assert.Equal("claude-other", model.GetProperty("request").GetProperty("model").GetString());
        }
    }

    [Fact]
    public async Task FailedNativeTerminalKeepsSafeErrorsWithoutPublishingDiagnosticTextAsAnAnswer()
    {
        var process = new ManualProcess();
        await using ILlmAgentSession session = await Client(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new() { WorkingDirectory = _directory }
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<LlmAgentEvent> feed = session.RunTurnAsync(
            new() { Input = [new LlmAgentTextContent("test")] }, limit.Token).GetAsyncEnumerator(limit.Token);
        Assert.True(await feed.MoveNextAsync());
        Assert.IsType<LlmAgentTurnStartedEvent>(feed.Current);
        string turnId = await process.WaitForUserMessage(limit.Token);
        process.Push(new { type = "user", uuid = turnId, isReplay = true });
        string[] errors = ["""{"access_token":"native-secret","detail":"The selected model is unavailable."}""", "Quota exceeded."];
        process.Push(new
        {
            type = "result",
            subtype = "error_during_execution",
            is_error = true,
            result = """{"refresh_token":"result-secret","detail":"Failed diagnostic, not an answer."}""",
            errors
        });

        List<LlmAgentEvent> events = await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);
        LlmAgentTurnCompletedEvent terminal = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
        Assert.Equal(LlmAgentCompletionReason.ProviderFailure, terminal.Reason);
        Assert.Contains("The selected model is unavailable.", terminal.Error!, StringComparison.Ordinal);
        Assert.Contains("Quota exceeded.", terminal.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("native-secret", terminal.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("result-secret", terminal.Error!, StringComparison.Ordinal);
        Assert.Empty(events.OfType<LlmAgentMessageCompletedEvent>());
        Assert.Empty(events.OfType<LlmAgentTextDeltaEvent>());
        Assert.False(terminal.IsContextOverflow);
    }

    // The CLI reports the refusal twice: as an assistant message marked with an error, then as the error result. Only the
    // result reaches the host, so the refusal is never shown as the model's reply. Preserved context forwards it unchanged.
    [Fact]
    public async Task APromptTooLongForTheWindowEndsTheTurnAsAContextOverflow()
    {
        var process = new ManualProcess();
        await using ILlmAgentSession session = await Client(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new() { WorkingDirectory = _directory },
            ExecutionOptions = new() { ContextPolicy = new() { PreserveSuppliedContext = true } }
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<LlmAgentEvent> feed = session.RunTurnAsync(
            new() { Input = [new LlmAgentTextContent("test")] }, limit.Token).GetAsyncEnumerator(limit.Token);
        Assert.True(await feed.MoveNextAsync());
        string turnId = await process.WaitForUserMessage(limit.Token);
        process.Push(new { type = "user", uuid = turnId, isReplay = true });
        process.Push(new
        {
            type = "assistant",
            error = "invalid_request",
            message = new
            {
                id = "synthetic",
                role = "assistant",
                model = "<synthetic>",
                content = new[] { new { type = "text", text = "Prompt is too long" } },
            },
        });
        process.Push(new
        {
            type = "result",
            subtype = "success",
            is_error = true,
            result = "Prompt is too long · the request is ~1053843 tokens (limit 1000000)",
        });

        List<LlmAgentEvent> events = await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);

        Assert.True(Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).IsContextOverflow);
        Assert.Empty(events.OfType<LlmAgentMessageCompletedEvent>());
    }

    [Fact]
    public async Task ReadOnlySessionsAllowRegisteredHostToolsWithoutEnteringPlanMode()
    {
        var factory = new Factory(new ManualProcess());
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new()
            {
                WorkingDirectory = _directory,
                Policy = new LlmAgentHostRuntimePolicy { Sandbox = LlmAgentSandboxMode.ReadOnly, ApprovalPolicy = LlmAgentApprovalPolicy.Never }
            },
            Tools = [new LlmAgentToolDefinition { Name = "echo", InputSchema = Json(new { type = "object" }) }],
            ToolHandler = (_, _) => Task.FromResult(LlmToolOutput.FromText("echo"))
        });
        IReadOnlyList<string> arguments = Assert.IsType<ClaudeCliSessionInvocation>(factory.Invocation).Arguments;
        Assert.Equal("dontAsk", arguments[arguments.ToList().IndexOf("--permission-mode") + 1]);
        Assert.Equal(string.Empty, arguments[arguments.ToList().IndexOf("--tools") + 1]);
        Assert.Equal("mcp__darkspyre-inference-connect__echo,mcp__darkspyre-inference-connect__update_plan", arguments[arguments.ToList().IndexOf("--allowedTools") + 1]);
        Assert.Contains("--restricted", arguments);
        Assert.DoesNotContain("--dangerously-skip-permissions", arguments);
    }

    [Fact]
    public async Task LaunchArgumentsAreIsolated()
    {
        var factory = new Factory(new ManualProcess());
        string other = Directory.CreateDirectory(Path.Combine(_directory, "other")).FullName;
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new()
            {
                WorkingDirectory = _directory,
                Policy = new LlmAgentHostRuntimePolicy
                {
                    Sandbox = LlmAgentSandboxMode.DangerFullAccess,
                    NetworkAllowed = true,
                    WritableRoots = [other],
                    AllowRuntimeBackgroundWork = true
                }
            },
            Tools = [new LlmAgentToolDefinition { Name = "echo", InputSchema = Json(new { type = "object" }) }],
            ToolHandler = (_, _) => Task.FromResult(LlmToolOutput.FromText("echo"))
        });

        List<string> arguments = [.. Assert.IsType<ClaudeCliSessionInvocation>(factory.Invocation).Arguments];
        int start = arguments.IndexOf(ClaudeCliConfiguration.IsolationArguments[0]);
        Assert.Equal(ClaudeCliConfiguration.IsolationArguments, arguments.Skip(start).Take(ClaudeCliConfiguration.IsolationArguments.Count));
        Assert.Equal("dontAsk", arguments[arguments.IndexOf("--permission-mode") + 1]);
        Assert.Equal("mcp__darkspyre-inference-connect__echo,mcp__darkspyre-inference-connect__update_plan", arguments[arguments.IndexOf("--allowedTools") + 1]);
        Assert.DoesNotContain("--add-dir", arguments);
        Assert.DoesNotContain("--dangerously-skip-permissions", arguments);
        Assert.DoesNotContain("--disallowedTools", arguments);
        Assert.DoesNotContain("--resume", arguments);
        Assert.DoesNotContain("--fork-session", arguments);
    }

    [Theory]
    [InlineData("result.json", false)]
    [InlineData("result.pdf", true)]
    public async Task NativeOpeningCarriesTheActualLogicalFilesWithoutTakingSelectionOwnership(string fileName, bool generatedByTool)
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var generated = new InferenceResourceReference("workspace", "generated");
        byte[] bytes = fileName.EndsWith(".pdf", StringComparison.Ordinal)
            ? "%PDF-1.7\nactual generated content"u8.ToArray() : "{\"answer\":42}"u8.ToArray();
        var access = new ResourcePreparationTests.Resources(new("workspace", "session", 4, [generated, reference]),
            [(reference, "reference.txt", "reference text"u8.ToArray()), (generated, fileName, bytes)]);
        var context = new LlmAgentContext
        {
            Revision = 9,
            ResourceSelectionVersion = new("workspace", "session", 4),
            SelectedResources = [generated, reference],
            Entries = [new("prompt", "old-turn", new("user", "Read the reference")
                { Attachments = [new LlmResourceAttachment(reference), new LlmResourceAttachment(reference)] }),
                new("answer", "old-turn", new("assistant", "Created " + fileName))]
        };
        if (generatedByTool)
            context = context with
            {
                Entries = [context.Entries[0], context.Entries[1] with
                { Message = context.Entries[1].Message with { ToolCalls = [new LlmToolCall("create-1", "create_file", "{}")] } },
                    new("tool", "old-turn", new("user", string.Empty)
                    { ToolResults = [new LlmToolResult("create-1", new() { Parts = [new LlmToolOutputSessionResource(generated)] })] })]
            };
        LlmAgentContext restored = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(context, ConnectLlmJson.AgentContext),
            ConnectLlmJson.AgentContext)!;
        var process = new ManualProcess();
        await using var providers = new LlmAgentProviderService(new ResourcePreparationTests.Source(Connection()), [Client(new Factory(process))]);
        await using ILlmAgentSession session = await providers.StartSessionAsync("connection", new()
        {
            ModelId = "claude-test",
            Context = restored,
            CallerServices = new() { Resources = access },
            Runtime = new() { WorkingDirectory = _directory }
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<LlmAgentEvent> feed = session.RunTurnAsync(
            new() { Input = [new LlmAgentTextContent("Compare the files")] }, limit.Token).GetAsyncEnumerator(limit.Token);
        Assert.True(await feed.MoveNextAsync());
        Assert.IsType<LlmAgentTurnStartedEvent>(feed.Current);
        string turnId = await process.WaitForUserMessage(limit.Token);
        JsonElement input = Assert.Single(process.Writes(), item => item.GetProperty("type").GetString() == "user");
        JsonElement[] documents = input.GetProperty("message").GetProperty("content").EnumerateArray()
            .Where(part => part.GetProperty("type").GetString() == "document").ToArray();
        Assert.Equal(["reference.txt", "reference.txt", fileName], documents.Select(part => part.GetProperty("title").GetString()));
        JsonElement source = documents[^1].GetProperty("source");
        Assert.Equal(bytes, source.GetProperty("type").GetString() == "base64"
            ? Convert.FromBase64String(source.GetProperty("data").GetString()!) : Encoding.UTF8.GetBytes(source.GetProperty("data").GetString()!));
        Assert.Equal(2, access.Reads.Count);
        Assert.All(access.Reads, stream => Assert.False(stream.CanRead));
        Assert.Equal(2, access.ReleaseAttempts);
        process.Push(new { type = "user", uuid = turnId, isReplay = true });
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "answer", stop_reason = "end_turn" });
        List<LlmAgentEvent> events = await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);
        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        await Assert.ThrowsAsync<NotSupportedException>(() => session.CaptureContextAsync());
        Assert.Equal([generated, reference], access.Selection.SelectedResources);
        Assert.Equal(4, access.Selection.Revision);
        Assert.Equal(9, restored.Revision);
        Assert.IsType<LlmResourceAttachment>(restored.Entries[0].Message.Attachments[0]);
    }

    /// <summary>A stored selection that differs from the caller's current one is replaced by it, and the process starts.</summary>
    [Fact]
    public async Task ClaudeOpeningAdoptsTheCurrentSelection()
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var stale = new InferenceResourceReference("workspace", "stale");
        var access = new ResourcePreparationTests.Resources(new("workspace", "session", 4, [reference]),
            [(reference, "reference.txt", "reference text"u8.ToArray())]);
        var factory = new Factory(new ManualProcess());
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Context = new() { ResourceSelectionVersion = new("workspace", "session", 3), SelectedResources = [stale] },
            CallerServices = new() { Resources = access },
            Runtime = new() { WorkingDirectory = _directory }
        });
        Assert.NotNull(factory.Invocation);
    }

    /// <summary>Claude offers no image generation, so a session that asks for it opens and runs without it.</summary>
    [Fact]
    public async Task ClaudeOpensWithTheImageToolAndRunsWithoutIt()
    {
        var process = new ManualProcess();
        await using ILlmAgentSession session = await Client(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            HostedTools = [new LlmImageGenerationTool()],
            Runtime = new() { WorkingDirectory = _directory }
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<LlmAgentEvent> feed = session.RunTurnAsync(new()
        {
            Input = [new LlmAgentTextContent("Reply with OK.")]
        }, limit.Token).GetAsyncEnumerator(limit.Token);

        Assert.True(await feed.MoveNextAsync());
        string turnId = await process.WaitForUserMessage(limit.Token);
        process.Push(new { type = "user", uuid = turnId, isReplay = true });
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "OK", stop_reason = "end_turn" });
        List<LlmAgentEvent> events = await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);

        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.IsType<LlmAgentTurnCompletedEvent>(events[^1]).Reason);
    }

    [Fact]
    public async Task NativeMcpMaterializesOnlyTheToolsNamedSelectedResourceAndKeepsItsLogicalResult()
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var generated = new InferenceResourceReference("workspace", "generated");
        var access = new ResourcePreparationTests.Resources(new("workspace", "session", 4, [generated, reference]),
            [(reference, "reference.txt", "reference text"u8.ToArray()), (generated, "result.json", "{\"answer\":42}"u8.ToArray())]);
        var process = new ManualProcess();
        await using ILlmAgentSession session = await Client(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new() { WorkingDirectory = _directory },
            CallerServices = new() { Resources = access },
            Tools = [new LlmAgentToolDefinition { Name = "hold", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }],
            ToolHandler = (_, _) => Task.FromResult(new LlmToolOutput { Parts = [new LlmToolOutputSessionResource(generated)] })
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<LlmAgentEvent> feed = await HostTurnAsync(session, process, limit.Token);
        // The turn's input carried the selection; the tool's result reads only the resource it names.
        int reads = access.Reads.Count;
        int releases = access.ReleaseAttempts;
        process.Push(ToolUse("tu1"));
        process.Push(ToolCall("c1", 7));
        JsonElement answer = await AnswerFor(process, "c1");
        JsonElement resource = Assert.Single(answer.GetProperty("result").GetProperty("content").EnumerateArray()).GetProperty("resource");
        Assert.Equal("application/json", resource.GetProperty("mimeType").GetString());
        Assert.Equal("{\"answer\":42}", resource.GetProperty("text").GetString());
        Assert.Equal(reads + 1, access.Reads.Count);
        Assert.False(access.Reads[^1].CanRead);
        Assert.Equal(releases + 1, access.ReleaseAttempts);
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "answer", stop_reason = "end_turn" });
        List<LlmAgentEvent> events = await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);
        LlmAgentToolEvent tool = Assert.Single(events.OfType<LlmAgentToolEvent>(), item => item.Output is not null);
        Assert.Equal(generated, Assert.IsType<LlmToolOutputSessionResource>(Assert.Single(tool.Output!.Parts)).Resource);
        Assert.Equal([generated, reference], access.Selection.SelectedResources);
    }

    [Fact]
    public async Task NativeMcpResourceTheRouteCannotCarryIsLeftOutWithANotice()
    {
        var generated = new InferenceResourceReference("workspace", "generated");
        var access = new ResourcePreparationTests.Resources(new("workspace", "session", 4, [generated]),
            [(generated, "result.pdf", "%PDF-1.7\nactual generated content"u8.ToArray())]);
        var process = new ManualProcess();
        await using ILlmAgentSession session = await Client(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new() { WorkingDirectory = _directory },
            CallerServices = new() { Resources = access },
            Tools = [new LlmAgentToolDefinition { Name = "hold", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }],
            ToolHandler = (_, _) => Task.FromResult(new LlmToolOutput { Parts = [new LlmToolOutputSessionResource(generated)] })
        });
        process.Push(ToolUse("tu1"));
        process.Push(ToolCall("c1", 7));

        JsonElement answer = await AnswerFor(process, "c1");
        string text = Assert.Single(answer.GetProperty("result").GetProperty("content").EnumerateArray()).GetProperty("text").GetString()!;
        Assert.Contains("This content was not supplied to the model.", text, StringComparison.Ordinal);
        Assert.Single(access.Reads);
        Assert.Equal(1, access.ReleaseAttempts);
    }

    [Fact]
    public async Task LongToolNameIsAliasedEverywhere()
    {
        string name = new('x', 130);
        string alias = LlmToolNames.Alias(name);
        var process = new ManualProcess();
        var factory = new Factory(process);
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new() { WorkingDirectory = _directory },
            Tools = [new LlmAgentToolDefinition { Name = name, InputSchema = Json(new { type = "object" }) }],
            ToolHandler = (call, _) => { seen.TrySetResult(call.Name); return Task.FromResult(LlmToolOutput.FromText("done")); }
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<LlmAgentEvent> feed = await HostTurnAsync(session, process, limit.Token);

        IReadOnlyList<string> arguments = factory.Invocation!.Arguments;
        Assert.Contains($"mcp__darkspyre-inference-connect__{alias}", arguments[arguments.ToList().IndexOf("--allowedTools") + 1].Split(','));
        JsonElement listed = process.Requests().Single(request => request.GetProperty("subtype").GetString() == "initialize")
            .GetProperty("sdkMcpServerManifests").GetProperty("darkspyre-inference-connect").GetProperty("toolsListResult").GetProperty("tools");
        Assert.Equal([alias, "update_plan"], listed.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()));

        process.Push(ToolUse("tu1", alias));
        process.Push(ToolCall("c1", 7, alias));
        Assert.Equal(name, await seen.Task.WaitAsync(limit.Token));
        Assert.False((await AnswerFor(process, "c1")).GetProperty("result").GetProperty("isError").GetBoolean());
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "" });
        List<LlmAgentEvent> events = await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);
        Assert.NotEmpty(events.OfType<LlmAgentToolEvent>());
        Assert.All(events.OfType<LlmAgentToolEvent>(), item => Assert.Equal(name, item.Tool));
    }

    [Fact]
    public async Task BridgeValidatesArgumentsBeforeHandler()
    {
        var process = new ManualProcess();
        var received = new List<JsonElement>();
        await using ILlmAgentSession session = await Client(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new() { WorkingDirectory = _directory },
            Tools = [new LlmAgentToolDefinition
            {
                Name = "count",
                InputSchema = JsonElement.Parse("""{"type":"object","properties":{"n":{"type":"integer"}},"required":["n"],"additionalProperties":false}""")
            }],
            ToolHandler = (call, _) => { lock (received) received.Add(call.Arguments); return Task.FromResult(LlmToolOutput.FromText("counted")); }
        });
        // The MCP tool list carries the schema without additionalProperties; the bridge checks the declared one.
        JsonElement listed = process.Requests().Single(request => request.GetProperty("subtype").GetString() == "initialize")
            .GetProperty("sdkMcpServerManifests").GetProperty("darkspyre-inference-connect").GetProperty("toolsListResult").GetProperty("tools");
        JsonElement schema = listed.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "count").GetProperty("inputSchema");
        Assert.False(schema.TryGetProperty("additionalProperties", out _));
        Assert.True(schema.GetProperty("properties").TryGetProperty("n", out _));

        process.Push(ToolUse("tu1", "count"));
        process.Push(ToolCall("c1", 1, "count", new { n = "many" }));
        JsonElement rejected = (await AnswerFor(process, "c1")).GetProperty("result");
        Assert.True(rejected.GetProperty("isError").GetBoolean());
        string text = rejected.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.StartsWith("Tool 'count' arguments were rejected:\n", text, StringComparison.Ordinal);
        Assert.Contains("\n/n: ", text, StringComparison.Ordinal);
        Assert.Empty(received);

        process.Push(ToolCall("c2", 2, "count", new { n = 3, extra = true }));
        Assert.False((await AnswerFor(process, "c2")).GetProperty("result").GetProperty("isError").GetBoolean());
        JsonElement arguments = Assert.Single(received);
        Assert.Equal(3, arguments.GetProperty("n").GetInt32());
        Assert.True(arguments.GetProperty("extra").GetBoolean());
    }

    [Fact]
    public async Task BridgeMatchesOneOfBranchesWhoseArgumentsCarryAnotherBranchsMembers()
    {
        // The search and read forms of a tool such as file_search: each branch is closed, and the CLI shows the model one
        // merged object, so a search can carry a read-only member.
        var process = new ManualProcess();
        var received = new List<JsonElement>();
        await using ILlmAgentSession session = await Client(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new() { WorkingDirectory = _directory },
            Tools = [new LlmAgentToolDefinition
            {
                Name = "lookup",
                InputSchema = JsonElement.Parse("""
                    {"type":"object","oneOf":[
                     {"type":"object","additionalProperties":false,"required":["operation","query"],
                      "properties":{"operation":{"const":"search"},"query":{"type":"string"}}},
                     {"type":"object","additionalProperties":false,"required":["operation","reference"],
                      "properties":{"operation":{"const":"read"},"reference":{"type":"object","additionalProperties":false,
                       "required":["id"],"properties":{"id":{"type":"string"}}},"maxBytes":{"type":"integer"}}}]}
                    """)
            }],
            ToolHandler = (call, _) => { lock (received) received.Add(call.Arguments); return Task.FromResult(LlmToolOutput.FromText("found")); }
        });

        process.Push(ToolUse("tu1", "lookup"));
        process.Push(ToolCall("c1", 1, "lookup", new { operation = "search", query = "pangolin", maxBytes = 4096 }));
        Assert.False((await AnswerFor(process, "c1")).GetProperty("result").GetProperty("isError").GetBoolean());
        process.Push(ToolCall("c2", 2, "lookup", new { operation = "read", reference = new { id = "a" } }));
        Assert.False((await AnswerFor(process, "c2")).GetProperty("result").GetProperty("isError").GetBoolean());
        process.Push(ToolCall("c3", 3, "lookup", new { operation = "read", query = "pangolin" }));
        JsonElement mixed = (await AnswerFor(process, "c3")).GetProperty("result");
        Assert.True(mixed.GetProperty("isError").GetBoolean());
        Assert.StartsWith("Tool 'lookup' arguments were rejected:\n", mixed.GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal(["search", "read"], received.Select(arguments => arguments.GetProperty("operation").GetString()));
    }

    [Fact]
    public async Task UpdatePlanRunsInsideConnectOnClaude()
    {
        var process = new ManualProcess();
        int hostCalls = 0;
        await using ILlmAgentSession session = await Client(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new() { WorkingDirectory = _directory },
            ToolHandler = (_, _) => { Interlocked.Increment(ref hostCalls); return Task.FromResult(LlmToolOutput.FromText("host")); }
        });

        process.Push(ToolUse("tu1", "update_plan"));
        process.Push(ToolCall("c1", 1, "update_plan", new { plan = new[] { new { step = "look", status = "in_progress" } } }));
        JsonElement answer = await AnswerFor(process, "c1");
        Assert.Equal("Plan updated.", answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(0, hostCalls);
    }

    [Theory]
    [InlineData("BASE")]
    [InlineData(null)]
    public async Task InitializeSendsInstructionsAsSystemPromptOnlyWhenPresent(string? instructions)
    {
        var process = new ManualProcess();
        await using ILlmAgentSession session = await Client(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Instructions = instructions,
            Runtime = new() { WorkingDirectory = _directory }
        });

        JsonElement initialize = process.Requests().Single(request => request.GetProperty("subtype").GetString() == "initialize");
        bool present = initialize.TryGetProperty("systemPrompt", out JsonElement systemPrompt);
        if (instructions is null)
            Assert.False(present);
        else
            Assert.Equal([instructions], systemPrompt.EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task SequentialToolsRunOneAtATime()
    {
        var process = new ManualProcess();
        int running = 0;
        int overlapped = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        await using ILlmAgentSession session = await Client(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new() { WorkingDirectory = _directory },
            Tools = [new LlmAgentToolDefinition { Name = "hold", InputSchema = Json(new { type = "object" }), RequiresSequentialExecution = true }],
            ToolHandler = async (_, token) =>
            {
                if (Interlocked.Increment(ref running) > 1) Interlocked.Increment(ref overlapped);
                if (Interlocked.Increment(ref calls) == 1) { first.TrySetResult(); await release.Task.WaitAsync(token); }
                Interlocked.Decrement(ref running);
                return LlmToolOutput.FromText("done");
            }
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        process.Push(ToolUse("tu1"));
        process.Push(ToolCall("c1", 1));
        await first.Task.WaitAsync(limit.Token);
        process.Push(ToolCall("c2", 2));
        await Task.Delay(100, limit.Token);
        Assert.Equal(1, Volatile.Read(ref calls));
        release.TrySetResult();
        await AnswerFor(process, "c1");
        await AnswerFor(process, "c2");
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Equal(0, overlapped);
    }

    [Fact]
    public async Task UsageReportsTheModel()
    {
        var process = new ManualProcess();
        await using ILlmAgentSession session = await Start(process);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<LlmAgentEvent> feed = session.RunTurnAsync(
            new() { Input = [new LlmAgentTextContent("Reply with OK.")] }, limit.Token).GetAsyncEnumerator(limit.Token);

        Assert.True(await feed.MoveNextAsync());
        string turnId = await process.WaitForUserMessage(limit.Token);
        process.Push(new { type = "user", uuid = turnId, isReplay = true });
        process.Push(new
        {
            type = "assistant",
            message = new { id = "m1", role = "assistant", model = "claude-answering", content = new[] { new { type = "text", text = "OK" } } }
        });
        process.Push(new
        {
            type = "result",
            subtype = "success",
            is_error = false,
            result = "OK",
            stop_reason = "end_turn",
            usage = new { input_tokens = 10, output_tokens = 2 }
        });
        List<LlmAgentEvent> events = await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);

        LlmAgentUsageEvent usage = Assert.Single(events.OfType<LlmAgentUsageEvent>());
        Assert.Equal("claude-answering", usage.ModelId);
    }

    [Fact]
    public async Task ApiRetryReportsAttemptDelayAndError()
    {
        var process = new ManualProcess();
        await using ILlmAgentSession session = await Start(process);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<LlmAgentEvent> feed = session.RunTurnAsync(
            new() { Input = [new LlmAgentTextContent("Reply with OK.")] }, limit.Token).GetAsyncEnumerator(limit.Token);

        Assert.True(await feed.MoveNextAsync());
        string turnId = await process.WaitForUserMessage(limit.Token);
        process.Push(new { type = "user", uuid = turnId, isReplay = true });
        process.Push(new { type = "system", subtype = "api_retry", attempt = 2, max_retries = 10, retry_delay_ms = 1500, error_status = 529, error = "overloaded" });
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "OK", stop_reason = "end_turn" });
        List<LlmAgentEvent> events = await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);

        LlmAgentResponseRecoveryEvent recovery = Assert.Single(events.OfType<LlmAgentResponseRecoveryEvent>());
        Assert.Equal(2, recovery.NextAttempt);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), recovery.Delay);
        Assert.Equal("529: overloaded", recovery.Error);
        Assert.Empty(events.OfType<LlmAgentWarningEvent>());
    }

    [Fact]
    public async Task TurnCapResultEndsWithItsReason()
    {
        var process = new ManualProcess();
        await using ILlmAgentSession session = await Start(process);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<LlmAgentEvent> feed = await HostTurnAsync(session, process, limit.Token);

        string[] errors = ["Reached maximum number of turns (3)"];
        process.Push(new { type = "result", subtype = "error_max_turns", is_error = true, errors });
        List<LlmAgentEvent> events = await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);

        LlmAgentTurnCompletedEvent turns = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
        Assert.Equal(LlmAgentCompletionReason.IterationLimit, turns.Reason);
        Assert.Equal("Reached maximum number of turns (3)", turns.Error);
    }

    [Fact]
    public async Task AHostToolCallRunsOffTheReaderAndTheCliCanCancelIt()
    {
        var process = new ManualProcess();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ILlmAgentSession session = await StartWithTool(process, async (_, token) =>
        {
            entered.TrySetResult(token);
            await Task.Delay(Timeout.Infinite, token);
            return LlmToolOutput.FromText("unreachable");
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        process.Push(ToolUse("tu1"));
        process.Push(ToolCall("c1", 7));
        CancellationToken token = await entered.Task.WaitAsync(limit.Token);

        Assert.False(token.IsCancellationRequested);

        process.Push(Cancelled("c2", 7));
        await WaitFor(() => token.IsCancellationRequested);
        JsonElement answer = await AnswerFor(process, "c1");
        Assert.Equal(7, answer.GetProperty("id").GetInt32());
        Assert.True(answer.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("The tool call was cancelled.", answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Object, (await AnswerFor(process, "c2")).GetProperty("result").ValueKind);
        AssertMcpWire(process, "c1", "{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"The tool call was cancelled.\"}],\"isError\":true}}");
        AssertMcpWire(process, "c2", "{\"jsonrpc\":\"2.0\",\"id\":0,\"result\":{}}");
    }

    [Fact]
    public async Task ReturnRequestsInterruptsAndReturnsCalls()
    {
        var process = new ManualProcess();
        var factory = new Factory(process);
        int handled = 0;
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new() { WorkingDirectory = _directory },
            Tools = [new LlmAgentToolDefinition
            {
                Name = "count",
                InputSchema = JsonElement.Parse("""{"type":"object","properties":{"n":{"type":"integer"}},"required":["n"]}""")
            }],
            ToolHandling = LlmAgentToolHandling.ReturnRequests,
            ToolHandler = (_, _) => { Interlocked.Increment(ref handled); return Task.FromResult(LlmToolOutput.FromText("counted")); }
        });
        Assert.Contains("--no-session-persistence", factory.Invocation!.Arguments);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<LlmAgentEvent> feed = await HostTurnAsync(session, process, limit.Token);

        process.Push(new
        {
            type = "assistant",
            message = new
            {
                id = "m1",
                content = new object[]
                {
                    new { type = "text", text = "Counting." },
                    new { type = "tool_use", id = "tu1", name = "mcp__darkspyre-inference-connect__count", input = new { n = 1 } },
                    new { type = "tool_use", id = "tu2", name = "mcp__darkspyre-inference-connect__count", input = new { n = "many" } }
                }
            }
        });
        process.Push(ToolCall("c1", 1, "count", new { n = 1 }));
        List<LlmAgentEvent> events = await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);

        Assert.Equal(LlmAgentCompletionReason.ToolRequests, Assert.IsType<LlmAgentTurnCompletedEvent>(events[^1]).Reason);
        LlmAgentToolEvent[] returned = [.. events.OfType<LlmAgentToolEvent>().Where(item => item.Status != "inProgress")];
        Assert.Equal(["tu1", "tu2"], returned.Select(item => item.ItemId));
        Assert.Equal(("returned", (bool?)null), (returned[0].Status, returned[0].Success));
        Assert.Equal(("failed", (bool?)false), (returned[1].Status, returned[1].Success));
        Assert.StartsWith("Tool 'count' arguments were rejected:", Assert.IsType<LlmToolOutputText>(Assert.Single(returned[1].Output!.Parts)).Text,
            StringComparison.Ordinal);
        Assert.All(events, item => Assert.Equal(events[0].TurnId, item.TurnId));
        Assert.Equal(0, handled);
        Assert.DoesNotContain(process.Writes(), root => root.GetProperty("type").GetString() == "control_response"
            && root.GetProperty("response").GetProperty("request_id").GetString() == "c1");
        Assert.Contains(process.Requests(), request => request.GetProperty("subtype").GetString() == "interrupt");
        Assert.Equal(1, process.Disposals);
    }

    [Fact]
    public async Task OpeningOverTriggerIsCompactedBeforeWrite()
    {
        // A history that fits is sent whole.
        var fits = new ManualProcess();
        await using (ILlmAgentSession session = await Client(new Factory(fits)).StartSessionAsync("connection", Connection(),
            Options(History(("t1", "first question", "first answer")), Window(4000))))
        {
            List<LlmAgentEvent> events = await CompleteTurnAsync(session, fits);
            Assert.Empty(events.OfType<LlmAgentCompactedEvent>());
            Assert.Contains("first question", UserText(fits), StringComparison.Ordinal);
        }

        // A switched history over the trigger is compacted before the first write, and the unused CLI is closed.
        var first = new ManualProcess();
        var second = new ManualProcess();
        var summaries = new Summaries();
        await using ILlmAgentSession compacted = await Client(new Factory(first, second), summaries).StartSessionAsync("connection",
            Connection(), Options(History(("t1", Words("alpha", 4000), "first answer"), ("t2", "second question", "second answer")), Window(4000)));
        List<LlmAgentEvent> turn = await CompleteTurnAsync(compacted, second);

        Assert.Equal(2, Assert.Single(turn.OfType<LlmAgentCompactedEvent>()).DroppedMessageCount);
        Assert.NotEmpty(summaries.Invocations);
        Assert.DoesNotContain(first.Writes(), item => item.GetProperty("type").GetString() == "user");
        Assert.Equal(1, first.Disposals);
        string sent = UserText(second);
        Assert.Contains("summary of the earlier work", sent, StringComparison.Ordinal);
        Assert.Contains("second question", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha alpha", sent, StringComparison.Ordinal);
        Assert.All(turn, item => Assert.Equal(turn[0].TurnId, item.TurnId));
    }

    [Fact]
    public async Task MidTurnTriggerReopensWithoutRepeatingTools()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int calls = 0;
        async Task<(ILlmAgentSession Session, IAsyncEnumerator<LlmAgentEvent> Feed, List<LlmAgentEvent> Events)> RunToTriggerAsync(
            ManualProcess first, Factory factory)
        {
            ILlmAgentSession session = await Client(factory).StartSessionAsync("connection", Connection(), Options(null, Window(4000)) with
            {
                Tools = [new LlmAgentToolDefinition { Name = "fetch", InputSchema = Json(new { type = "object" }) }],
                ToolHandler = (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(LlmToolOutput.FromText(Words("beta", 4000))); }
            });
            IAsyncEnumerator<LlmAgentEvent> feed = await HostTurnAsync(session, first, limit.Token);
            List<LlmAgentEvent> events = [feed.Current];
            first.Push(ToolUse("tu1", "fetch"));
            first.Push(new { type = "stream_event", @event = new { type = "message_stop" } });
            first.Push(ToolCall("c1", 1, "fetch"));
            return (session, feed, events);
        }

        // The settled step measures over the trigger: the result stays unwritten and a new CLI continues from the
        // compacted history, which holds the call and its shortened result.
        var first = new ManualProcess();
        var second = new ManualProcess();
        var (session, feed, events) = await RunToTriggerAsync(first, new Factory(first, second));
        await using (session)
        await using (feed)
        {
            string continued = await second.WaitForUserMessage(limit.Token);
            string sent = UserText(second);
            Assert.Contains("Continue from where you stopped.", sent, StringComparison.Ordinal);
            Assert.Contains("removed by compaction.]", sent, StringComparison.Ordinal);
            second.Push(new { type = "user", uuid = continued, isReplay = true });
            second.Push(new { type = "assistant", message = new { id = "m2", content = new[] { new { type = "text", text = "done" } } } });
            second.Push(new { type = "result", subtype = "success", is_error = false, result = "done", stop_reason = "end_turn" });
            events.AddRange(await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent));

            Assert.Equal(LlmAgentCompletionReason.Completed, Assert.IsType<LlmAgentTurnCompletedEvent>(events[^1]).Reason);
            LlmAgentCompactedEvent compacted = Assert.Single(events.OfType<LlmAgentCompactedEvent>());
            Assert.Equal(1, compacted.ShortenedToolOutputCount);
            Assert.Equal(1, calls);
            Assert.DoesNotContain(first.Writes(), root => root.GetProperty("type").GetString() == "control_response"
                && root.GetProperty("response").GetProperty("request_id").GetString() == "c1");
            Assert.Equal(1, first.Disposals);
            Assert.All(events, item => Assert.Equal(events[0].TurnId, item.TurnId));
        }
    }

    [Fact]
    public async Task RefusalReopensFromSavedHistoryOnce()
    {
        const string TooLong = "Prompt is too long · the request is ~1200 tokens (limit 1000)";
        LlmAgentSessionOptions options = Options(History(("t1", Words("alpha", 1000), "first answer")),
            new() { ContextPolicy = new() { ReservedOutputTokens = 100 } });

        // The first refusal is compacted and sent once more from the saved history in a new CLI session.
        var first = new ManualProcess();
        var second = new ManualProcess();
        var factory = new Factory(first, second);
        await using (ILlmAgentSession session = await Client(factory).StartSessionAsync("connection", Connection(), options))
        {
            List<LlmAgentEvent> events = await RefuseThenAnswerAsync(session, first, second, refuseAgain: false);

            LlmAgentTurnCompletedEvent completed = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
            Assert.Equal(LlmAgentCompletionReason.Completed, completed.Reason);
            Assert.False(completed.IsContextOverflow);
            Assert.Equal(2, Assert.Single(events.OfType<LlmAgentCompactedEvent>()).DroppedMessageCount);
            Assert.Equal(1, first.Disposals);
            string sent = UserText(second);
            Assert.Contains("hello", sent, StringComparison.Ordinal);
            Assert.Contains("summary of the earlier work", sent, StringComparison.Ordinal);
            Assert.DoesNotContain("alpha alpha", sent, StringComparison.Ordinal);
            IReadOnlyList<ClaudeCliSessionInvocation> started = factory.Invocations;
            Assert.Equal(2, started.Count);
            Assert.All(started, invocation => Assert.DoesNotContain("--resume", invocation.Arguments));
            Assert.NotEqual(started[0].Arguments.Single(item => item.StartsWith("--session-id=", StringComparison.Ordinal)),
                started[1].Arguments.Single(item => item.StartsWith("--session-id=", StringComparison.Ordinal)));
        }

        // A second refusal in the turn ends it with the provider's reason, and nothing is retried.
        first = new ManualProcess();
        second = new ManualProcess();
        factory = new Factory(first, second);
        await using (ILlmAgentSession session = await Client(factory).StartSessionAsync("connection", Connection(), options))
        {
            List<LlmAgentEvent> events = await RefuseThenAnswerAsync(session, first, second, refuseAgain: true);

            LlmAgentTurnCompletedEvent completed = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
            Assert.Equal(LlmAgentCompletionReason.ProviderFailure, completed.Reason);
            Assert.True(completed.IsContextOverflow);
            Assert.Equal(2, factory.Invocations.Count);
        }

        async Task<List<LlmAgentEvent>> RefuseThenAnswerAsync(ILlmAgentSession session, ManualProcess refused, ManualProcess next,
            bool refuseAgain)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using IAsyncEnumerator<LlmAgentEvent> feed = await HostTurnAsync(session, refused, limit.Token, "hello");
            List<LlmAgentEvent> events = [feed.Current];
            refused.Push(new { type = "result", subtype = "success", is_error = true, result = TooLong });
            string resent = await next.WaitForUserMessage(limit.Token);
            next.Push(new { type = "user", uuid = resent, isReplay = true });
            next.Push(refuseAgain
                ? new { type = "result", subtype = "success", is_error = true, result = TooLong }
                : new { type = "result", subtype = "success", is_error = false, result = "answer" });
            events.AddRange(await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent));
            return events;
        }
    }

    [Fact]
    public async Task CompactNowReopensFromTheCompactedHistory()
    {
        var first = new ManualProcess();
        var second = new ManualProcess();
        var factory = new Factory(first, second);
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("connection", Connection(),
            Options(History(("t1", Words("alpha", 2000), "first answer"), ("t2", "second question", "second answer")), Window(4000)));

        LlmAgentCompactedEvent? compacted = await session.CompactNowAsync();

        Assert.NotNull(compacted);
        Assert.Equal(2, compacted.DroppedMessageCount);
        Assert.Equal("t2", compacted.TurnId);
        // The host keeps its own copy of a Claude conversation and replaces it with this context.
        LlmAgentContext carried = Assert.IsType<LlmAgentContext>(compacted.Context);
        Assert.Equal(compacted.ContextRevision, carried.Revision);
        Assert.Equal([ConversationCompactor.SummaryTurnId, "t2", "t2"], carried.Entries.Select(entry => entry.TurnId));
        Assert.Equal(1, first.Disposals);
        Assert.Equal(2, factory.Invocations.Count);
        await CompleteTurnAsync(session, second);
        string sent = UserText(second);
        Assert.Contains("summary of the earlier work", sent, StringComparison.Ordinal);
        Assert.Contains("second question", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha alpha", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextStatusReportsUsedAndTotal()
    {
        var process = new ManualProcess();
        await using ILlmAgentSession session = await Client(new Factory(process)).StartSessionAsync("connection", Connection(),
            Options(History(("t1", "first question", "first answer")), null) with { ModelId = "claude-sonnet-4-5" });

        LlmAgentContextStatus status = await session.GetContextStatusAsync();
        LlmAgentContextStatus prospective = await session.GetContextStatusAsync(new() { Input = [new LlmAgentTextContent(Words("gamma", 100))] });

        Assert.Equal(200_000, status.MaxContextTokens);
        Assert.True(status.UsedContextTokens > 0);
        Assert.False(status.NeedsCompaction);
        Assert.Equal(LlmContextMeasurementSource.Estimated, status.Source);
        Assert.True(prospective.UsedContextTokens >= status.UsedContextTokens + 100);
    }

    [Fact]
    public async Task ASteerDuringAToolCallJoinsTheSameTurn()
    {
        var process = new ManualProcess();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ILlmAgentSession session = await StartWithTool(process, async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return LlmToolOutput.FromText("held");
        });
        Assert.Equal(InferenceSupport.Supported, (await session.GetCapabilitiesAsync()).SupportsSteering);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<LlmAgentEvent> feed = await HostTurnAsync(session, process, limit.Token);
        List<LlmAgentEvent> events = [feed.Current];
        string turnId = feed.Current.TurnId!;
        string hostInput = await process.WaitForUserMessage(limit.Token);
        process.Push(ToolUse("tu1"));
        process.Push(ToolCall("c1", 1));
        await entered.Task.WaitAsync(limit.Token);

        await session.SteerAsync(turnId, [new LlmAgentTextContent("Also end with BANANA.")], limit.Token);

        string steerId = await process.WaitForUserMessage(limit.Token, index: 1);
        JsonElement steer = process.Writes().Single(root => root.GetProperty("type").GetString() == "user"
            && root.GetProperty("uuid").GetString() == steerId);
        Assert.Equal("next", steer.GetProperty("priority").GetString());
        Assert.Equal("human", steer.GetProperty("origin").GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, steer.GetProperty("parent_tool_use_id").ValueKind);
        Assert.Equal("Also end with BANANA.", steer.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());
        // The CLI folds the steer into the running turn once the tool result arrives: its echo starts no host turn.
        process.Push(Lifecycle(steerId, "queued"));
        release.TrySetResult();
        await AnswerFor(process, "c1");
        process.Push(new { type = "user", uuid = steerId, isReplay = true, origin = new { kind = "human" } });
        process.Push(Lifecycle(steerId, "started"));
        process.Push(new { type = "assistant", message = new { id = "m2", content = new[] { new { type = "text", text = "DONE BANANA" } } } });
        process.Push(Lifecycle(steerId, "completed"));
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "DONE BANANA", stop_reason = "end_turn", queued_turn_count = 0 });
        process.Push(Lifecycle(hostInput, "completed"));
        events.AddRange(await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent));

        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        Assert.Equal(["DONE BANANA"], events.OfType<LlmAgentMessageCompletedEvent>().Select(item => item.Text));
        Assert.All(events, item => Assert.Equal(turnId, item.TurnId));
        Assert.Empty(events.OfType<LlmAgentWarningEvent>());
    }

    [Fact]
    public async Task ASteerDuringTextContinuesInTheTurnTheCliStartsForIt()
    {
        var process = new ManualProcess();
        await using ILlmAgentSession session = await Start(process);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<LlmAgentEvent> feed = await HostTurnAsync(session, process, limit.Token);
        List<LlmAgentEvent> events = [feed.Current];
        string turnId = feed.Current.TurnId!;
        string hostInput = await process.WaitForUserMessage(limit.Token);
        process.Push(new { type = "assistant", message = new { id = "m1", content = new[] { new { type = "text", text = "first reply" } } } });

        await session.SteerAsync(turnId, [new LlmAgentTextContent("Also end with BANANA.")], limit.Token);
        string steerId = await process.WaitForUserMessage(limit.Token, index: 1);
        process.Push(Lifecycle(steerId, "queued"));
        // The steer came too late for this reply: the CLI ends its turn, then starts one of its own for the steer.
        process.Push(Result("first reply", queued: 1, input: 10, output: 5));
        process.Push(Lifecycle(hostInput, "completed"));
        process.Push(Lifecycle(steerId, "started"));
        process.Push(new { type = "system", subtype = "init" });
        process.Push(new { type = "user", uuid = steerId, isReplay = true, origin = new { kind = "human" } });
        process.Push(new { type = "assistant", message = new { id = "m2", content = new[] { new { type = "text", text = "second reply BANANA" } } } });
        process.Push(Result("second reply BANANA", queued: 0, input: 20, output: 7));
        process.Push(Lifecycle(steerId, "completed"));
        events.AddRange(await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent));

        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        Assert.Equal(["first reply", "second reply BANANA"], events.OfType<LlmAgentMessageCompletedEvent>().Select(item => item.Text));
        Assert.All(events, item => Assert.Equal(turnId, item.TurnId));
        LlmAgentUsageEvent[] usage = [.. events.OfType<LlmAgentUsageEvent>()];
        Assert.Equal(usage.Length, usage.Select(item => item.RequestId).Distinct().Count());
        Assert.Equal((30, 12), (usage[^1].RunTotals!.InputTokens, usage[^1].RunTotals!.OutputTokens));

        // The next host turn runs on its own: the steer's echo and turn did not take its place.
        await feed.DisposeAsync();
        List<LlmAgentEvent> next = await CompleteTurnAsync(session, process, index: 2);
        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.IsType<LlmAgentTurnCompletedEvent>(next[^1]).Reason);
        Assert.NotEqual(turnId, next[0].TurnId);
    }

    [Fact]
    public async Task AnInterruptWithdrawsAQueuedSteer()
    {
        var process = new ManualProcess();
        await using ILlmAgentSession session = await Start(process);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<LlmAgentEvent> feed = await HostTurnAsync(session, process, limit.Token);
        List<LlmAgentEvent> events = [feed.Current];
        string turnId = feed.Current.TurnId!;
        await session.SteerAsync(turnId, [new LlmAgentTextContent("Also end with BANANA.")], limit.Token);
        string steerId = await process.WaitForUserMessage(limit.Token, index: 1);
        process.Push(Lifecycle(steerId, "queued"));

        await session.InterruptAsync(turnId, limit.Token);
        events.AddRange(await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent));

        Assert.Equal(LlmAgentCompletionReason.Interruption, Assert.IsType<LlmAgentTurnCompletedEvent>(events[^1]).Reason);
        string[] requests = [.. process.Requests().Select(request => request.GetProperty("subtype").GetString()!)];
        int withdrawn = Array.FindIndex(requests, subtype => subtype == "cancel_async_message");
        Assert.True(withdrawn >= 0 && withdrawn < Array.IndexOf(requests, "interrupt"));
        Assert.Equal(steerId, process.Requests().First(request => request.GetProperty("subtype").GetString() == "cancel_async_message")
            .GetProperty("message_uuid").GetString());
        // The interrupted turn ends at its result even though the CLI still counted the steer.
        process.Push(Lifecycle(steerId, "cancelled"));
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "", terminal_reason = "aborted_streaming", queued_turn_count = 1 });
        await feed.DisposeAsync();
        List<LlmAgentEvent> next = await CompleteTurnAsync(session, process, index: 2);
        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.IsType<LlmAgentTurnCompletedEvent>(next[^1]).Reason);
        Assert.Empty(next.OfType<LlmAgentWarningEvent>());
    }

    [Fact]
    public async Task ASteerTheClosingCliNeverStartedReachesTheReopenedCli()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new ManualProcess();
        var second = new ManualProcess();
        await using ILlmAgentSession session = await Client(new Factory(first, second)).StartSessionAsync("connection", Connection(),
            Options(null, Window(4000)) with
            {
                Tools = [new LlmAgentToolDefinition { Name = "fetch", InputSchema = Json(new { type = "object" }) }],
                ToolHandler = async (_, token) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return LlmToolOutput.FromText(Words("beta", 4000));
                }
            });
        await using IAsyncEnumerator<LlmAgentEvent> feed = await HostTurnAsync(session, first, limit.Token);
        string turnId = feed.Current.TurnId!;
        first.Push(ToolUse("tu1", "fetch"));
        first.Push(new { type = "stream_event", @event = new { type = "message_stop" } });
        first.Push(ToolCall("c1", 1, "fetch"));
        await entered.Task.WaitAsync(limit.Token);
        await session.SteerAsync(turnId, [new LlmAgentTextContent("Also end with BANANA.")], limit.Token);
        await first.WaitForUserMessage(limit.Token, index: 1);

        // The settled step measures over the trigger, so the result is never written and the steer never starts.
        release.TrySetResult();
        await second.WaitForUserMessage(limit.Token);

        Assert.Contains("Also end with BANANA.", UserText(second), StringComparison.Ordinal);
    }

    private static object Lifecycle(string commandId, string state) => new { type = "command_lifecycle", command_uuid = commandId, state };

    private static object Result(string text, int queued, int input, int output) => new
    {
        type = "result",
        subtype = "success",
        is_error = false,
        result = text,
        stop_reason = "end_turn",
        queued_turn_count = queued,
        usage = new { input_tokens = input, output_tokens = output }
    };

    private LlmAgentSessionOptions Options(LlmAgentContext? context, LlmAgentExecutionOptions? execution) => new()
    {
        ModelId = "claude-test",
        Runtime = new() { WorkingDirectory = _directory },
        Context = context,
        ExecutionOptions = execution
    };

    /// <summary>A window of <paramref name="tokens"/> with a 100-token output reserve.</summary>
    private static LlmAgentExecutionOptions Window(long tokens) =>
        new() { ContextPolicy = new() { MaxContextTokens = tokens, ReservedOutputTokens = 100 } };

    private static LlmAgentContext History(params (string TurnId, string User, string Assistant)[] turns) => new()
    {
        Entries = [.. turns.SelectMany(turn => new LlmAgentContextEntry[]
        {
            new($"{turn.TurnId}-user", turn.TurnId, new("user", turn.User)),
            new($"{turn.TurnId}-assistant", turn.TurnId, new("assistant", turn.Assistant))
        })]
    };

    private static string Words(string word, int count) => string.Concat(Enumerable.Repeat(word + " ", count));

    /// <summary>The text the CLI received in its first user message.</summary>
    private static string UserText(ManualProcess process) => string.Join("\n", process.Writes()
        .First(item => item.GetProperty("type").GetString() == "user").GetProperty("message").GetProperty("content")
        .EnumerateArray().Where(part => part.GetProperty("type").GetString() == "text").Select(part => part.GetProperty("text").GetString()));

    /// <summary>Runs one turn the CLI answers with "done".</summary>
    private static async Task<List<LlmAgentEvent>> CompleteTurnAsync(ILlmAgentSession session, ManualProcess process, int index = 0)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<LlmAgentEvent> feed = await HostTurnAsync(session, process, limit.Token, index: index);
        List<LlmAgentEvent> events = [feed.Current];
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "done", stop_reason = "end_turn" });
        events.AddRange(await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent));
        return events;
    }

    /// <summary>Starts a host turn and echoes its input, so the CLI output that follows belongs to it.</summary>
    private static async Task<IAsyncEnumerator<LlmAgentEvent>> HostTurnAsync(ILlmAgentSession session, ManualProcess process,
        CancellationToken token, string input = "go", int index = 0)
    {
        IAsyncEnumerator<LlmAgentEvent> feed = session.RunTurnAsync(new() { Input = [new LlmAgentTextContent(input)] }, token)
            .GetAsyncEnumerator(token);
        Assert.True(await feed.MoveNextAsync());
        Assert.IsType<LlmAgentTurnStartedEvent>(feed.Current);
        string turnId = await process.WaitForUserMessage(token, index);
        process.Push(new { type = "user", uuid = turnId, isReplay = true });
        return feed;
    }

    private Task<ILlmAgentSession> Start(ManualProcess process, LlmConnection? connection = null) =>
        Client(new Factory(process)).StartSessionAsync("connection", connection ?? Connection(), new() { ModelId = "claude-test", Runtime = new() { WorkingDirectory = _directory } });

    private Task<ILlmAgentSession> StartWithTool(ManualProcess process, LlmAgentToolHandler handler) =>
        Client(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new() { WorkingDirectory = _directory },
            Tools = [new LlmAgentToolDefinition { Name = "hold", Description = "Holds.", InputSchema = Json(new { type = "object" }) }],
            ToolHandler = handler,
        });

    private static object ToolUse(string id, string name = "hold") => new
    {
        type = "assistant",
        message = new { id = "m-" + id, content = new object[] { new { type = "tool_use", id, name = "mcp__darkspyre-inference-connect__" + name, input = new { } } } },
    };

    private static object ToolCall(string requestId, object id, string name = "hold", object? arguments = null) => new
    {
        type = "control_request",
        request_id = requestId,
        request = new { subtype = "mcp_message", server_name = "darkspyre-inference-connect", message = new { method = "tools/call", @params = new { name, arguments = arguments ?? new { } }, jsonrpc = "2.0", id } },
    };

    private static object Cancelled(string requestId, object id) => new
    {
        type = "control_request",
        request_id = requestId,
        request = new { subtype = "mcp_message", server_name = "darkspyre-inference-connect", message = new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = id, reason = "AbortError: remote-cancel" } } },
    };

    /// <summary>The MCP message Connect answered a control request with.</summary>
    private static async Task<JsonElement> AnswerFor(ManualProcess process, string requestId)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            if (process.Writes().FirstOrDefault(root => root.GetProperty("type").GetString() == "control_response"
                && root.GetProperty("response").GetProperty("request_id").GetString() == requestId) is { ValueKind: JsonValueKind.Object } answer)
            {
                return answer.GetProperty("response").GetProperty("response").GetProperty("mcp_response");
            }
            await Task.Delay(10, limit.Token);
        }
    }

    private static LlmConnection Connection() => new() { Version = "v1", Transport = LlmTransport.AnthropicClaudeCli, Endpoint = "claude" };

    private static void AssertMcpWire(ManualProcess process, string controlRequestId, string message)
    {
        string wire = Assert.Single(process.RawWrites(), line =>
        {
            JsonElement root = JsonElement.Parse(line);
            return root.GetProperty("type").GetString() == "control_response"
                && root.GetProperty("response").GetProperty("request_id").GetString() == controlRequestId;
        });
        string id = JsonElement.Parse(wire).GetProperty("response").GetProperty("request_id").GetRawText();
        Assert.Equal("{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":" + id + ",\"response\":{\"mcp_response\":" + message + "}}}", wire);
        Assert.DoesNotContain('\n', wire);
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static async Task WaitFor(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { await Task.Delay(10, limit.Token); }
    }

    private static async Task<List<LlmAgentEvent>> ReadUntil(IAsyncEnumerator<LlmAgentEvent> feed, Func<LlmAgentEvent, bool> stop)
    {
        var events = new List<LlmAgentEvent>();
        while (await feed.MoveNextAsync())
        {
            events.Add(feed.Current);
            if (stop(feed.Current)) { return events; }
        }
        return events;
    }

    private static readonly HttpClient Http = new();

    private static ClaudeAgentClient Client(IClaudeCliSessionProcessFactory factory, Summaries? summaries = null) =>
        new(factory, new ClaudeCliLlmClient(Http, summaries ?? new Summaries(), factory), new LearnedModelContext(),
            NullLogger<ClaudeAgentClient>.Instance);

    /// <summary>Starts the given processes in order; the last one serves every later start.</summary>
    private sealed class Factory(params IClaudeCliSessionProcess[] processes) : IClaudeCliSessionProcessFactory
    {
        private readonly List<ClaudeCliSessionInvocation> _invocations = [];

        public ClaudeCliSessionInvocation? Invocation { get { lock (_invocations) return _invocations.Count == 0 ? null : _invocations[^1]; } }

        public IReadOnlyList<ClaudeCliSessionInvocation> Invocations { get { lock (_invocations) return [.. _invocations]; } }

        public Task<IClaudeCliSessionProcess> StartAsync(ClaudeCliSessionInvocation invocation, CancellationToken cancellationToken = default)
        {
            int index;
            lock (_invocations)
            {
                index = _invocations.Count;
                _invocations.Add(invocation);
            }
            return Task.FromResult(processes[Math.Min(index, processes.Length - 1)]);
        }
    }

    /// <summary>The print-mode CLI behind summary side requests: every request returns the same summary.</summary>
    private sealed class Summaries(string text = "summary of the earlier work") : IClaudeCliRunner
    {
        public ConcurrentQueue<ClaudeCliInvocation> Invocations { get; } = new();

        public Task<ClaudeCliProcessResult> RunAsync(ClaudeCliInvocation invocation, CancellationToken cancellationToken = default)
        {
            Invocations.Enqueue(invocation);
            return Task.FromResult(new ClaudeCliProcessResult(0, JsonSerializer.Serialize(new
            {
                type = "result",
                subtype = "success",
                is_error = false,
                result = text,
                stop_reason = "end_turn",
                usage = new { input_tokens = 7, output_tokens = 3 }
            }), string.Empty));
        }
    }

    /// <summary>A CLI whose output the test pushes. Control requests other than interrupt are answered with success.</summary>
    private sealed class ManualProcess : IClaudeCliSessionProcess
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        private readonly ConcurrentQueue<string> _writes = new();

        public JsonElement? InitializationPayload { get; init; }
        public int Disposals { get; private set; }

        public void Push(object value) => _output.Writer.TryWrite(JsonSerializer.Serialize(value));

        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            _writes.Enqueue(line);
            if (root.GetProperty("type").GetString() == "control_request"
                && root.GetProperty("request").GetProperty("subtype").GetString() != "interrupt")
            {
                JsonElement payload = root.GetProperty("request").GetProperty("subtype").GetString() == "initialize"
                    && InitializationPayload is { } initialization ? initialization : JsonElement.Parse("{}");
                _output.Writer.TryWrite(new JsonObject
                {
                    ["type"] = "control_response",
                    ["response"] = new JsonObject
                    {
                        ["subtype"] = "success",
                        ["request_id"] = root.GetProperty("request_id").GetString(),
                        ["response"] = JsonNode.Parse(payload.GetRawText())
                    }
                }.ToJsonString());
            }
            return ValueTask.CompletedTask;
        }

        public IEnumerable<JsonElement> Writes() => _writes.Select(line => JsonSerializer.Deserialize<JsonElement>(line));

        public string[] RawWrites() => [.. _writes];

        public IEnumerable<JsonElement> Requests() => _writes
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(root => root.GetProperty("type").GetString() == "control_request")
            .Select(root => root.GetProperty("request"));

        public async Task<string> WaitForUserMessage(CancellationToken cancellationToken, int index = 0)
        {
            while (true)
            {
                if (_writes.Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                    .Where(root => root.GetProperty("type").GetString() == "user").Skip(index)
                    .FirstOrDefault() is { ValueKind: JsonValueKind.Object } user)
                {
                    return user.GetProperty("uuid").GetString()!;
                }
                await Task.Delay(10, cancellationToken);
            }
        }

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _output.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public Task<ClaudeCliProcessResult> WaitForExitAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClaudeCliProcessResult(0, string.Empty, string.Empty));

        public ValueTask DisposeAsync()
        {
            Disposals++;
            _output.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
