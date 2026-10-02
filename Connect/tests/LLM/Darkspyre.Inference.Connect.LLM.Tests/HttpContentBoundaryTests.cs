using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HttpContentBoundaryTests
{
    [Theory]
    [InlineData(false, "string")]
    [InlineData(false, "bytes")]
    [InlineData(false, "copy")]
    [InlineData(false, "stream")]
    [InlineData(true, "string")]
    [InlineData(true, "bytes")]
    [InlineData(true, "copy")]
    [InlineData(true, "stream")]
    public async Task BufferedConsumptionMustUseSdkOwnedFence(bool explicitBuffer, string consumption)
    {
        var services = new ServiceCollection(); services.AddLlmConnect();
        services.AddHttpClient("OpenAiResponsesLlmClient").ConfigurePrimaryHttpMessageHandler(() => new BufferedHandler());
        await using ServiceProvider root = services.BuildServiceProvider(); SdkHttpGenerationPool pool = root.GetRequiredService<SdkHttpGenerationPool>();
        using HttpClient client = pool.CreateClient("OpenAiResponsesLlmClient");
        using HttpResponseMessage response = await client.GetAsync("https://controlled.invalid/", explicitBuffer ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead);
        if (explicitBuffer) await response.Content.LoadIntoBufferAsync();
        Task retirement = pool.DisposeAsync().AsTask();
        try
        {
            Assert.False(retirement.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                switch (consumption)
                {
                    case "string": _ = await response.Content.ReadAsStringAsync(); break;
                    case "bytes": _ = await response.Content.ReadAsByteArrayAsync(); break;
                    case "copy": using (var destination = new MemoryStream()) await response.Content.CopyToAsync(destination); break;
                    case "stream": await using (Stream stream = await response.Content.ReadAsStreamAsync()) { _ = await stream.ReadAsync(new byte[1]); } break;
                    default: throw new InvalidOperationException();
                }
            });
        }
        finally { response.Dispose(); await retirement.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private sealed class BufferedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("actual buffered body") });
    }
}
