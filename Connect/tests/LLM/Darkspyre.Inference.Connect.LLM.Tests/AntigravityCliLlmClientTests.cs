using Darkspyre.Inference.Connect.LLM.Internal;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AntigravityCliLlmClientTests
{
    [Fact]
    public async Task SendsPromptThroughStandardInputAndParsesTerminalResult()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(
            0,
            """
            {"event":"init","conversation_id":"conversation-1","init":{}}
            {"event":"step_update","step_update":{"step_type":"agent_response","text_delta":"Hello"}}
            {"event":"result","result":{"conversation_id":"conversation-1","status":"SUCCESS","response":"Hello from Antigravity.","usage":{"input_tokens":12,"output_tokens":5,"total_tokens":17}}}
            """,
            string.Empty));
        var client = new AntigravityCliLlmClient(runner);

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(
            Connection(),
            new LlmChatRequest
            {
                ModelId = "auto",
                Instructions = "Be concise.",
                Messages = [new LlmChatMessage("user", "Secret prompt text")]
            }));

        AntigravityCliInvocation invocation = Assert.Single(runner.Invocations);
        Assert.Equal("agy", invocation.Executable);
        Assert.Contains("--input-format", invocation.Arguments);
        Assert.Contains("stream-json", invocation.Arguments);
        Assert.Contains("--output-format", invocation.Arguments);
        Assert.DoesNotContain("--mode=plan", invocation.Arguments);
        Assert.Contains("--sandbox", invocation.Arguments);
        Assert.Contains("--disable-slash-commands", invocation.Arguments);
        Assert.DoesNotContain("--model", invocation.Arguments);
        Assert.Contains("Be concise.", invocation.StandardInput, StringComparison.Ordinal);
        Assert.Contains("Secret prompt text", invocation.StandardInput, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret prompt text", string.Join(' ', invocation.Arguments), StringComparison.Ordinal);

        Assert.Equal("Hello", Assert.Single(events.OfType<LlmTextDeltaEvent>()).Delta);
        Assert.NotEmpty(events.OfType<LlmActivityEvent>());
        LlmChatResult result = Assert.Single(events.OfType<LlmCompletionEvent>()).Result;
        Assert.Equal("Hello from Antigravity.", result.Text);
        Assert.Equal("conversation-1", result.ProviderResponseId);
        Assert.Equal(17, result.Usage?.TotalTokens);
        Assert.Null(result.Diagnostics.RawFinalResponseJson);
    }

    [Fact]
    public async Task RejectsPromptAttachmentsBeforeStartingTextOnlyHeadlessCli()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(
            0,
            string.Empty,
            string.Empty));
        var client = new AntigravityCliLlmClient(runner);

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            ReadAllAsync(client.StreamAsync(
                Connection(),
                new LlmChatRequest
                {
                    ModelId = "auto",
                    Messages =
                    [
                        new LlmChatMessage("user", "Read this")
                        {
                            Attachments =
                            [
                                new LlmAttachment
                                {
                                    Kind = LlmAttachmentKind.Document,
                                    FileName = "notes.txt",
                                    Data = "text"u8.ToArray()
                                }
                            ]
                        }
                    ]
                })));

        Assert.Contains("document attachments on this lane", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Invocations);
        Assert.False(client.GetCapabilities(Connection()).SupportsPromptDocuments);
    }

    [Fact]
    public async Task AccountUsageUsesStandaloneSlashCommandAndNormalizesQuotas()
    {
        const string report = """
            Model Quotas
            Gemini weekly: 93.78% remaining · resets 2026-07-06T07:50:32Z
            Claude tokens: 120 tokens remaining · resets 2026-07-01T07:50:32Z
            Account: secret@example.test
            """;
        string output = JsonSerializer.Serialize(new
        {
            conversation_id = string.Empty,
            status = "SUCCESS",
            response = report
        });
        var runner = new RecordingRunner(new AntigravityCliProcessResult(
            0,
            output,
            string.Empty));
        var client = new AntigravityCliLlmClient(runner);

        LlmAccountUsageSnapshot usage = await client.GetAccountUsageAsync(Connection());

        AntigravityCliInvocation invocation = Assert.Single(runner.Invocations);
        Assert.Equal(["--output-format", "json", "--print=/usage"], invocation.Arguments);
        Assert.Null(invocation.StandardInput);
        Assert.True(client.GetCapabilities(Connection()).SupportsAccountUsage);
        Assert.Equal(2, usage.Quotas.Count);
        Assert.Equal(0.9378d, usage.Quotas[0].RemainingFraction);
        Assert.Equal(120m, usage.Quotas[1].Remaining);
        Assert.Equal("tokens", usage.Quotas[1].Unit);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 6, 7, 50, 32, TimeSpan.Zero),
            usage.Quotas[0].ResetsAt);
        Assert.DoesNotContain("secret@example.test", usage.Summary, StringComparison.Ordinal);
        Assert.Contains("[REDACTED EMAIL]", usage.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredStatusPayloadOmitsAccountIdentity()
    {
        const string payload = """
            {
              "plan_tier": "Pro",
              "email": "secret@example.test",
              "quota": {
                "gemini-weekly": {
                  "remaining_fraction": 0.9378,
                  "reset_time": "2026-07-06T07:50:32Z"
                }
              }
            }
            """;

        LlmAccountUsageSnapshot usage = AntigravityAccountUsageParser.Parse(
            payload,
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("Pro", usage.Plan);
        Assert.Null(usage.Summary);
        Assert.Single(usage.Quotas);
        Assert.DoesNotContain("secret@example.test", JsonSerializer.Serialize(usage), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmitsModelOutputBeforeTheCliProcessCompletes()
    {
        var runner = new PausingStreamingRunner();
        var client = new AntigravityCliLlmClient(runner);
        await using IAsyncEnumerator<LlmStreamEvent> events = client.StreamAsync(
            Connection(),
            new LlmChatRequest
            {
                ModelId = "auto",
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
        Assert.IsType<LlmCompletionEvent>(events.Current);
        Assert.False(await events.MoveNextAsync());
    }

    [Fact]
    public async Task ProcessRunnerWritesUtf8WithoutAByteOrderMark()
    {
        AntigravityCliInvocation invocation = CreateFirstByteProbeInvocation();

        AntigravityCliProcessResult result = await new AntigravityCliProcessRunner().RunAsync(invocation);

        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.StandardInputError);
        Assert.Equal("123", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task ProcessRunnerCapturesDiagnosticsWhenChildClosesInputEarly()
    {
        AntigravityCliInvocation invocation = CreateEarlyExitProbeInvocation(
            new string('x', 1024 * 1024));

        AntigravityCliProcessResult result = await new AntigravityCliProcessRunner().RunAsync(invocation);

        Assert.Equal(23, result.ExitCode);
        Assert.Contains("synthetic agy failure", result.StandardError, StringComparison.Ordinal);
        Assert.NotNull(result.StandardInputError);
    }

    [Fact]
    public async Task ClosedInputFailureIncludesSafeCliDiagnostics()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(
            2,
            string.Empty,
            "invalid JSON at the beginning of the input stream",
            "The Antigravity CLI closed standard input before the request was completely sent."));
        var client = new AntigravityCliLlmClient(runner);

        LlmProviderException exception = await Assert.ThrowsAsync<LlmProviderException>(() =>
            ReadAllAsync(client.StreamAsync(
                Connection(),
                new LlmChatRequest
                {
                    ModelId = "auto",
                    Messages = [new LlmChatMessage("user", "test")]
                })));

        Assert.Equal("antigravity_cli_input_closed", exception.ProviderCode);
        Assert.Contains("closed standard input", exception.Message, StringComparison.Ordinal);
        Assert.Contains("invalid JSON", exception.Message, StringComparison.Ordinal);
        Assert.Equal("invalid JSON at the beginning of the input stream", exception.SanitizedResponseBody);
    }

    [Fact]
    public async Task RejectsAReasoningLevelAboveHighWithoutStartingTheCli()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(0, string.Empty, string.Empty));
        var client = new AntigravityCliLlmClient(runner);

        var error = await Assert.ThrowsAsync<Darkspyre.Inference.Common.InferenceException>(() => ReadAllAsync(client.StreamAsync(
            Connection(),
            new LlmChatRequest
            {
                ModelId = "gemini-3.1-pro-high",
                ReasoningLevel = LlmReasoningLevel.ExtraHigh,
                Messages = [new LlmChatMessage("user", "Score it.")]
            })));

        Assert.Equal(Darkspyre.Inference.Common.InferenceFailureCode.InvalidRequest, error.Code);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task UsesNativeJsonSchemaAndReasoningEffort()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(
            0,
            """
            {"event":"result","result":{"conversation_id":"conversation-2","status":"SUCCESS","response":"{\"score\":7}","usage":{}}}
            """,
            string.Empty));
        var client = new AntigravityCliLlmClient(runner);

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(
            Connection(),
            new LlmChatRequest
            {
                ModelId = "gemini-3.1-pro-high",
                ReasoningLevel = LlmReasoningLevel.High,
                Messages = [new LlmChatMessage("user", "Score it.")],
                StructuredOutput = new LlmStructuredOutputRequest
                {
                    Name = "score",
                    Schema = Schema(
                        """{"type":"object","properties":{"score":{"type":"integer"}},"required":["score"]}""")
                }
            }));

        AntigravityCliInvocation invocation = Assert.Single(runner.Invocations);
        Assert.Contains("--model", invocation.Arguments);
        Assert.Contains("gemini-3.1-pro-high", invocation.Arguments);
        Assert.Contains("--effort", invocation.Arguments);
        Assert.Contains("high", invocation.Arguments);
        int schemaIndex = invocation.Arguments.ToList().IndexOf("--json-schema");
        Assert.True(schemaIndex >= 0);
        Assert.EndsWith("output-schema.json", invocation.Arguments[schemaIndex + 1], StringComparison.Ordinal);
        Assert.Contains("\"score\"", runner.SchemaContent, StringComparison.Ordinal);

        LlmChatResult result = Assert.Single(events.OfType<LlmCompletionEvent>()).Result;
        Assert.True(result.StructuredOutput?.IsValid);
        Assert.Equal(
            LlmStructuredOutputEnforcement.ProviderJsonSchema,
            result.StructuredOutput?.Enforcement);
    }

    [Fact]
    public async Task PrefersCanonicalStructuredOutputOverDuplicatedResponseText()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(
            0,
            """
            {"event":"result","result":{"conversation_id":"conversation-structured","status":"SUCCESS","response":"{\"score\":7}\n{\"score\":8}","structured_output":{"score":7},"usage":{}}}
            """,
            string.Empty));
        var client = new AntigravityCliLlmClient(runner);

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(
            Connection(),
            new LlmChatRequest
            {
                ModelId = "gemini-3.8-flash-high",
                Messages = [new LlmChatMessage("user", "Score it.")],
                StructuredOutput = new LlmStructuredOutputRequest
                {
                    Name = "score",
                    Schema = Schema(
                        """{"type":"object","properties":{"score":{"type":"integer"}},"required":["score"]}""")
                }
            }));

        Assert.Equal("{\"score\":7}", Assert.Single(events.OfType<LlmTextDeltaEvent>()).Delta);
        LlmChatResult result = Assert.Single(events.OfType<LlmCompletionEvent>()).Result;
        Assert.Equal("{\"score\":7}", result.Text);
        Assert.True(result.StructuredOutput?.IsValid);
    }

    [Fact]
    public async Task ListsLiveModelsAndUsesTheListAsAuthenticationProbe()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(
            0,
            """
            gemini-3.8-flash-high     Gemini 3.8 Flash (High)
            claude-sonnet-4-6         Claude Sonnet 4.6 (Thinking)
            """,
            string.Empty));
        var client = new AntigravityCliLlmClient(runner);

        IReadOnlyList<LlmModel> models = await client.ListModelsAsync(Connection());
        bool ready = await client.IsAuthenticatedAsync(Connection());

        Assert.Equal(3, models.Count);
        Assert.Equal("auto", models[0].Id);
        Assert.Equal("gemini-3.8-flash-high", models[1].Id);
        Assert.Equal("Anthropic via Google", models[2].OwnedBy);
        Assert.True(ready);
        Assert.All(runner.Invocations, invocation => Assert.Equal(["models"], invocation.Arguments));
    }

    [Fact]
    public async Task AuthenticationProbeRejectsACommandThatCannotListModels()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(
            1,
            string.Empty,
            "authentication required"));
        var client = new AntigravityCliLlmClient(runner);

        Assert.False(await client.IsAuthenticatedAsync(Connection()));
    }

    [Fact]
    public async Task ProviderServiceUsesExternalAuthenticationProbeWithoutStoredCredential()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(
            0,
            "gemini-3.8-flash-high Gemini 3.8 Flash (High)",
            string.Empty));
        var client = new AntigravityCliLlmClient(runner);
        var service = new LlmProviderService(
            new StubConnectionSource(Connection()),
            [client]);

        Assert.True(await service.IsAuthenticatedAsync("antigravity"));
    }

    [Fact]
    public async Task ReportsCliFailureWithoutPersistingRawOutput()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(
            53,
            "prompt or response contents",
            "Authentication required for secret@example.test"));
        var client = new AntigravityCliLlmClient(runner);

        LlmProviderException exception = await Assert.ThrowsAsync<LlmProviderException>(() =>
            ReadAllAsync(client.StreamAsync(
                Connection(),
                new LlmChatRequest
                {
                    ModelId = "auto",
                    Messages = [new LlmChatMessage("user", "test")]
                })));

        Assert.Equal("antigravity_cli_exit_53", exception.ProviderCode);
        Assert.Contains("complete sign-in", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret@example.test", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.SanitizedResponseBody);
    }

    [Fact]
    public async Task MissingCliIsNotAuthenticatedAndReturnsActionableProviderFailure()
    {
        var client = new AntigravityCliLlmClient(new UnavailableRunner());

        Assert.False(await client.IsAuthenticatedAsync(Connection()));

        LlmProviderException exception = await Assert.ThrowsAsync<LlmProviderException>(() =>
            ReadAllAsync(client.StreamAsync(
                Connection(),
                new LlmChatRequest
                {
                    ModelId = "auto",
                    Messages = [new LlmChatMessage("user", "test")]
                })));
        Assert.Equal("antigravity_cli_not_found", exception.ProviderCode);
        Assert.Contains("Install agy", exception.Message, StringComparison.Ordinal);
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

        Assert.Equal("522,000", Assert.Single(events.OfType<LlmCompletionEvent>()).Result.Text);
        string input = Assert.Single(runner.Invocations).StandardInput!;
        Assert.Contains("[TOOL CALL id=c1 name=lookup]", input, StringComparison.Ordinal);
        Assert.Contains("[TOOL RESULT id=c1 status=ok]", input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponseStateIsRejectedByNameAndOfferedToolsStillFail()
    {
        var client = new AntigravityCliLlmClient(new RecordingRunner(new AntigravityCliProcessResult(0, string.Empty, string.Empty)));
        var state = new LlmProviderResponseState
        {
            Transport = LlmTransport.OpenAiCodex,
            ModelId = "model",
            ScopeId = "scope",
            Payload = "[]"
        };

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => ReadAllAsync(client.StreamAsync(Connection(), new LlmChatRequest
        {
            ModelId = "auto",
            Messages = [new LlmChatMessage("user", "Hi"), new LlmChatMessage("assistant", "Hello") { ResponseState = state }]
        })));
        Assert.Contains("response state", error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<NotSupportedException>(() => ReadAllAsync(client.StreamAsync(Connection(), new LlmChatRequest
        {
            ModelId = "auto",
            Messages = ToolHistory(),
            Tools = [new() { Name = "lookup", InputSchema = System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" }) }]
        })));
    }

    [Fact]
    public async Task MeasuringThroughTheServiceEstimatesTheProjectedPromptWithItsQualification()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(0, "auto Auto", string.Empty));
        var service = new LlmProviderService(new StubConnectionSource(Connection()), [new AntigravityCliLlmClient(runner)]);

        LlmRequestAccounting accounting = await service.MeasureRequestAsync(
            "antigravity", new LlmChatRequest { ModelId = "auto", Messages = ToolHistory() });

        Assert.True(accounting.IsSupported);
        Assert.True(accounting.UsedContextTokens > 0);
        Assert.Equal(LlmContextMeasurementSource.Estimated, accounting.Source);
        Assert.Contains(LocalTokenCount.CliDiagnostic, accounting.Diagnostic, StringComparison.Ordinal);
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

    private static AntigravityCliInvocation CreateFirstByteProbeInvocation()
    {
        const string standardInput = "{\"event\":\"user\"}\n";
        return OperatingSystem.IsWindows()
            ? new AntigravityCliInvocation(
                "powershell.exe",
                [
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "[Console]::Write([Console]::OpenStandardInput().ReadByte())"
                ],
                standardInput,
                Path.GetTempPath())
            : new AntigravityCliInvocation(
                "/bin/sh",
                ["-c", "od -An -tu1 -N1"],
                standardInput,
                Path.GetTempPath());
    }

    private static AntigravityCliInvocation CreateEarlyExitProbeInvocation(string standardInput) =>
        OperatingSystem.IsWindows()
            ? new AntigravityCliInvocation(
                "powershell.exe",
                [
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "[Console]::Error.WriteLine('synthetic agy failure'); exit 23"
                ],
                standardInput,
                Path.GetTempPath())
            : new AntigravityCliInvocation(
                "/bin/sh",
                ["-c", "printf 'synthetic agy failure\\n' >&2; exit 23"],
                standardInput,
                Path.GetTempPath());

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

    private sealed class UnavailableRunner : IAntigravityCliRunner
    {
        public Task<AntigravityCliProcessResult> RunAsync(
            AntigravityCliInvocation invocation,
            CancellationToken cancellationToken = default) =>
            throw new AntigravityCliUnavailableException(
                invocation.Executable,
                new InvalidOperationException("not installed"));
    }

    private sealed class PausingStreamingRunner : IAntigravityCliRunner
    {
        public TaskCompletionSource WaitingForRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AntigravityCliProcessResult> RunAsync(
            AntigravityCliInvocation invocation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<AntigravityCliProcessEvent> StreamAsync(
            AntigravityCliInvocation invocation,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AntigravityCliOutputLine(
                "{\"event\":\"step_update\",\"step_update\":{\"step_type\":\"agent_response\",\"text_delta\":\"live output\"}}");
            WaitingForRelease.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            const string terminal =
                "{\"event\":\"result\",\"result\":{\"conversation_id\":\"live-1\",\"status\":\"SUCCESS\",\"response\":\"live output\",\"usage\":{}}}";
            yield return new AntigravityCliOutputLine(terminal);
            yield return new AntigravityCliProcessCompleted(new AntigravityCliProcessResult(
                0,
                terminal,
                string.Empty));
        }
    }

    private sealed class StubConnectionSource(LlmConnection connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(
            string connectionId,
            CancellationToken cancellationToken = default) => Task.FromResult<LlmConnection?>(connection);

        public Task SaveCredentialAsync(
            string connectionId,
            string credentialJson,
            string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
