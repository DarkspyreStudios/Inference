using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.STT;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.STT.Tests;

public sealed class XaiSttTests
{
    private static readonly string[] Terms = ["first", "second"];
    internal static SttConnection Connection => new() { Transport = SttTransport.XaiStt, Endpoint = new("https://api.x.ai/v1"), Xai = new("selected") };
    internal static SpeechTranscriptionInput Input => new BinarySpeechInput("audio/wav", new byte[] { 1, 2, 3 }, "actual.wav");

    [Fact]
    public async Task RestPreservesMultipartOrderControlsAndObservedTranscriptFacts()
    {
        var source = new Source(); await using var access = new LlmConnectionAccess(source, []);
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("https://api.x.ai/v1/stt", request.RequestUri!.AbsoluteUri);
            Assert.Equal("actual-token", request.Headers.Authorization!.Parameter);
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToArray();
            Assert.Equal("file", parts[^1].Headers.ContentDisposition!.Name!.Trim('"'));
            var fields = new Dictionary<string, List<string>>();
            foreach (HttpContent part in parts[..^1])
            {
                string name = part.Headers.ContentDisposition!.Name!.Trim('"');
                if (!fields.TryGetValue(name, out var values)) fields.Add(name, values = []);
                values.Add(await part.ReadAsStringAsync());
            }
            Assert.Equal("false", Assert.Single(fields["diarize"])); Assert.Equal("0", Assert.Single(fields["vad_threshold"]));
            Assert.Equal("true", Assert.Single(fields["format"])); Assert.Equal(Terms, fields["keyterm"]);
            Assert.False(fields.ContainsKey("model")); Assert.False(fields.ContainsKey("audio_format"));
            Assert.Equal(new byte[] { 1, 2, 3 }, await parts[^1].ReadAsByteArrayAsync());
            return Json("{\"text\":\"actual\",\"language\":\"en\",\"duration\":1.25,\"words\":[{\"text\":\"actual\",\"start\":0,\"end\":1.25,\"confidence\":0,\"speaker\":0}],\"channels\":[{\"index\":0,\"text\":\"actual\",\"words\":[]}]}");
        }));
        await using var client = Provider(http).Bind(Connection, new(new("selected"), access));
        var result = await client.TranscribeAsync(Input, new(Language: "en", Diarization: false, KeyTerms: ["first", "second"], VadThreshold: 0, TextNormalization: true));
        Assert.Equal("actual", result.Text); Assert.Null(result.Provenance.ModelId); Assert.Equal(TimeSpan.FromSeconds(1.25), result.Duration);
        Assert.Equal(0, Assert.Single(result.Words!).Confidence); Assert.Equal(0, Assert.Single(result.Words!).Speaker);
        Assert.Empty(Assert.Single(result.Channels!).Words!); Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task UrlInputIsPassedToProviderWithoutClientSideFetch()
    {
        using var access = new LlmConnectionAccess(new Source(), []);
        using var http = new HttpClient(new Handler(async request =>
        {
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToArray();
            Assert.Single(parts); Assert.Equal("url", parts[0].Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.Equal("https://example.test/actual?opaque=value", await parts[0].ReadAsStringAsync());
            return Json("{\"text\":\"\"}");
        }));
        await using var client = Provider(http).Bind(Connection, new(new("selected"), access));
        var result = await client.TranscribeAsync(new UrlSpeechInput(new("https://example.test/actual?opaque=value")), new());
        Assert.Equal("", result.Text); Assert.Null(result.Words); Assert.Null(result.Duration); Assert.Null(result.Language);
    }

    [Theory]
    [InlineData("rate")]
    [InlineData("vad")]
    [InlineData("format")]
    [InlineData("terms")]
    [InlineData("raw")]
    public async Task InvalidControlsRefuseBeforeSourceAndHttp(string option)
    {
        var source = new Source(); using var access = new LlmConnectionAccess(source, []);
        var handler = new Handler(_ => throw new InvalidOperationException("No effects.")); using var http = new HttpClient(handler);
        await using var client = Provider(http).Bind(Connection, new(new("selected"), access));
        var options = option switch
        {
            "rate" => new SpeechTranscriptionOptions(SampleRate: 1),
            "vad" => new(VadThreshold: 2),
            "format" => new(TextNormalization: true),
            "terms" => new(KeyTerms: Enumerable.Repeat("x", 101).ToArray()),
            _ => new(RawAudioFormat: SpeechAudioEncoding.Opus)
        };
        Assert.NotNull(await Record.ExceptionAsync(() => client.TranscribeAsync(Input, options)));
        Assert.Equal(0, source.Calls); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"text\":null}")]
    [InlineData("{\"text\":\"x\",\"duration\":-1}")]
    [InlineData("{\"text\":\"x\",\"words\":[{\"text\":\"x\",\"end\":1}]}")]
    public async Task MalformedActualFactsNeverBecomeInventedZeros(string json)
    {
        using var access = new LlmConnectionAccess(new Source(), []); using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(json))));
        await using var client = Provider(http).Bind(Connection, new(new("selected"), access));
        var error = Assert.IsType<InferenceException>(await Record.ExceptionAsync(() => client.TranscribeAsync(Input, new())));
        Assert.Equal(InferenceFailureCode.ProtocolFailure, error.Code);
    }

    internal static ISttProvider Provider(HttpClient http)
    {
        Type? type = typeof(ISttProvider).Assembly.GetType("Darkspyre.Inference.Connect.STT.XaiSttProvider");
        Assert.NotNull(type); return Assert.IsAssignableFrom<ISttProvider>(Activator.CreateInstance(type!, http));
    }
    internal static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    internal sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return send(request); }
    }
    internal sealed class Source : ILlmConnectionSource
    {
        internal int Calls;
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<LlmConnection?>(new() { Version = "actual", Transport = LlmTransport.XaiResponses, Endpoint = "https://api.x.ai/v1", Credential = new LlmCredential { OAuth = new() { AccessToken = "actual-token" } }.Serialize() }); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No refresh.");
    }
}
