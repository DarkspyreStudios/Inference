using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.STT;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.Voice.Tests;

namespace Darkspyre.Inference.Connect.STT.Tests;

public sealed class XaiSttSessionTests
{
    [Theory]
    [InlineData("dummy-test-token")]
    [InlineData("default-secret")]
    public async Task ProtectedTranscriptIdentityRefusesOpeningWithoutPublishingAReplacement(string secret)
    {
        using var socket = new VoiceSocket();
        socket.Json(new JsonObject { ["type"] = "transcript.created", ["id"] = "transcript-" + secret }.ToJsonString());
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Default", "default-secret");
        await using var access = new LlmConnectionAccess(new VoiceConnectionSource(), []);
        var binding = new XaiFamilyBinding(new("selected"), access);
        await using ISpeechTranscriptionClient client = new XaiSttProvider(http, socket.ConnectAsync).Bind(new()
        {
            Transport = SttTransport.XaiStt,
            Endpoint = new(VoiceTestData.Connection.Endpoint),
            Xai = binding.Selection
        }, binding);

        InferenceException failure = await Assert.ThrowsAsync<InferenceException>(async () =>
        {
            await using ISpeechTranscriptionSession session = await client.OpenStreamAsync(new(), VoiceTestData.Limits())
                .WaitAsync(VoiceTestData.Timeout);
        });

        Assert.Equal(InferenceFailureCode.ProtocolFailure, failure.Code);
        Assert.Equal("The xAI transcription response contains invalid protocol facts.", failure.Message);
        Assert.DoesNotContain(secret, failure.ToString(), StringComparison.Ordinal);
        Assert.Empty(socket.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingErrorsRetainRedactedProviderExplanation(bool beforeReady)
    {
        using var socket = new VoiceSocket();
        if (!beforeReady) socket.Json("""{"type":"transcript.created","id":"transcript-1"}""");
        socket.Json("""{"type":"error","message":"Unsupported encoding: dummy-test-token refresh-secret identity-secret binding-secret family-secret default-secret bearer-default-secret."}""");
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Default", "default-secret");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "bearer-default-secret");
        await using var access = new LlmConnectionAccess(new VoiceConnectionSource(), []);
        var binding = new XaiFamilyBinding(new("selected"), access);
        await using ISpeechTranscriptionClient client = new XaiSttProvider(http, socket.ConnectAsync).Bind(new()
        {
            Transport = SttTransport.XaiStt,
            Endpoint = new(VoiceTestData.Connection.Endpoint),
            Xai = binding.Selection,
            AdditionalHeaders = new Dictionary<string, string> { ["X-Family"] = "family-secret" }
        }, binding);
        const string explanation = "Unsupported encoding: [REDACTED] [REDACTED] [REDACTED] [REDACTED] [REDACTED] [REDACTED] [REDACTED].";

        if (beforeReady)
        {
            InferenceException failure = await Assert.ThrowsAsync<InferenceException>(() =>
                client.OpenStreamAsync(new(), VoiceTestData.Limits()).WaitAsync(VoiceTestData.Timeout));
            Assert.Equal(InferenceFailureCode.ExecutionFailed, failure.Code);
            Assert.Equal(explanation, failure.Message);
            return;
        }

        await using ISpeechTranscriptionSession session = await client.OpenStreamAsync(new(), VoiceTestData.Limits());
        await using var events = session.Events.GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(VoiceTestData.Timeout));
        Assert.IsType<SpeechTranscriptCreatedEvent>(events.Current);
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(VoiceTestData.Timeout));
        var error = Assert.IsType<SpeechTranscriptErrorEvent>(events.Current);
        Assert.Equal(InferenceFailureCode.ExecutionFailed, error.Error.Code);
        Assert.Equal(explanation, error.Error.Message);
        await session.FinalizeAsync();
        Assert.Equal("finalize", VoiceTestData.Type(Assert.Single(socket.Sent).Bytes));
    }

