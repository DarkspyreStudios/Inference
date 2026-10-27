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

public sealed class AntigravityCliLlmClientTests : IDisposable
{
    private const string ErrorDetail = """{"access_token":"diagnostic-secret","detail":"The selected model is unavailable in this region."}""";

    private readonly TestDataFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task AccountUsageFailureKeepsTheSafeTerminalDetailWithoutExposingNestedSecrets()
    {
        const string detail = """{"access_token":"usage-secret","detail":"Quota report is unavailable for this account."}""";
        var runner = new RecordingRunner(new(0,
            new JsonObject { ["status"] = "ERROR", ["response"] = string.Empty, ["error"] = detail }.ToJsonString(), string.Empty));
        var client = new AntigravityCliLlmClient(runner, _folder);

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() => client.GetAccountUsageAsync(Connection()));

        Assert.Equal("antigravity_cli_usage_error", failure.ProviderCode);
        Assert.Contains("Quota report is unavailable for this account.", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("usage-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("usage-secret", failure.SanitizedResponseBody ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccountUsagePreservesCliTablePercentagesAndResetTimes()
    {
        const string summary = "Gemini Models\tWeekly Limit Remaining\t88%\t2026-10-14T09:21:58Z\n"
            + "Claude and GPT models\tWeekly Limit Remaining\t71%\t2026-10-14T11:03:44Z";
        var runner = new RecordingRunner(new(0,
            new JsonObject { ["status"] = "SUCCESS", ["response"] = summary }.ToJsonString(), string.Empty));
        var client = new AntigravityCliLlmClient(runner, _folder);

        LlmAccountUsageSnapshot result = await client.GetAccountUsageAsync(Connection());

        Assert.Equal(summary, result.Summary);
        Assert.Collection(result.Quotas,
            quota =>
            {
                Assert.Equal("Gemini Models", quota.DisplayName);
                Assert.Equal(0.88d, quota.RemainingFraction);
                Assert.Equal(new DateTimeOffset(2026, 10, 14, 9, 21, 58, TimeSpan.Zero), quota.ResetsAt);
            },
            quota =>
            {
                Assert.Equal("Claude and GPT models", quota.DisplayName);
                Assert.Equal(0.71d, quota.RemainingFraction);
                Assert.Equal(new DateTimeOffset(2026, 10, 14, 11, 3, 44, TimeSpan.Zero), quota.ResetsAt);
            });
    }

    [Fact]
    public async Task TerminalFailurePreservesSanitizedProviderDetail()
    {
        var payload = new JsonObject { ["status"] = "ERROR", ["response"] = string.Empty, ["error"] = ErrorDetail };
        string output = new JsonObject { ["event"] = "result", ["result"] = payload }.ToJsonString();
        var client = new AntigravityCliLlmClient(new RecordingRunner(new(0, output, string.Empty)), _folder);

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() => ReadAllAsync(
            client.StreamAsync(Connection(), new() { ModelId = "auto", Messages = [new("user", "test")] })));

        Assert.Equal("antigravity_cli_error", failure.ProviderCode);
        Assert.Contains("The selected model is unavailable in this region.", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic-secret", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeTerminalFailurePreservesSanitizedProviderDetail()
    {
        var process = new TerminalProcess();
        await using AntigravityAgentSession session = await AntigravityAgentSession.OpenAsync(process, "agy",
            Environment.CurrentDirectory, _folder, new("auto", null, null), null, Connection(), null, null, null, CancellationToken.None);

        IReadOnlyList<LlmAgentEvent> events = await ReadAllAsync(session.RunTurnAsync(
            new() { Input = [new LlmAgentTextContent("test")] }));
        LlmAgentTurnCompletedEvent completed = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());

        Assert.Equal(LlmAgentCompletionReason.ProviderFailure, completed.Reason);
        Assert.Contains("The selected model is unavailable in this region.", completed.Error!, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", completed.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic-secret", completed.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ClaudeModelOffersNoEffortControl()
    {
        LlmConnection connection = Connection() with
        {
            CapabilityOverrides = new()
            {
                ReasoningLevels = LlmReasoningEffort.Levels(LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High)
            }
        };

        Assert.Empty(new AntigravityCliLlmClient(new RecordingRunner(new(0, string.Empty, string.Empty)), _folder)
            .GetCapabilities(connection, "claude-sonnet-4-6").ReasoningLevels);
    }

    /// <summary>AGY reports a refused or failed tool call as an ERROR step; the turn continues to its result.</summary>
    [Fact]
    public async Task FailedToolStepIsAFinishedStepNotMalformedOutput()
    {
        const string output = """
            {"event":"step_update","step_update":{"step_index":1,"state":"ERROR","step_type":"tool","tool_name":"view_file","tool_info":{"name":"view_file","parameters":{"AbsolutePath":"/missing.md"},"error":{"type":"TOOL_ERROR","message":"no such file or directory"}}}}
            {"event":"result","result":{"status":"SUCCESS","response":"OK","usage":{"input_tokens":12,"output_tokens":2,"total_tokens":14}}}
            """;
        var request = new LlmChatRequest { ModelId = "auto", Messages = [new("user", "Reply with OK.")] };
        var client = new AntigravityCliLlmClient(new RecordingRunner(new(0, output, string.Empty)), _folder);

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(Connection(), request));
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("OK", result.Text);
    }

    [Theory]
    [InlineData("custom-model", LlmReasoningLevel.Maximum, "max")]
    [InlineData("gemini-3.8-flash-low", LlmReasoningLevel.Maximum, "low")]
    public async Task NativeTurnUsesTheSameModelMappingBeforeReopening(string model, LlmReasoningLevel effort, string word)
    {
        var process = new TerminalProcess();
        await using AntigravityAgentSession session = await AntigravityAgentSession.OpenAsync(process, "agy",
            Environment.CurrentDirectory, _folder, new("auto", null, null), null, Connection(), null, null, null, CancellationToken.None);
        var request = new LlmAgentTurnRequest { ModelId = model, ReasoningLevel = effort, Input = [new LlmAgentTextContent("answer")] };

        await ReadAllAsync(session.RunTurnAsync(request));

        IReadOnlyList<string> arguments = process.Invocations[1];
        Assert.Equal(model, arguments[arguments.ToList().IndexOf("--model") + 1]);
        Assert.Equal(word, arguments[arguments.ToList().IndexOf("--effort") + 1]);
    }

    [Fact]
    public async Task ResumeReopensTheProviderConversationThroughTheFacade()
    {
        var process = new TerminalProcess();
        var source = new ResourcePreparationTests.Source(Connection());
        await using var catalogs = new LlmProviderService(source, [], []);
        await using var providers = new LlmAgentProviderService(source, [Agents(process)], catalogs);

        await using ILlmAgentSession session = await providers.ResumeSessionAsync("connection", "conversation-1",
            new() { Runtime = ProviderRuntime() });

        Assert.Equal("conversation-1", session.ThreadId);
        IReadOnlyList<string> arguments = Assert.Single(process.Invocations);
        Assert.Equal("conversation-1", arguments[arguments.ToList().IndexOf("--conversation") + 1]);
    }

    [Fact]
    public async Task FirstTurnCarriesSessionInstructionsAndOpeningHistoryOnce()
    {
        var process = new TerminalProcess();
        await using ILlmAgentSession session = await Agents(process).StartSessionAsync("connection",
            Connection(), new()
            {
                ModelId = "auto",
                Runtime = ProviderRuntime(),
                Instructions = "Answer in French.",
                Context = new() { Entries = [new("e1", "t0", new("user", "My name is Ada."))] }
            });

        await ReadAllAsync(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("Hello")] }));
        await ReadAllAsync(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("Again")] }));

        Assert.Equal(2, process.Writes.Count);
        Assert.Contains("Answer in French.", process.Writes[0], StringComparison.Ordinal);
        Assert.Contains("My name is Ada.", process.Writes[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Answer in French.", process.Writes[1], StringComparison.Ordinal);
        Assert.DoesNotContain(AgentOpeningContext.Header, process.Writes[1], StringComparison.Ordinal);
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
        await using ILlmAgentSession session = await Agents(process).StartSessionAsync("connection",
            Connection(), new()
            {
                ModelId = "auto",
                Runtime = ProviderRuntime(),
                Context = new()
                {
                    Entries =
                    [
                        new("e1", "t1", new("user", "Population of Lyon?") { Attachments = [image] }),
                        new("e2", "t1", new("assistant", string.Empty) { ToolCalls = [new LlmToolCall("c1", "lookup", """{"city":"Lyon"}""")] }),
                        new("e3", "t1", new("user", string.Empty) { ToolResults = [new LlmToolResult("c1", LlmToolOutput.FromText("522,250"))] }),
                        new("e4", "t1", new("assistant", "About 522,000."))
                    ]
                }
            });

        await ReadAllAsync(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("Round it.")] }));

        string written = Assert.Single(process.Writes);
        Assert.Contains("Population of Lyon?", written, StringComparison.Ordinal);
        Assert.Contains("[Attachment 0 (chart.png): The selected model does not support this attachment.", written, StringComparison.Ordinal);
        Assert.Contains("[TOOL CALL id=c1 name=lookup]", written, StringComparison.Ordinal);
        Assert.Contains("522,250", written, StringComparison.Ordinal);
        Assert.Contains("About 522,000.", written, StringComparison.Ordinal);
        Assert.DoesNotContain("--conversation", Assert.Single(process.Invocations));
    }

    [Fact]
    public async Task TurnInstructionsReachThePromptAsText()
    {
        var process = new TerminalProcess();
        await using AntigravityAgentSession session = await AntigravityAgentSession.OpenAsync(process, "agy",
            Environment.CurrentDirectory, _folder, new("auto", null, null), null, Connection(), null, null, null, CancellationToken.None);

        await ReadAllAsync(session.RunTurnAsync(new() { Instructions = "Be brief.", Input = [new LlmAgentTextContent("Look")] }));

        Assert.Contains("Instructions for this turn:", Assert.Single(process.Writes), StringComparison.Ordinal);
    }

    /// <summary>A structured call's schema file is scratch in the host data folder, and it is gone once the call ends.</summary>
    [Fact]
    public async Task StructuredCallWritesItsSchemaUnderTheDataFolderScratchAndRemovesIt()
    {
        string? schemaPath = null;
        bool written = false;
        var runner = new RecordingRunner(
            new(0, """{"event":"result","result":{"status":"SUCCESS","response":"","structured_output":{"answer":"OK"}}}""", string.Empty),
            invocation =>
            {
                schemaPath = invocation.Arguments[invocation.Arguments.ToList().IndexOf("--json-schema") + 1];
                written = File.Exists(schemaPath);
            });
        var client = new AntigravityCliLlmClient(runner, _folder);

        await ReadAllAsync(client.StreamAsync(Connection(), new()
        {
            ModelId = "auto",
            Messages = [new("user", "Answer.")],
            StructuredOutput = new()
            {
                Name = "answer",
                Schema = JsonElement.Parse("""{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}""")
            }
        }));

        Assert.True(written);
        Assert.StartsWith(Path.Combine(_folder.Path, "scratch") + Path.DirectorySeparatorChar, schemaPath, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.GetDirectoryName(schemaPath)));
    }

    private static LlmAgentNativeRuntimeOptions ProviderRuntime() => new()
    {
        WorkingDirectory = Environment.CurrentDirectory,
        Policy = new LlmAgentProviderRuntimePolicy()
    };

    private AntigravityAgentClient Agents(TerminalProcess process)
    {
        var runner = new RecordingRunner(new(1, string.Empty, "no side request is expected"));
        return new(process, runner, new AntigravityCliLlmClient(runner, _folder), new LearnedModelContext(), _folder);
    }

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

    private sealed class RecordingRunner(AntigravityCliProcessResult result, Action<AntigravityCliInvocation>? inspect = null)
        : IAntigravityCliRunner
    {
        public Task<AntigravityCliProcessResult> RunAsync(
            AntigravityCliInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            inspect?.Invoke(invocation);
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

        public IAntigravityCliSessionProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment)
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
