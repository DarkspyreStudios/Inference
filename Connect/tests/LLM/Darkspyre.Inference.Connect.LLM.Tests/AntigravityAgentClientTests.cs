using System.Text.Json;
using System.Threading.Channels;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AntigravityAgentClientTests : IDisposable
{
    private const string ConversationId = "d2fb65d7-e4c7-484a-9565-09b2cd4551b2";
    private static readonly string[] RequiredAnswer = ["answer"];
    private static readonly string[] NativeTools = ["view_file"];
    private readonly string _directory = Directory.CreateTempSubdirectory("agy-agent-tests").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task StartWaitsForTheHandshakeAndUsesTheRealConversationId()
    {
        var factory = new Factory();
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());

        Assert.Equal(ConversationId, session.ThreadId);
        Assert.Equal(ConversationId, session.SessionId);
        FakeProcess process = Assert.Single(factory.Processes);
        Assert.Equal(_directory, process.WorkingDirectory);
        Assert.Equal(
            ["--input-format", "stream-json", "--output-format", "stream-json", "--sandbox", "--disable-slash-commands",
                "--model", "gemini-pro"],
            process.Arguments);
        Assert.Empty(process.Written);
        LlmAgentCapabilities capabilities = await session.GetCapabilitiesAsync();
        Assert.Equal([LlmAgentRuntimePolicyKind.ProviderConfigured], capabilities.SupportedRuntimePolicies);
        Assert.Equal(LlmAgentToolExecutionOwnership.Provider, capabilities.ToolExecutionOwnership);
        Assert.Equal(LlmOperationSupport.Unsupported, capabilities.ResourceObservation);
        Assert.True(capabilities.SupportsNativeResume);
        Assert.False(capabilities.SupportsInterrupt);
    }

    public static TheoryData<int> UnsupportedStarts => [1, 2, 3, 4, 5, 6, 7, 8];

    [Theory]
    [MemberData(nameof(UnsupportedStarts))]
    public async Task StartRejectsUnsupportedInputsBeforeStartingAProcess(int field)
    {
        var factory = new Factory();
        LlmAgentSessionOptions options = Options();
        options = field switch
        {
            1 => options with { Tools = [new() { Name = "echo", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }] },
            2 => options with { ToolHandler = (_, _) => Task.FromResult(new LlmToolOutput()) },
            3 => options with { Instructions = "Be terse." },
            4 => options with { Context = new LlmAgentContext() },
            5 => options with { Runtime = options.Runtime! with { Ephemeral = true } },
            6 => options with { Runtime = options.Runtime! with { Policy = new LlmAgentHostRuntimePolicy() } },
            7 => options with { Runtime = null },
            _ => options with { ThreadId = "host-thread" }
        };

        await Assert.ThrowsAnyAsync<Exception>(() => Client(factory).StartSessionAsync("c", Connection(), options));
        Assert.Empty(factory.Processes);
    }

    [Fact]
    public async Task ResumeOpensTheExactConversationAndRequiresAnExplicitProviderPolicy()
    {
        var factory = new Factory();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            Client(factory).ResumeSessionAsync("c", Connection(), ConversationId));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Client(factory).ResumeSessionAsync("c", Connection(), "../other", ResumeOptions()));
        Assert.Empty(factory.Processes);

        await using ILlmAgentSession session = await Client(factory).ResumeSessionAsync(
            "c", Connection(), ConversationId, ResumeOptions());

        Assert.Equal(ConversationId, session.ThreadId);
        FakeProcess process = Assert.Single(factory.Processes);
        Assert.Equal(["--conversation", ConversationId], process.Arguments.TakeLast(2));
        Assert.DoesNotContain("--continue", process.Arguments);
        Assert.DoesNotContain("--model", process.Arguments);
    }

    [Fact]
    public async Task ResumeFailsWhenTheCliOpensAnotherConversation()
    {
        var factory = new Factory { InitConversationId = "another-conversation" };

        LlmProviderException exception = await Assert.ThrowsAsync<LlmProviderException>(() =>
            Client(factory).ResumeSessionAsync("c", Connection(), ConversationId, ResumeOptions()));

        Assert.Equal("antigravity_cli_identity_mismatch", exception.ProviderCode);
        Assert.True(Assert.Single(factory.Processes).Disposed);
    }

    [Fact]
    public async Task StartFailsWhenTheProcessEndsBeforeTheHandshake()
    {
        var factory = new Factory { SendInit = false, EndBeforeInit = true };

        LlmProviderException exception = await Assert.ThrowsAsync<LlmProviderException>(() =>
            Client(factory).StartSessionAsync("c", Connection(), Options()));

        Assert.Equal("antigravity_cli_invalid_response", exception.ProviderCode);
    }

    [Fact]
    public async Task CallerCancellationDuringTheHandshakeSettlesTheProcess()
    {
        var factory = new Factory { SendInit = false };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(factory).StartSessionAsync("c", Connection(), Options(), cancellation.Token));

        Assert.True(Assert.Single(factory.Processes).Exited);
    }

    [Fact]
    public async Task TextTurnsStreamResponseDeltasAndChargeOnlyTheirOwnUsage()
    {
        var factory = new Factory();
        factory.Turns.Enqueue(TextTurn(0, "OK", 13077, 1, 13078, 13077, 1, 13078));
        factory.Turns.Enqueue(TextTurn(2, "YES", 13155, 1, 13156, 26232, 2, 26234));
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());

        List<LlmAgentEvent> first = await Read(session.RunTurnAsync(Turn("Reply OK")));
        List<LlmAgentEvent> second = await Read(session.RunTurnAsync(Turn("Reply YES")));

        Assert.IsType<LlmAgentTurnStartedEvent>(first[0]);
        Assert.Equal("OK\n", string.Concat(first.OfType<LlmAgentTextDeltaEvent>().Select(item => item.Delta)));
        Assert.Contains(first, item => item is LlmAgentActivityEvent);
        Assert.Equal("OK\n", Assert.Single(first.OfType<LlmAgentMessageCompletedEvent>()).Text);
        Assert.Equal(LlmAgentTurnStatus.Completed, Assert.IsType<LlmAgentTurnCompletedEvent>(first[^1]).Status);
        Assert.Equal(2, first.OfType<LlmAgentUsageEvent>().Count());
        Assert.Equal(new LlmUsage(13077, 1, 13078), first.OfType<LlmAgentUsageEvent>().Last().Usage);
        LlmUsage secondUsage = second.OfType<LlmAgentUsageEvent>().Last().Usage!;
        Assert.Equal(new LlmUsage(13155, 1, 13156), secondUsage);
        Assert.Null(secondUsage.CachedInputTokens);
        Assert.Null(secondUsage.ReasoningOutputTokens);

        FakeProcess process = Assert.Single(factory.Processes);
        Assert.Equal(2, process.Written.Count);
        using JsonDocument prompt = JsonDocument.Parse(process.Written[0]);
        Assert.Equal("user", prompt.RootElement.GetProperty("event").GetString());
        Assert.Equal("Reply OK", prompt.RootElement.GetProperty("message").GetProperty("content").GetString());
        Assert.NotEqual(first[0].TurnId, second[0].TurnId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunningUsageReplacesStepsAndHonorsTheSelectedModelOverride(bool enabled)
    {
        var factory = new Factory();
        string done = Step(1, "DONE", "agent_response", "x", 5, 1, 6);
        factory.Turns.Enqueue(_ =>
        [
            Step(0, "DONE", "user_input", input: 999, output: 999, total: 1998),
            done, done, Step(1, "DONE", "agent_response", "x", 5, 3, 8),
            Step(2, "DONE", "checkpoint", input: 2, output: 1, total: 3),
            Result("SUCCESS", "x", 10, 4, 14)
        ]);
        var connection = Connection() with
        {
            CapabilityOverrides = new() { SupportsUsageDeltas = !enabled },
            ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides>
            { ["gemini-pro"] = new() { SupportsUsageDeltas = enabled } }
        };
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", connection, Options());
        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("one")));

        LlmAgentUsageEvent[] usage = [.. events.OfType<LlmAgentUsageEvent>()];
        Assert.Equal(enabled ? 4 : 1, usage.Length);
        if (enabled) { Assert.Equal(new int?[] { 6, 8, 11, 14 }, usage.Select(item => item.Usage?.TotalTokens)); }
        Assert.Equal(new LlmUsage(10, 4, 14), usage[^1].Usage);
        Assert.All(usage, item =>
        {
            Assert.Equal(usage[0].RequestId, item.RequestId);
            Assert.Equal(item.Usage, item.RunTotals);
        });
        Assert.Equal("x", Assert.Single(events.OfType<LlmAgentTextDeltaEvent>()).Delta);
        Assert.IsType<LlmAgentTurnCompletedEvent>(events[^1]);
    }

    [Fact]
    public async Task PreviouslyAccountedStepsDoNotEnterTheNextTurnUsage()
    {
        var factory = new Factory();
        string old = Step(1, "DONE", "agent_response", "old", 5, 1, 6);
        factory.Turns.Enqueue(_ => [Step(0, "DONE", "user_input"), old, Result("SUCCESS", "old", 5, 1, 6)]);
        factory.Turns.Enqueue(_ => [Step(2, "DONE", "user_input"), old,
            Step(3, "DONE", "agent_response", "new", 7, 2, 9), Result("SUCCESS", "new", 12, 3, 15)]);
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());
        await Read(session.RunTurnAsync(Turn("one")));
        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("two")));

        Assert.Equal("new", Assert.Single(events.OfType<LlmAgentTextDeltaEvent>()).Delta);
        Assert.Equal(2, events.OfType<LlmAgentUsageEvent>().Count());
        Assert.All(events.OfType<LlmAgentUsageEvent>(), item => Assert.Equal(new LlmUsage(7, 2, 9), item.Usage));
    }

    [Fact]
    public async Task TheFirstTurnAfterResumeUsesTheStepLedgerRatherThanTheCumulativeTotal()
    {
        var factory = new Factory();
        factory.Turns.Enqueue(TextTurn(4, "later", 13155, 1, 13156, 26232, 2, 26234));
        await using ILlmAgentSession session = await Client(factory).ResumeSessionAsync(
            "c", Connection(), ConversationId, ResumeOptions());

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("again")));

        Assert.Equal(2, events.OfType<LlmAgentUsageEvent>().Count());
        Assert.All(events.OfType<LlmAgentUsageEvent>(), item => Assert.Equal(new LlmUsage(13155, 1, 13156), item.Usage));
    }

    [Fact]
    public async Task DecreasedCumulativeCountersLeaveTheTurnTotalUnavailable()
    {
        var factory = new Factory();
        factory.Turns.Enqueue(TextTurn(0, "OK", 100, 1, 101, 100, 1, 101));
        factory.Turns.Enqueue(TextTurn(2, "OK", 50, 1, 51, 50, 1, 51));
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());
        await Read(session.RunTurnAsync(Turn("one")));

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("two")));

        Assert.Null(events.OfType<LlmAgentUsageEvent>().Last().Usage);
        Assert.Null(events.OfType<LlmAgentUsageEvent>().Last().RunTotals);
        Assert.Contains(events, item => item is LlmAgentWarningEvent);
        Assert.Equal(LlmAgentTurnStatus.Completed, events.OfType<LlmAgentTurnCompletedEvent>().Single().Status);
    }

    [Fact]
    public async Task SchemaTurnsReopenTheConversationAndPublishOnlyTheCanonicalResult()
    {
        var factory = new Factory();
        factory.Turns.Enqueue(generation => SchemaTurn(generation, """{"answer":42}"""));
        factory.Turns.Enqueue(TextTurn(4, "plain", 1, 1, 2, 3, 3, 6));
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());
        JsonElement schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { answer = new { type = "integer" } },
            required = RequiredAnswer
        });

        List<LlmAgentEvent> structured = await Read(session.RunTurnAsync(Turn("answer") with { OutputSchema = schema }));
        List<LlmAgentEvent> plain = await Read(session.RunTurnAsync(Turn("plain")));

        Assert.DoesNotContain(structured, item => item is LlmAgentTextDeltaEvent);
        Assert.Equal(42, Assert.Single(structured.OfType<LlmAgentStructuredOutputSnapshotEvent>()).Content.GetProperty("answer").GetInt32());
        Assert.Equal("""{"answer":42}""", Assert.Single(structured.OfType<LlmAgentMessageCompletedEvent>()).Text);
        Assert.Equal(LlmAgentTurnStatus.Completed, structured.OfType<LlmAgentTurnCompletedEvent>().Single().Status);
        Assert.Equal("plain\n", Assert.Single(plain.OfType<LlmAgentMessageCompletedEvent>()).Text);

        Assert.Equal(3, factory.Processes.Count);
        Assert.True(factory.Processes[0].InputClosed);
        Assert.Contains("--json-schema", factory.Processes[1].Arguments);
        Assert.Equal(["--conversation", ConversationId], factory.Processes[1].Arguments.TakeLast(2));
        Assert.DoesNotContain("--json-schema", factory.Processes[2].Arguments);
        Assert.Equal(["--conversation", ConversationId], factory.Processes[2].Arguments.TakeLast(2));
    }

    [Fact]
    public async Task InvalidCanonicalStructuredOutputFailsTheTurn()
    {
        var factory = new Factory();
        factory.Turns.Enqueue(generation => SchemaTurn(generation, """{"answer":"text"}"""));
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());
        JsonElement schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { answer = new { type = "integer" } }
        });

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("answer") with { OutputSchema = schema }));

        Assert.DoesNotContain(events, item => item is LlmAgentStructuredOutputSnapshotEvent or LlmAgentMessageCompletedEvent);
        Assert.Equal(LlmAgentCompletionReason.ProviderFailure, events.OfType<LlmAgentTurnCompletedEvent>().Single().Reason);
    }

    [Fact]
    public async Task EffortAndModelOverridesApplyToOneTurnOnly()
    {
        var factory = new Factory();
        factory.Turns.Enqueue(TextTurn(0, "a", 1, 1, 2, 1, 1, 2));
        factory.Turns.Enqueue(TextTurn(2, "b", 1, 1, 2, 2, 2, 4));
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());

        await Read(session.RunTurnAsync(Turn("a") with { ReasoningLevel = LlmReasoningLevel.High, ModelId = "gemini-flash" }));
        await Read(session.RunTurnAsync(Turn("b")));

        Assert.Equal(3, factory.Processes.Count);
        Assert.Contains("high", factory.Processes[1].Arguments);
        Assert.Contains("gemini-flash", factory.Processes[1].Arguments);
        Assert.DoesNotContain("--effort", factory.Processes[2].Arguments);
        Assert.Contains("gemini-pro", factory.Processes[2].Arguments);
    }

    [Fact]
    public async Task NativeToolStepsAreObservationsWithTheirReportedOutput()
    {
        var factory = new Factory();
        factory.Turns.Enqueue(generation =>
        [
            Step(0, "DONE", "user_input"),
            ToolStep(1, "ACTIVE", "view_file", null, null),
            ToolStep(1, "DONE", "view_file", "fixture value", null),
            ToolStep(2, "DONE", "run_command", null, "denied by policy"),
            Step(3, "DONE", "agent_response", "done"),
            Result("SUCCESS", "done", 1, 1, 2)
        ]);
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("read")));

        LlmAgentToolEvent[] tools = [.. events.OfType<LlmAgentToolEvent>()];
        Assert.Equal(3, tools.Length);
        Assert.Equal(("inProgress", (bool?)null, (LlmToolOutput?)null), (tools[0].Status, tools[0].Success, tools[0].Output));
        Assert.Equal("view_file", tools[1].Tool);
        Assert.Equal("completed", tools[1].Status);
        Assert.Equal("fixture value", Assert.IsType<LlmToolOutputText>(Assert.Single(tools[1].Output!.Parts)).Text);
        Assert.Equal("/tmp/fixture.txt", tools[1].Arguments.GetProperty("AbsolutePath").GetString());
        Assert.Equal(("failed", (bool?)false, true), (tools[2].Status, tools[2].Success, tools[2].Output!.IsError));
        Assert.Equal(LlmAgentTurnStatus.Completed, events.OfType<LlmAgentTurnCompletedEvent>().Single().Status);
        Assert.Equal("done", string.Concat(events.OfType<LlmAgentTextDeltaEvent>().Select(item => item.Delta)));
    }

    [Theory]
    [InlineData("ERROR", LlmAgentCompletionReason.ProviderFailure)]
    [InlineData("INVALID", LlmAgentCompletionReason.ProviderFailure)]
    [InlineData("CANCELED", LlmAgentCompletionReason.Interruption)]
    [InlineData("INTERRUPTED", LlmAgentCompletionReason.Interruption)]
    [InlineData("WAITING", LlmAgentCompletionReason.Unknown)]
    [InlineData("SOMETHING_NEW", LlmAgentCompletionReason.Unknown)]
    public async Task TerminalStatusesKeepTheirDistinctMeanings(string status, LlmAgentCompletionReason reason)
    {
        var factory = new Factory();
        factory.Turns.Enqueue(_ => [Step(0, "DONE", "user_input"), Result(status, "partial", 1, 1, 2)]);
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("x")));

        LlmAgentTurnCompletedEvent completed = events.OfType<LlmAgentTurnCompletedEvent>().Single();
        Assert.Equal(reason, completed.Reason);
        Assert.NotNull(completed.Error);
        Assert.DoesNotContain(events, item => item is LlmAgentMessageCompletedEvent);
    }

    [Fact]
    public async Task EndOfOutputWithoutAResultFailsTheTurnAndTheSession()
    {
        var factory = new Factory();
        factory.Turns.Enqueue(_ => [Step(0, "DONE", "user_input"), Step(1, "ACTIVE", "agent_response", "partial"), null]);
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("x")));

        LlmAgentTurnCompletedEvent completed = events.OfType<LlmAgentTurnCompletedEvent>().Single();
        Assert.Equal(LlmAgentCompletionReason.ProviderFailure, completed.Reason);
        Assert.DoesNotContain(events, item => item is LlmAgentMessageCompletedEvent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(session.RunTurnAsync(Turn("again"))));
    }

    [Fact]
    public async Task MalformedOutputFailsTheTurn()
    {
        var factory = new Factory();
        factory.Turns.Enqueue(_ => [Step(0, "DONE", "user_input"), "{not json"]);
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("x")));

        Assert.Equal(LlmAgentCompletionReason.ProviderFailure, events.OfType<LlmAgentTurnCompletedEvent>().Single().Reason);
    }

    [Fact]
    public async Task RepeatedIdenticalStepsCountOnceAndConflictingStepsFail()
    {
        var factory = new Factory();
        string done = Step(1, "DONE", "agent_response", "x", 5, 1, 6);
        factory.Turns.Enqueue(_ => [Step(0, "DONE", "user_input"), done, done, Result("SUCCESS", "x", 5, 1, 6)]);
        factory.Turns.Enqueue(_ => [Step(2, "DONE", "user_input"), Step(3, "DONE", "agent_response", "y", 5, 1, 6),
            Step(3, "DONE", "agent_response", "z", 5, 1, 6), Result("SUCCESS", "y", 10, 2, 12)]);
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());

        List<LlmAgentEvent> first = await Read(session.RunTurnAsync(Turn("one")));
        List<LlmAgentEvent> second = await Read(session.RunTurnAsync(Turn("two")));

        Assert.Equal("x", Assert.Single(first.OfType<LlmAgentTextDeltaEvent>()).Delta);
        Assert.Equal(LlmAgentCompletionReason.ProviderFailure, second.OfType<LlmAgentTurnCompletedEvent>().Single().Reason);
    }

    [Fact]
    public async Task WorkBeforeTheTurnInputBoundaryIsNotAttributed()
    {
        var factory = new Factory();
        factory.Turns.Enqueue(_ => [Step(5, "DONE", "agent_response", "stray"), Step(6, "DONE", "user_input"), Result("SUCCESS", "", 1, 1, 2)]);
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("x")));

        Assert.DoesNotContain(events, item => item is LlmAgentTextDeltaEvent);
        Assert.Equal(LlmAgentCompletionReason.ProviderFailure, events.OfType<LlmAgentTurnCompletedEvent>().Single().Reason);
    }

    [Fact]
    public async Task OutputWhileIdleFaultsTheSession()
    {
        var factory = new Factory();
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());

        Task consumed = factory.Processes[0].NextWait();
        factory.Processes[0].Emit(Step(9, "DONE", "agent_response", "autonomous"));
        await consumed.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(session.RunTurnAsync(Turn("x"))));
        Assert.Empty(factory.Processes[0].Written);
    }

    [Fact]
    public async Task OverlappingTurnsAreRejectedBeforeWriting()
    {
        var factory = new Factory();
        factory.Turns.Enqueue(_ => [Step(0, "DONE", "user_input")]);
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());
        await using IAsyncEnumerator<LlmAgentEvent> first = session.RunTurnAsync(Turn("one")).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(session.RunTurnAsync(Turn("two"))));
        Assert.Empty(factory.Processes[0].Written);
    }

    [Fact]
    public async Task CancellationKillsTheOwnedProcessAndFaultsTheSession()
    {
        var factory = new Factory();
        factory.Turns.Enqueue(_ => [Step(0, "DONE", "user_input"), Step(1, "ACTIVE", "agent_response", "partial")]);
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (LlmAgentEvent item in session.RunTurnAsync(Turn("x"), cancellation.Token))
            {
                if (item is LlmAgentTextDeltaEvent)
                {
                    await cancellation.CancelAsync();
                }
            }
        });

        Assert.True(factory.Processes[0].Killed);
        Assert.True(factory.Processes[0].Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(session.RunTurnAsync(Turn("again"))));
    }

    [Fact]
    public async Task AProcessThatDoesNotExitAfterTheKillFaultsTheSessionWithItsPid()
    {
        var factory = new Factory { KillFailure = new CliProcessTerminationException(4242) };
        factory.Turns.Enqueue(_ => [Step(0, "DONE", "user_input"), Step(1, "ACTIVE", "agent_response", "partial")]);
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (LlmAgentEvent item in session.RunTurnAsync(Turn("x"), cancellation.Token))
            {
                if (item is LlmAgentTextDeltaEvent)
                {
                    await cancellation.CancelAsync();
                }
            }
        });

        Assert.True(factory.Processes[0].Disposed);
        InvalidOperationException fault = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Read(session.RunTurnAsync(Turn("again"))));
        Assert.Contains("4242", fault.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AbandonedEnumerationSettlesTheProcess()
    {
        var factory = new Factory();
        factory.Turns.Enqueue(_ => [Step(0, "DONE", "user_input"), Step(1, "ACTIVE", "agent_response", "partial")]);
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());

        await using (IAsyncEnumerator<LlmAgentEvent> events = session.RunTurnAsync(Turn("x")).GetAsyncEnumerator())
        {
            while (await events.MoveNextAsync() && events.Current is not LlmAgentTextDeltaEvent)
            {
            }
        }

        Assert.True(factory.Processes[0].Killed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(session.RunTurnAsync(Turn("again"))));
    }

    [Fact]
    public async Task UnsupportedTurnInputsAndOperationsFailBeforeEffects()
    {
        var factory = new Factory();
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());

        await Assert.ThrowsAsync<NotSupportedException>(() => Read(session.RunTurnAsync(
            new LlmAgentTurnRequest { Input = [new LlmAgentImageContent("https://example.invalid/a.png")] })));
        await Assert.ThrowsAsync<NotSupportedException>(() => Read(session.RunTurnAsync(Turn("x") with
        {
            Attachments = [new LlmAttachment { Kind = LlmBinaryKind.Document, FileName = "a.txt", Data = "a"u8.ToArray() }]
        })));
        await Assert.ThrowsAsync<NotSupportedException>(() => Read(session.RunTurnAsync(Turn("x") with { Instructions = "" })));
        await Assert.ThrowsAnyAsync<Exception>(() => Read(session.RunTurnAsync(Turn("x") with { ReasoningLevel = LlmReasoningLevel.Maximum })));
        await Assert.ThrowsAsync<NotSupportedException>(() => session.InterruptAsync("turn"));
        await Assert.ThrowsAsync<NotSupportedException>(() => session.SteerAsync("turn", [new LlmAgentTextContent("x")]));
        await Assert.ThrowsAsync<NotSupportedException>(() => session.GetResourcesAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => session.CaptureContextAsync());
        Assert.Empty(factory.Processes[0].Written);
    }

    [Fact]
    public async Task DisposeClosesTheProcess()
    {
        var factory = new Factory();
        ILlmAgentSession session = await Client(factory).StartSessionAsync("c", Connection(), Options());

        await session.DisposeAsync();

        Assert.True(factory.Processes[0].Disposed);
        Assert.True(factory.Processes[0].InputClosed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GetCapabilitiesAsync());
    }

    private static AntigravityAgentClient Client(Factory factory) => new(factory);

    private LlmAgentSessionOptions Options() => new()
    {
        ModelId = "gemini-pro",
        Runtime = new() { WorkingDirectory = _directory, Policy = new LlmAgentProviderRuntimePolicy() }
    };

    private LlmAgentResumeOptions ResumeOptions() => new()
    {
        Runtime = new() { WorkingDirectory = _directory, Policy = new LlmAgentProviderRuntimePolicy() }
    };

    private static LlmConnection Connection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.GoogleAntigravityCli,
        Endpoint = "agy"
    };

    private static LlmAgentTurnRequest Turn(string text) => new() { Input = [new LlmAgentTextContent(text)] };

    private static async Task<List<LlmAgentEvent>> Read(IAsyncEnumerable<LlmAgentEvent> events)
    {
        var list = new List<LlmAgentEvent>();
        await foreach (LlmAgentEvent item in events)
        {
            list.Add(item);
        }
        return list;
    }

    private static Func<int, IReadOnlyList<string?>> TextTurn(int inputIndex, string text, long stepInput, long stepOutput,
        long stepTotal, long input, long output, long total) => _ =>
    [
        Step(inputIndex, "DONE", "user_input"),
        Step(inputIndex + 1, "ACTIVE", "agent_response", text),
        Step(inputIndex + 1, "DONE", "agent_response", "\n", stepInput, stepOutput, stepTotal),
        Result("SUCCESS", text + "\n", input, output, total)
    ];

    private static IReadOnlyList<string?> SchemaTurn(int generation, string structured) =>
    [
        Step(0, "DONE", "user_input"),
        Step(1, "ACTIVE", "agent_response", "{\"wrapper\":"),
        Step(1, "DONE", "agent_response", "true}", 3, 1, 4),
        JsonSerializer.Serialize(new
        {
            @event = "result",
            result = new
            {
                conversation_id = ConversationId,
                status = "SUCCESS",
                response = "{\"wrapper\":true}",
                structured_output = JsonElement.Parse(structured),
                usage = new { input_tokens = 3, output_tokens = 1, total_tokens = 4 }
            }
        })
    ];

    private static string Step(int index, string state, string type, string? delta = null,
        long? input = null, long? output = null, long? total = null) => JsonSerializer.Serialize(new
        {
            @event = "step_update",
            step_update = new Dictionary<string, object?>
            {
                ["conversation_id"] = ConversationId,
                ["step_index"] = index,
                ["state"] = state,
                ["step_type"] = type,
                ["text_delta"] = delta,
                ["usage"] = input is null ? null : new { input_tokens = input, output_tokens = output, total_tokens = total }
            }
        });

    private static string ToolStep(int index, string state, string tool, string? output, string? error) => JsonSerializer.Serialize(new
    {
        @event = "step_update",
        step_update = new
        {
            conversation_id = ConversationId,
            step_index = index,
            state,
            step_type = "tool",
            tool_name = tool,
            tool_info = new Dictionary<string, object?>
            {
                ["name"] = tool,
                ["parameters"] = new { AbsolutePath = "/tmp/fixture.txt" },
                ["output"] = output,
                ["error"] = error is null ? null : new { type = "permission", message = error }
            }
        }
    });

    private static string Result(string status, string response, long input, long output, long total) => JsonSerializer.Serialize(new
    {
        @event = "result",
        result = new
        {
            conversation_id = ConversationId,
            status,
            response,
            usage = new { input_tokens = input, output_tokens = output, total_tokens = total }
        }
    });

    /// <summary>Scripts processes; each turn's lines are emitted after its prompt arrives. A null line ends output.</summary>
    private sealed class Factory : IAntigravityCliSessionProcessFactory
    {
        public List<FakeProcess> Processes { get; } = [];

        public Queue<Func<int, IReadOnlyList<string?>>> Turns { get; } = new();

        public string InitConversationId { get; init; } = ConversationId;

        public bool SendInit { get; init; } = true;

        public bool EndBeforeInit { get; init; }

        /// <summary>When set, killing a process fails with this exception after the process ends its output.</summary>
        public Exception? KillFailure { get; init; }

        public IAntigravityCliSessionProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
        {
            var process = new FakeProcess(this, Processes.Count + 1, [.. arguments], workingDirectory);
            Processes.Add(process);
            if (SendInit)
            {
                process.Emit(JsonSerializer.Serialize(new
                {
                    @event = "init",
                    conversation_id = InitConversationId,
                    init = new { cwd = workingDirectory, tools = NativeTools }
                }));
            }
            if (EndBeforeInit)
            {
                process.Emit(null);
            }
            return process;
        }
    }

    /// <summary>A persistent pipe: output appears only when the test or a written prompt produces it.</summary>
    private sealed class FakeProcess(Factory factory, int generation, List<string> arguments, string workingDirectory)
        : IAntigravityCliSessionProcess
    {
        private readonly Channel<string?> _output = Channel.CreateUnbounded<string?>();
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Arguments { get; } = arguments;

        public string WorkingDirectory { get; } = workingDirectory;

        public List<string> Written { get; } = [];

        public bool InputClosed { get; private set; }

        public bool Killed { get; private set; }

        public bool Disposed { get; private set; }

        public bool Exited => _exited.Task.IsCompleted;

        private TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes the next time the reader has consumed everything and waits for more output.</summary>
        public Task NextWait()
        {
            _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _waiting.Task;
        }

        public void Emit(string? line) => _output.Writer.TryWrite(line);

        public ValueTask WriteAsync(string text, CancellationToken cancellationToken = default)
        {
            if (InputClosed || Exited)
            {
                throw new IOException("closed");
            }
            Written.Add(text);
            if (factory.Turns.TryDequeue(out Func<int, IReadOnlyList<string?>>? turn))
            {
                foreach (string? line in turn(generation))
                {
                    Emit(line);
                }
            }
            return ValueTask.CompletedTask;
        }

        public void CloseInput()
        {
            InputClosed = true;
            _output.Writer.TryComplete();
            _exited.TrySetResult();
        }

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
        {
            if (!_output.Reader.TryRead(out string? line))
            {
                _waiting.TrySetResult();
                try
                {
                    line = await _output.Reader.ReadAsync(cancellationToken);
                }
                catch (ChannelClosedException)
                {
                    return null;
                }
            }
            if (line is null)
            {
                _output.Writer.TryComplete();
            }
            return line;
        }

        public async Task<(int ExitCode, string StandardError)> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            await _exited.Task.WaitAsync(cancellationToken);
            return (0, string.Empty);
        }

        public Task KillAsync()
        {
            Killed = true;
            _output.Writer.TryComplete();
            _exited.TrySetResult();
            return factory.KillFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            CloseInput();
            return ValueTask.CompletedTask;
        }
    }
}
