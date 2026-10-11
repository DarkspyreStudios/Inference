using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.Audio;

/// <summary>
/// External HTTP only. The host owns HttpClient and must disable automatic redirects when using
/// protected headers. No provider process, model installation, playback or application cache is managed.
/// </summary>
public sealed class TangoFluxHttpProvider(HttpClient httpClient) : IAudioProvider
{
    private static readonly IReadOnlyList<string> ContentTypes = Array.AsReadOnly(new[] { "audio/wav" });
    /// <inheritdoc />
    public AudioTransport Transport => AudioTransport.TangoFluxHttp;
    /// <inheritdoc />
    public InferenceSetupDefinition SetupDefinition { get; } = new(
        new("darkspyre.connect.tangoflux", "TangoFlux HTTP", InferenceImplementationKind.Connect, InferenceTask.AudioGeneration),
        "Generate audio through an externally managed TangoFlux endpoint. Model listing and health checks are unavailable.");

    /// <inheritdoc />
    public IAudioGenerationClient Bind(AudioConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ValidateConnection(connection);
        return new Client(this, connection with { Headers = new Dictionary<string, string>(connection.Headers, StringComparer.OrdinalIgnoreCase) });
    }

    internal static void ValidateConnection(AudioConnection connection)
    {
        if (connection.Transport != AudioTransport.TangoFluxHttp || connection.Endpoint is not { IsAbsoluteUri: true }
            || connection.Endpoint.Scheme is not ("http" or "https") || connection.Endpoint.UserInfo.Length != 0
            || connection.Endpoint.Query.Length != 0 || connection.Endpoint.Fragment.Length != 0
            || connection.RequestTimeout <= TimeSpan.Zero || connection.RequestTimeout > TimeSpan.FromDays(1))
        { throw Invalid("Specify a valid TangoFlux HTTP connection and timeout of at most one day."); }
        string path = connection.TangoFlux.GeneratePath;
        if (string.IsNullOrWhiteSpace(path) || path != path.Trim() || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\')
            || path.IndexOfAny(['?', '#', '\r', '\n', '\0']) >= 0 || Uri.TryCreate(path.TrimStart('/'), UriKind.Absolute, out _))
        { throw Invalid("The generation path must be relative to the configured endpoint."); }
        foreach ((string name, string value) in connection.Headers)
        {
            if (!IsHeaderName(name) || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            { throw Invalid("Protected headers must have valid names and values."); }
        }
    }

    internal static void ValidateRequest(AudioGenerationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Duration < TimeSpan.FromSeconds(1) || request.Duration > TimeSpan.FromSeconds(30)
            || request.Steps is < 10 or > 100 || request.GuidanceScale is { } guidance && (!double.IsFinite(guidance) || guidance is < 0.1 or > 20))
        { throw Invalid("Supply a prompt, duration of 1–30 seconds, steps of 10–100 and finite guidance of 0.1–20."); }
    }

    private static bool IsHeaderName(string value) => value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character)
        || "!#$%&'*+-.^_`|~".Contains(character, StringComparison.Ordinal));
    private static InferenceException Invalid(string message) => new(InferenceFailureCode.InvalidRequest, message);
    private static InferenceException Protocol() => new(InferenceFailureCode.ProtocolFailure, "TangoFlux returned an invalid WAV response.");

    private sealed class Client(TangoFluxHttpProvider provider, AudioConnection connection) : IAudioGenerationClient
    {
        public InferenceImplementationDescriptor Descriptor => provider.SetupDefinition.Implementation;

        public Task<AudioGenerationCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AudioGenerationCapabilities(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
                10, 100, 0.1, 20, InferenceSupport.Supported, InferenceSupport.Supported,
                InferenceSupport.Unsupported, InferenceSupport.Unsupported, ContentTypes));
        }

        public async Task<AudioGenerationResult> GenerateAsync(AudioGenerationRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRequest(request);
            using var consumption = provider.Enter(cancellationToken); cancellationToken = consumption.Token;
            long started = Stopwatch.GetTimestamp();
            AudioGenerationResult result;
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(connection.RequestTimeout);
                using var message = new HttpRequestMessage(HttpMethod.Post,
                    new Uri(new Uri(connection.Endpoint.AbsoluteUri.TrimEnd('/') + "/"), connection.TangoFlux.GeneratePath.TrimStart('/')))
                {
                    Content = JsonContent.Create(new TangoFluxGeneratePayload(request.Prompt, request.Duration.TotalSeconds,
                        request.Steps ?? 10, request.GuidanceScale ?? 4.5, request.Seed, request.UseCache), TangoFluxJsonContext.Default.TangoFluxGeneratePayload)
                };
                foreach ((string name, string value) in connection.Headers)
                { if (!message.Headers.TryAddWithoutValidation(name, value)) { throw Invalid("A configured header cannot be used on this request."); } }
                try
                {
                    using HttpResponseMessage response = await provider.SendAsync(message, timeout.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InferenceException(response.StatusCode switch
                        {
                            HttpStatusCode.Unauthorized => InferenceFailureCode.AuthenticationRequired,
                            HttpStatusCode.Forbidden => InferenceFailureCode.AccessDenied,
                            >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => InferenceFailureCode.ProtocolFailure,
                            _ => InferenceFailureCode.ExecutionFailed
                        }, $"TangoFlux request failed with HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}.");
                    }
                    if (response.Content.Headers.ContentType?.MediaType is { } contentType
                        && !contentType.Equals("audio/wav", StringComparison.OrdinalIgnoreCase)) { throw Protocol(); }
                    byte[] bytes = await SdkHttpConsumption.ReadAsByteArrayAsync(response.Content, timeout.Token).ConfigureAwait(false);
                    ValidateWave(bytes);
                    result = new AudioGenerationResult("audio/wav", bytes, null, new()
                    {
                        ImplementationId = Descriptor.Id,
                        ModelId = null,
                        Runtime = "HTTP",
                        Elapsed = Stopwatch.GetElapsedTime(started)
                    });
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new InferenceException(InferenceFailureCode.TimedOut, "TangoFlux request timed out; it was not retried."); }
                catch (HttpRequestException) { throw new InferenceException(InferenceFailureCode.ExecutionFailed, "TangoFlux HTTP transport failed; it was not retried."); }
                catch (IOException) { throw new InferenceException(InferenceFailureCode.ExecutionFailed, "TangoFlux response could not be read; it was not retried."); }
            }
            return consumption.Publish(result);
        }
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    private SdkHttpConsumption.Operation Enter(CancellationToken token) => SdkHttpConsumption.Enter(httpClient, token);

    // Protocol framing validation only, not an audio decoder or duration estimator.
    private static void ValidateWave(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 44 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != bytes.Length - 8) { throw Protocol(); }
        bool format = false, data = false;
        long offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            int start = (int)offset;
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(start + 4)..]);
            if (length > bytes.Length - start - 8) { throw Protocol(); }
            if (bytes.Slice(start, 4).SequenceEqual("fmt "u8))
            {
                if (format || length < 16 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[(start + 8)..]) == 0
                    || BinaryPrimitives.ReadUInt16LittleEndian(bytes[(start + 10)..]) == 0
                    || BinaryPrimitives.ReadUInt32LittleEndian(bytes[(start + 12)..]) == 0) { throw Protocol(); }
                format = true;
            }
            if (bytes.Slice(start, 4).SequenceEqual("data"u8))
            {
                if (data || length == 0) { throw Protocol(); }
                data = true;
            }
            offset += 8L + length + (length & 1);
        }
        if (!format || !data || offset != bytes.Length) { throw Protocol(); }
    }

}
