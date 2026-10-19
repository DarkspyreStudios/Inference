using System.Net;
using System.Text;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ResponsesHookTests
{
    /// <summary>A response that ends incomplete because the context is too long is a context overflow, so the session
    /// compacts and resends.</summary>
    [Fact]
    public async Task IncompleteForContextLengthIsAContextOverflow()
    {
        using var handler = new DirectCodexTests.Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"r1","model":"model","status":"incomplete","incomplete_details":{"reason":"context_length_exceeded"},"output":[]}""",
                Encoding.UTF8, "application/json")
        });
        var client = new HookedClient(new HttpClient(handler));
        LlmConnection connection = DirectCodexTests.OpenAiConnection("https://provider.example.test/v1") with
        {
            CapabilityOverrides = new() { SupportsStreaming = InferenceSupport.Unsupported }
        };
        var request = new LlmChatRequest { ModelId = "model", Messages = [new("user", "hello")] };

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(async () =>
        {
            await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { }
        });
        Assert.True(failure.IsContextOverflow);
    }

    private sealed class HookedClient(HttpClient http) : ResponsesLlmClientBase(http)
    {
        public override LlmTransport Transport => LlmTransport.OpenAiResponses;
        public override LlmProviderDefinition ProviderDefinition => OpenAiApiDefinition.Value;
        protected override LlmProviderCapabilities DefaultCapabilities { get; } = new();
        protected override bool ReasonsByDefault(LlmConnection connection, string modelId) => false;
    }
}
