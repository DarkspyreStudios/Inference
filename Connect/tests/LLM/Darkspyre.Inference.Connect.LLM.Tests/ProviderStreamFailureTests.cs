using System.Net;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ProviderStreamFailureTests
{
    /// <summary>The llama.cpp server's too-long refusal is recognised on the OpenAI API with its reported size (B6).</summary>
    [Fact]
    public async Task LocalServerOverflowIsRecognisedWithItsSize()
    {
        LlmProviderException error = await RefusalAsync(HttpStatusCode.BadRequest,
            """{"error":{"code":400,"message":"request (140068 tokens) exceeds the available context size (131072 tokens), try increasing it","type":"exceed_context_size_error","n_prompt_tokens":140068,"n_ctx":131072}}""");

        Assert.True(error.IsContextOverflow);
        Assert.Equal(140068, error.ReportedInputTokens);
        Assert.Equal(131072, error.ReportedContextLimit);
    }

    /// <summary>OpenAI's unsupported refusals name the value (B4).</summary>
    [Theory]
    [InlineData("""{"error":{"message":"Unsupported parameter: 'temperature' is not supported with this model.","param":"temperature","code":"unsupported_parameter"}}""", "Temperature")]
    [InlineData("""{"error":{"message":"Unsupported value: 'reasoning.effort' does not support 'high'.","param":"reasoning.effort","code":"unsupported_value"}}""", "Reasoning")]
    public async Task UnsupportedRefusalNamesTheSetting(string body, string expected)
    {
        LlmProviderException error = await RefusalAsync(HttpStatusCode.BadRequest, body);

        Assert.Equal(Enum.Parse<LlmRefusedSettings>(expected), error.RefusedSettings);
        Assert.False(error.IsContextOverflow);
    }

    private static async Task<LlmProviderException> RefusalAsync(HttpStatusCode status, string body)
    {
        using var response = new HttpResponseMessage(status)
        { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        return await Assert.ThrowsAsync<LlmProviderException>(() => ProviderHttp.ThrowIfUnsuccessfulAsync(response,
            LlmTransport.OpenAiResponses, null, "OpenAI Responses request", [], OpenAiApiErrors.Reading, CancellationToken.None));
    }

    /// <summary>The OpenAI API client has no client timeout: a busy local server answers a request after more than
    /// HttpClient's default 100 seconds, and only the caller's cancellation ends it.</summary>
    [Fact]
    public void OpenAiApiClientHasNoClientTimeout()
    {
        using var folder = new TestDataFolder();
        var services = new ServiceCollection();
        services.AddLlmConnect();
        services.AddSingleton<ILlmDataFolder>(folder);
        using ServiceProvider container = services.BuildServiceProvider();

        using HttpClient client = container.GetRequiredService<SdkHttpGenerationPool>().CreateClient(nameof(OpenAiResponsesLlmClient));

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }
}
