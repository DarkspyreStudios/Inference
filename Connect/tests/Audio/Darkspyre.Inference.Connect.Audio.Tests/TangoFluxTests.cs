using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.Audio.Tests;

public sealed class TangoFluxTests
{
    [Theory]
    [InlineData(null, null, "{\"prompt\":\"\\u003C\\u0026\\u003E\",\"duration\":2.5,\"steps\":10,\"guidanceScale\":4.5}")]
    [InlineData(0, false, "{\"prompt\":\"\\u003C\\u0026\\u003E\",\"duration\":2.5,\"steps\":10,\"guidanceScale\":4.5,\"seed\":0,\"useCache\":false}")]
    public async Task GeneratedPayloadPreservesExactWebWireBytesAndOptionalNullOmission(int? seed, bool? cache, string expected)
    {
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal(expected, await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("application/json", request.Content.Headers.ContentType!.MediaType);
            return Wave();
        });
        using var http = new HttpClient(handler);
        await new TangoFluxHttpProvider(http).Bind(Connection()).GenerateAsync(new()
        { Prompt = "<&>", Duration = TimeSpan.FromSeconds(2.5), Seed = seed, UseCache = cache });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualCollectibleTangoFluxMetadataAndHttpCallerRetire(bool exerciseCaller)
    {
        WeakReference generation = ExerciseTangoGeneration(exerciseCaller);
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive, "TangoFlux metadata or settled HTTP callers retain the collectible generation.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseTangoGeneration(bool exerciseCaller)
    {
        string path = typeof(TangoFluxHttpProvider).Assembly.Location;
        var generation = new TangoGeneration(Path.GetDirectoryName(path)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly audio = generation.LoadFromAssemblyPath(path);
            Type owner = audio.GetType("Darkspyre.Inference.Connect.Audio.TangoFluxJsonContext", throwOnError: true)!;
            object context = owner.GetProperty("Default")!.GetValue(null)!;
            JsonTypeInfo metadata = (JsonTypeInfo)owner.GetProperty("TangoFluxGeneratePayload")!.GetValue(context)!;
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Type.Assembly));
            Assert.Same(context, metadata.Options.TypeInfoResolver);
            object payload = JsonSerializer.Deserialize("""
                {"PROMPT":"text","duration":"5","steps":"10","guidanceScale":"4.5","seed":null,"useCache":false,"future":true}
                """, metadata)!;
            Assert.Equal("{\"prompt\":\"text\",\"duration\":5,\"steps\":10,\"guidanceScale\":4.5,\"useCache\":false}", JsonSerializer.Serialize(payload, metadata));
            if (exerciseCaller)
            {
                using var handler = new Handler(async (request, token) =>
                {
                    Assert.Equal("{\"prompt\":\"text\",\"duration\":5,\"steps\":10,\"guidanceScale\":4.5}", await request.Content!.ReadAsStringAsync(token));
                    return Wave();
                });
                using var http = new HttpClient(handler);
                Type providerType = audio.GetType(typeof(TangoFluxHttpProvider).FullName!, throwOnError: true)!;
                object provider = Activator.CreateInstance(providerType, http)!;
                Type connectionType = audio.GetType(typeof(AudioConnection).FullName!, throwOnError: true)!;
                object connection = Activator.CreateInstance(connectionType)!;
                connectionType.GetProperty("Endpoint")!.SetValue(connection, new Uri("https://provider.invalid"));
                object client = providerType.GetMethod("Bind")!.Invoke(provider, [connection])!;
                Type requestType = audio.GetType(typeof(AudioGenerationRequest).FullName!, throwOnError: true)!;
                object request = Activator.CreateInstance(requestType)!;
                requestType.GetProperty("Prompt")!.SetValue(request, "text");
                requestType.GetProperty("Duration")!.SetValue(request, TimeSpan.FromSeconds(5));
                Task operation = (Task)client.GetType().GetMethod("GenerateAsync")!.Invoke(client, [request, CancellationToken.None])!;
                operation.GetAwaiter().GetResult();
                object result = operation.GetType().GetProperty("Result")!.GetValue(operation)!;
                object provenance = result.GetType().GetProperty("Provenance")!.GetValue(result)!;
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(provenance.GetType().Assembly));
                Assert.Equal(1, handler.Calls);
            }
        }
        finally { generation.Unload(); }
        return weak;
    }

    private sealed class TangoGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    [Fact]
    public async Task MapsExplicitGenerationParametersAndReturnsAudioWithoutInventingDuration()
    {
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://example.test/api/custom-generate", request.RequestUri!.AbsoluteUri);
            Assert.Equal("protected", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            using JsonDocument body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(token), cancellationToken: token);
            Assert.Equal("rain on leaves", body.RootElement.GetProperty("prompt").GetString());
            Assert.Equal(2.5, body.RootElement.GetProperty("duration").GetDouble());
            Assert.Equal(23, body.RootElement.GetProperty("steps").GetInt32());
            Assert.Equal(3.75, body.RootElement.GetProperty("guidanceScale").GetDouble());
            Assert.Equal(42, body.RootElement.GetProperty("seed").GetInt32());
            Assert.False(body.RootElement.GetProperty("useCache").GetBoolean());
            return Wave();
        });
        using var http = new HttpClient(handler);
        var headers = new Dictionary<string, string> { ["X-Api-Key"] = "protected" };
        IAudioGenerationClient client = new TangoFluxHttpProvider(http).Bind(Connection() with
        { Headers = headers, TangoFlux = new() { GeneratePath = "/custom-generate" } });
        headers["X-Api-Key"] = "changed-after-binding";
        AudioGenerationResult result = await client.GenerateAsync(Request() with
        { Duration = TimeSpan.FromSeconds(2.5), Steps = 23, GuidanceScale = 3.75, Seed = 42, UseCache = false });
        Assert.Equal("audio/wav", result.ContentType);
        Assert.Equal(WaveBytes(), result.Audio.ToArray());
        Assert.Null(result.MeasuredDuration);
        Assert.Equal(client.Descriptor.Id, result.Provenance.ImplementationId);
        Assert.Equal("tangoflux", result.Provenance.ModelId);
        Assert.Equal("HTTP", result.Provenance.Runtime);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachePreferenceNeverInventsSeedAndAbsentParametersUseDocumentedDefaults(bool? cache)
    {
        using var handler = new Handler(async (request, token) =>
        {
            using JsonDocument body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(token), cancellationToken: token);
            Assert.False(body.RootElement.TryGetProperty("seed", out _));
            Assert.Equal(cache.HasValue, body.RootElement.TryGetProperty("useCache", out JsonElement useCache));
            if (cache.HasValue) { Assert.Equal(cache.Value, useCache.GetBoolean()); }
            Assert.Equal(10, body.RootElement.GetProperty("steps").GetInt32());
            Assert.Equal(4.5, body.RootElement.GetProperty("guidanceScale").GetDouble());
            return Wave();
        });
        using var http = new HttpClient(handler);
        await new TangoFluxHttpProvider(http).Bind(Connection()).GenerateAsync(Request() with { UseCache = cache });
    }

    [Theory]
    [InlineData(0.9, 10, 4.5)]
    [InlineData(30.1, 10, 4.5)]
    [InlineData(1, 9, 4.5)]
    [InlineData(1, 101, 4.5)]
    [InlineData(1, 10, 0.09)]
    [InlineData(1, 10, 20.1)]
    [InlineData(1, 10, double.NaN)]
    [InlineData(1, 10, double.PositiveInfinity)]
    public async Task InvalidInputsFailBeforeTransportWithoutClamping(double duration, int steps, double guidance)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No network expected."));
        using var http = new HttpClient(handler);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => new TangoFluxHttpProvider(http).Bind(Connection())
            .GenerateAsync(Request() with { Duration = TimeSpan.FromSeconds(duration), Steps = steps, GuidanceScale = guidance }));
        Assert.Equal(InferenceFailureCode.InvalidRequest, error.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(1, 10, 0.1, int.MinValue)]
    [InlineData(30, 100, 20, int.MaxValue)]
    public async Task ExactConstraintBoundariesReachTransport(double duration, int steps, double guidance, int seed)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Wave()));
        using var http = new HttpClient(handler);
        await new TangoFluxHttpProvider(http).Bind(Connection()).GenerateAsync(Request() with
        { Duration = TimeSpan.FromSeconds(duration), Steps = steps, GuidanceScale = guidance, Seed = seed });
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(401, InferenceFailureCode.AuthenticationRequired)]
    [InlineData(403, InferenceFailureCode.AccessDenied)]
    [InlineData(307, InferenceFailureCode.ProtocolFailure)]
    [InlineData(500, InferenceFailureCode.ExecutionFailed)]
    public async Task FailedResponsesAreSanitizedDisposedAndNotRetried(int status, InferenceFailureCode code)
    {
        using var content = new ObservedContent("private response"u8.ToArray());
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = content }));
        using var http = new HttpClient(handler);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => new TangoFluxHttpProvider(http).Bind(Connection()).GenerateAsync(Request()));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("private response", error.ToString(), StringComparison.Ordinal);
        Assert.True(content.IsDisposed);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task MalformedOrEmptyWaveIsRejected(int corruption)
    {
        byte[] bytes = WaveBytes();
        switch (corruption)
        {
            case 0: bytes = []; break;
            case 1: bytes[0] = 0; break;
            case 2: bytes[4] = 0; break;
            case 3: bytes[40] = 255; break;
            case 4: bytes[22] = 0; break; // Zero channels.
            case 5: bytes[36] = 0; break; // Missing data chunk.
        }
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        using var http = new HttpClient(handler);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => new TangoFluxHttpProvider(http).Bind(Connection()).GenerateAsync(Request()));
        Assert.Equal(InferenceFailureCode.ProtocolFailure, error.Code);
    }

    [Fact]
    public async Task ContentTypeIsValidatedAndSuccessfulResponseIsDisposed()
    {
        using var content = new ObservedContent(WaveBytes());
        content.Headers.ContentType = new("audio/wav");
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        await new TangoFluxHttpProvider(http).Bind(Connection()).GenerateAsync(Request());
        Assert.True(content.IsDisposed);
        using var wrongHandler = new Handler((_, _) =>
        {
            HttpResponseMessage response = Wave();
            response.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(response);
        });
        using var wrongHttp = new HttpClient(wrongHandler);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => new TangoFluxHttpProvider(wrongHttp).Bind(Connection()).GenerateAsync(Request()));
        Assert.Equal(InferenceFailureCode.ProtocolFailure, error.Code);
    }

    [Fact]
    public async Task CancellationAndTimeoutAreDistinctAndDoNotRetry()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler((_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(Wave()); });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TangoFluxHttpProvider(http).Bind(Connection()).GenerateAsync(Request(), cancellation.Token));
        Assert.Equal(1, handler.Calls);
        using var timeoutHandler = new Handler((_, _) => throw new TaskCanceledException());
        using var timeoutHttp = new HttpClient(timeoutHandler);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => new TangoFluxHttpProvider(timeoutHttp).Bind(Connection()).GenerateAsync(Request()));
        Assert.Equal(InferenceFailureCode.TimedOut, error.Code);
        Assert.Equal(1, timeoutHandler.Calls);
    }

    [Fact]
    public async Task NetworkFailureAndEmptyPromptDoNotLeakInputOrReachUnexpectedTransport()
    {
        using var handler = new Handler((_, _) => throw new HttpRequestException("private transport detail"));
        using var http = new HttpClient(handler);
        IAudioGenerationClient client = new TangoFluxHttpProvider(http).Bind(Connection());
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => client.GenerateAsync(Request()));
        Assert.Equal(InferenceFailureCode.ExecutionFailed, error.Code);
        Assert.DoesNotContain("private transport detail", error.ToString(), StringComparison.Ordinal);
        error = await Assert.ThrowsAsync<InferenceException>(() => client.GenerateAsync(Request() with { Prompt = " " }));
        Assert.Equal(InferenceFailureCode.InvalidRequest, error.Code);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GenerateAsync(Request(), cancellation.Token));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("https://another.test/generate")]
    [InlineData("//another.test/generate")]
    [InlineData("\\\\another.test\\generate")]
    [InlineData("/generate?token=private")]
    [InlineData(" //another.test/generate")]
    public void PathsCannotSelectAnAlternateOriginOrCarryCredentials(string path)
    {
        using var http = new HttpClient();
        Assert.Throws<InferenceException>(() => new TangoFluxHttpProvider(http).Bind(Connection() with { TangoFlux = new() { GeneratePath = path } }));
    }

    [Theory]
    [InlineData("Authorization", "secret\r\nInjected: value")]
    [InlineData("Host", "another.test")]
    [InlineData("Invalid name", "value")]
    public void InvalidProtectedHeadersFailAtBinding(string name, string value)
    {
        using var http = new HttpClient();
        Assert.Throws<InferenceException>(() => new TangoFluxHttpProvider(http).Bind(Connection() with { Headers = new Dictionary<string, string> { [name] = value } }));
    }

    [Fact]
    public async Task DiscoveryAndCapabilitiesRequireNoNetworkOrProviderStartup()
    {
        var services = new ServiceCollection();
        services.AddAudioConnect();
        using ServiceProvider container = services.BuildServiceProvider();
        AudioProviderService providers = container.GetRequiredService<AudioProviderService>();
        Assert.Equal(InferenceTask.AudioGeneration, Assert.Single(Assert.Single(providers.SetupDefinitions).Implementation.Tasks));
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No network expected."));
        using var http = new HttpClient(handler);
        AudioGenerationCapabilities capabilities = await new TangoFluxHttpProvider(http).Bind(Connection()).GetCapabilitiesAsync();
        Assert.False(capabilities.SupportsHealthCheck);
        Assert.False(capabilities.SupportsModelListing);
        Assert.True(capabilities.SupportsSeed);
        Assert.True(capabilities.SupportsCachePreference);
        Assert.Equal("audio/wav", Assert.Single(capabilities.ContentTypes));
        Assert.Equal(0, handler.Calls);
    }

    internal static AudioConnection Connection() => new() { Endpoint = new("https://example.test/api") };
    private static AudioGenerationRequest Request() => new() { Prompt = "rain on leaves", Duration = TimeSpan.FromSeconds(5) };
    private static byte[] WaveBytes() => Convert.FromHexString("524946462600000057415645666D74201000000001000100803E0000007D00000200100064617461020000000000");
    internal static HttpResponseMessage Wave()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(WaveBytes()) };
        response.Content.Headers.ContentType = new("audio/wav");
        return response;
    }
    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
    }
    private sealed class ObservedContent(byte[] bytes) : ByteArrayContent(bytes)
    {
        public bool IsDisposed { get; private set; }
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
    }
}
