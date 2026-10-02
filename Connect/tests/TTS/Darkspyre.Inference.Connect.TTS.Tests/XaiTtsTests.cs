using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.TTS.Tests;

public sealed class XaiTtsTests
{
    private static readonly string[] TimestampText = ["a", " ", "tag"];
    private static readonly string[] VoiceIds = ["EVE", "manual"];
    internal static TtsConnection Connection => new() { Transport = TtsTransport.XaiTts, Endpoint = new("https://api.x.ai/v1"), Xai = new("selected") };
    internal static TtsRequest Request => new() { Text = "actual text", Language = "en", Format = TtsAudioFormat.Mp3 };

    [Fact]
    public async Task ActualRestClientMapsTypedControlsAndTimestampFacts()
    {
        var source = new Source(); using var access = new LlmConnectionAccess(source, []);
        var handler = new Handler(async request =>
        {
            Assert.Equal("https://api.x.ai/v1/tts", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal("controlled-token", request.Headers.Authorization.Parameter);
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("actual text", body.RootElement.GetProperty("text").GetString());
            Assert.Equal("wav", body.RootElement.GetProperty("output_format").GetProperty("codec").GetString());
            Assert.Equal(2, body.RootElement.GetProperty("optimize_streaming_latency").GetInt32());
            Assert.False(body.RootElement.GetProperty("text_normalization").GetBoolean());
            Assert.Empty(body.RootElement.GetProperty("replace").EnumerateObject());
            return Json("{\"audio\":\"AQID\",\"content_type\":\"audio/wav\",\"duration\":1.25,\"audio_timestamps\":{\"graph_chars\":[\"a\",\" \",\"tag\"],\"graph_times\":[[0,0.1],[0.1,0.2],[0.2,1.25]]}}");
        });
        using var http = new HttpClient(handler);
        await using ITtsClient client = Provider(http).Bind(Connection with { XaiDefaults = new(1, true, new Dictionary<string, string> { ["old"] = "value" }) }, new(new("selected"), access));
        TtsResult result = await client.SpeakAsync(Request with { Format = TtsAudioFormat.Wav, IncludeTimestamps = true, Xai = new(2, false, new Dictionary<string, string>()) });
        Assert.Null(result.Provenance.ModelId);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Audio.ToArray()); Assert.Equal(TimeSpan.FromSeconds(1.25), result.Duration);
        Assert.Equal(TimestampText, result.Timestamps!.Select(item => item.Text)); Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task VoiceListingPreservesActualOrderAndFacts()
    {
        var source = new Source(); using var access = new LlmConnectionAccess(source, []);
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("/v1/tts/voices", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("{\"voices\":[{\"voice_id\":\"EVE\",\"name\":\"Eve\"},{\"voice_id\":\"manual\",\"name\":\"Actual\",\"description\":\"source\"}]}"));
        }));
        await using var facade = Facade(source, access, http);
        using var unusedHttp = new HttpClient(new Handler(_ => throw new InvalidOperationException("The selected facade owns catalog HTTP.")));
        await using ITtsClient client = Provider(unusedHttp).Bind(Connection, new(new("selected"), access, facade));
        var voices = await client.ListVoicesAsync(); Assert.Equal(VoiceIds, voices.Select(item => item.Id));
        Assert.Null(voices[0].Locale); Assert.Equal("source", voices[1].Description); Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task MissingCatalogFacadeIsUnavailableBeforeResolutionOrHttp()
    {
        var source = new Source(); using var access = new LlmConnectionAccess(source, []);
        var handler = new Handler(_ => throw new InvalidOperationException("No HTTP permitted.")); using var http = new HttpClient(handler);
        await using ITtsClient client = Provider(http).Bind(Connection, new(new("selected"), access));
        Assert.False((await client.GetCapabilitiesAsync()).SupportsVoiceListing);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.ListVoicesAsync());
        Assert.Equal(0, source.Calls); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RefreshedCredentialIsUsedWithoutASecondResolution()
    {
        var source = new RefreshSource(); var authenticator = new Authenticator();
        await using var access = new LlmConnectionAccess(source, [authenticator]);
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("refreshed-token", request.Headers.Authorization!.Parameter);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1 }) };
            response.Content.Headers.ContentType = new("audio/mpeg"); return Task.FromResult(response);
        }));
        await using ITtsClient client = Provider(http).Bind(Connection, new(new("selected"), access));
        Assert.Single((await client.SpeakAsync(Request)).Audio.ToArray());
        Assert.Equal(1, source.Resolutions); Assert.Equal(1, source.Saves); Assert.Equal(1, authenticator.Calls);
    }

    [Theory]
    [InlineData("language")]
    [InlineData("speed")]
    [InlineData("format")]
    [InlineData("rate")]
    [InlineData("bitrate")]
    public async Task UnsupportedRequestRefusesBeforeSourceOrHttp(string control)
    {
        var source = new Source(); using var access = new LlmConnectionAccess(source, []);
        var handler = new Handler(_ => throw new InvalidOperationException("No HTTP permitted.")); using var http = new HttpClient(handler);
        await using ITtsClient client = Provider(http).Bind(Connection, new(new("selected"), access));
        var request = control switch
        {
            "language" => Request with { Language = null }, "speed" => Request with { Speed = double.NaN },
            "format" => Request with { Format = (TtsAudioFormat)99 }, "rate" => Request with { SampleRate = 0 },
            _ => Request with { Format = TtsAudioFormat.Pcm, BitRate = 128000 }
        };
        Assert.NotNull(await Record.ExceptionAsync(() => client.SpeakAsync(request))); Assert.Equal(0, source.Calls); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("{\"audio\":\"AQ==\",\"content_type\":\"audio/mpeg\",\"audio_timestamps\":{\"graph_chars\":[\"x\"],\"graph_times\":[]}}")]
    [InlineData("{\"audio\":null,\"content_type\":\"audio/mpeg\"}")]
    public async Task MalformedActualResponseDoesNotFabricateAudioOrTimings(string body)
    {
        using var access = new LlmConnectionAccess(new Source(), []); using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(body))));
        await using ITtsClient client = Provider(http).Bind(Connection, new(new("selected"), access));
        Assert.NotNull(await Record.ExceptionAsync(() => client.SpeakAsync(Request with { IncludeTimestamps = true })));
    }

    internal static ITtsProvider Provider(HttpClient http)
    {
        Type? type = typeof(ITtsProvider).Assembly.GetType("Darkspyre.Inference.Connect.TTS.XaiTtsProvider");
        Assert.NotNull(type); return Assert.IsAssignableFrom<ITtsProvider>(Activator.CreateInstance(type!, http));
    }
    internal static LlmProviderService Facade(ILlmConnectionSource source, LlmConnectionAccess access, HttpClient http)
    {
        Type type = typeof(LlmProviderService).Assembly.GetType("Darkspyre.Inference.Connect.LLM.Providers.XaiResponsesLlmClient", throwOnError: true)!;
        ILlmClient client = Assert.IsAssignableFrom<ILlmClient>(Activator.CreateInstance(type, http));
        return new(source, [client], [], connectionAccess: access);
    }
    internal static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    internal sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return send(request); }
    }
    internal sealed class Source : ILlmConnectionSource
    {
        internal int Calls;
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Calls++; return Task.FromResult<LlmConnection?>(new() { Version = "actual", Transport = LlmTransport.XaiResponses, Endpoint = "https://api.x.ai/v1", Credential = new LlmCredential { OAuth = new() { AccessToken = "controlled-token" } }.Serialize() });
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No refresh required.");
    }

    private sealed class RefreshSource : ILlmConnectionSource
    {
        internal int Resolutions, Saves;
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Resolutions++;
            return Task.FromResult<LlmConnection?>(new() { Version = "actual", Transport = LlmTransport.XaiResponses, Endpoint = "https://api.x.ai/v1", Credential = new LlmCredential { OAuth = new() { AccessToken = "old-token" } }.Serialize() });
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        {
            Assert.Equal("actual", expectedVersion); Assert.Equal("refreshed-token", LlmCredential.Parse(credentialJson)!.OAuth!.AccessToken);
            Saves++; return Task.CompletedTask;
        }
    }
    private sealed class Authenticator : ILlmAuthenticator
    {
        internal int Calls;
        public LlmTransport Transport => LlmTransport.XaiResponses;
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(credential with { OAuth = credential.OAuth! with { AccessToken = "refreshed-token" } }); }
    }
}
