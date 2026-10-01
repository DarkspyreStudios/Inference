using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HostedFacadeSnapshotTests
{
    public static IEnumerable<object[]> Cases()
    {
        for (int boundary = 0; boundary < 3; boundary++)
            for (int operation = 0; operation < 4; operation++)
                if (boundary != 1 || operation != 3) yield return [boundary, operation];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AdmissionFreezesHostedDataBeforeResolutionRefreshAndCatalog(int boundary, int operation)
    {
        var gate = new Gate(boundary);
        var source = new Source(gate);
        var client = new Client(gate);
        var authenticator = new Authenticator(gate);
        var authorization = new Authorization();
        List<string> filters = ["original-tool"];
        var headers = new Dictionary<string, string> { ["header"] = "original-header" };
        var descriptor = new LlmRemoteMcpTool("https://example.test/mcp", "original-server", Authorization: authorization)
        { AllowedTools = filters, Headers = headers };
        List<LlmHostedTool> tools = [descriptor];
        var request = new LlmChatRequest { ModelId = "model", HostedTools = tools, MaxHostedTurns = 3 };
        var service = new LlmProviderService(source, [client], [authenticator]);
        Task run = ExecuteAsync();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            filters[0] = "late-tool"; headers["header"] = "late-header";
            tools.Add(new LlmCodeExecutionTool());
            gate.Release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            var sent = Assert.IsType<LlmRemoteMcpTool>(Assert.Single(Assert.IsType<LlmChatRequest>(client.Sent).HostedTools));
            Assert.Equal("original-tool", Assert.Single(sent.AllowedTools!));
            Assert.Equal("original-header", sent.Headers!["header"]);
            Assert.Same(authorization, sent.Authorization);
            Assert.Equal(3, client.Sent!.MaxHostedTurns);
            Assert.Equal(2, request.HostedTools.Count);
        }
        finally
        {
            Task retirement = service.DisposeAsync().AsTask();
            gate.Release.TrySetResult();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            finally { await retirement.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        Assert.False(source.Disposed); Assert.False(authenticator.Disposed); Assert.False(authorization.Disposed);
        Assert.Equal(0, authorization.Calls);

        async Task ExecuteAsync()
        {
            if (operation == 1) await service.CompleteAsync("connection", request);
            else if (operation == 2) await service.CountInputTokensAsync("connection", request);
            else if (operation == 3) await service.MeasureRequestAsync("connection", request);
            else await foreach (var unused in service.StreamAsync("connection", request)) { }
        }
    }

    private sealed class Gate(int boundary)
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task WaitAsync(int stage, CancellationToken token)
        {
            if (stage != boundary) return;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
        }
    }

    private sealed class Source(Gate gate) : ILlmConnectionSource, IDisposable
    {
        internal bool Disposed { get; private set; }
        public async Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(0, cancellationToken);
            return new()
            {
                Transport = LlmTransport.OpenAiResponses,
                Version = "1",
                Endpoint = "https://example.test",
                Credential = new LlmCredential { OAuth = new() { AccessToken = "controlled" } }.Serialize()
            };
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The unchanged fixture credential cannot be persisted.");
        public void Dispose() => Disposed = true;
    }

    private sealed class Authenticator(Gate gate) : ILlmAuthenticator, IDisposable
    {
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        internal bool Disposed { get; private set; }
        public async Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { await gate.WaitAsync(1, cancellationToken); return credential; }
        public void Dispose() => Disposed = true;
    }

    private sealed class Client(Gate gate) : ILlmClient, ILlmCatalogCapabilities, ILlmProviderTokenCounter, ILlmContextEstimator
    {
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        public LlmProviderDefinition ProviderDefinition => new()
        {
            Id = "controlled",
            DisplayName = "Controlled",
            Description = "Controlled",
            Transport = Transport,
            DefaultEndpoint = "https://example.test",
            SetupSteps = []
        };
        internal LlmChatRequest? Sent { get; private set; }
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new()
        { SupportsStreaming = true, SupportsModelListing = true, HostedTools = new HashSet<LlmHostedToolKind> { LlmHostedToolKind.RemoteMcp, LlmHostedToolKind.CodeExecution } };
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Sent = request; await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            yield return new LlmChatCompletionEvent(new() { Text = "actual", Status = LlmCompletionStatus.Completed, Diagnostics = new(200, null, null) });
        }
        public async Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
        { await gate.WaitAsync(2, cancellationToken); return [new("model", "Model")]; }
        public Task<LlmProviderInputTokenCount> CountInputTokensAsync(LlmConnection connection, LlmChatRequest request, CancellationToken cancellationToken = default)
        { Sent = request; return Task.FromResult(new LlmProviderInputTokenCount { ModelId = request.ModelId, InputTokens = 1 }); }
        public string? EstimateDiagnostic => null;
        public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request) { Sent = request; return new(1); }
    }

    private sealed class Authorization : IProviderAuthorization, IDisposable
    {
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        public ValueTask<ProviderAuthorizationHeader> GetAsync(CancellationToken cancellationToken = default)
        { Calls++; return ValueTask.FromResult(new ProviderAuthorizationHeader("Bearer", "controlled")); }
        public void Dispose() => Disposed = true;
    }
}
