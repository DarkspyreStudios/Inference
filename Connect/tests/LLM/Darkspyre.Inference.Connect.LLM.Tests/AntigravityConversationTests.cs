using System.Text.Json.Nodes;
using System.Threading.Channels;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>Context counting and compaction of the Antigravity conversation, against a scripted AGY process.</summary>
public sealed class AntigravityConversationTests
{
    private const string Model = "gemini-3.8-flash-low";
    private const string Summary = "SUMMARY-OF-EARLIER-TURNS";

    [Fact]
    public async Task StatusIsEstimatedBeforeTheFirstReportAndMixedAfterIt()
    {
        var agy = new ScriptedAgy { InputTokens = 5_000 };
        await using ILlmAgentSession session = await StartAsync(agy, new(), capped: false);

        IReadOnlyList<LlmAgentEvent> events = await ReadAllAsync(session.RunTurnAsync(Turn("Hello")));
        LlmAgentContextStatus after = await session.GetContextStatusAsync();

        LlmAgentContextStatus before = Assert.Single(events.OfType<LlmAgentContextStatusEvent>()).Status;
        Assert.Equal(LlmContextMeasurementSource.Estimated, before.Source);
        Assert.Equal(1_048_576, before.MaxContextTokens);
        Assert.False(before.NeedsCompaction);
        Assert.Equal(LlmContextMeasurementSource.Mixed, after.Source);
        Assert.True(after.UsedContextTokens >= 5_001);
        Assert.Equal(InferenceSupport.Supported, (await session.GetCapabilitiesAsync()).SupportsContextStatus);
    }

