using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class RequestAccountingTests
{
    [Fact]
    public async Task StandaloneUsesActualProjectionAndResolvesLimitsWithoutGenerationOrCredentialWrites()
    {
        using var handler = new MetadataHandler();
        using var http = new HttpClient(handler);
        var client = new KnownModelResponsesClient(http);
        var connection = Connection();
        var source = new Source(connection);
        var service = new LlmProviderService(source, [client], [new ForbiddenRefresh()]);
        var request = Request() with
        {
            Instructions = "Summarize this material as data.",
            MaxOutputTokens = 100,
            Tools = [new() { Name = "echo", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }],
            ToolChoice = new() { Mode = LlmToolChoiceMode.None }
        };
        var policy = new LlmAgentContextPolicy { MaxContextTokens = 800, ReservedOutputTokens = 50, SafetyMarginTokens = 20 };
        var before = JsonSerializer.Serialize(request);
        var accounting = await service.MeasureRequestAsync("selected", request, policy);
        Assert.True(accounting.IsSupported);
        Assert.Equal(client.EstimateInputTokens(connection, request), accounting.UsedContextTokens);
        Assert.Equal(1000, accounting.ModelMaxContextTokens);
        Assert.Equal(200, accounting.ModelMaxOutputTokens);
        Assert.Equal(800, accounting.MaxContextTokens);
        Assert.Equal(100, accounting.ReservedOutputTokens);
        Assert.Equal(20, accounting.SafetyMarginTokens);
        Assert.Equal(680, accounting.InputBudgetTokens);
        Assert.Equal(Math.Max(0, 680 - accounting.UsedContextTokens!.Value), accounting.AvailableInputTokens);
        Assert.Equal(LlmContextMeasurementSource.Estimated, accounting.Source);
        Assert.True(accounting.IsEstimated);
        Assert.Equal(LlmOutputLimitEnforcement.ProviderEnforced, accounting.OutputLimitEnforcement);
        Assert.Null(accounting.ContextRevision);
        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Equal(50, policy.ReservedOutputTokens);
        Assert.Single(handler.Methods);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public async Task UnknownMetadataDoesNotAcquireInventedLimitsAndCancellationIsPreserved()
    {
        using var handler = new MetadataHandler { Fail = true };
        using var http = new HttpClient(handler);
        var service = new LlmProviderService(new Source(Connection()), [new KnownModelResponsesClient(http)]);
        var unknown = await service.MeasureRequestAsync("selected", Request());
        Assert.True(unknown.IsSupported);
        Assert.Null(unknown.ModelMaxContextTokens);
        Assert.Null(unknown.MaxContextTokens);
        Assert.Null(unknown.InputBudgetTokens);
        Assert.Null(unknown.NeedsCompaction);
        Assert.NotNull(unknown.UsedContextTokens);
        Assert.Contains("metadata is unavailable", unknown.Diagnostic);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.MeasureRequestAsync("selected", Request(), cancellationToken: cancel.Token));
        Assert.Single(handler.Methods);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingAndUncountableAttachmentsRemainUnknown(bool omitted)
    {
        using var handler = new MetadataHandler();
        using var http = new HttpClient(handler);
        var service = new LlmProviderService(new Source(Connection()), [new KnownModelResponsesClient(http)]);
        var message = omitted
            ? new LlmChatMessage("user", "read") { OmittedAttachments = [new(LlmBinaryKind.Document, "doc.txt", 0)] }
            : new LlmChatMessage("user", "read") { Attachments = [new() { Kind = LlmAttachmentKind.Document, FileName = "doc.txt", Data = Encoding.UTF8.GetBytes("text") }] };
        var result = await service.MeasureRequestAsync("selected", Request() with { Messages = [message] });
        Assert.True(result.IsSupported);
        Assert.Null(result.UsedContextTokens);
        Assert.Null(result.NeedsCompaction);
        Assert.Equal(LlmContextMeasurementSource.Unknown, result.Source);
        Assert.NotNull(result.InputBudgetTokens);
    }

    [Fact]
    public async Task CodexRejectsOutputCapBeforeHttpAndAdvertisesItThroughBothContracts()
    {
        using var handler = new MetadataHandler();
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        var source = new Source(DirectCodexTests.Connection());
        var service = new LlmProviderService(source, [client]);
        var request = Request() with { MaxOutputTokens = 100 };
        var result = await service.MeasureRequestAsync("selected", request);
        Assert.False(result.IsSupported);
        Assert.Equal(LlmOutputLimitEnforcement.Unsupported, result.OutputLimitEnforcement);
        Assert.Contains("reserve is not an output cap", result.Diagnostic);
        Assert.Equal(LlmOutputLimitEnforcement.Unsupported,
            (await service.Bind("selected").GetCapabilitiesAsync("model")).OutputLimitEnforcement);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.CompleteAsync("selected", request));
        Assert.Empty(handler.Methods);
    }

    [Fact]
    public async Task MissingEstimatorIsUnsupportedNotZeroAndCompatibleEndpointEnforcementIsUnknown()
    {
        using var handler = new MetadataHandler();
        using var http = new HttpClient(handler);
        var connection = Connection() with { Transport = LlmTransport.OpenAiCompatibleChat };
        var client = new OpenAiCompatibleChatLlmClient(http);
        var service = new LlmProviderService(new Source(connection), [client]);
        var result = await service.MeasureRequestAsync("selected", Request());
        Assert.False(result.IsSupported);
        Assert.Null(result.UsedContextTokens);
        Assert.Equal(LlmOutputLimitEnforcement.Unknown, result.OutputLimitEnforcement);
        Assert.Empty(handler.Methods);
    }

    [Fact]
    public async Task CodexAccountingDoesNotRefreshExpiredOAuthOrCountEncryptedReasoning()
    {
        using var handler = new MetadataHandler();
        using var http = new HttpClient(handler);
        var connection = DirectCodexTests.Connection() with
        {
            Credential = new LlmCredential { OAuth = new() { AccessToken = "expired", AccountId = "account", ExpiresAtUnixSeconds = 1 } }.Serialize()
        };
        var state = ProviderResponseStateCodec.Encode(connection, "model",
            System.Text.Json.Nodes.JsonNode.Parse("""[{"type":"reasoning","id":"reason","encrypted_content":"opaque","summary":[]}]""")!.AsArray());
        var service = new LlmProviderService(new Source(connection), [new OpenAiCodexLlmClient(http)],
            [new ForbiddenRefresh { Transport = LlmTransport.OpenAiCodex }]);
        var result = await service.MeasureRequestAsync("selected", Request() with
        { Messages = [new("assistant", "") { ResponseState = state }, new("user", "summarize")] },
            new() { MaxContextTokens = 1000, ReservedOutputTokens = 100, SafetyMarginTokens = 10 });
        Assert.True(result.IsSupported);
        Assert.Null(result.UsedContextTokens);
        Assert.Null(result.NeedsCompaction);
        Assert.Equal(LlmContextMeasurementSource.Unknown, result.Source);
        Assert.Equal(890, result.InputBudgetTokens);
        Assert.Single(handler.Methods);
    }

    [Fact]
    public async Task KnownLimitsCannotBeEnlargedAndReserveCannotUnderbudgetRequestedCap()
    {
        using var handler = new MetadataHandler();
        using var http = new HttpClient(handler);
        var service = new LlmProviderService(new Source(Connection()), [new KnownModelResponsesClient(http)]);
        await Assert.ThrowsAsync<ArgumentException>(() => service.MeasureRequestAsync("selected", Request(), new() { MaxContextTokens = 1001 }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.MeasureRequestAsync("selected", Request() with { MaxOutputTokens = 201 }));
        var result = await service.MeasureRequestAsync("selected", Request() with { MaxOutputTokens = 100 },
            new() { ReservedOutputTokens = 150, SafetyMarginTokens = 0 });
        Assert.Equal(150, result.ReservedOutputTokens);
        Assert.Equal(850, result.InputBudgetTokens);
    }

    [Fact]
    public void BudgetArithmeticHandlesExtremeFiniteValuesWithoutOverflow()
    {
        var accounting = ManagedAgentPolicy.Account("model", long.MaxValue,
            new() { MaxContextTokens = long.MaxValue, ReservedOutputTokens = long.MaxValue, SafetyMarginTokens = long.MaxValue }, null);
        Assert.Equal(0, accounting.InputBudgetTokens);
        Assert.Equal(0, accounting.AvailableInputTokens);
        Assert.True(accounting.NeedsCompaction);
    }

    private static LlmChatRequest Request() => new() { ModelId = "model", Messages = [new("user", "hello")] };
    private static LlmConnection Connection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.OpenAiResponses,
        Endpoint = "https://provider.test/v1",
        Credential = new LlmCredential { ApiKey = "test-key" }.Serialize()
    };
    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public int Reads { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult<LlmConnection?>(connection);
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Accounting must not persist credentials.");
    }
    private sealed class ForbiddenRefresh : ILlmAuthenticator
    {
        public LlmTransport Transport { get; init; } = LlmTransport.OpenAiResponses;
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Accounting must not refresh credentials.");
    }
    // Controlled catalog metadata, not invented limits for the real OpenAI model catalog.
    private sealed class KnownModelResponsesClient(HttpClient http) : ResponsesLlmClientBase(http)
    {
        public override LlmTransport Transport => LlmTransport.OpenAiResponses;
        public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.OpenAiApiKey;
        protected override LlmProviderCapabilities DefaultCapabilities { get; } =
            new OpenAiResponsesLlmClient(http).GetCapabilities(Connection());
        public override async Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection,
            CancellationToken cancellationToken = default) =>
            (await base.ListModelsAsync(connection, cancellationToken))
                .Select(model => model with { MaxContextTokens = 1000, MaxOutputTokens = 200 }).ToArray();
    }

    private sealed class MetadataHandler : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];
        public bool Fail { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            Assert.Equal(HttpMethod.Get, request.Method);
            if (Fail) { throw new HttpRequestException("private error must not be exposed"); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"model","context_window":1000,"max_output_tokens":200}]}""", Encoding.UTF8, "application/json")
            });
        }
    }
}