    [Theory]
    [InlineData(true, "en", "?language=en&format=true")]
    [InlineData(false, null, "?format=false")]
    [InlineData(null, null, "")]
    public async Task StreamingTextNormalizationPreservesExplicitAndOmittedControls(
        bool? normalize, string? language, string query)
    {
        using var socket = new VoiceSocket();
        socket.Json("""{"type":"transcript.created","id":"transcript-1"}""");
        using var http = new HttpClient();
        await using var access = new LlmConnectionAccess(new VoiceConnectionSource(), []);
        var binding = new XaiFamilyBinding(new("selected"), access);
        Uri? sentUri = null;
        var provider = new XaiSttProvider(http, (uri, headers, credential, fence, cleanup, token) =>
        {
            sentUri = uri;
            return socket.ConnectAsync(uri, headers, credential, fence, cleanup, token);
        });
        await using ISpeechTranscriptionClient client = provider.Bind(new()
        {
            Transport = SttTransport.XaiStt,
            Endpoint = new(VoiceTestData.Connection.Endpoint),
            Xai = binding.Selection
        }, binding);

        await using ISpeechTranscriptionSession session = await client.OpenStreamAsync(
            new(Language: language, TextNormalization: normalize), VoiceTestData.Limits());

        Assert.NotNull(sentUri);
        Assert.Equal("/v1/stt", sentUri.AbsolutePath);
        Assert.Equal(query, sentUri.Query);
        Assert.True((await client.GetCapabilitiesAsync()).Streaming.SupportsTextNormalization);
    }

