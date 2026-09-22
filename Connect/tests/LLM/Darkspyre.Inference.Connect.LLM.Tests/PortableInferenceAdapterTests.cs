using System.Runtime.CompilerServices;
using Common = Darkspyre.Inference.Common;
using CommonLlm = Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class PortableInferenceAdapterTests
{
    [Fact]
    public async Task BoundInferenceMapsTypedRequestResultAndProvenance()
    {
        var client = new RecordingClient();
        var service = new LlmProviderService(new ConnectionSource(), [client]);
        CommonLlm.ILlmInference inference = service.Bind("primary");

        CommonLlm.LlmInferenceResult result = await CommonLlm.LlmInferenceExtensions.CompleteAsync(
            inference,
            new CommonLlm.LlmInferenceRequest
            {
                ModelId = "model-a",
                Instructions = "Be concise.",
                Messages = [new CommonLlm.LlmMessage("user", "Hello")],
                MaxOutputTokens = 64
            });

        Assert.Equal("Hello back", result.Text);
        Assert.Equal(CommonLlm.LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("model-a", result.Provenance.ModelId);
        Assert.Equal("darkspyre.connect.llm", result.Provenance.ImplementationId);
        Assert.Equal("model-a", client.Request?.ModelId);
        Assert.Equal("Be concise.", client.Request?.Instructions);
        Assert.Equal("Hello", Assert.Single(client.Request!.Messages).Content);
    }

    [Fact]
    public async Task BoundInferenceMapsProviderFailureToPortableFailure()
    {
        var client = new RecordingClient { Failure = new NotSupportedException("not supported") };
        var service = new LlmProviderService(new ConnectionSource(), [client]);

        Common.InferenceException exception = await Assert.ThrowsAsync<Common.InferenceException>(
            () => CommonLlm.LlmInferenceExtensions.CompleteAsync(
                service.Bind("primary"),
                new CommonLlm.LlmInferenceRequest
                {
                    ModelId = "model-a"
                }));

        Assert.Equal(Common.InferenceFailureCode.UnsupportedFeature, exception.Code);
    }

    private sealed class ConnectionSource : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(
            string connectionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(new LlmConnection
            {
                Version = "v1",
                Transport = LlmTransport.OpenAiCompatibleChat,
                Endpoint = "https://provider.test/v1"
            });

        public Task SaveCredentialAsync(
            string connectionId,
            string credentialJson,
            string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingClient : ILlmClient
    {
        public LlmChatRequest? Request { get; private set; }

        public Exception? Failure { get; init; }

        public LlmTransport Transport => LlmTransport.OpenAiCompatibleChat;

        public LlmProviderDefinition ProviderDefinition { get; } = new()
        {
            Id = "test",
            DisplayName = "Test",
            Description = "Test provider",
            Transport = LlmTransport.OpenAiCompatibleChat,
            DefaultEndpoint = "https://provider.test/v1",
            SetupSteps = []
        };

        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) =>
            new() { SupportsStreaming = true };

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            LlmConnection connection,
            LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Request = request;
            if (Failure is not null)
                throw Failure;

            yield return new LlmTextDeltaEvent("Hello back");
            yield return new LlmCompletionEvent(new LlmChatResult
            {
                Text = "Hello back",
                Status = LlmCompletionStatus.Completed,
                Diagnostics = new LlmProviderDiagnostics(200, null, null)
            });
            await Task.CompletedTask;
        }

        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(
            LlmConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmModel>>([]);
    }
}
