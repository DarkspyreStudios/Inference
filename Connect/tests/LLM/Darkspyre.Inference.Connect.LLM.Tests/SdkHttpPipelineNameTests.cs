using System.Net;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Logging;

namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>Each Connect library records the HTTP pipeline names it registers, and the SDK HTTP pool checks the names a
/// host configured against them when the container first creates it.</summary>
public sealed class SdkHttpPipelineNameTests : IDisposable
{
    private readonly TestDataFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>A logging configuration for a name no Connect library registered is refused when the pool is created.</summary>
    [Fact]
    public void AConfiguredNameNoLibraryRegisteredIsRefusedWhenThePoolIsCreated()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILlmDataFolder>(_folder);
        services.AddLlmConnect(http => http.ConfigureLogging("NotAPipeline", logging => logging.AddDefaultLogger()));
        using ServiceProvider container = services.BuildServiceProvider();

        ArgumentException refusal = Assert.Throws<ArgumentException>(() => container.GetRequiredService<SdkHttpGenerationPool>());

        Assert.Equal("name", refusal.ParamName);
        Assert.StartsWith("The HTTP pipeline name 'NotAPipeline' is not registered by a Connect library.", refusal.Message,
            StringComparison.Ordinal);
    }

    /// <summary>A host may configure a pipeline before the library that registers it: the Codex client's logger set up
    /// ahead of AddLlmConnect is accepted and runs.</summary>
    [Fact]
    public async Task ANameConfiguredBeforeItsLibraryRegistersIsAcceptedAndItsLoggerRuns()
    {
        var logger = new CountingLogger();
        using var primary = new OkHandler();
        var services = new ServiceCollection();
        services.AddSingleton<ILlmDataFolder>(_folder);
        services.ConfigureInferenceHttpPipelines(http =>
            http.ConfigureLogging(nameof(OpenAiCodexLlmClient), logging => logging.AddLogger(_ => logger)));
        services.AddHttpClient(nameof(OpenAiCodexLlmClient)).ConfigurePrimaryHttpMessageHandler(() => primary);
        services.AddLlmConnect();
        await using ServiceProvider container = services.BuildServiceProvider();

        using HttpClient client = container.GetRequiredService<SdkHttpGenerationPool>().CreateClient(nameof(OpenAiCodexLlmClient));
        using HttpResponseMessage response = await client.GetAsync(new Uri("https://provider.test/ping"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, logger.Started);
        Assert.Equal(1, logger.Stopped);
    }

    private sealed class CountingLogger : IHttpClientLogger
    {
        public int Started { get; private set; }
        public int Stopped { get; private set; }
        public object? LogRequestStart(HttpRequestMessage request) { Started++; return null; }
        public void LogRequestStop(object? context, HttpRequestMessage request, HttpResponseMessage response, TimeSpan elapsed) => Stopped++;
        public void LogRequestFailed(object? context, HttpRequestMessage request, HttpResponseMessage? response, Exception exception,
            TimeSpan elapsed) { }
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
