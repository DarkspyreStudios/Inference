using Darkspyre.Inference.Connect.LLM.Internal;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
    /// <summary>A schema whose root is not an object is checked locally on every route, as the universal contract requires.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task TrueSchemaIsCheckedLocally(bool streaming, bool local)
    {
        const string payload = """{"status":"SUCCESS","response":"17","structured_output":17}""";
        var runner = new RecordingRunner(new(0, streaming ? "{\"event\":\"result\",\"result\":" + payload + "}" : payload, string.Empty));
        var client = new AntigravityCliLlmClient(runner);
        LlmConnection connection = Connection() with
        {
            CapabilityOverrides = new()
            {
                SupportsStreaming = streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported,
                SupportsJsonSchema = local ? InferenceSupport.Unsupported : InferenceSupport.Unknown
            }
        };
        var request = new LlmChatRequest
        {
            ModelId = "auto", Messages = [new("user", "Return 17 as JSON.")],
            StructuredOutput = new()
            {
                Name = "answer", Schema = JsonElement.Parse("true")
            }
        };

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(connection, request));

        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal("17", result.Text);
        Assert.True(result.StructuredOutput!.WasEvaluated);
        Assert.True(result.StructuredOutput.IsValid);
        Assert.Equal(LlmStructuredOutputEnforcement.LocalValidationOnly, result.StructuredOutput.Enforcement);
        AntigravityCliInvocation invocation = Assert.Single(runner.Invocations);
        Assert.DoesNotContain("--json-schema", invocation.Arguments);
        Assert.Null(runner.SchemaContent);
        string prompt = streaming ? invocation.StandardInput! : invocation.Arguments[invocation.Arguments.ToList().IndexOf("-p") + 1];
        Assert.Contains("true", prompt, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.True, request.StructuredOutput.Schema.ValueKind);
    }

    private const string ErrorDetail = """{"access_token":"diagnostic-secret","detail":"The selected model is unavailable in this region."}""";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task AccountUsageFailureKeepsTheSafeTerminalDetailWithoutExposingNestedSecrets(int exitCode)
    {
        const string detail = """{"access_token":"usage-secret","detail":"Quota report is unavailable for this account."}""";
        var runner = new RecordingRunner(new(exitCode,
            new JsonObject { ["status"] = "ERROR", ["response"] = string.Empty, ["error"] = detail }.ToJsonString(), string.Empty));
        var client = new AntigravityCliLlmClient(runner);

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() => client.GetAccountUsageAsync(Connection()));

        Assert.Equal(exitCode == 0 ? "antigravity_cli_usage_error" : "antigravity_cli_exit_1", failure.ProviderCode);
        Assert.Contains("Quota report is unavailable for this account.", failure.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("usage-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("usage-secret", failure.SanitizedResponseBody ?? string.Empty, StringComparison.Ordinal);
        AntigravityCliInvocation invocation = Assert.Single(runner.Invocations);
        Assert.Contains("--print=/usage", invocation.Arguments);
    }

    [Fact]
    public async Task AccountUsagePreservesCliTablePercentagesAndResetTimes()
    {
        const string summary = "Gemini Models\tWeekly Limit Remaining\t88%\t2026-10-14T09:21:58Z\n"
            + "Claude and GPT models\tWeekly Limit Remaining\t71%\t2026-10-14T11:03:44Z";
        var runner = new RecordingRunner(new(0,
            new JsonObject { ["status"] = "SUCCESS", ["response"] = summary }.ToJsonString(), string.Empty));
        var client = new AntigravityCliLlmClient(runner);

        LlmAccountUsageSnapshot result = await client.GetAccountUsageAsync(Connection());

        Assert.Equal(summary, result.Summary);
        Assert.Collection(result.Quotas,
            quota =>
            {
                Assert.Equal("Gemini Models", quota.DisplayName);
                Assert.Equal(0.88d, quota.RemainingFraction);
                Assert.Equal(0.12d, quota.UsedFraction);
                Assert.Equal(new DateTimeOffset(2026, 10, 14, 9, 21, 58, TimeSpan.Zero), quota.ResetsAt);
                Assert.Equal(LlmQuotaState.Available, quota.State);
            },
            quota =>
            {
                Assert.Equal("Claude and GPT models", quota.DisplayName);
                Assert.Equal(0.71d, quota.RemainingFraction);
                Assert.Equal(0.29d, quota.UsedFraction);
                Assert.Equal(new DateTimeOffset(2026, 10, 14, 11, 3, 44, TimeSpan.Zero), quota.ResetsAt);
                Assert.Equal(LlmQuotaState.Available, quota.State);
            });
    }

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
        LlmConnection connection = Connection() with { CapabilityOverrides = new() { SupportsStreaming = (streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported) } };

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
            Environment.CurrentDirectory, new("auto", null, null), null, Connection(), null, null, CancellationToken.None);

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
    public async Task ClaudeModelDropsEffortItHasNoControlFor(string model)
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

        Assert.Empty(client.GetCapabilities(connection, model).ReasoningLevels);

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(connection, request));
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
    public async Task PinnedModelMapsDesiredEffortWithoutChangingTheSelectedModel(bool streaming)
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
                SupportsStreaming = (streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported),
                ReasoningLevels = LlmReasoningEffort.Levels(LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High)
            }
        };
        var request = new LlmChatRequest
        {
            ModelId = model,
            Messages = [new("user", "Reply with OK.")],
            ReasoningLevel = LlmReasoningLevel.Medium
        };

        Assert.Equal(LlmReasoningLevel.Low, Assert.Single(client.GetCapabilities(connection, model).ReasoningLevels));

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(connection, request));
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("OK", result.Text);
        AntigravityCliInvocation invocation = Assert.Single(runner.Invocations);
        Assert.Equal(model, invocation.Arguments[invocation.Arguments.ToList().IndexOf("--model") + 1]);
        Assert.Equal("low", invocation.Arguments[invocation.Arguments.ToList().IndexOf("--effort") + 1]);
        Assert.Equal(LlmReasoningLevel.Medium, request.ReasoningLevel);
    }

    [Theory]
    [InlineData(true, LlmReasoningLevel.Maximum, "max")]
    [InlineData(false, LlmReasoningLevel.ExtraHigh, "xhigh")]
    [InlineData(true, LlmReasoningLevel.None, "low")]
    [InlineData(false, LlmReasoningLevel.Minimal, "low")]
    public async Task UnprofiledChatEffortUsesTheExistingNativeCliVocabulary(bool streaming, LlmReasoningLevel effort, string word)
    {
        string output = streaming
            ? """{"event":"result","result":{"status":"SUCCESS","response":"OK"}}"""
            : """{"status":"SUCCESS","response":"OK"}""";
        var runner = new RecordingRunner(new(0, output, string.Empty));
        var client = new AntigravityCliLlmClient(runner);
        LlmConnection connection = Connection() with
        { CapabilityOverrides = new() { SupportsStreaming = streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported } };
        var request = new LlmChatRequest
        { ModelId = "custom-model", Messages = [new("user", "answer")], ReasoningLevel = effort };

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(connection, request));

        Assert.Equal("OK", Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Text);
        AntigravityCliInvocation invocation = Assert.Single(runner.Invocations);
        Assert.Equal(word, invocation.Arguments[invocation.Arguments.ToList().IndexOf("--effort") + 1]);
        Assert.Equal(effort, request.ReasoningLevel);
    }

    [Theory]
    [InlineData("custom-model", LlmReasoningLevel.Maximum, "max")]
    [InlineData("gemini-3.8-flash-low", LlmReasoningLevel.Maximum, "low")]
    [InlineData("gemini-3.8-flash-high", LlmReasoningLevel.None, "high")]
    public async Task NativeTurnUsesTheSameModelMappingBeforeReopening(string model, LlmReasoningLevel effort, string word)
    {
        var process = new TerminalProcess();
        await using ILlmAgentSession session = await AntigravityAgentSession.OpenAsync(process, "agy",
            Environment.CurrentDirectory, new("auto", null, null), null, Connection(), null, null, CancellationToken.None);
        var request = new LlmAgentTurnRequest { ModelId = model, ReasoningLevel = effort, Input = [new LlmAgentTextContent("answer")] };

        IReadOnlyList<LlmAgentEvent> events = await ReadAllAsync(session.RunTurnAsync(request));

        IReadOnlyList<string> arguments = process.Invocations[1];
        Assert.Equal(model, arguments[arguments.ToList().IndexOf("--model") + 1]);
        Assert.Equal(word, arguments[arguments.ToList().IndexOf("--effort") + 1]);
        Assert.Equal(effort, request.ReasoningLevel);
        Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
    }

    [Fact]
    public async Task ResumeReopensTheProviderConversationThroughTheFacade()
    {
        var process = new TerminalProcess();
        await using var providers = new LlmAgentProviderService(new ResourcePreparationTests.Source(Connection()),
            [new AntigravityAgentClient(process)]);

        await using ILlmAgentSession session = await providers.ResumeSessionAsync("connection", "conversation-1",
            new() { Runtime = ProviderRuntime() });

        Assert.Equal("conversation-1", session.ThreadId);
        IReadOnlyList<string> arguments = Assert.Single(process.Invocations);
        Assert.Equal("conversation-1", arguments[arguments.ToList().IndexOf("--conversation") + 1]);
        Assert.Equal(InferenceSupport.Supported, (await session.GetCapabilitiesAsync()).SupportsNativeResume);
    }

    [Fact]
    public async Task FirstTurnCarriesSessionInstructionsAndOpeningHistoryOnce()
    {
        var process = new TerminalProcess();
        await using ILlmAgentSession session = await new AntigravityAgentClient(process).StartSessionAsync("connection",
            Connection(), new()
            {
                ModelId = "auto",
                Runtime = ProviderRuntime(),
                Instructions = "Answer in French.",
                Context = new() { Entries = [new("e1", "t0", new("user", "My name is Ada."))] }
            });

        IReadOnlyList<LlmAgentEvent> first = await ReadAllAsync(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("Hello")] }));
        await ReadAllAsync(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("Again")] }));

        Assert.Equal(2, process.Writes.Count);
        Assert.Contains("Answer in French.", process.Writes[0], StringComparison.Ordinal);
        Assert.Contains(AgentOpeningContext.Header, process.Writes[0], StringComparison.Ordinal);
        Assert.Contains("My name is Ada.", process.Writes[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Answer in French.", process.Writes[1], StringComparison.Ordinal);
        Assert.DoesNotContain(AgentOpeningContext.Header, process.Writes[1], StringComparison.Ordinal);
        Assert.Single(first.OfType<LlmAgentWarningEvent>());
    }

    /// <summary>
    /// A fork or a cut history cannot reopen the provider conversation, so a start rebuilds it from the host's history.
    /// Every entry reaches the first turn as text; content the CLI cannot take is named, never refused.
    /// </summary>
    [Fact]
    public async Task StartRebuildsAConversationFromAnyHostHistory()
    {
        var process = new TerminalProcess();
        var image = new LlmAttachment { Kind = LlmBinaryKind.Image, FileName = "chart.png", MediaType = "image/png", Data = new byte[] { 1 } };
        var resource = new LlmResourceAttachment(new Darkspyre.Inference.Common.Resources.InferenceResourceReference("workspace", "notes"));
        await using ILlmAgentSession session = await new AntigravityAgentClient(process).StartSessionAsync("connection",
            Connection(), new()
            {
                ModelId = "auto",
                Runtime = ProviderRuntime(),
                Context = new()
                {
                    Entries =
                    [
                        new("e1", "t1", new("user", "Population of Lyon?") { Attachments = [image, resource] }),
                        new("e2", "t1", new("assistant", string.Empty) { ToolCalls = [new LlmToolCall("c1", "lookup", """{"city":"Lyon"}""")] }),
                        new("e3", "t1", new("user", string.Empty) { ToolResults = [new LlmToolResult("c1", LlmToolOutput.FromText("522,250"))] }),
                        new("e4", "t1", new("assistant", "About 522,000."))
                    ]
                }
            });

        IReadOnlyList<LlmAgentEvent> events = await ReadAllAsync(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("Round it.")] }));

        Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
        string written = Assert.Single(process.Writes);
        Assert.Contains(AgentOpeningContext.Header, written, StringComparison.Ordinal);
        Assert.Contains("Population of Lyon?", written, StringComparison.Ordinal);
        Assert.Contains("[Attachment 0 (chart.png): The selected model does not support this attachment.", written, StringComparison.Ordinal);
        Assert.Contains("[Attachment 1 (attachment): No resource access was supplied.", written, StringComparison.Ordinal);
        Assert.Contains("[TOOL CALL id=c1 name=lookup]", written, StringComparison.Ordinal);
        Assert.Contains("522,250", written, StringComparison.Ordinal);
        Assert.Contains("About 522,000.", written, StringComparison.Ordinal);
        string content = JsonNode.Parse(written)!["message"]!["content"]!.GetValue<string>();
        Assert.EndsWith("Round it.", content, StringComparison.Ordinal);
        Assert.DoesNotContain("--conversation", Assert.Single(process.Invocations));
    }

    [Fact]
    public async Task TurnInstructionsAndUncarriedAttachmentsReachThePromptAsText()
    {
        var process = new TerminalProcess();
        await using ILlmAgentSession session = await AntigravityAgentSession.OpenAsync(process, "agy",
            Environment.CurrentDirectory, new("auto", null, null), null, Connection(), null, null, CancellationToken.None);
        var image = new LlmAttachment { Kind = LlmBinaryKind.Image, FileName = "chart.png", MediaType = "image/png", Data = new byte[] { 1 } };

        await ReadAllAsync(session.RunTurnAsync(new()
        {
            Instructions = "Be brief.", Input = [new LlmAgentTextContent("Look")], Attachments = [image]
        }));

        string written = Assert.Single(process.Writes);
        Assert.Contains("Instructions for this turn:", written, StringComparison.Ordinal);
        Assert.Contains("[Attachment 0 (chart.png): The selected model does not support this attachment. "
            + "This content was not supplied to the model.]", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyTurnCompletesWithoutWritingToTheCli()
    {
        var process = new TerminalProcess();
        await using ILlmAgentSession session = await AntigravityAgentSession.OpenAsync(process, "agy",
            Environment.CurrentDirectory, new("auto", null, null), null, Connection(), null, null, CancellationToken.None);

        IReadOnlyList<LlmAgentEvent> events = await ReadAllAsync(session.RunTurnAsync(new()));

        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        Assert.Empty(process.Writes);
    }

    private static LlmAgentNativeRuntimeOptions ProviderRuntime() => new()
    {
        WorkingDirectory = Environment.CurrentDirectory,
        Policy = new LlmAgentProviderRuntimePolicy()
    };

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
        // Every process the factory starts records its input here.
        private readonly List<string> _writes;

        public TerminalProcess() : this([]) { }

        private TerminalProcess(List<string> writes) => _writes = writes;

        public List<IReadOnlyList<string>> Invocations { get; } = [];

        public List<string> Writes => _writes;

        public IAntigravityCliSessionProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
        {
            Invocations.Add(arguments);
            var process = new TerminalProcess(_writes);
            process._lines.Writer.TryWrite("""{"event":"init","conversation_id":"conversation-1"}""");
            return process;
        }

        public ValueTask WriteAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _writes.Add(text);
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
