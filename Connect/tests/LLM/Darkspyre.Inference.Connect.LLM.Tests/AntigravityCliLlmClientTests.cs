using Darkspyre.Inference.Connect.LLM.Internal;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AntigravityCliLlmClientTests
{
    [Fact]
    public async Task CancellationAfterRunningUsageDoesNotDeliverABufferedCompletion()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(0,
            """
            {"event":"step_update","step_update":{"step_index":1,"state":"DONE","step_type":"checkpoint","usage":{"input_tokens":5,"output_tokens":1,"total_tokens":6}}}
            {"event":"result","result":{"status":"SUCCESS","response":"done","usage":{"input_tokens":5,"output_tokens":1,"total_tokens":6}}}
            """, string.Empty));
        using var cancellation = new CancellationTokenSource();
        await using var iterator = new AntigravityCliLlmClient(runner).StreamAsync(Connection(),
            new() { ModelId = "auto", Messages = [new("user", "hi")] }, cancellation.Token).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.IsType<LlmUsageDeltaEvent>(iterator.Current);
        cancellation.Cancel();
        // The activity for this same step may already be available; no completion may follow cancellation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            while (await iterator.MoveNextAsync()) { Assert.IsNotType<LlmChatCompletionEvent>(iterator.Current); }
        });
    }

    [Fact]
    public async Task AProviderErrorKeepsRunningUsageWithoutFabricatingCompletion()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(0,
            """
            {"event":"step_update","step_update":{"step_index":1,"state":"DONE","step_type":"checkpoint","usage":{"input_tokens":5,"output_tokens":1,"total_tokens":6}}}
            {"event":"result","result":{"status":"ERROR","response":"","usage":{"input_tokens":5,"output_tokens":1,"total_tokens":6}}}
            """, string.Empty));
        var seen = new List<LlmStreamEvent>();
        await Assert.ThrowsAsync<LlmProviderException>(async () =>
        {
            await foreach (var item in new AntigravityCliLlmClient(runner).StreamAsync(Connection(),
                new() { ModelId = "auto", Messages = [new("user", "hi")] })) { seen.Add(item); }
        });
        Assert.Equal(6, Assert.Single(seen.OfType<LlmUsageDeltaEvent>()).Cumulative.TotalTokens);
        Assert.Empty(seen.OfType<LlmChatCompletionEvent>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunningUsageSharesTheStepLedgerAndKeepsTheResultAuthoritative(bool enabled)
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(0,
            """
            {"event":"step_update","step_update":{"step_index":0,"state":"DONE","step_type":"user_input","usage":{"input_tokens":999,"output_tokens":999,"total_tokens":1998}}}
            {"event":"step_update","step_update":{"step_index":1,"state":"ACTIVE","step_type":"agent_response","usage":{"input_tokens":999,"output_tokens":999,"total_tokens":1998}}}
            {"event":"step_update","step_update":{"step_index":1,"state":"DONE","step_type":"agent_response","text_delta":"ok","usage":{"input_tokens":100,"output_tokens":10,"total_tokens":110}}}
            {"event":"step_update","step_update":{"step_index":1,"state":"DONE","step_type":"agent_response","text_delta":"ok","usage":{"input_tokens":100,"output_tokens":10,"total_tokens":110}}}
            {"event":"step_update","step_update":{"step_index":1,"state":"DONE","step_type":"agent_response","text_delta":"ok","usage":{"input_tokens":100,"output_tokens":12,"total_tokens":112}}}
            {"event":"step_update","step_update":{"step_index":2,"state":"DONE","step_type":"checkpoint","usage":{"input_tokens":5,"output_tokens":2,"total_tokens":7}}}
            {"event":"result","result":{"status":"SUCCESS","response":"ok","usage":{"input_tokens":90,"output_tokens":16,"total_tokens":106}}}
            {"event":"step_update","step_update":{"step_index":3,"state":"DONE","step_type":"checkpoint","usage":{"input_tokens":999,"output_tokens":999,"total_tokens":1998}}}
            """, string.Empty));
        var connection = Connection() with
        {
            CapabilityOverrides = new() { SupportsUsageDeltas = !enabled },
            ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides>
            { ["auto"] = new() { SupportsUsageDeltas = enabled } }
        };
        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(new AntigravityCliLlmClient(runner).StreamAsync(
            connection, new() { ModelId = "auto", Messages = [new("user", "hi")] }));
        Assert.Equal("ok", Assert.Single(events.OfType<LlmTextDeltaEvent>()).Delta);
        LlmUsageDeltaEvent[] usage = [.. events.OfType<LlmUsageDeltaEvent>()];
        Assert.Equal(enabled ? 3 : 0, usage.Length);
        if (enabled) { Assert.Equal(new int?[] { 110, 112, 119 }, usage.Select(item => item.Cumulative.TotalTokens)); }
        Assert.Equal(new LlmUsage(90, 16, 106), Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result.Usage);
    }

    [Fact]
    public async Task SendsPromptThroughStandardInputAndParsesTerminalResult()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(
            0,
            """
            {"event":"init","conversation_id":"conversation-1","init":{}}
            {"event":"step_update","step_update":{"conversation_id":"conversation-1","step_index":1,"state":"ACTIVE","step_type":"agent_response","text_delta":"Hello"}}
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
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal("Hello from Antigravity.", result.Text);
        Assert.Equal("conversation-1", result.ProviderResponseId);
        Assert.Equal(17, result.Usage?.TotalTokens);
        Assert.Null(result.Diagnostics.RawFinalResponseJson);
    }

    private static LlmConnection StreamingOffConnection() => Connection() with
    {
        CapabilityOverrides = new() { SupportsStreaming = false }
    };

    [Fact]
    public async Task StreamingOffPassesThePromptAsAnArgumentAndParsesTheEnvelope()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(
            0,
            """{"conversation_id":"conversation-2","status":"SUCCESS","response":"Complete answer.","duration_seconds":1.5,"num_turns":1,"usage":{"input_tokens":9,"output_tokens":4,"total_tokens":13}}""",
            string.Empty));
        var client = new AntigravityCliLlmClient(runner);

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(
            StreamingOffConnection(),
            new LlmChatRequest { ModelId = "auto", Messages = [new LlmChatMessage("user", "Secret prompt text")] }));

        AntigravityCliInvocation invocation = Assert.Single(runner.Invocations);
        Assert.Equal("-p", invocation.Arguments[0]);
        Assert.Contains("Secret prompt text", invocation.Arguments[1], StringComparison.Ordinal);
        Assert.Equal(["--output-format", "json"], invocation.Arguments.Skip(2).Take(2));
        Assert.DoesNotContain("--input-format", invocation.Arguments);
        Assert.Null(invocation.StandardInput);
        Assert.Collection(events,
            item => Assert.Equal(new LlmTextDeltaEvent("Complete answer."), item),
            item =>
            {
                LlmChatResult result = Assert.IsType<LlmChatCompletionEvent>(item).Result;
                Assert.Equal("Complete answer.", result.Text);
                Assert.Equal("conversation-2", result.ProviderResponseId);
                Assert.Equal(new LlmUsage(9, 4, 13), result.Usage);
            });
    }

    [Fact]
    public async Task StreamingOffReportsProviderAndExitErrors()
    {
        var failed = new AntigravityCliLlmClient(new RecordingRunner(new AntigravityCliProcessResult(
            0, """{"status":"ERROR","response":"","error":{"message":"quota"}}""", string.Empty)));
        LlmProviderException providerError = await Assert.ThrowsAsync<LlmProviderException>(() => ReadAllAsync(failed.StreamAsync(
            StreamingOffConnection(), new LlmChatRequest { ModelId = "auto", Messages = [new LlmChatMessage("user", "Hi")] })));
        Assert.Equal("antigravity_cli_error", providerError.ProviderCode);

        var exited = new AntigravityCliLlmClient(new RecordingRunner(new AntigravityCliProcessResult(3, string.Empty, "boom")));
        LlmProviderException exitError = await Assert.ThrowsAsync<LlmProviderException>(() => ReadAllAsync(exited.StreamAsync(
            StreamingOffConnection(), new LlmChatRequest { ModelId = "auto", Messages = [new LlmChatMessage("user", "Hi")] })));
        Assert.Equal("antigravity_cli_exit_3", exitError.ProviderCode);
    }

    [Fact]
    public async Task StreamingOffRejectsAPromptOverTheArgumentLimitBeforeStarting()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(0, string.Empty, string.Empty));
        var client = new AntigravityCliLlmClient(runner);
        int length = OperatingSystem.IsWindows()
            ? AntigravityCliLlmClient.MaximumWindowsArgumentCharacters + 1
            : AntigravityCliLlmClient.MaximumUnixArgumentBytes + 1;

        await Assert.ThrowsAsync<NotSupportedException>(() => ReadAllAsync(client.StreamAsync(
            StreamingOffConnection(),
            new LlmChatRequest { ModelId = "auto", Messages = [new LlmChatMessage("user", new string('a', length))] })));

        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task StreamingOffWithASchemaReturnsTheStructuredOutput()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(
            0,
            """{"conversation_id":"c3","status":"SUCCESS","response":"prose","structured_output":{"answer":"yes"},"usage":{}}""",
            string.Empty));
        var client = new AntigravityCliLlmClient(runner);

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(
            StreamingOffConnection(),
            new LlmChatRequest
            {
                ModelId = "auto",
                Messages = [new LlmChatMessage("user", "Answer.")],
                StructuredOutput = new LlmStructuredOutputRequest
                {
                    Name = "answer",
                    Schema = Schema("""{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}""")
                }
            }));

        AntigravityCliInvocation invocation = Assert.Single(runner.Invocations);
        Assert.Contains("--json-schema", invocation.Arguments);
        Assert.NotNull(runner.SchemaContent);
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal("{\"answer\":\"yes\"}", result.Text);
        Assert.True(result.StructuredOutput?.IsValid);
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
                                    Kind = LlmBinaryKind.Document,
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
        Assert.IsType<LlmChatCompletionEvent>(events.Current);
        Assert.False(await events.MoveNextAsync());
    }

    [Fact]
    public async Task ProcessRunnerWritesUtf8WithoutAByteOrderMark()
    {
        AntigravityCliInvocation invocation = CreateFirstByteProbeInvocation();

        AntigravityCliProcessResult result = await new AntigravityCliProcessRunner(new AntigravityCliSessionProcessFactory()).RunAsync(invocation);

        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.StandardInputError);
        Assert.Equal("123", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task ProcessRunnerCapturesDiagnosticsWhenChildClosesInputEarly()
    {
        AntigravityCliInvocation invocation = CreateEarlyExitProbeInvocation(
            new string('x', 1024 * 1024));

        AntigravityCliProcessResult result = await new AntigravityCliProcessRunner(new AntigravityCliSessionProcessFactory()).RunAsync(invocation);

        Assert.Equal(23, result.ExitCode);
        Assert.Contains("synthetic agy failure", result.StandardError, StringComparison.Ordinal);
        Assert.NotNull(result.StandardInputError);
    }

    [UnixFact]
    public Task CancelledProcessRunnerEndsAStoppedDescendant() => CliProcessTerminationTests.Bounded(async () =>
    {
        var invocation = new AntigravityCliInvocation(
            "/bin/sh",
            ["-c", CliProcessTerminationTests.StoppedDescendantScript],
            StandardInput: null,
            Path.GetTempPath());
        var runner = new AntigravityCliProcessRunner(new AntigravityCliSessionProcessFactory());
        using var cancellation = new CancellationTokenSource();
        int grandchild = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (AntigravityCliProcessEvent processEvent in runner.StreamAsync(invocation, cancellation.Token))
            {
                grandchild = CliProcessTerminationTests.ParseProcessId(Assert.IsType<AntigravityCliOutputLine>(processEvent).Line);
                await cancellation.CancelAsync();
            }
        });

        await CliProcessTerminationTests.AssertGoneAsync(grandchild);
    });

    [Fact]
    public async Task ProcessExitTimeoutIsAProviderFailure()
    {
        var client = new AntigravityCliLlmClient(new ExitTimeoutRunner());

        LlmProviderException exception = await Assert.ThrowsAsync<LlmProviderException>(() =>
            ReadAllAsync(client.StreamAsync(
                Connection(),
                new LlmChatRequest
                {
                    ModelId = "auto",
                    Messages = [new LlmChatMessage("user", "test")]
                })));

        Assert.Equal("antigravity_cli_exit_timeout", exception.ProviderCode);
        Assert.Contains(ExitTimeoutEvents<AntigravityCliProcessEvent>.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            exception.Message, StringComparison.Ordinal);
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
    public async Task UsesProviderJsonSchemaAndReasoningEffort()
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

        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.True(result.StructuredOutput?.IsValid);
        Assert.Equal(
            LlmStructuredOutputEnforcement.ProviderJsonSchema,
            result.StructuredOutput?.Enforcement);
    }

    [Fact]
    public async Task SchemaChatStreamsOnlyTheCanonicalStructuredOutput()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(
            0,
            """
            {"event":"step_update","step_update":{"step_index":0,"state":"DONE","step_type":"user_input"}}
            {"event":"step_update","step_update":{"step_index":1,"state":"ACTIVE","step_type":"agent_response","text_delta":"{\"wrapper\":"}}
            {"event":"step_update","step_update":{"step_index":2,"state":"DONE","step_type":"tool","tool_name":"view_file","tool_info":{"name":"view_file","parameters":{},"output":"tool metadata"}}}
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
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
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

        Assert.Equal("522,000", Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Text);
        string input = Assert.Single(runner.Invocations).StandardInput!;
        Assert.Contains("[TOOL CALL id=c1 name=lookup]", input, StringComparison.Ordinal);
        Assert.Contains("[TOOL RESULT id=c1 status=ok]", input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContinuationStateIsRejectedByNameAndOfferedToolsStillFail()
    {
        var client = new AntigravityCliLlmClient(new RecordingRunner(new AntigravityCliProcessResult(0, string.Empty, string.Empty)));
        var state = new LlmContinuationState
        {
            ImplementationId = ProviderContinuationStateCodec.ImplementationId(LlmTransport.OpenAiCodex),
            ModelId = "model",
            ScopeId = "scope",
            Payload = "[]"
        };

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => ReadAllAsync(client.StreamAsync(Connection(), new LlmChatRequest
        {
            ModelId = "auto",
            Messages = [new LlmChatMessage("user", "Hi"), new LlmChatMessage("assistant", "Hello") { ContinuationState = state }]
        })));
        Assert.Contains("continuation state", error.Message, StringComparison.Ordinal);
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

    private sealed class ExitTimeoutRunner : IAntigravityCliRunner
    {
        public Task<AntigravityCliProcessResult> RunAsync(
            AntigravityCliInvocation invocation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<AntigravityCliProcessEvent> StreamAsync(
            AntigravityCliInvocation invocation,
            CancellationToken cancellationToken = default) =>
            new ExitTimeoutEvents<AntigravityCliProcessEvent>();
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
                "{\"event\":\"step_update\",\"step_update\":{\"step_index\":1,\"state\":\"ACTIVE\",\"step_type\":\"agent_response\",\"text_delta\":\"live output\"}}");
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
