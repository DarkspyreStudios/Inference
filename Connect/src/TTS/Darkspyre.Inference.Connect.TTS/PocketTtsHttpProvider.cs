using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.TTS;

/// <summary>
/// External HTTP only. The host owns HttpClient and must disable automatic redirects when using
/// protected headers. No processes, certificates, model files or application lifetime are managed.
/// </summary>
public sealed class PocketTtsHttpProvider(HttpClient httpClient) : ITtsProvider
{
    public TtsTransport Transport => TtsTransport.PocketTtsHttp;
    public InferenceSetupDefinition SetupDefinition { get; } = new(
        new("darkspyre.connect.pockettts", "PocketTTS HTTP", InferenceImplementationKind.Connect, InferenceTask.SpeechSynthesis),
        "Synthesize speech through an externally managed PocketTTS endpoint.");

    public ITtsClient Bind(TtsConnection connection, XaiFamilyBinding? xai = null,
        IInferenceResourceAccess? resources = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (xai is not null || connection.Xai is not null || connection.Authorization is not null)
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "PocketTTS does not implement borrowed provider authorization or xAI binding.");
        ValidateConnection(connection);
        return new Client(this, connection with { Headers = new Dictionary<string, string>(connection.Headers, StringComparer.OrdinalIgnoreCase) });
    }

    internal static void ValidateConnection(TtsConnection connection)
    {
        if (connection.Transport != TtsTransport.PocketTtsHttp || !Enum.IsDefined(connection.PocketTts.Protocol)
            || connection.Endpoint is not { IsAbsoluteUri: true } || connection.Endpoint.Scheme is not ("http" or "https")
            || connection.Endpoint.UserInfo.Length != 0 || connection.Endpoint.Query.Length != 0 || connection.Endpoint.Fragment.Length != 0
            || connection.RequestTimeout <= TimeSpan.Zero || connection.RequestTimeout > TimeSpan.FromDays(1))
        { throw Invalid("Specify a valid PocketTTS HTTP connection and timeout of at most one day."); }
        foreach (string path in new[] { connection.PocketTts.VoicesPath, connection.PocketTts.SpeakPath, connection.PocketTts.HealthPath })
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\')
                || path.IndexOfAny(['?', '#', '\r', '\n', '\0']) >= 0
                || Uri.TryCreate(path.TrimStart('/'), UriKind.Absolute, out _))
            { throw Invalid("PocketTTS paths must be relative paths on the configured endpoint."); }
        }
        if (!IsToken(connection.PocketTts.VoiceFormField)) { throw Invalid("Specify a valid voice form field name."); }
        foreach ((string name, string value) in connection.Headers)
        {
            if (!IsToken(name) || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            { throw Invalid("Protected headers must have valid names and values."); }
        }
    }

    private static bool IsToken(string value) => value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character)
        || "!#$%&'*+-.^_`|~".Contains(character, StringComparison.Ordinal));
    private static InferenceException Invalid(string message) => new(InferenceFailureCode.InvalidRequest, message);
    private static InferenceException Protocol() => new(InferenceFailureCode.ProtocolFailure, "PocketTTS returned an invalid response.");

    internal static void ValidateRequest(TtsConnection connection, TtsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text) || request.Speed is { } speed && (!double.IsFinite(speed) || speed <= 0))
        { throw Invalid("Supply text and a positive finite speed."); }
        if (request.Format != TtsAudioFormat.Wav || connection.PocketTts.Protocol == PocketTtsProtocol.Multipart && request.Speed is not (null or 1))
        { throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "This protocol supports WAV; multipart does not support non-default speed."); }
        if (request.Language is not null || request.SampleRate is not null || request.BitRate is not null
            || request.IncludeTimestamps is not null || request.Xai is not null)
        { throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "PocketTTS does not implement these explicit speech controls."); }
    }

    private sealed class Client(PocketTtsHttpProvider provider, TtsConnection connection) : ITtsClient
    {
        public InferenceImplementationDescriptor Descriptor => provider.SetupDefinition.Implementation;

        public Task<TtsCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new TtsCapabilities(true, connection.PocketTts.Protocol == PocketTtsProtocol.BridgeJson,
                Array.AsReadOnly(new[] { TtsAudioFormat.Wav })));
        }

        public async Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<TtsVoice> voices = await ListAsync(connection.PocketTts.VoicesPath, cancellationToken).ConfigureAwait(false);
            if (voices.Count == 0) { voices = await ListAsync(connection.PocketTts.HealthPath, cancellationToken).ConfigureAwait(false); }
            return voices.Count > 0 ? voices : throw Protocol();
        }

        private Task<IReadOnlyList<TtsVoice>> ListAsync(string path, CancellationToken cancellationToken) =>
            SendAsync(HttpMethod.Get, path, null, async (response, token) =>
            {
                PocketTtsVoiceList? list = await response.Content.ReadFromJsonAsync(PocketTtsJsonContext.Default.PocketTtsVoiceList, token).ConfigureAwait(false);
                IEnumerable<TtsVoice> voices = list?.VoiceOptions is { Count: > 0 }
                    ? list.VoiceOptions.Where(option => option is not null).Select(option =>
                        new TtsVoice(option.Id ?? option.Name ?? string.Empty, option.Name ?? option.Id ?? string.Empty, Description: option.Category))
                    : (list?.Voices ?? []).Where(voice => voice is not null).Select(voice => new TtsVoice(voice, voice));
                return (IReadOnlyList<TtsVoice>)Array.AsReadOnly(voices.Where(voice => !string.IsNullOrWhiteSpace(voice.Id))
                    .DistinctBy(voice => voice.Id, StringComparer.Ordinal).ToArray());
            }, cancellationToken);

        public async Task<TtsResult> SpeakAsync(TtsRequest request, CancellationToken cancellationToken = default)
        {
            ValidateRequest(connection, request);
            long started = Stopwatch.GetTimestamp();
            using HttpContent body = CreateContent(request);
            byte[] audio = await SendAsync(HttpMethod.Post, connection.PocketTts.SpeakPath, body, async (response, token) =>
            {
                if (response.Content.Headers.ContentType?.MediaType is { } contentType
                    && !contentType.Equals("audio/wav", StringComparison.OrdinalIgnoreCase)
                    && !contentType.Equals("audio/x-wav", StringComparison.OrdinalIgnoreCase)) { throw Protocol(); }
                byte[] bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                ValidateWave(bytes);
                return bytes;
            }, cancellationToken).ConfigureAwait(false);
            return new("audio/wav", audio, new()
            {
                ImplementationId = Descriptor.Id,
                ModelId = "pockettts",
                Runtime = "HTTP",
                Elapsed = Stopwatch.GetElapsedTime(started)
            });
        }

        private HttpContent CreateContent(TtsRequest request)
        {
            if (connection.PocketTts.Protocol == PocketTtsProtocol.BridgeJson)
            {
                return JsonContent.Create(new PocketTtsBridgePayload(request.Text, request.VoiceId,
                    request.VoiceId, request.Speed ?? 1, "wav"), PocketTtsJsonContext.Default.PocketTtsBridgePayload);
            }
            var form = new MultipartFormDataContent
            {
                { new StringContent(request.Text), "text" },
                { new StringContent("wav"), "output_format" }
            };
            if (!string.IsNullOrWhiteSpace(request.VoiceId)) { form.Add(new StringContent(request.VoiceId), connection.PocketTts.VoiceFormField); }
            return form;
        }

        private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? body,
            Func<HttpResponseMessage, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(connection.RequestTimeout);
            using var request = new HttpRequestMessage(method, new Uri(new Uri(connection.Endpoint.AbsoluteUri.TrimEnd('/') + "/"), path.TrimStart('/'))) { Content = body };
            foreach ((string name, string value) in connection.Headers)
            { if (!request.Headers.TryAddWithoutValidation(name, value)) { throw Invalid("A configured header cannot be used on this request."); } }
            try
            {
                using HttpResponseMessage response = await provider.SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InferenceException(response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized => InferenceFailureCode.AuthenticationRequired,
                        HttpStatusCode.Forbidden => InferenceFailureCode.AccessDenied,
                        >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => InferenceFailureCode.ProtocolFailure,
                        _ => InferenceFailureCode.ExecutionFailed
                    }, $"PocketTTS request failed with HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}.");
                }
                return await read(response, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new InferenceException(InferenceFailureCode.TimedOut, "PocketTTS request timed out; it was not retried."); }
            catch (HttpRequestException) { throw new InferenceException(InferenceFailureCode.ExecutionFailed, "PocketTTS HTTP transport failed; it was not retried."); }
            catch (JsonException) { throw Protocol(); }
        }
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

    private static void ValidateWave(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 44 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != bytes.Length - 8) { throw Protocol(); }
        bool format = false, data = false;
        int offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            if (length > bytes.Length - offset - 8) { throw Protocol(); }
            if (bytes.Slice(offset, 4).SequenceEqual("fmt "u8)) { format = length >= 16; }
            if (bytes.Slice(offset, 4).SequenceEqual("data"u8)) { data = length > 0; }
            offset = checked(offset + 8 + (int)length + (int)(length & 1));
        }
        if (!format || !data || offset != bytes.Length) { throw Protocol(); }
    }

}
