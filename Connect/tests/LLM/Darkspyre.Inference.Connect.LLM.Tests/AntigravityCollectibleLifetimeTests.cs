using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AntigravityCollectibleLifetimeTests
{
    [UnixFact]
    public void DirectSessionRetiresItsRealProcessAndCollectibleConnectCommonGeneration()
    {
        WeakReference generation = ExerciseGeneration();
        for (int attempt = 0; generation.IsAlive && attempt < 30; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive, "The retired direct Antigravity process/session retains its generation.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseGeneration()
    {
        string path = typeof(AntigravityCollectibleLifetimeTests).Assembly.Location;
        var generation = new ConnectGeneration(Path.GetDirectoryName(path)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly tests = generation.LoadFromAssemblyPath(path);
            Assembly connect = generation.LoadFromAssemblyPath(typeof(AntigravityAgentSession).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(LlmReasoningLevel).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(tests));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(connect));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(common));
            Assert.Equal(typeof(AntigravityAgentSession).Assembly.ManifestModule.ModuleVersionId, connect.ManifestModule.ModuleVersionId);
            Assert.Equal(typeof(LlmReasoningLevel).Assembly.ManifestModule.ModuleVersionId, common.ManifestModule.ModuleVersionId);
            MethodInfo scenario = tests.GetType(typeof(AntigravityCollectibleLifetimeTests).FullName!, true)!
                .GetMethod(nameof(ExerciseOwnedSessionAsync))!;
            ((Task)scenario.Invoke(null, null)!).WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
        finally { generation.Unload(); }
        return weak;
    }

    public static async Task ExerciseOwnedSessionAsync()
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(AntigravityCollectibleLifetimeTests).Assembly)!;
        Assert.True(generation.IsCollectible);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(AntigravityAgentSession).Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(LlmReasoningLevel).Assembly));
        await using var factory = new RealProcessFactory();
        ILlmAgentSession session = await AntigravityAgentSession.OpenAsync(factory, "/bin/sh", Environment.CurrentDirectory,
            new("gemini-pro", null, null), null,
            new() { Version = "v1", Transport = LlmTransport.GoogleAntigravityCli, Endpoint = "agy" }, CancellationToken.None);
        await using IAsyncEnumerator<LlmAgentEvent> observation = session.ObserveAsync().GetAsyncEnumerator();
        await using IAsyncEnumerator<LlmAgentEvent> turn = session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("hello")] }).GetAsyncEnumerator();
        Task<bool> observing = observation.MoveNextAsync().AsTask();
        try
        {
            Assert.True(await turn.MoveNextAsync());
            Assert.IsType<LlmAgentTurnStartedEvent>(turn.Current);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(turn.Current.GetType().Assembly));
            Assert.True(await turn.MoveNextAsync());
            Assert.IsType<LlmAgentActivityEvent>(turn.Current);
            Task retirement = session.DisposeAsync().AsTask();
            Assert.Same(retirement, session.DisposeAsync().AsTask());
            Assert.False(await observing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(retirement.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.MoveNextAsync().AsTask());
            await turn.DisposeAsync();
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("d2fb65d7-e4c7-484a-9565-09b2cd4551b2", session.ThreadId);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GetCapabilitiesAsync());
            await CliProcessTerminationTests.AssertGoneAsync(factory.ProcessId);
        }
        finally
        {
            await turn.DisposeAsync();
            await session.DisposeAsync();
        }
    }

    private sealed class RealProcessFactory : IAntigravityCliSessionProcessFactory, IAsyncDisposable
    {
        private readonly AntigravityCliSessionProcess _process;

        internal RealProcessFactory()
        {
            const string script = """
                printf '%s\n' '{"event":"init","conversation_id":"d2fb65d7-e4c7-484a-9565-09b2cd4551b2","init":{"tools":[]}}'
                IFS= read -r line
                printf '%s\n' '{"event":"step_update","step_update":{"step_index":0,"state":"DONE","step_type":"user_input"}}'
                printf '%s\n' '{"event":"step_update","step_update":{"step_index":1,"state":"ACTIVE","step_type":"agent_response","text_delta":"partial"}}'
                exec sleep 300
                """;
            ProcessStartInfo start = CliProcessLauncher.CreateStartInfo("/bin/sh", ["-c", script], Environment.CurrentDirectory);
            Process process = Process.Start(start) ?? throw new InvalidOperationException("The controlled fixture did not start.");
            ProcessId = process.Id;
            _process = new AntigravityCliSessionProcess(process);
        }

        internal int ProcessId { get; }
        public IAntigravityCliSessionProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory) => _process;
        public ValueTask DisposeAsync() => _process.DisposeAsync();
    }

    private sealed class ConnectGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
