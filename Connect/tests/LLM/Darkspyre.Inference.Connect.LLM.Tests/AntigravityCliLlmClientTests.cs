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
