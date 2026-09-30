using Darkspyre.Inference.Connect.LLM.Internal;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ClaudeCliLlmClientTests
{
    [Fact]
    public async Task SubscriptionPromptsUseOfficialCliWithStreamingJsonAndNativeSchema()
    {
        var runner = new RecordingRunner(new ClaudeCliProcessResult(
            0,
            """
            {"type":"system","subtype":"init","session_id":"session-1"}
            {"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"{\"status\":\"ready\"}"}}}
            {"type":"result","subtype":"success","is_error":false,"result":"{\"status\":\"ready\"}","structured_output":{"status":"ready"},"session_id":"session-1","stop_reason":"end_turn","terminal_reason":"completed","usage":{"input_tokens":7,"output_tokens":5}}
            """,
            string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner);
        var request = new LlmChatRequest
        {
            ModelId = "claude-test",
            Instructions = "Return JSON.",
            Messages =
            [
                new LlmChatMessage("user", "Secret prompt text")
                {
                    Attachments =
                    [
                        new LlmAttachment
                        {
                            Kind = LlmBinaryKind.Image,
                            FileName = "chart.png",
                            Data = new byte[]
                            {
                                0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a
                            }
                        },
                        new LlmAttachment
                        {
                            Kind = LlmBinaryKind.Document,
                            FileName = "notes.txt",
                            Data = "attachment text"u8.ToArray()
                        }
                    ]
                }
            ],
            ReasoningLevel = LlmReasoningLevel.ExtraHigh,
            StructuredOutput = new LlmStructuredOutputRequest
            {
                Name = "status",
                Schema = Schema(
                    """{"type":"object","properties":{"status":{"type":"string"}},"required":["status"]}""")
            }
        };

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(
            client.StreamAsync(CliConnection(), request));

        ClaudeCliInvocation invocation = Assert.Single(runner.Invocations);
        Assert.Equal("claude", invocation.Executable);
        Assert.Contains("-p", invocation.Arguments);
        Assert.Contains("--input-format", invocation.Arguments);
        Assert.Contains("--output-format", invocation.Arguments);
        Assert.Contains("--include-partial-messages", invocation.Arguments);
        Assert.Contains("--no-session-persistence", invocation.Arguments);
        Assert.Contains("--safe-mode", invocation.Arguments);
        Assert.Contains("--disable-slash-commands", invocation.Arguments);
        Assert.Contains("--permission-prompts", invocation.Arguments);
        Assert.Contains("--model", invocation.Arguments);
        Assert.Contains("claude-test", invocation.Arguments);
        Assert.Contains("--effort", invocation.Arguments);
        Assert.Contains("xhigh", invocation.Arguments);
        Assert.Contains("--json-schema", invocation.Arguments);
        Assert.Contains("Secret prompt text", invocation.StandardInput, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Secret prompt text",
            string.Join(' ', invocation.Arguments),
            StringComparison.Ordinal);
        using (JsonDocument input = JsonDocument.Parse(invocation.StandardInput!))
        {
            JsonElement content = input.RootElement
                .GetProperty("message")
                .GetProperty("content");
            Assert.Equal("text", content[0].GetProperty("type").GetString());
            Assert.Equal("image", content[1].GetProperty("type").GetString());
            Assert.Equal(
                "image/png",
                content[1].GetProperty("source").GetProperty("media_type").GetString());
            Assert.Equal("document", content[2].GetProperty("type").GetString());
            Assert.Equal("notes.txt", content[2].GetProperty("title").GetString());
            Assert.Equal(
                "attachment text",
                content[2].GetProperty("source").GetProperty("data").GetString());
        }

        Assert.Equal("{\"status\":\"ready\"}", Assert.Single(events.OfType<LlmTextDeltaEvent>()).Delta);
        Assert.NotEmpty(events.OfType<LlmActivityEvent>());
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal("{\"status\":\"ready\"}", result.Text);
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("session-1", result.ProviderResponseId);
        Assert.Equal(12, result.Usage?.TotalTokens);
        Assert.True(result.StructuredOutput?.IsValid);
        Assert.Equal(
            LlmStructuredOutputEnforcement.ProviderJsonSchema,
            result.StructuredOutput?.Enforcement);

        LlmProviderCapabilities capabilities = client.GetCapabilities(CliConnection());
        Assert.True(capabilities.SupportsJsonSchema);
        Assert.True(capabilities.SupportsReasoning);
        Assert.False(capabilities.SupportsTemperature);
    }

    [Fact]
    public async Task IntermediateRefusalDoesNotOverrideSuccessfulFallbackResult()
    {
        var runner = new RecordingRunner(new ClaudeCliProcessResult(
            0,
            """
            {"type":"assistant","message":{"stop_reason":"refusal","stop_details":{"type":"refusal","category":"cyber","explanation":"Declined."}}}
            {"type":"system","subtype":"model_fallback"}
            {"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"OK"}}}
            {"type":"result","subtype":"success","is_error":false,"result":"OK","session_id":"fallback-1","stop_reason":"end_turn","terminal_reason":"completed","usage":{"input_tokens":4,"output_tokens":1}}
            """,
            string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner);

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(
            CliConnection(),
            new LlmChatRequest
            {
                ModelId = "claude-test",
                Messages = [new LlmChatMessage("user", "Reply only with OK.")]
            }));

        Assert.Empty(events.OfType<LlmRefusalDeltaEvent>());
        Assert.Contains(events.OfType<LlmActivityEvent>(), activity =>
            activity.Activity.Contains("switching", StringComparison.OrdinalIgnoreCase));
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("OK", result.Text);
        Assert.Null(result.Refusal);
    }

    [Fact]
    public async Task EmitsModelOutputBeforeTheCliProcessCompletes()
    {
        var runner = new PausingStreamingRunner();
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner);
        await using IAsyncEnumerator<LlmStreamEvent> events = client.StreamAsync(
            CliConnection(),
            new LlmChatRequest
            {
                ModelId = "claude-test",
                Messages = [new LlmChatMessage("user", "Stream it.")]
            }).GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync());
        Assert.IsType<LlmActivityEvent>(events.Current);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("live output", Assert.IsType<LlmTextDeltaEvent>(events.Current).Delta);

        Task<bool> pendingCompletion = events.MoveNextAsync().AsTask();
        await runner.WaitingForRelease.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pendingCompletion.IsCompleted);

        runner.Release.TrySetResult();
        Assert.True(await pendingCompletion);
        Assert.IsType<LlmActivityEvent>(events.Current);
        Assert.True(await events.MoveNextAsync());
        Assert.IsType<LlmChatCompletionEvent>(events.Current);
        Assert.False(await events.MoveNextAsync());
    }

    [Fact]
    public async Task AuthenticationProbeUsesOnlyFirstPartyCliLogin()
    {
        var runner = new RecordingRunner(new ClaudeCliProcessResult(
            0,
            """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty"}""",
            string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner);

        Assert.True(await client.IsAuthenticatedAsync(CliConnection()));
        Assert.Single(runner.Invocations);
        Assert.Equal(["auth", "status", "--json"], runner.Invocations[0].Arguments);
    }

    [Fact]
    public async Task SubscriptionAccountUsageUsesCliSlashCommandAndNormalizesReport()
    {
        const string report = """
            Usage
            Current session: 13% used · resets Sep 20 at 8:50am (America/Chicago)
            Current week (all models): 42% used · resets Sep 26 at 11:59pm (America/Chicago)
            Current week (Sonnet only): 5% used · resets Sep 26 at 11:59pm (America/Chicago)
            Last 24h · 825 requests · 9 sessions
            """;
        string output = JsonSerializer.Serialize(new
        {
            type = "result",
            is_error = false,
            result = report,
            local_command = "usage"
        });
        var runner = new RecordingRunner(new ClaudeCliProcessResult(0, output, string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner);

        LlmAccountUsageSnapshot usage = await client.GetAccountUsageAsync(CliConnection());

        ClaudeCliInvocation invocation = Assert.Single(runner.Invocations);
        Assert.Equal(
            [
                "-p",
                "/usage",
                "--output-format", "json",
                "--no-session-persistence",
                "--setting-sources", "user"
            ],
            invocation.Arguments);
        Assert.Null(invocation.StandardInput);
        Assert.Equal(3, usage.Quotas.Count);
        Assert.Equal(0.13d, usage.Quotas[0].UsedFraction);
        Assert.Equal(0.87d, usage.Quotas[0].RemainingFraction);
        Assert.Equal("Sonnet only", usage.Quotas[2].MeterId);
        Assert.Equal(825m, usage.Metrics.Single(metric => metric.Unit == "requests").Value);
        Assert.All(usage.Metrics, metric => Assert.True(metric.IsApproximate));
        Assert.Contains("Current session", usage.Summary, StringComparison.Ordinal);
        Assert.True(client.GetCapabilities(CliConnection()).SupportsAccountUsage);
    }

    [Fact]
    public void SubscriptionConnectionRejectsApiKeyCredentials()
    {
        var runner = new RecordingRunner(new ClaudeCliProcessResult(0, string.Empty, string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner);
        LlmConnection connection = CliConnection() with
        {
            Credential = new LlmCredential { ApiKey = "api-key" }.Serialize()
        };

        Assert.Throws<NotSupportedException>(() => client.GetCapabilities(connection));
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task CliOnlyConnectionCanReadSubscriptionAccountUsage()
    {
        string output = JsonSerializer.Serialize(new
        {
            type = "result",
            is_error = false,
            result = "Current session: 1% used"
        });
        var runner = new RecordingRunner(new ClaudeCliProcessResult(0, output, string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner);
        LlmConnection connection = CliConnection();

        Assert.True(client.GetCapabilities(connection).SupportsAccountUsage);
        LlmAccountUsageSnapshot usage = await client.GetAccountUsageAsync(connection);
        Assert.Equal(0.01d, Assert.Single(usage.Quotas).UsedFraction);
    }

    [Fact]
    public async Task MissingCliIsNotAuthenticatedAndReturnsActionableFailure()
    {
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, new UnavailableRunner());

        Assert.False(await client.IsAuthenticatedAsync(CliConnection()));
        LlmProviderException exception = await Assert.ThrowsAsync<LlmProviderException>(() =>
            ReadAllAsync(client.StreamAsync(
                CliConnection(),
                new LlmChatRequest
                {
                    ModelId = "claude-test",
                    Messages = [new LlmChatMessage("user", "test")]
                })));

        Assert.Equal("claude_cli_not_found", exception.ProviderCode);
        Assert.Contains("Install the official Claude CLI", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliFailureDoesNotExposeRawProcessOutput()
    {
        var runner = new RecordingRunner(new ClaudeCliProcessResult(
            53,
            "prompt or response contents",
            "Authentication required for secret@example.test"));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner);

        LlmProviderException exception = await Assert.ThrowsAsync<LlmProviderException>(() =>
            ReadAllAsync(client.StreamAsync(
                CliConnection(),
                new LlmChatRequest
                {
                    ModelId = "claude-test",
                    Messages = [new LlmChatMessage("user", "test")]
                })));

        Assert.Equal("claude_cli_exit_53", exception.ProviderCode);
        Assert.Contains("complete Claude sign-in", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret@example.test", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt or response", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.SanitizedResponseBody);
    }

    [Fact]
    public async Task CliRejectsUnsupportedMaximumOutputTokensBeforeStartingProcess()
    {
        var runner = new RecordingRunner(new ClaudeCliProcessResult(0, string.Empty, string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner);

        await Assert.ThrowsAsync<NotSupportedException>(() => ReadAllAsync(client.StreamAsync(
            CliConnection(),
            new LlmChatRequest
            {
                ModelId = "claude-test",
                MaxOutputTokens = 100,
                Messages = [new LlmChatMessage("user", "test")]
            })));

        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task ProcessRunnerWritesUtf8WithoutAByteOrderMark()
    {
        const string standardInput = "{\"type\":\"user\"}\n";
        ClaudeCliInvocation invocation = OperatingSystem.IsWindows()
            ? new ClaudeCliInvocation(
                "powershell.exe",
                [
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "[Console]::Write([Console]::OpenStandardInput().ReadByte())"
                ],
                standardInput,
                Path.GetTempPath())
            : new ClaudeCliInvocation(
                "/bin/sh",
                ["-c", "od -An -tu1 -N1"],
                standardInput,
                Path.GetTempPath());

        ClaudeCliProcessResult result = await new ClaudeCliProcessRunner().RunAsync(invocation);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("123", result.StandardOutput.Trim());
    }

    [UnixFact]
    public Task CancelledProcessRunnerEndsAStoppedDescendant() => CliProcessTerminationTests.Bounded(async () =>
    {
        var invocation = new ClaudeCliInvocation(
            "/bin/sh",
            ["-c", CliProcessTerminationTests.StoppedDescendantScript],
            StandardInput: null,
            Path.GetTempPath());
        using var cancellation = new CancellationTokenSource();
        int grandchild = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (ClaudeCliProcessEvent processEvent in new ClaudeCliProcessRunner().StreamAsync(invocation, cancellation.Token))
            {
                grandchild = CliProcessTerminationTests.ParseProcessId(Assert.IsType<ClaudeCliOutputLine>(processEvent).Line);
                await cancellation.CancelAsync();
            }
        });

        await CliProcessTerminationTests.AssertGoneAsync(grandchild);
    });

    [Fact]
    public async Task ProcessExitTimeoutIsAProviderFailure()
    {
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, new ExitTimeoutRunner());

        LlmProviderException exception = await Assert.ThrowsAsync<LlmProviderException>(() =>
            ReadAllAsync(client.StreamAsync(
                CliConnection(),
                new LlmChatRequest
                {
                    ModelId = "claude-test",
                    Messages = [new LlmChatMessage("user", "test")]
                })));

        Assert.Equal("claude_cli_exit_timeout", exception.ProviderCode);
        Assert.Contains(ExitTimeoutEvents<ClaudeCliProcessEvent>.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsMinimalReasoningWithoutStartingTheCli()
    {
        var runner = new RecordingRunner(new ClaudeCliProcessResult(0, string.Empty, string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner);

        var error = await Assert.ThrowsAsync<Darkspyre.Inference.Common.InferenceException>(() => ReadAllAsync(client.StreamAsync(
            CliConnection(),
            new LlmChatRequest
            {
                ModelId = "claude-test",
                ReasoningLevel = LlmReasoningLevel.Minimal,
                Messages = [new LlmChatMessage("user", "Ready?")]
            })));

        Assert.Equal(Darkspyre.Inference.Common.InferenceFailureCode.InvalidRequest, error.Code);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task ToolHistoryReachesThePromptAsLabelledBlocks()
    {
        var runner = new RecordingRunner(new ClaudeCliProcessResult(
            0,
            """
            {"type":"system","subtype":"init","session_id":"session-1"}
            {"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"522,000"}}}
            {"type":"result","subtype":"success","is_error":false,"result":"522,000","session_id":"session-1","stop_reason":"end_turn","terminal_reason":"completed","usage":{"input_tokens":7,"output_tokens":2}}
            """,
            string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner);

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(
            CliConnection(), new LlmChatRequest { ModelId = "claude-test", Messages = ToolHistory() }));

        Assert.Equal("522,000", Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Text);
        string input = Assert.Single(runner.Invocations).StandardInput!;
        Assert.Contains("[TOOL CALL id=c1 name=lookup]", input, StringComparison.Ordinal);
        Assert.Contains("[TOOL RESULT id=c1 status=ok]", input, StringComparison.Ordinal);
        Assert.Equal(LlmToolHistory.Text, client.GetCapabilities(CliConnection()).ToolHistory);
    }

    [Fact]
    public async Task OfferedToolsAreStillRejectedBeforeStartingProcess()
    {
        var runner = new RecordingRunner(new ClaudeCliProcessResult(0, string.Empty, string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner);

        await Assert.ThrowsAsync<NotSupportedException>(() => ReadAllAsync(client.StreamAsync(CliConnection(), new LlmChatRequest
        {
            ModelId = "claude-test",
            Messages = ToolHistory(),
            Tools = [new() { Name = "lookup", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }]
        })));
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public void EstimateCountsTheProjectedPromptAndQualifiesIt()
    {
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, new RecordingRunner(new ClaudeCliProcessResult(0, string.Empty, string.Empty)));
        var request = new LlmChatRequest { ModelId = "claude-test", Messages = ToolHistory() };

        Assert.True(client.EstimateInputTokens(CliConnection(), request)?.Tokens > 0);
        Assert.Equal(LocalTokenCount.CliDiagnostic, client.EstimateDiagnostic);
        Assert.Null(client.EstimateInputTokens(CliConnection(), request with
        {
            Messages = [new LlmChatMessage("user", "See this.") { Attachments = [new LlmAttachment
            {
                Kind = LlmBinaryKind.Document, FileName = "note.txt", Data = "hello"u8.ToArray()
            }] }]
        }));
    }

    private static LlmChatMessage[] ToolHistory() =>
    [
        new LlmChatMessage("user", "Population of Lyon?"),
        new LlmChatMessage("assistant", string.Empty) { ToolCalls = [new LlmToolCall("c1", "lookup", """{"city":"Lyon"}""")] },
        new LlmChatMessage("user", string.Empty) { ToolResults = [new LlmToolResult("c1", LlmToolOutput.FromText("522,250"))] },
        new LlmChatMessage("user", "Round it."),
    ];

    private static LlmConnection CliConnection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.AnthropicClaudeCli,
        Endpoint = "claude",
        Credential = null
    };

    private static JsonElement Schema(string json)
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

    private sealed class RecordingRunner(ClaudeCliProcessResult result) : IClaudeCliRunner
    {
        public List<ClaudeCliInvocation> Invocations { get; } = [];

        public Task<ClaudeCliProcessResult> RunAsync(
            ClaudeCliInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(invocation);
            return Task.FromResult(result);
        }
    }

    private sealed class UnavailableRunner : IClaudeCliRunner
    {
        public Task<ClaudeCliProcessResult> RunAsync(
            ClaudeCliInvocation invocation,
            CancellationToken cancellationToken = default) =>
            throw new ClaudeCliUnavailableException(
                invocation.Executable,
                new InvalidOperationException("not installed"));
    }

    private sealed class ExitTimeoutRunner : IClaudeCliRunner
    {
        public Task<ClaudeCliProcessResult> RunAsync(
            ClaudeCliInvocation invocation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ClaudeCliProcessEvent> StreamAsync(
            ClaudeCliInvocation invocation,
            CancellationToken cancellationToken = default) =>
            new ExitTimeoutEvents<ClaudeCliProcessEvent>();
    }

    private sealed class PausingStreamingRunner : IClaudeCliRunner
    {
        public TaskCompletionSource WaitingForRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ClaudeCliProcessResult> RunAsync(
            ClaudeCliInvocation invocation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ClaudeCliProcessEvent> StreamAsync(
            ClaudeCliInvocation invocation,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ClaudeCliOutputLine(
                "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"live-1\"}");
            yield return new ClaudeCliOutputLine(
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"live output\"}}}");
            WaitingForRelease.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            const string terminal =
                "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"live output\",\"session_id\":\"live-1\",\"stop_reason\":\"end_turn\",\"terminal_reason\":\"completed\",\"usage\":{}}";
            yield return new ClaudeCliOutputLine(terminal);
            yield return new ClaudeCliProcessCompleted(new ClaudeCliProcessResult(
                0,
                terminal,
                string.Empty));
        }
    }

    private sealed class UnexpectedHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("OAuth prompt unexpectedly used HTTP.");
    }
}
