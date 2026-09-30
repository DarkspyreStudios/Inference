using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class TokenCountingServiceTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static LlmConnection Connection => new()
    { Version = "1", Transport = LlmTransport.AnthropicMessages, Endpoint = "https://provider.invalid", Credential = new LlmCredential { ApiKey = "fixture" }.Serialize() };

    [Fact]
    public async Task ExplicitCountPreparesScopesAttachesCatalogAndDoesNotGenerateOrEstimate()
    {
        var source = new Source();
        using var http = new HttpClient();
        var client = new CountingClient(http);
        await using var service = new LlmProviderService(source, [client]);
        LlmConnection prepared = Connection with { ResolvedContinuationScope = "resolved-scope" };
        LlmContinuationState state = ProviderContinuationStateCodec.Encode(prepared, "model", JsonNode.Parse("""[{"type":"text","text":"prior"}]""")!.AsArray());
        var request = new LlmChatRequest
        { ModelId = "model", Messages = [new("assistant", "prior") { ContinuationState = state }, new("user", "next")] };
        LlmProviderInputTokenCount result = await service.CountInputTokensAsync("connection", request);
        Assert.Equal(17, result.InputTokens);
        Assert.Equal(0, result.OmittedContinuationStateCount);
        Assert.Equal("resolved-scope", client.LastConnection!.ResolvedContinuationScope);
        Assert.Single(client.LastConnection.Catalog!);
        Assert.Same(state, client.LastRequest!.Messages[0].ContinuationState);
        Assert.Equal(1, client.Preparations);
        Assert.Equal(1, client.Listings);
        Assert.Equal(1, client.Counts);
        Assert.Equal(0, client.Generations);
        Assert.Equal(0, client.Estimates);
        Assert.Equal(0, source.Writes);
        await service.MeasureRequestAsync("connection", request with { Messages = [new("user", "estimate only")] });
        Assert.Equal(1, client.Counts);
        Assert.Equal(1, client.Estimates);
        // Local measurement does not prepare credentials, so it cannot reuse a resolved-scope listing.
        Assert.Equal(2, client.Listings);
    }

    [Fact]
    public async Task CountProjectionReportsExplicitOmissionWithoutMutatingTheCaller()
    {
        using var http = new HttpClient();
        var client = new CountingClient(http);
        await using var service = new LlmProviderService(new Source(), [client]);
        LlmContinuationState state = ProviderContinuationStateCodec.Encode(Connection, "model",
            JsonNode.Parse("""[{"type":"text","text":"prior"}]""")!.AsArray());
        var request = new LlmChatRequest
        {
            ModelId = "model",
            UnboundContinuationState = LlmUnboundContinuationState.Omit,
            Messages = [new("assistant", "prior") { ContinuationState = state }, new("user", "next")]
        };
        LlmProviderInputTokenCount result = await service.CountInputTokensAsync("connection", request);
        Assert.Equal(1, result.OmittedContinuationStateCount);
        Assert.Null(client.LastRequest!.Messages[0].ContinuationState);
        Assert.Same(state, request.Messages[0].ContinuationState);
    }

    [Fact]
    public async Task MissingCounterReportsUnsupportedWithoutDiscoveryOrGeneration()
    {
        using var http = new HttpClient();
        var client = new UnsupportedClient(http);
        await using var service = new LlmProviderService(new Source(), [client]);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.CountInputTokensAsync("connection", new() { ModelId = "model" }));
        Assert.Equal(0, client.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetirementCancelsAndDrainsCountingAndRejectsLateResultsAndFailures(bool ignoreCancellation, bool fail)
    {
        using var http = new HttpClient();
        var client = new CountingClient(http) { Block = true, IgnoreCancellation = ignoreCancellation, Fail = fail };
        await using var service = new LlmProviderService(new Source(), [client]);
        Task<LlmProviderInputTokenCount> pending = service.CountInputTokensAsync("connection", new() { ModelId = "model" });
        await client.Started.Task.WaitAsync(Bound);
        Task retirement = service.DisposeAsync().AsTask();
        try
        {
            await client.Stopped.Task.WaitAsync(Bound);
            if (ignoreCancellation) Assert.False(retirement.IsCompleted);
            client.Release.TrySetResult();
            await retirement.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.CountInputTokensAsync("connection", new() { ModelId = "model" }));
            Assert.Equal(1, client.Counts);
        }
        finally { client.Release.TrySetResult(); }
    }

    private sealed class Source : ILlmConnectionSource
    {
        internal int Writes { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<LlmConnection?>(Connection); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        { Writes++; throw new InvalidOperationException("No credential write is expected."); }
    }

    private sealed class UnsupportedClient(HttpClient http) : LlmClientBase(http)
    {
        internal int Calls { get; private set; }
        public override LlmTransport Transport => LlmTransport.AnthropicMessages;
        public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.AnthropicApiKey;
        protected override LlmProviderCapabilities DefaultCapabilities => new() { SupportsModelListing = true };
        public override Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException(); }
        public override async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { Calls++; await Task.CompletedTask; yield break; }
    }

    private sealed class CountingClient(HttpClient http) : LlmClientBase(http), ILlmProviderTokenCounter,
        ILlmConnectionPreparation, ILlmCatalogCapabilities, ILlmContextEstimator
    {
        public override LlmTransport Transport => LlmTransport.AnthropicMessages;
        public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.AnthropicApiKey;
        protected override LlmProviderCapabilities DefaultCapabilities => new() { SupportsModelListing = true };
        internal int Counts { get; private set; }
        internal int Generations { get; private set; }
        internal int Estimates { get; private set; }
        internal int Preparations { get; private set; }
        internal int Listings { get; private set; }
        internal LlmConnection? LastConnection { get; private set; }
        internal LlmChatRequest? LastRequest { get; private set; }
        internal bool Block { get; init; }
        internal bool IgnoreCancellation { get; init; }
        internal bool Fail { get; init; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? EstimateDiagnostic => "Local estimate only.";
        public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request)
        { Estimates++; return new(5); }
        public Task<LlmConnection> PrepareAsync(LlmConnection connection, CancellationToken cancellationToken)
        { Preparations++; return Task.FromResult(connection with { ResolvedContinuationScope = "resolved-scope" }); }
        public async Task<LlmProviderInputTokenCount> CountInputTokensAsync(LlmConnection connection, LlmChatRequest request,
            CancellationToken cancellationToken = default)
        {
            Counts++;
            LastConnection = connection;
            LastRequest = request;
            if (Block)
            {
                using CancellationTokenRegistration registration = cancellationToken.Register(() => Stopped.TrySetResult());
                Started.TrySetResult();
                try { await Release.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : cancellationToken); }
                finally { if (cancellationToken.IsCancellationRequested) Stopped.TrySetResult(); }
            }
            if (Fail) throw new LlmProviderException("Controlled late count failure", Transport);
            return new() { ModelId = request.ModelId, InputTokens = 17 };
        }
        public override Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
        { Listings++; return Task.FromResult<IReadOnlyList<LlmModel>>([new("model", "Model")]); }
        public override async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { Generations++; await Task.CompletedTask; yield break; }
    }
}