    [Fact]
    public async Task CompactionAtTheTriggerOpensANewConversationWithTheCompactedHistory()
    {
        var agy = new ScriptedAgy();
        await using ILlmAgentSession session = await StartAsync(agy, LongHistory());

        IReadOnlyList<LlmAgentEvent> events = await ReadAllAsync(session.RunTurnAsync(Turn("Continue")));

        Assert.True(events.OfType<LlmAgentContextStatusEvent>().First().Status.NeedsCompaction);
        LlmAgentCompactedEvent compacted = Assert.Single(events.OfType<LlmAgentCompactedEvent>());
        Assert.Equal(2, compacted.DroppedMessageCount);
        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        Assert.Equal(2, agy.Invocations.Count);
        Assert.Empty(agy.Writes[0]);
        string sent = Assert.Single(agy.Writes[1]);
        Assert.Contains(Summary, sent, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha alpha", sent, StringComparison.Ordinal);
        Assert.Equal("conversation-1", session.ThreadId);
        Assert.All(events, item => Assert.Equal("conversation-1", item.ThreadId));
    }

    [Fact]
    public async Task CompactNowOpensANewConversationWithTheCompactedHistory()
    {
        var agy = new ScriptedAgy();
        await using ILlmAgentSession session = await StartAsync(agy, LongHistory());

        LlmAgentCompactedEvent? compacted = await session.CompactNowAsync();
        await ReadAllAsync(session.RunTurnAsync(Turn("Continue")));

        Assert.NotNull(compacted);
        Assert.Equal(2, compacted.DroppedMessageCount);
        Assert.Equal(2, agy.Invocations.Count);
        Assert.Contains(Summary, Assert.Single(agy.Writes[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeResumeIsUnsupportedOnceCompactionMovedTheConversation()
    {
        var agy = new ScriptedAgy();
        await using ILlmAgentSession session = await StartAsync(agy, LongHistory());
        InferenceSupport before = (await session.GetCapabilitiesAsync()).SupportsNativeResume;

        await session.CompactNowAsync();

        Assert.Equal(InferenceSupport.Supported, before);
        Assert.Equal(InferenceSupport.Unsupported, (await session.GetCapabilitiesAsync()).SupportsNativeResume);
    }

    [Fact]
    public async Task AgyCheckpointIsReportedAsACompaction()
    {
        var agy = new ScriptedAgy { Checkpoint = true };
        await using ILlmAgentSession session = await StartAsync(agy, new());

        IReadOnlyList<LlmAgentEvent> events = await ReadAllAsync(session.RunTurnAsync(Turn("Hello")));

        LlmAgentCompactedEvent compacted = Assert.Single(events.OfType<LlmAgentCompactedEvent>());
        Assert.Null(compacted.DroppedMessageCount);
        Assert.Single(agy.Invocations);
    }

    private static Task<ILlmAgentSession> StartAsync(ScriptedAgy agy, LlmAgentContext context, bool capped = true)
    {
        // A 20,000-token cap with a 1,000-token reserve puts the trigger at 16,150 tokens; uncapped, the model's window applies.
        var chat = new AntigravityCliLlmClient(new SummaryRunner());
        return new AntigravityAgentClient(agy, chat, new LearnedModelContext()).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = Model,
            Runtime = new() { WorkingDirectory = Environment.CurrentDirectory, Policy = new LlmAgentProviderRuntimePolicy() },
            Context = context,
            ExecutionOptions = capped ? new() { ContextPolicy = new() { MaxContextTokens = 20_000, ReservedOutputTokens = 1_000 } } : null
        });
    }

    /// <summary>A turn of about 20,000 tokens, above the trigger on its own, and a short later turn.</summary>
    private static LlmAgentContext LongHistory() => new()
    {
        Entries =
        [
            new("e1", "t0", new("user", string.Join(' ', Enumerable.Repeat("alpha", 20_000)))),
            new("e2", "t0", new("assistant", "Noted.")),
            new("e3", "t1", new("user", "Thanks.")),
            new("e4", "t1", new("assistant", "You are welcome."))
        ]
    };

    private static LlmAgentTurnRequest Turn(string text) => new() { Input = [new LlmAgentTextContent(text)] };

    private static LlmConnection Connection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.GoogleAntigravityCli,
        Endpoint = "agy"
    };

    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        var results = new List<T>();
        await foreach (T item in source) results.Add(item);
        return results;
    }

    /// <summary>Answers every summary side request with <see cref="Summary"/>.</summary>
    private sealed class SummaryRunner : IAntigravityCliRunner
    {
        public Task<AntigravityCliProcessResult> RunAsync(AntigravityCliInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AntigravityCliProcessResult(0, new JsonObject
            {
                ["event"] = "result",
                ["result"] = new JsonObject { ["status"] = "SUCCESS", ["response"] = Summary }
            }.ToJsonString(), string.Empty));
    }

    /// <summary>
    /// AGY processes that open conversation-1, conversation-2 and so on, and answer each input with one model response
    /// that reports <see cref="InputTokens"/>, after a checkpoint step when <see cref="Checkpoint"/> is set.
    /// </summary>
    private sealed class ScriptedAgy : IAntigravityCliSessionProcessFactory
    {
        public List<IReadOnlyList<string>> Invocations { get; } = [];

        // The inputs each started process received, in start order.
        public List<List<string>> Writes { get; } = [];

        public int InputTokens { get; init; } = 100;

        public bool Checkpoint { get; init; }

        public IAntigravityCliSessionProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
        {
            Invocations.Add(arguments);
            var writes = new List<string>();
            Writes.Add(writes);
            return new Process(this, $"conversation-{Invocations.Count}", writes);
        }

        private sealed class Process : IAntigravityCliSessionProcess
        {
            private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
            private readonly ScriptedAgy _owner;
            private readonly string _id;
            private readonly List<string> _writes;
            private int _step;

            public Process(ScriptedAgy owner, string id, List<string> writes)
            {
                (_owner, _id, _writes) = (owner, id, writes);
                _lines.Writer.TryWrite(new JsonObject { ["event"] = "init", ["conversation_id"] = id }.ToJsonString());
            }

            public ValueTask WriteAsync(string text, CancellationToken cancellationToken = default)
            {
                _writes.Add(text);
                Step("user_input", null);
                if (_owner.Checkpoint) Step("checkpoint", 3_000);
                Step("agent_response", _owner.InputTokens);
                _lines.Writer.TryWrite(new JsonObject
                {
                    ["event"] = "result",
                    ["result"] = new JsonObject { ["conversation_id"] = _id, ["status"] = "SUCCESS", ["response"] = "ok" }
                }.ToJsonString());
                return ValueTask.CompletedTask;
            }

            private void Step(string type, int? inputTokens)
            {
                var step = new JsonObject
                {
                    ["conversation_id"] = _id,
                    ["step_index"] = _step++,
                    ["state"] = "DONE",
                    ["step_type"] = type
                };
                if (inputTokens is int input) step["usage"] = new JsonObject { ["input_tokens"] = input, ["output_tokens"] = 1 };
                _lines.Writer.TryWrite(new JsonObject { ["event"] = "step_update", ["step_update"] = step }.ToJsonString());
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
}
