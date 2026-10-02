using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.TTS;

/// <summary>Explicit xAI speech composition. Borrows HTTP, selected OAuth access and optional workspace access.</summary>
public sealed class XaiTtsProvider : ITtsProvider
{
    private readonly HttpClient _http;
    private readonly Func<Uri, HttpRequestMessage, CancellationToken, Task<WebSocket>> _connect;
    public XaiTtsProvider(HttpClient httpClient) { _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient)); _connect = ConnectAsync; }
    internal XaiTtsProvider(HttpClient httpClient, Func<Uri, HttpRequestMessage, CancellationToken, Task<WebSocket>> connect)
    { _http = httpClient; _connect = connect; }
    public TtsTransport Transport => TtsTransport.XaiTts;
    public InferenceSetupDefinition SetupDefinition { get; } = new(new("darkspyre.connect.xai.tts", "xAI speech", InferenceImplementationKind.Connect, InferenceTask.SpeechSynthesis), "Synthesize speech through the selected xAI OAuth connection.");
    public ITtsClient Bind(TtsConnection connection, XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null)
    {
        ArgumentNullException.ThrowIfNull(connection); TtsProviderService.ValidateBinding(connection, xai);
        return new Client(this, connection, xai!, resources);
    }
    private async Task<WebSocket> ConnectAsync(Uri uri, HttpRequestMessage headers, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        try
        {
            foreach (var header in headers.Headers) socket.Options.SetRequestHeader(header.Key, string.Join(", ", header.Value));
            await socket.ConnectAsync(uri, _http, token).ConfigureAwait(false); return socket;
        }
        catch (Exception original)
        {
            try { socket.Dispose(); } catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            throw;
        }
    }

    private sealed class Client : ITtsClient, ITtsStreamingClient
    {
        private readonly LlmOperationLifetime _lifetime = new();
        private readonly List<Exception> _cleanup = [];
        private XaiTtsProvider? _provider;
        private TtsConnection? _connection;
        private XaiFamilyBinding? _binding;
        private IInferenceResourceAccess? _resources;
        internal Client(XaiTtsProvider provider, TtsConnection connection, XaiFamilyBinding binding, IInferenceResourceAccess? resources)
        { _provider = provider; _connection = connection; _binding = binding; _resources = resources; }
        public InferenceImplementationDescriptor Descriptor { get { using var lease = _lifetime.Enter(default); return _provider!.SetupDefinition.Implementation; } }
        public Task<TtsCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            using var lease = _lifetime.Enter(cancellationToken);
            return Task.FromResult(new TtsCapabilities(_binding!.HasCatalogService, true, Enum.GetValues<TtsAudioFormat>())
            {
                SupportsLanguage = true,
                SupportsSampleRate = true,
                SupportsBitRate = true,
                SupportsTimestamps = true,
                SupportsStreaming = true,
                SupportsRepeatedUtterances = true,
                SupportsReplacementUpdates = true,
                SupportsUtteranceClearing = true,
                SupportsTextNormalization = true,
                SupportsTextReplacement = true,
                SupportsOptimizeStreamingLatency = true,
                SampleRates = XaiTtsProtocol.Rates,
                BitRates = XaiTtsProtocol.BitRates
            });
        }
        private async Task<LlmConnection> AcquireAsync(CancellationToken token)
        {
            XaiFamilyBinding acquired = await _binding!.AcquireBoundAsync(selected =>
            {
                if (!string.Equals(_connection!.Endpoint.AbsoluteUri.TrimEnd('/'), selected.Endpoint.TrimEnd('/'), StringComparison.Ordinal)) throw new ArgumentException("The speech endpoint must match its selected xAI region.");
            }, token).ConfigureAwait(false);
            Fence(token);
            LlmConnection snapshot = await acquired.AcquireAsync(token).ConfigureAwait(false);
            Fence(token); return snapshot;
        }
        public async Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken cancellationToken = default)
        {
            using var lease = _lifetime.Enter(cancellationToken);
            _binding!.RequireCatalogService();
            Fence(lease.Token);
            LlmConnection snapshot = await AcquireAsync(lease.Token).ConfigureAwait(false);
            var voices = await _binding.RunCatalogAsync(snapshot, () => Fence(lease.Token), (http, selected, fence, current) =>
                SendAsync(selected, "tts/voices", null, async (response, token) =>
            {
                using JsonDocument document = JsonDocument.Parse(await SdkHttpConsumption.ReadAsStringAsync(response.Content, token).ConfigureAwait(false));
                if (!document.RootElement.TryGetProperty("voices", out var values) || values.ValueKind != JsonValueKind.Array) throw XaiTtsProtocol.Protocol();
                return (IReadOnlyList<TtsVoice>)Array.AsReadOnly(values.EnumerateArray().Select(value => new TtsVoice(XaiTtsProtocol.RequiredString(value, "voice_id"), XaiTtsProtocol.RequiredString(value, "name"),
                    XaiTtsProtocol.OptionalString(value, "locale"), XaiTtsProtocol.OptionalString(value, "description"))).ToArray());
            }, current, http, fence), lease.Token).ConfigureAwait(false);
            Fence(lease.Token); return voices;
        }
        public async Task<TtsResult> SpeakAsync(TtsRequest request, CancellationToken cancellationToken = default)
        {
            using var lease = _lifetime.Enter(cancellationToken); TtsRequest admitted = XaiTtsProtocol.Snapshot(_connection!, request);
            using var consumption = SdkHttpConsumption.Enter(_provider!._http, lease.Token); CancellationToken token = consumption.Token;
            LlmConnection snapshot = await AcquireAsync(token).ConfigureAwait(false);
            var scope = _resources is null ? null : await TtsResourceWriter.ReadScopeAsync(_resources, () => Fence(token), token).ConfigureAwait(false);
            Fence(token); long started = Stopwatch.GetTimestamp();
            var content = new ByteArrayContent(XaiTtsProtocol.Payload(admitted)); content.Headers.ContentType = new("application/json");
            TtsResult result = await SendAsync(snapshot, "tts", content, async (response, current) =>
            {
                byte[] audio; string contentType; TimeSpan? duration = null; IReadOnlyList<TtsTimestamp>? timestamps = null;
                if (admitted.IncludeTimestamps == true)
                {
                    using JsonDocument document = JsonDocument.Parse(await SdkHttpConsumption.ReadAsStringAsync(response.Content, current).ConfigureAwait(false));
                    audio = XaiTtsProtocol.Audio(document.RootElement, "audio"); contentType = XaiTtsProtocol.RequiredString(document.RootElement, "content_type");
                    duration = XaiTtsProtocol.Seconds(document.RootElement, "duration"); timestamps = XaiTtsProtocol.Timings(document.RootElement);
                }
                else
                {
                    contentType = response.Content.Headers.ContentType?.MediaType ?? throw XaiTtsProtocol.Protocol();
                    audio = await SdkHttpConsumption.ReadAsByteArrayAsync(response.Content, current).ConfigureAwait(false);
                }
                return new TtsResult(contentType, audio, new() { ImplementationId = _provider!.SetupDefinition.Implementation.Id, ModelId = null, Runtime = "HTTP", Elapsed = Stopwatch.GetElapsedTime(started) }) { Duration = duration, Timestamps = timestamps };
            }, token).ConfigureAwait(false);
            Fence(token);
            if (_resources is not null) result = result with { Resource = await TtsResourceWriter.CommitAsync(_resources, scope!, result.Audio, result.ContentType, () => Fence(token), RecordCleanup, token).ConfigureAwait(false) };
            Fence(token); return consumption.Publish(result);
        }
        private async Task<T> SendAsync<T>(LlmConnection snapshot, string path, HttpContent? body,
            Func<HttpResponseMessage, CancellationToken, Task<T>> read, CancellationToken token,
            HttpClient? http = null, Action? outerFence = null)
        {
            var request = new HttpRequestMessage { Content = body }; HttpResponseMessage? response = null;
            var errors = new List<Exception>(); T result = default!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                CombinedFence(); request.Method = body is null ? HttpMethod.Get : HttpMethod.Post; request.RequestUri = ProviderHttp.BuildUri(snapshot, path);
                XaiRequestHeaders.Apply(request, snapshot, false); foreach (var header in _connection!.Headers) if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value)) throw XaiTtsProtocol.Invalid();
                timeout.CancelAfter(_connection.RequestTimeout);
                response = await (http ?? _provider!._http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); CombinedFence();
                await XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, LlmCredential.Parse(snapshot.Credential), [], timeout.Token, diagnosticBody: false, diagnosticMessage: "Provider rejected the speech request.").ConfigureAwait(false); CombinedFence();
                result = await read(response, timeout.Token).ConfigureAwait(false); CombinedFence();
            }
            catch (JsonException) { errors.Add(XaiTtsProtocol.Protocol()); }
            catch (Exception error) { errors.Add(error); }
            try { response?.Dispose(); } catch (Exception error) { var cleanup = LlmResourcePreparation.AsCleanupFailure(error, refusedOwner: response); RecordCleanup(cleanup); errors.Add(cleanup); }
            try { request.Dispose(); } catch (Exception error) { var cleanup = LlmResourcePreparation.AsCleanupFailure(error, refusedOwner: request); RecordCleanup(cleanup); errors.Add(cleanup); }
            if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw(); if (errors.Count > 1) throw new AggregateException(errors);
            CombinedFence(); return result;
            void CombinedFence() { Fence(token); outerFence?.Invoke(); Fence(token); }
        }
        public async Task<ITtsStreamingSession> OpenStreamAsync(TtsStreamOptions options, InferenceStreamLimits limits, CancellationToken cancellationToken = default)
        {
            using var lease = _lifetime.Enter(cancellationToken); ArgumentNullException.ThrowIfNull(limits);
            if (limits.MaximumBufferedEvents <= 0 || limits.MaximumBufferedPayloadBytes <= 0 || limits.MaximumMessageBytes <= 0) throw XaiTtsProtocol.Invalid();
            var admitted = XaiTtsProtocol.Snapshot(_connection!, options);
            using var consumption = SdkHttpConsumption.Enter(_provider!._http, lease.Token);
            LlmConnection snapshot = await AcquireAsync(consumption.Token).ConfigureAwait(false);
            var session = await XaiTtsSession.OpenAsync(_provider!._connect, snapshot, _connection!, admitted, limits, _resources, () => { Fence(consumption.Token); consumption.Fence(); }, consumption.Token).ConfigureAwait(false);
            try { Fence(consumption.Token); return consumption.Publish<ITtsStreamingSession>(session); }
            catch (Exception original) { try { await session.DisposeAsync().ConfigureAwait(false); } catch (Exception cleanup) { throw new AggregateException(original, cleanup); } throw; }
        }
        public async IAsyncEnumerable<TtsStreamEvent> SpeakStreamAsync(IAsyncEnumerable<string> text, TtsStreamOptions options, InferenceStreamLimits limits,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(text);
            ITtsStreamingSession session = await OpenStreamAsync(options, limits, cancellationToken).ConfigureAwait(false);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task sender = SendInputAsync();
            IAsyncEnumerator<TtsStreamEvent> reader = session.Events.GetAsyncEnumerator(cancellationToken);
            Exception? original = null;
            try
            {
                while (await MoveNextAsync().ConfigureAwait(false))
                {
                    TtsStreamEvent observation = reader.Current;
                    yield return observation;
                    if (observation is TtsUtteranceCompleted) { await AwaitSenderAsync().ConfigureAwait(false); break; }
                }
                await AwaitSenderAsync().ConfigureAwait(false);
            }
            finally
            {
                await DrainAsync().ConfigureAwait(false);
            }
            async Task DrainAsync()
            {
                Task cancel = stop.CancelAsync(); Task retirement = session.DisposeAsync().AsTask();
                var failures = new List<Exception>();
                if (original is not null) failures.Add(original);
                await CaptureAsync(cancel).ConfigureAwait(false); await CaptureAsync(retirement).ConfigureAwait(false);
                try { await sender.ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception error) { Add(error); }
                try { await reader.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { Add(error); }
                if (failures.Count > 1) throw new AggregateException(failures);
                if (failures.Count == 1 && !ReferenceEquals(failures[0], original)) ExceptionDispatchInfo.Capture(failures[0]).Throw();
                async Task CaptureAsync(Task task) { try { await task.ConfigureAwait(false); } catch (Exception error) { Add(error); } }
                void Add(Exception error) { if (!failures.Contains(error)) failures.Add(error); }
            }
            async Task<bool> MoveNextAsync()
            {
                try { return await reader.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception error) { original = error; throw; }
            }
            async Task AwaitSenderAsync()
            {
                try { await sender.ConfigureAwait(false); }
                catch (Exception error) { original = error; throw; }
            }
            async Task SendInputAsync()
            {
                try
                {
                    await foreach (string chunk in text.WithCancellation(stop.Token).ConfigureAwait(false)) await session.AppendTextAsync(chunk, stop.Token).ConfigureAwait(false);
                    await session.CompleteUtteranceAsync(stop.Token).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    try { await session.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
                    throw;
                }
            }
        }
        private void Fence(CancellationToken token) { token.ThrowIfCancellationRequested(); try { _lifetime.ThrowIfStopping(); } catch (ObjectDisposedException) { throw new OperationCanceledException(token); } }
        private void RecordCleanup(Exception error) { lock (_cleanup) _cleanup.Add(error); }
        public void Dispose() => RetireAsync().GetAwaiter().GetResult();
        public ValueTask DisposeAsync() => new(RetireAsync());
        private Task RetireAsync() => _lifetime.RetireAsync(() =>
        {
            _provider = null; _connection = null; _binding = null; _resources = null;
            return _cleanup.Count switch { 0 => Task.CompletedTask, 1 => Task.FromException(_cleanup[0]), _ => Task.FromException(new AggregateException(_cleanup)) };
        });
    }
}
