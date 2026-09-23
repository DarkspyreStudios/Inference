using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.TTS.Tests;

public sealed class PocketTtsTests
{
    private static readonly string[] ServerVoices = ["server-voice"];
    private static readonly string[] TestVoices = ["voice"];
    [Fact]
    public async Task VoiceOptionsPreserveServerIdentifiersAndDescriptions()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("/api/voices", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(new { voiceOptions = new[] { new { id = "voice-id", name = "A voice", category = "Built-in" } } }));
        });
        using var http = new HttpClient(handler);
        TtsVoice voice = Assert.Single(await new PocketTtsHttpProvider(http).Bind(Connection()).ListVoicesAsync());
        Assert.Equal("voice-id", voice.Id);
        Assert.Equal("A voice", voice.DisplayName);
        Assert.Equal("Built-in", voice.Description);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task EmptyVoiceListFallsBackToHealthButHttpFailuresDoNot()
    {
        using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/voices", StringComparison.Ordinal)
            ? Json(new { voices = Array.Empty<string>() }) : Json(new { voices = ServerVoices })));
        using var http = new HttpClient(handler);
        ITtsClient client = new PocketTtsHttpProvider(http).Bind(Connection());
        Assert.Equal("server-voice", Assert.Single(await client.ListVoicesAsync()).Id);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task MultipartUsesExactFieldsAndDoesNotPretendToSupportSpeed()
    {
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/tts", request.RequestUri!.AbsolutePath);
            string body = await request.Content!.ReadAsStringAsync(token);
            Assert.Contains("name=text", body, StringComparison.Ordinal);
            Assert.Contains("name=output_format", body, StringComparison.Ordinal);
            Assert.Contains("name=voice_url", body, StringComparison.Ordinal);
            Assert.Contains("test speech", body, StringComparison.Ordinal);
            Assert.DoesNotContain("speed", body, StringComparison.Ordinal);
            return Wave();
        });
        using var http = new HttpClient(handler);
        ITtsClient client = new PocketTtsHttpProvider(http).Bind(Connection());
        Assert.False((await client.GetCapabilitiesAsync()).SupportsSpeed);
        TtsResult result = await client.SpeakAsync(new() { Text = "test speech", VoiceId = "voice", Speed = 1 });
        Assert.Equal("audio/wav", result.ContentType);
        Assert.Equal(46, result.Audio.Length);
        Assert.Equal(client.Descriptor.Id, result.Provenance.ImplementationId);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => client.SpeakAsync(new() { Text = "test", Speed = 2 }));
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, error.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task BridgeJsonPreservesVoiceSpeakerSpeedAndFormat()
    {
        using var handler = new Handler(async (request, token) =>
        {
            using JsonDocument body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(token), cancellationToken: token);
            Assert.Equal("test speech", body.RootElement.GetProperty("text").GetString());
            Assert.Equal("voice", body.RootElement.GetProperty("voice").GetString());
            Assert.Equal("voice", body.RootElement.GetProperty("speaker").GetString());
            Assert.Equal(1.25, body.RootElement.GetProperty("speed").GetDouble());
            Assert.Equal("wav", body.RootElement.GetProperty("response_format").GetString());
            return Wave();
        });
        using var http = new HttpClient(handler);
        ITtsClient client = new PocketTtsHttpProvider(http).Bind(Connection() with { PocketTts = new() { Protocol = PocketTtsProtocol.BridgeJson } });
        Assert.True((await client.GetCapabilitiesAsync()).SupportsSpeed);
        await client.SpeakAsync(new() { Text = "test speech", VoiceId = "voice", Speed = 1.25 });
    }

    [Theory]
    [InlineData(401, InferenceFailureCode.AuthenticationRequired)]
    [InlineData(403, InferenceFailureCode.AccessDenied)]
    [InlineData(302, InferenceFailureCode.ProtocolFailure)]
    [InlineData(500, InferenceFailureCode.ExecutionFailed)]
    public async Task HttpFailuresAreTypedSanitizedAndNeverRetried(int status, InferenceFailureCode code)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private response") }));
        using var http = new HttpClient(handler);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => new PocketTtsHttpProvider(http).Bind(Connection()).ListVoicesAsync());
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("private response", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("RIFFbrokenWAVE")]
    public async Task InvalidAudioIsRejected(string data)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data) }));
        using var http = new HttpClient(handler);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => new PocketTtsHttpProvider(http).Bind(Connection()).SpeakAsync(new() { Text = "test" }));
        Assert.Equal(InferenceFailureCode.ProtocolFailure, error.Code);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellationAndTimeoutIsDistinct()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler((_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(Wave()); });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PocketTtsHttpProvider(http).Bind(Connection())
            .SpeakAsync(new() { Text = "test" }, cancellation.Token));
        using var timeoutHandler = new Handler((_, _) => throw new TaskCanceledException());
        using var timeoutHttp = new HttpClient(timeoutHandler);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => new PocketTtsHttpProvider(timeoutHttp).Bind(Connection()).SpeakAsync(new() { Text = "test" }));
        Assert.Equal(InferenceFailureCode.TimedOut, error.Code);
        Assert.Equal(1, timeoutHandler.Calls);
    }

    [Theory]
    [InlineData("https://another.test/tts")]
    [InlineData("//another.test/tts")]
    [InlineData("\\\\another.test\\tts")]
    public void EndpointPathsCannotSendCredentialsToAnotherOrigin(string path)
    {
        using var http = new HttpClient();
        InferenceException error = Assert.Throws<InferenceException>(() => new PocketTtsHttpProvider(http)
            .Bind(Connection() with { PocketTts = new() { SpeakPath = path } }));
        Assert.Equal(InferenceFailureCode.InvalidRequest, error.Code);
    }

    [Fact]
    public async Task SetupSaveIsTypedAndDoesNotConnectOrDiscardHeaders()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No requests expected."));
        using var http = new HttpClient(handler);
        var target = new Target();
        var service = new TtsProviderService([new PocketTtsHttpProvider(http)]);
        await using TtsSetupSession session = service.CreateSetupSession(TtsTransport.PocketTtsHttp,
            new(SetupChangeIntent.Edit, "connection", "v1"), target, new(Connection() with { Headers = new Dictionary<string, string> { ["Authorization"] = "private-key" } }));
        SetupRepeatableGroupField group = Assert.Single(Fields(session).OfType<SetupRepeatableGroupField>());
        SetupSecretField secret = Assert.Single(Assert.Single(group.Rows).Fields.OfType<SetupSecretField>());
        Assert.True(secret.HasValue);
        Assert.Equal(SetupEditState.Applied, secret.SetValue(SetupSecretEdit.Replace("new-key")).State);
        Assert.Equal(SetupCommitState.Committed, (await session.CommitAsync()).State);
        Assert.Equal("v1", target.Changes!.Identity.ExpectedVersion);
        Assert.Equal("new-key", target.Changes.Headers.Set["Authorization"]);
        Assert.Empty(target.Changes.Headers.Remove);
        Assert.Empty(target.Changes.Configuration.Connection.Headers);
        Assert.Null(session.Snapshot.Status.Ready.Value);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SetupSeparatesVoiceChecksFromSynthesisAndExposesProtocolConstraints()
    {
        using var handler = new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Get ? Json(new { voices = TestVoices }) : Wave()));
        using var http = new HttpClient(handler);
        var service = new TtsProviderService([new PocketTtsHttpProvider(http)]);
        await using TtsSetupSession session = service.CreateSetupSession(TtsTransport.PocketTtsHttp, new(SetupChangeIntent.Create, "new", null), new Target());
        await Assert.Single(session.Snapshot.Actions, action => action.Descriptor.Purpose == SetupActionPurpose.CheckReadiness).ExecuteAsync();
        Assert.True(session.Snapshot.Status.Verified.Value);
        Assert.Null(session.Snapshot.Status.Ready.Value);
        ISetupAction synthesize = Assert.Single(session.Snapshot.Actions, action => action.Descriptor.Purpose == SetupActionPurpose.Test);
        Assert.True(synthesize.Descriptor.SideEffects.HasFlag(SetupSideEffect.PotentialBilling));
        Assert.Equal(SetupActionState.Completed, (await synthesize.ExecuteAsync()).State);
        Assert.True(session.Snapshot.Status.Ready.Value);
        SetupDecimalField speed = Assert.Single(Fields(session).OfType<SetupDecimalField>());
        Assert.Equal(1, speed.Maximum);
        speed.SetValue(2);
        Assert.False((await session.ValidateAsync()).IsValid);
        Assert.Single(Fields(session).OfType<SetupChoiceField>(), field => field.Description.Id == "protocol").SetValue(nameof(PocketTtsProtocol.BridgeJson));
        Assert.True((await session.ValidateAsync()).IsValid);
        Assert.True(session.Snapshot.Status.Ready.IsStale);
    }

    [Fact]
    public void RegistrationExposesAnAudioTaskWithoutStartingAnything()
    {
        var services = new ServiceCollection();
        services.AddTtsConnect();
        using ServiceProvider container = services.BuildServiceProvider();
        InferenceSetupDefinition definition = Assert.Single(container.GetRequiredService<TtsProviderService>().SetupDefinitions);
        Assert.Equal(InferenceCapability.Audio, Assert.Single(definition.Implementation.Tasks).GetCapability());
    }

    private static IEnumerable<SetupField> Fields(TtsSetupSession session) => session.Snapshot.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields);
    private static TtsConnection Connection() => new() { Endpoint = new("https://example.test/api") };
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static HttpResponseMessage Wave()
    {
        // One 16-bit mono sample, 16 kHz. Complete RIFF/fmt/data chunks.
        byte[] bytes = Convert.FromHexString("524946462600000057415645666D74201000000001000100803E0000007D00000200100064617461020000000000");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new("audio/wav");
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
    }
    private sealed class Target : ISetupCommitTarget<TtsSetupChanges>
    {
        public TtsSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(TtsSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "v2")); }
    }
}
