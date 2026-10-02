using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AgentAdmissionSnapshotTests
{
    [Theory]
    [InlineData("start")]
    [InlineData("resume")]
    [InlineData("fork")]
    public async Task FacadeFreezesOrderedToolsAndContextBeforeSourceAwait(string path)
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        List<LlmAgentClientToolDefinition> tools = [new LlmAgentToolDefinition { Name = "original", InputSchema = schema.RootElement }, new LlmAgentShellTool(new([]))];
        List<LlmAgentContextEntry> entries = [new("entry", "turn", new("user", "original"))];
        var executor = new Executor();
        var callers = new LlmAgentCallerServices { ShellExecutor = executor };
        LlmAgentToolHandler handler = (_, _) => Task.FromResult(LlmToolOutput.FromText("unused"));
        var source = new Source(hold: true);
        var client = new Client();
        var facade = new LlmAgentProviderService(source, [client]);
        Task<ILlmAgentSession> opening = path switch
        {
            "start" => facade.StartSessionAsync("connection", new() { ModelId = "model", Tools = tools, Context = new() { Entries = entries }, CallerServices = callers, ToolHandler = handler }),
            "resume" => facade.ResumeSessionAsync("connection", "thread", new() { ModelId = "model", Tools = tools, CallerServices = callers, ToolHandler = handler }),
            _ => facade.ForkSessionAsync("connection", "thread", options: new() { ModelId = "model", Tools = tools, CallerServices = callers, ToolHandler = handler })
        };
        Task? retirement = null;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            tools.Clear(); entries.Clear(); schema.Dispose();
            source.Release.TrySetResult();
            await opening;
            var capturedTools = client.Start?.Tools ?? client.Resume!.Tools;
            Assert.Collection(capturedTools,
                tool => Assert.Equal("object", Assert.IsType<LlmAgentToolDefinition>(tool).InputSchema.GetProperty("type").GetString()),
                tool => Assert.IsType<LlmAgentShellTool>(tool));
            Assert.Equal("original", Assert.IsType<LlmAgentToolDefinition>(capturedTools[0]).Name);
            Assert.Same(executor, (client.Start?.CallerServices ?? client.Resume!.CallerServices)!.ShellExecutor);
            Assert.Same(handler, client.Start?.ToolHandler ?? client.Resume!.ToolHandler);
            if (path == "start") Assert.Equal("original", Assert.Single(client.Start!.Context!.Entries).Message.Content);
            retirement = facade.DisposeAsync().AsTask();
            await retirement;
            Assert.False(executor.Disposed); Assert.False(client.Disposed); Assert.False(source.Disposed);
            Assert.Equal(0, executor.Calls);
        }
        finally
        {
            retirement ??= facade.DisposeAsync().AsTask();
            source.Release.TrySetResult();
            await Drain(opening); await Drain(retirement);
        }
    }

    [Fact]
    public async Task DirectManagedOpeningFreezesToolsAndContextBeforeCatalogAwait()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        List<LlmAgentClientToolDefinition> tools = [new LlmAgentToolDefinition { Name = "original", InputSchema = schema.RootElement }];
        List<LlmAgentContextEntry> entries = [new("entry", "turn", new("user", "original"))];
        using var httpHandler = new Catalog();
        using var http = new HttpClient(httpHandler);
        var source = new Source(hold: false);
        var client = new GeminiApiLlmClient(http);
        var providers = new LlmProviderService(source, [client]);
        var agent = new ManagedGeminiApiAgentClient(client, providers);
        Task<ILlmAgentSession> opening = agent.StartSessionAsync("connection", Source.Connection, new()
        {
            ModelId = "gemini-3-flash-preview",
            Tools = tools,
            Context = new() { Entries = entries },
            ToolHandler = (_, _) => Task.FromResult(LlmToolOutput.FromText("unused"))
        });
        ILlmAgentSession? session = null;
        Task? retirement = null;
        try
        {
            await httpHandler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            tools.Clear(); tools.Add(new LlmAgentShellTool(new([]))); entries.Clear(); schema.Dispose();
            httpHandler.Release.TrySetResult();
            session = await opening;
            Assert.Equal("original", Assert.Single((await session.CaptureContextAsync()).Entries).Message.Content);
            await session.DisposeAsync();
            session = null;
            Assert.False(source.Disposed);
            Assert.Equal(1, httpHandler.Calls);
        }
        finally
        {
            retirement ??= providers.DisposeAsync().AsTask();
            httpHandler.Release.TrySetResult();
            await Drain(opening);
            if (session is null && opening.IsCompletedSuccessfully) session = await opening;
            if (session is not null) await Drain(session.DisposeAsync().AsTask());
            await Drain(retirement);
        }
    }

    private static async Task Drain(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) when (task.IsCompleted) { }
    }

    private sealed class Source(bool hold) : ILlmConnectionSource, IDisposable
    {
        internal static LlmConnection Connection => new()
        {
            Transport = LlmTransport.GoogleGeminiApi,
            Endpoint = GeminiApiLlmClient.DefaultEndpoint,
            Version = "version",
            Credential = new LlmCredential { ApiKey = "controlled" }.Serialize()
        };
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        public async Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); if (hold) await Release.Task; return Connection; }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }

    private sealed class Client : ILlmAgentResumeClient, ILlmAgentForkClient, ILlmContextEstimator, IDisposable
    {
        public LlmTransport Transport => LlmTransport.GoogleGeminiApi;
        internal LlmAgentSessionOptions? Start { get; private set; }
        internal LlmAgentResumeOptions? Resume { get; private set; }
        internal bool Disposed { get; private set; }
        public Task<ILlmAgentSession> StartSessionAsync(string connectionId, LlmConnection connection, LlmAgentSessionOptions options, CancellationToken cancellationToken = default)
        { Start = options; return Open(connection); }
        public Task<ILlmAgentSession> ResumeSessionAsync(string connectionId, LlmConnection connection, string threadId, LlmAgentResumeOptions? options = null, CancellationToken cancellationToken = default)
        { Resume = options; return Open(connection); }
        public Task<ILlmAgentSession> ForkSessionAsync(string connectionId, LlmConnection connection, string threadId, string? lastTurnId = null, LlmAgentResumeOptions? options = null, CancellationToken cancellationToken = default)
        { Resume = options; return Open(connection); }
        private async Task<ILlmAgentSession> Open(LlmConnection connection) => await ManagedAgentSession.CreateAsync(connection,
            new() { ModelId = "model" }, [], this, (_, token) => Empty(token), _ => new() { SupportsAgentSessions = true, Agent = ManagedAgentPolicy.Capabilities });
        public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request) => new(1);
        public string? EstimateDiagnostic => null;
        private static async IAsyncEnumerable<LlmStreamEvent> Empty([EnumeratorCancellation] CancellationToken token)
        { await Task.CompletedTask; token.ThrowIfCancellationRequested(); yield break; }
        public void Dispose() => Disposed = true;
    }

    private sealed class Executor : ILlmAgentShellExecutor, IDisposable
    {
        internal bool Disposed { get; private set; }
        internal int Calls { get; private set; }
        public Task<LlmShellCallResult> ExecuteAsync(LlmAgentShellRequest request, CancellationToken cancellationToken = default)
        { Calls++; throw new NotSupportedException(); }
        public void Dispose() => Disposed = true;
    }

    private sealed class Catalog : HttpMessageHandler
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Calls++; Entered.TrySetResult(); await Release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"models\":[{\"name\":\"models/gemini-3-flash-preview\",\"supportedGenerationMethods\":[\"generateContent\"]}]}") };
        }
    }
}