    [Fact]
    public void StreamingTextNormalizationRequiresLanguage()
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() =>
            XaiSttProtocol.Snapshot(new SpeechStreamOptions(TextNormalization: true)));

        Assert.Equal("The xAI transcription input or controls are invalid.", failure.Message);
    }

    [Fact]
    public async Task FailedUpgradeRetainsSafeRequestIdAndRetryDelay()
    {
        using var handler = new RejectedVoiceUpgrade("/v1/stt");
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("X-Default", "default-secret");
        await using var access = new LlmConnectionAccess(new VoiceConnectionSource(), []);
        var binding = new XaiFamilyBinding(new("selected"), access);
        await using ISpeechTranscriptionClient client = new XaiSttProvider(http).Bind(new()
        {
            Transport = SttTransport.XaiStt,
            Endpoint = new(VoiceTestData.Connection.Endpoint),
            Xai = binding.Selection,
            AdditionalHeaders = new Dictionary<string, string> { ["X-Family"] = "family-secret" }
        }, binding);

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() =>
            client.OpenStreamAsync(new(), VoiceTestData.Limits()));

        VoiceTestData.AssertUpgradeFailure(failure);
        Assert.Equal(1, handler.Sends);
    }

    [Fact]
    public async Task RestTransportFailureRetainsRedactedExplanationAndErrorKind()
    {
        using var handler = new FailedVoiceHttp("/v1/stt");
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("X-Default", "default-secret");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "bearer-default-secret");
        await using var access = new LlmConnectionAccess(new VoiceConnectionSource(), []);
        var binding = new XaiFamilyBinding(new("selected"), access);
        await using ISpeechTranscriptionClient client = new XaiSttProvider(http).Bind(new()
        {
            Transport = SttTransport.XaiStt,
            Endpoint = new(VoiceTestData.Connection.Endpoint),
            Xai = binding.Selection,
            AdditionalHeaders = new Dictionary<string, string> { ["X-Family"] = "family-secret" }
        }, binding);

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.TranscribeAsync(new BinarySpeechInput("audio/wav", new byte[] { 1, 2, 3 }, "speech.wav"), new()));

        VoiceTestData.AssertTransportFailure(failure);
        Assert.Equal(1, handler.Sends);
    }

    [Fact]
    public async Task RestFailurePreservesSafeEvidenceWithoutExposingProtectedHeaders()
    {
        using var handler = new RejectedVoiceHttp("/v1/stt");
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("X-Default", "default-secret");
        await using var access = new LlmConnectionAccess(new VoiceConnectionSource(), []);
        var binding = new XaiFamilyBinding(new("selected"), access);
        await using ISpeechTranscriptionClient client = new XaiSttProvider(http).Bind(new()
        {
            Transport = SttTransport.XaiStt,
            Endpoint = new(VoiceTestData.Connection.Endpoint),
            Xai = binding.Selection,
            AdditionalHeaders = new Dictionary<string, string> { ["X-Family"] = "family-secret" }
        }, binding);

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() =>
            client.TranscribeAsync(new BinarySpeechInput("audio/wav", new byte[] { 1, 2, 3 }, "speech.wav"), new()));

        VoiceTestData.AssertRestFailure(failure);
        Assert.Equal(1, handler.Sends);
    }

    private static readonly string[] AudioCommands = ["finalize", "audio.done"];
    private static Task<XaiSttSession> OpenAsync(VoiceSocket socket, int events = 16)
        => XaiSttSession.OpenAsync(socket.ConnectAsync, VoiceTestData.Connection,
            new() { Transport = SttTransport.XaiStt, Endpoint = new(VoiceTestData.Connection.Endpoint) },
            new(), VoiceTestData.Limits(events), [], () => { }, _ => { }, default);

    [Fact]
    public async Task ActualReadyAndDoneObservationsBoundOrderedAudioCommands()
    {
        using var socket = new VoiceSocket();
        Task<XaiSttSession> opening = OpenAsync(socket);
        Assert.False(opening.IsCompleted);
        socket.Json("""{"type":"transcript.created","id":"transcript-1"}""");
        await using var session = await opening.WaitAsync(VoiceTestData.Timeout);
        await session.AppendAudioAsync(new byte[] { 1, 2, 3 }).WaitAsync(VoiceTestData.Timeout);
        await session.FinalizeAsync();
        await session.CompleteAudioAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AppendAudioAsync(new byte[] { 4 }));
        Assert.Equal(WebSocketMessageType.Binary, socket.Sent[0].Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, socket.Sent[0].Bytes);
        Assert.Equal(AudioCommands, socket.Sent.Skip(1).Select(frame => VoiceTestData.Type(frame.Bytes)));
        socket.Json("""{"type":"transcript.done","text":"hello","duration":1.25,"words":[]}""");
        socket.End();
        await using var events = session.Events.GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(VoiceTestData.Timeout));
        Assert.Equal("transcript-1", Assert.IsType<SpeechTranscriptCreatedEvent>(events.Current).SessionId);
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(VoiceTestData.Timeout));
        var done = Assert.IsType<SpeechTranscriptDoneEvent>(events.Current);
        Assert.Equal("hello", done.Text);
        Assert.Equal(TimeSpan.FromSeconds(1.25), done.Duration);
        Assert.False(await events.MoveNextAsync().AsTask().WaitAsync(VoiceTestData.Timeout));
    }

    [Fact]
    public async Task PausedTranscriptReaderDoesNotHoldSocketRetirement()
    {
        using var socket = new VoiceSocket();
        socket.Json("""{"type":"transcript.created","id":"transcript-1"}""");
        await using var session = await OpenAsync(socket, 1).WaitAsync(VoiceTestData.Timeout);
        await using var events = session.Events.GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        const string partial = """{"type":"transcript.partial","text":"hello","start":0,"duration":1,"is_final":false,"speech_final":false,"words":[]}""";
        socket.Json(partial); socket.Json(partial);
        await socket.WaitReceivedAsync(3);
        await session.DisposeAsync().AsTask().WaitAsync(VoiceTestData.Timeout);
        Assert.True(socket.Disposed);
        Assert.False(await events.MoveNextAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.FinalizeAsync());
    }
}
