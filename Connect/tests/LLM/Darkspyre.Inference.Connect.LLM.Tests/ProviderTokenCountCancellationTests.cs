using System.Net;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ProviderTokenCountCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationRetainsTheActualProviderAccountingFailure(bool tokenize)
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new LlmProviderException("The provider rejected accounting.",
            LlmTransport.OpenAiResponses, HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(3));
        await using var service = new LlmProviderService(new Source(), [new FailingAccountingClient(cancellation, failure)]);

        OperationCanceledException canceled = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            tokenize ? service.TokenizeAsync("connection", "model", "text", cancellation.Token)
                : service.CountInputTokensAsync("connection", new() { ModelId = "model" }, cancellation.Token));

        Assert.Same(failure, canceled.InnerException);
        Assert.True(canceled.CancellationToken.IsCancellationRequested);
    }

    private sealed class Source : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(new()
            {
                Version = "1",
                Transport = LlmTransport.OpenAiResponses,
                Endpoint = "https://provider.example.test/v1",
                Credential = new LlmCredential { ApiKey = "test-key" }.Serialize()
            });

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FailingAccountingClient(CancellationTokenSource cancellation, Exception failure)
        : ILlmClient, ILlmProviderTokenCounter, ILlmTextTokenizerClient
    {
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        public LlmProviderDefinition ProviderDefinition => ProviderDefinitions.OpenAiApiKey;
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new();

        public Task<LlmProviderInputTokenCount> CountInputTokensAsync(LlmConnection connection, LlmChatRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return Task.FromException<LlmProviderInputTokenCount>(failure);
        }

        public Task<LlmTokenization> TokenizeAsync(LlmConnection connection, string modelId, string text,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return Task.FromException<LlmTokenization>(failure);
        }

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
