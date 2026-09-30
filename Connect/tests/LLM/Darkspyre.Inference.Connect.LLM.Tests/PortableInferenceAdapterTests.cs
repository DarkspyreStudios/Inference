using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common.LLM;
using Common = Darkspyre.Inference.Common;
using CommonLlm = Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class PortableInferenceAdapterTests
{
    [Fact]
    public async Task BoundInferenceMirrorsRunningUsageCapabilityAndEvent()
    {
        var client = new RecordingClient { RunningUsage = new(4, 1, 5) };
        var service = new LlmProviderService(new ConnectionSource(), [client]);
        ILlmInference inference = service.Bind("primary");
        Assert.True((await inference.GetCapabilitiesAsync("model-a")).SupportsUsageDeltas);
        var events = new List<LlmStreamEvent>();
        await foreach (var item in inference.StreamAsync(new() { ModelId = "model-a" })) { events.Add(item); }
        Assert.Equal(client.RunningUsage, Assert.Single(events.OfType<LlmUsageDeltaEvent>()).Cumulative);
        Assert.IsType<LlmCompletionEvent>(events[^1]);
    }

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
    public async Task BoundInferenceKeepsAttachmentMediaTypesAndToolOutputs()
    {
        var client = new RecordingClient();
        var service = new LlmProviderService(new ConnectionSource(), [client]);
        var image = new LlmAttachment
        {
            Kind = LlmBinaryKind.Image,
            FileName = "chart",
            MediaType = "image/webp",
            Data = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }
        };
        var result = new LlmToolResult("call-1", LlmToolOutput.FromText("done"));

        await LlmInferenceExtensions.CompleteAsync(service.Bind("primary"), new LlmInferenceRequest
        {
            ModelId = "model-a",
            Messages = [new LlmMessage("user", "Look") { Attachments = [image] }, new LlmMessage("tool", "") { ToolResults = [result] }]
        });

        Assert.Same(image, Assert.Single(client.Request!.Messages[0].Attachments));
        Assert.Same(result, Assert.Single(client.Request.Messages[1].ToolResults));
        Assert.Equal("image/webp", Internal.LlmAttachments.ResolveMediaType(image));
        Assert.Equal("image/png", Internal.LlmAttachments.ResolveMediaType(image with { MediaType = null }));
        Assert.Throws<ArgumentException>(() => Internal.LlmAttachments.Validate(
            [image with { Kind = LlmBinaryKind.Audio }], supportsImages: true, supportsDocuments: true, "attachments"));
    }

    [Fact]
    public async Task ConnectionWithStreamingOffReportsItAndCompletes()
    {
        using var http = new HttpClient(new JsonHandler("""
            {"id":"chatcmpl-1","choices":[{"index":0,"message":{"role":"assistant","content":"Complete."},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}
            """));
        var source = new ConnectionSource { Overrides = new LlmCapabilityOverrides { SupportsStreaming = false } };
        var service = new LlmProviderService(source, [new Providers.OpenAiCompatibleChatLlmClient(http)]);
        CommonLlm.ILlmInference inference = service.Bind("primary");

        CommonLlm.LlmCapabilities capabilities = await inference.GetCapabilitiesAsync("model-a");
        CommonLlm.LlmInferenceResult result = await CommonLlm.LlmInferenceExtensions.CompleteAsync(
            inference,
            new CommonLlm.LlmInferenceRequest { ModelId = "model-a", Messages = [new CommonLlm.LlmMessage("user", "Hi")] });

        Assert.False(capabilities.SupportsStreaming);
        Assert.Equal("Complete.", result.Text);
        Assert.Equal(CommonLlm.LlmCompletionStatus.Completed, result.Status);
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
        public LlmCapabilityOverrides? Overrides { get; init; }

        public Task<LlmConnection?> GetConnectionAsync(
            string connectionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(new LlmConnection
            {
                Version = "v1",
                Transport = LlmTransport.OpenAiCompatibleChat,
                Endpoint = "https://provider.test/v1",
                CapabilityOverrides = Overrides
            });

        public Task SaveCredentialAsync(
            string connectionId,
            string credentialJson,
            string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class JsonHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class RecordingClient : ILlmClient
    {
        public LlmChatRequest? Request { get; private set; }

        public Exception? Failure { get; init; }
        public LlmUsage? RunningUsage { get; init; }

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
            new() { SupportsStreaming = true, SupportsUsageDeltas = RunningUsage is not null };

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            LlmConnection connection,
            LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Request = request;
            if (Failure is not null)
                throw Failure;

            yield return new LlmTextDeltaEvent("Hello back");
            if (RunningUsage is { } usage) { yield return new LlmUsageDeltaEvent(usage); }
            yield return new LlmChatCompletionEvent(new LlmChatResult
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
