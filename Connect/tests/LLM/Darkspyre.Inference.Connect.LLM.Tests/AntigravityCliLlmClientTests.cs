using Darkspyre.Inference.Connect.LLM.Internal;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AntigravityCliLlmClientTests
{
    private const string ErrorDetail = """{"access_token":"diagnostic-secret","detail":"The selected model is unavailable in this region."}""";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TerminalFailurePreservesSanitizedProviderDetail(bool streaming)
    {
        var payload = new JsonObject { ["status"] = "ERROR", ["response"] = string.Empty, ["error"] = ErrorDetail };
        string output = streaming
            ? new JsonObject { ["event"] = "result", ["result"] = payload }.ToJsonString()
            : payload.ToJsonString();
        var client = new AntigravityCliLlmClient(new RecordingRunner(new(0, output, string.Empty)));
        LlmConnection connection = Connection() with { CapabilityOverrides = new() { SupportsStreaming = streaming } };

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() => ReadAllAsync(
            client.StreamAsync(connection, new() { ModelId = "auto", Messages = [new("user", "test")] })));

        Assert.Equal("antigravity_cli_error", failure.ProviderCode);
        Assert.Contains("The selected model is unavailable in this region.", failure.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic-secret", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeTerminalFailurePreservesSanitizedProviderDetail()
    {
        var process = new TerminalProcess();
        await using ILlmAgentSession session = await AntigravityAgentSession.OpenAsync(process, "agy",
            Environment.CurrentDirectory, new("auto", null, null), null, Connection(), CancellationToken.None);

        IReadOnlyList<LlmAgentEvent> events = await ReadAllAsync(session.RunTurnAsync(
            new() { Input = [new LlmAgentTextContent("test")] }));
        LlmAgentTurnCompletedEvent completed = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());

        Assert.Equal(LlmAgentCompletionReason.ProviderFailure, completed.Reason);
        Assert.Contains("The selected model is unavailable in this region.", completed.Error!, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", completed.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic-secret", completed.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude-sonnet-4-6")]
    [InlineData("claude-opus-4-6-thinking")]
    public async Task ClaudeModelRejectsEffortBeforeInvocationButAllowsDefault(string model)
    {
        var runner = new RecordingRunner(new(0,
            """{"event":"result","result":{"status":"SUCCESS","response":"OK"}}""", string.Empty));
        var client = new AntigravityCliLlmClient(runner);
        LlmConnection connection = Connection() with
        {
            CapabilityOverrides = new()
            {
                ReasoningLevels = LlmReasoningEffort.Levels(LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High)
            }
        };
        var request = new LlmChatRequest
        {
            ModelId = model,
            Messages = [new("user", "Reply with OK.")],
            ReasoningLevel = LlmReasoningLevel.Medium
        };

        InferenceException failure = await Assert.ThrowsAsync<InferenceException>(() =>
            ReadAllAsync(client.StreamAsync(connection, request)));
        Assert.Equal(InferenceFailureCode.InvalidRequest, failure.Code);
        Assert.Empty(runner.Invocations);
        Assert.Empty(client.GetCapabilities(connection, model).ReasoningLevels);

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(connection,
            request with { ReasoningLevel = null }));
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("OK", result.Text);
        AntigravityCliInvocation invocation = Assert.Single(runner.Invocations);
        Assert.Equal(model, invocation.Arguments[invocation.Arguments.ToList().IndexOf("--model") + 1]);
        Assert.DoesNotContain("--effort", invocation.Arguments);
    }

    [Fact]
    public async Task TerminalResultFencesTrailingTextAndStillRequiresSuccessfulProcessExit()
    {
        const string output = """
            {"event":"result","result":{"status":"SUCCESS","response":"OK","usage":{"input_tokens":12,"output_tokens":2,"total_tokens":14}}}
            {"event":"step_update","step_update":{"step_index":1,"state":"DONE","step_type":"agent_response","text_delta":"STALE","usage":{"input_tokens":90,"output_tokens":9,"total_tokens":99}}}
            """;
        var request = new LlmChatRequest { ModelId = "auto", Messages = [new("user", "Reply with OK.")] };
        var client = new AntigravityCliLlmClient(new RecordingRunner(new(0, output, string.Empty)));

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(Connection(), request));
        Assert.Equal("OK", string.Concat(events.OfType<LlmTextDeltaEvent>().Select(item => item.Delta)));
        Assert.Empty(events.OfType<LlmUsageDeltaEvent>());
        Assert.Single(events.OfType<LlmActivityEvent>());
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("OK", result.Text);
        Assert.Equal(new LlmUsage(12, 2, 14), result.Usage);

        var failedClient = new AntigravityCliLlmClient(new RecordingRunner(new(1, output, "process failure")));
        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() =>
            ReadAllAsync(failedClient.StreamAsync(Connection(), request)));
        Assert.Equal("antigravity_cli_exit_1", failure.ProviderCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PinnedModelRejectsConflictingEffortBeforeInvocationAndAcceptsItsOwnLevel(bool streaming)
    {
        const string model = "gemini-3.8-flash-low";
        string output = streaming
            ? "{\"event\":\"result\",\"result\":{\"status\":\"SUCCESS\",\"response\":\"OK\"}}"
            : "{\"status\":\"SUCCESS\",\"response\":\"OK\"}";
        var runner = new RecordingRunner(new(0, output, string.Empty));
        var client = new AntigravityCliLlmClient(runner);
        LlmConnection connection = Connection() with
        {
            CapabilityOverrides = new()
            {
                SupportsStreaming = streaming,
                ReasoningLevels = LlmReasoningEffort.Levels(LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High)
            }
        };
        var request = new LlmChatRequest
        {
            ModelId = model,
            Messages = [new("user", "Reply with OK.")],
            ReasoningLevel = LlmReasoningLevel.Medium
        };

        InferenceException failure = await Assert.ThrowsAsync<InferenceException>(() => ReadAllAsync(client.StreamAsync(connection, request)));
        Assert.Equal(InferenceFailureCode.InvalidRequest, failure.Code);
        Assert.Empty(runner.Invocations);
        Assert.Equal(LlmReasoningLevel.Low, Assert.Single(client.GetCapabilities(connection, model).ReasoningLevels));

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(connection,
            request with { ReasoningLevel = LlmReasoningLevel.Low }));
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("OK", result.Text);
        AntigravityCliInvocation invocation = Assert.Single(runner.Invocations);
        Assert.Equal(model, invocation.Arguments[invocation.Arguments.ToList().IndexOf("--model") + 1]);
        Assert.Equal("low", invocation.Arguments[invocation.Arguments.ToList().IndexOf("--effort") + 1]);
    }

    [Fact]
    public async Task ToolHistoryReachesThePromptAsLabelledBlocks()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(
            0,
            """
            {"event":"init","conversation_id":"conversation-1","init":{}}
            {"event":"result","result":{"conversation_id":"conversation-1","status":"SUCCESS","response":"522,000","usage":{"input_tokens":12,"output_tokens":2,"total_tokens":14}}}
            """,
            string.Empty));
        var client = new AntigravityCliLlmClient(runner);

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(
            Connection(), new LlmChatRequest { ModelId = "auto", Messages = ToolHistory() }));

        Assert.Equal("522,000", Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Text);
        string input = Assert.Single(runner.Invocations).StandardInput!;
        Assert.Contains("[TOOL CALL id=c1 name=lookup]", input, StringComparison.Ordinal);
        Assert.Contains("[TOOL RESULT id=c1 status=ok]", input, StringComparison.Ordinal);
    }

    private static LlmChatMessage[] ToolHistory() =>
    [
        new LlmChatMessage("user", "Population of Lyon?"),
        new LlmChatMessage("assistant", string.Empty) { ToolCalls = [new LlmToolCall("c1", "lookup", """{"city":"Lyon"}""")] },
        new LlmChatMessage("user", string.Empty) { ToolResults = [new LlmToolResult("c1", LlmToolOutput.FromText("522,250"))] },
        new LlmChatMessage("user", "Round it."),
    ];

    private static LlmConnection Connection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.GoogleAntigravityCli,
        Endpoint = "agy"
    };

    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        var results = new List<T>();
        await foreach (T item in source)
        {
            results.Add(item);
        }
        return results;
    }

    private sealed class RecordingRunner(AntigravityCliProcessResult result) : IAntigravityCliRunner
    {
        public List<AntigravityCliInvocation> Invocations { get; } = [];

        public string? SchemaContent { get; private set; }

        public Task<AntigravityCliProcessResult> RunAsync(
            AntigravityCliInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(invocation);
            int schemaIndex = invocation.Arguments.ToList().IndexOf("--json-schema");
            if (schemaIndex >= 0)
            {
                SchemaContent = File.ReadAllText(invocation.Arguments[schemaIndex + 1]);
            }
            return Task.FromResult(result);
        }
    }

    private sealed class TerminalProcess : IAntigravityCliSessionProcessFactory, IAntigravityCliSessionProcess
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        public IAntigravityCliSessionProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
        {
            _lines.Writer.TryWrite("""{"event":"init","conversation_id":"conversation-1"}""");
            return this;
        }

        public ValueTask WriteAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _lines.Writer.TryWrite(new JsonObject
            {
                ["event"] = "result",
                ["result"] = new JsonObject
                {
                    ["conversation_id"] = "conversation-1",
                    ["status"] = "ERROR",
                    ["response"] = string.Empty,
                    ["error"] = ErrorDetail
                }
            }.ToJsonString());
            return ValueTask.CompletedTask;
        }

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default) =>
            await _lines.Reader.WaitToReadAsync(cancellationToken) ? await _lines.Reader.ReadAsync(cancellationToken) : null;

        public void CloseInput() => _lines.Writer.TryComplete();

        public Task<(int ExitCode, string StandardError)> WaitForExitAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((0, string.Empty));

        public Task KillAsync()
        {
            CloseInput();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            CloseInput();
            return ValueTask.CompletedTask;
        }
    }

}
