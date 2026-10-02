using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Logging;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HttpGenerationConfigurationTests
{
    private const string Pipeline = "OpenAiResponsesLlmClient";

    [Fact]
    public void ActualRuntimeConfigurationRefusesJsonWithoutInvokingCallbacksAndPreservesNull()
    {
        var options = new InferenceHttpPipelineOptions(); InferenceHttpLoggingBuilder? logging = null; int callbacks = 0;
        options.ConfigureLogging(Pipeline, builder => { logging = builder; builder.AddLogger(_ => { callbacks++; throw new InvalidOperationException(); }); });
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(options, HttpConfigurationJsonContext.Default.InferenceHttpPipelineOptions));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{}", HttpConfigurationJsonContext.Default.InferenceHttpPipelineOptions));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(logging, HttpConfigurationJsonContext.Default.InferenceHttpLoggingBuilder));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{}", HttpConfigurationJsonContext.Default.InferenceHttpLoggingBuilder));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new HttpConfigurationEnvelope(options, logging), HttpConfigurationJsonContext.Default.HttpConfigurationEnvelope));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"Options\":{},\"Logging\":null}", HttpConfigurationJsonContext.Default.HttpConfigurationEnvelope));
        Assert.Equal("null", JsonSerializer.Serialize<InferenceHttpPipelineOptions>(null!, HttpConfigurationJsonContext.Default.InferenceHttpPipelineOptions));
        Assert.Equal("null", JsonSerializer.Serialize<InferenceHttpLoggingBuilder>(null!, HttpConfigurationJsonContext.Default.InferenceHttpLoggingBuilder));
        Assert.Null(JsonSerializer.Deserialize("null", HttpConfigurationJsonContext.Default.InferenceHttpPipelineOptions));
        Assert.Null(JsonSerializer.Deserialize("null", HttpConfigurationJsonContext.Default.InferenceHttpLoggingBuilder));
        Assert.Equal("{\"Options\":null,\"Logging\":null}", JsonSerializer.Serialize(new HttpConfigurationEnvelope(null, null), HttpConfigurationJsonContext.Default.HttpConfigurationEnvelope));
        Assert.Equal(0, callbacks);
        Assert.Equal(nameof(InferenceHttpPipelineOptions), options.ToString()); Assert.Equal(nameof(InferenceHttpLoggingBuilder), logging!.ToString());
    }

    [Fact]
    public async Task TwoContainersKeepIndependentPoolLoggerConfigurationAfterFirstRetires()
    {
        var services = new ServiceCollection(); int factories = 0;
        services.AddLlmConnect(options => options.ConfigureLogging(Pipeline, builder => builder.RemoveAllLoggers().AddLogger(_ => { factories++; return new EmptyLogger(); })));
        services.AddHttpClient(Pipeline).ConfigurePrimaryHttpMessageHandler(() => new EmptyHandler());
        await using ServiceProvider first = services.BuildServiceProvider(); await using ServiceProvider second = services.BuildServiceProvider();
        SdkHttpGenerationPool firstPool = first.GetRequiredService<SdkHttpGenerationPool>();
        using (HttpClient client = firstPool.CreateClient(Pipeline)) { }
        await first.DisposeAsync(); Assert.Equal(1, factories);
        SdkHttpGenerationPool secondPool = second.GetRequiredService<SdkHttpGenerationPool>(); Assert.NotSame(firstPool, secondPool);
        using HttpClient surviving = secondPool.CreateClient(Pipeline);
        using HttpResponseMessage response = await surviving.GetAsync("https://controlled.invalid/", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(2, factories);
    }

    private sealed class EmptyLogger : IHttpClientLogger
    {
        public object? LogRequestStart(HttpRequestMessage request) => null;
        public void LogRequestStop(object? context, HttpRequestMessage request, HttpResponseMessage response, TimeSpan elapsed) { }
        public void LogRequestFailed(object? context, HttpRequestMessage request, HttpResponseMessage? response, Exception exception, TimeSpan elapsed) { }
    }
    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}

internal sealed record HttpConfigurationEnvelope(InferenceHttpPipelineOptions? Options, InferenceHttpLoggingBuilder? Logging);

[JsonSerializable(typeof(InferenceHttpPipelineOptions))]
[JsonSerializable(typeof(InferenceHttpLoggingBuilder))]
[JsonSerializable(typeof(HttpConfigurationEnvelope))]
internal sealed partial class HttpConfigurationJsonContext : JsonSerializerContext;
