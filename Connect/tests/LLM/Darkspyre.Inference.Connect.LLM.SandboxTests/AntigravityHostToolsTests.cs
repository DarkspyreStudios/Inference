using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.HostTools;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.LocalServices;

namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>
/// An Antigravity session's host tools against the real host-tools service in the test's data folder, with a scripted
/// AGY process. The first session with host tools in a process adds AGY's allow rule to the user's AGY settings when it
/// is missing, as the app does.
/// </summary>
public sealed class AntigravityHostToolsTests : IDisposable
{
    private readonly TestDataFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>AGY's MCP program finds the service of this app copy through the folder AGY's environment names.</summary>
    [Fact]
    public async Task SessionWithHostToolsStartsAgyWithTheDataFolder()
    {
        var process = new ScriptedAgy();
        try
        {
            await using ILlmAgentSession session = await HostToolAgents(process).StartSessionAsync("connection", Connection(), HostToolOptions());

            Assert.Equal(_folder.Path, Assert.Single(process.Environments)[HostToolsProtocol.FolderVariable]);
        }
        finally
        {
            await AntigravityHostTools.ReleaseAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A move's release stops the host-tools service, and a rolled-back move leaves the folder as it was. The next turn
    /// registers the session's tools again with the folder's service and restarts AGY with the same folder.
    /// </summary>
    [Fact]
    public async Task NextTurnAfterARolledBackMoveRegistersTheHostToolsAgainAndRestartsAgy()
    {
        var process = new ScriptedAgy();
        try
        {
            await using ILlmAgentSession session = await HostToolAgents(process).StartSessionAsync("connection", Connection(), HostToolOptions());
            await AntigravityHostTools.ReleaseAsync(CancellationToken.None);

            await foreach (LlmAgentEvent _ in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("Again")] })) { }

            Assert.Equal(2, process.Environments.Count);
            IReadOnlyDictionary<string, string> restarted = process.Environments[1];
            Assert.Equal(_folder.Path, restarted[HostToolsProtocol.FolderVariable]);
            string package = Path.Combine(Path.GetDirectoryName(typeof(HostToolsRuntime).Assembly.Location)!, HostToolsRuntime.PackageFolder);
            await using HostToolsClient joined = await HostToolsRuntime.ConnectAsync(package, new ServiceFolders(_folder.Path, null, CreateIfMissing: false));
            JsonElement tools = await joined.ListAsync(restarted[HostToolsProtocol.SessionVariable]);
            Assert.Equal("echo", tools.GetProperty("tools")[0].GetProperty("name").GetString());
        }
        finally
        {
            await AntigravityHostTools.ReleaseAsync(CancellationToken.None);
        }
    }

    private static LlmAgentSessionOptions HostToolOptions() => new()
    {
        ModelId = "auto",
        Runtime = new() { WorkingDirectory = Environment.CurrentDirectory, Policy = new LlmAgentProviderRuntimePolicy() },
        Tools = [new LlmAgentToolDefinition { Name = "echo", InputSchema = JsonElement.Parse("""{"type":"object"}""") }],
        ToolHandler = (_, _) => Task.FromResult(LlmToolOutput.FromText("echo"))
    };

    /// <summary>The first session with host tools in a process runs "agy mcp add", which succeeds here.</summary>
    private AntigravityAgentClient HostToolAgents(ScriptedAgy process)
    {
        var runner = new SucceedingRunner();
        return new(process, runner, new AntigravityCliLlmClient(runner, _folder), new LearnedModelContext(), _folder);
    }

    private static LlmConnection Connection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.GoogleAntigravityCli,
        Endpoint = "agy"
    };

    private sealed class SucceedingRunner : IAntigravityCliRunner
    {
        public Task<AntigravityCliProcessResult> RunAsync(AntigravityCliInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AntigravityCliProcessResult(0, string.Empty, string.Empty));
    }

    /// <summary>An AGY process that reports its conversation and ends each turn with a provider failure.</summary>
    private sealed class ScriptedAgy : IAntigravityCliSessionProcessFactory, IAntigravityCliSessionProcess
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        // Every process the factory starts records its environment here.
        private readonly List<IReadOnlyDictionary<string, string>> _environments;

        public ScriptedAgy() : this([]) { }

        private ScriptedAgy(List<IReadOnlyDictionary<string, string>> environments) => _environments = environments;

        public List<IReadOnlyDictionary<string, string>> Environments => _environments;

        public IAntigravityCliSessionProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment)
        {
            _environments.Add(environment);
            var process = new ScriptedAgy(_environments);
            process._lines.Writer.TryWrite("""{"event":"init","conversation_id":"conversation-1"}""");
            return process;
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
                    ["error"] = "The scripted process answers no turn."
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
