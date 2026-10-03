using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.STT;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.STT;

/// <summary>Explicit xAI transcription composition. HTTP and selected OAuth/workspace access are borrowed.</summary>
public sealed class XaiSttProvider : ISttProvider
{
    private readonly HttpClient _http;
    private readonly Func<Uri, HttpRequestMessage, Action, Action<Exception>, CancellationToken, Task<WebSocket>> _connect;
    public XaiSttProvider(HttpClient httpClient)
    { _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient)); _connect = ConnectAsync; }
    internal XaiSttProvider(HttpClient httpClient, Func<Uri, HttpRequestMessage, Action, Action<Exception>, CancellationToken, Task<WebSocket>> connect)
    { _http = httpClient; _connect = connect; }
    public SttTransport Transport => SttTransport.XaiStt;
    public InferenceSetupDefinition SetupDefinition { get; } = new(new("darkspyre.connect.xai.stt", "xAI transcription", InferenceImplementationKind.Connect, InferenceTask.SpeechTranscription), "Transcribe audio through the selected xAI OAuth connection.");
    public ISpeechTranscriptionClient Bind(SttConnection connection, XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null)
    {
        ArgumentNullException.ThrowIfNull(connection); SttProviderService.ValidateBinding(connection, xai);
        if (connection.Transport != Transport) throw XaiSttProtocol.Invalid();
        return new Client(this, connection, xai!, resources);
    }
    private async Task<WebSocket> ConnectAsync(Uri uri, HttpRequestMessage headers, Action openingFence, Action<Exception> recordCleanup, CancellationToken token)
    {
        var socket = new ClientWebSocket(); bool dispatched = false;
        try
        {
            foreach (var header in headers.Headers) socket.Options.SetRequestHeader(header.Key, string.Join(", ", header.Value));
            socket.Options.CollectHttpResponseDetails = true; dispatched = true;
            await socket.ConnectAsync(uri, _http, token).ConfigureAwait(false); return socket;
        }
        catch (Exception original)
        {
            Exception failure = original;
            if (original is not OperationCanceledException)
            {
                try { openingFence(); if (dispatched) failure = XaiErrorClassifier.SpeechUpgradeFailure(socket.HttpStatusCode, original); }
                catch (Exception refused) { failure = refused; }
            }
            try { socket.Dispose(); }
            catch (Exception error)
            { Exception cleanup = LlmResourcePreparation.AsCleanupFailure(error, refusedOwner: socket); recordCleanup(cleanup); throw new AggregateException(failure, cleanup); }
            ExceptionDispatchInfo.Capture(failure).Throw(); throw;
        }
    }

    private sealed class Client : ISpeechTranscriptionClient
    {
        private readonly LlmOperationLifetime _lifetime = new();
        private readonly List<Exception> _cleanup = [];
        private XaiSttProvider? _provider;
        private SttConnection? _connection;
        private XaiFamilyBinding? _binding;
        private IInferenceResourceAccess? _resources;
        internal Client(XaiSttProvider provider, SttConnection connection, XaiFamilyBinding binding, IInferenceResourceAccess? resources)
        { _provider = provider; _connection = connection; _binding = binding; _resources = resources; }
        public Task<SpeechTranscriptionCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default)
        {
            using var operation = _lifetime.Enter(cancellationToken);
            return Task.FromResult(XaiSttProtocol.Capabilities());
        }
        private async Task<LlmConnection> AcquireAsync(CancellationToken token)
        {
            XaiFamilyBinding acquired = await _binding!.AcquireBoundAsync(selected =>
            {
                if (!string.Equals(_connection!.Endpoint.AbsoluteUri.TrimEnd('/'), selected.Endpoint.TrimEnd('/'), StringComparison.Ordinal)) throw XaiSttProtocol.Invalid();
            }, token).ConfigureAwait(false);
            Fence(token); LlmConnection snapshot = await acquired.AcquireAsync(token).ConfigureAwait(false); Fence(token); return snapshot;
        }
        public async Task<SpeechTranscriptionResult> TranscribeAsync(SpeechTranscriptionInput input, SpeechTranscriptionOptions options, CancellationToken cancellationToken = default)
        {
            using var operation = _lifetime.Enter(cancellationToken);
            SpeechTranscriptionInput admitted = XaiSttProtocol.Snapshot(input); SpeechTranscriptionOptions controls = XaiSttProtocol.Snapshot(options);
            if (admitted is BinarySpeechInput binaryMetadata) XaiSttProtocol.ValidateFileMetadata(binaryMetadata.ContentType, binaryMetadata.FileName, controls);
            if (admitted is ResourceSpeechInput && _resources is null) throw XaiSttProtocol.Unsupported();
            using var consumption = SdkHttpConsumption.Enter(_provider!._http, operation.Token); CancellationToken token = consumption.Token;
            void Check() { Fence(token); consumption.Fence(); }
            LlmConnection snapshot = await AcquireAsync(token).ConfigureAwait(false); Check();
            var body = new MultipartFormDataContent(); LlmResourcePreparation? reading = null;
            HttpContent? unhanded = body; SpeechTranscriptionResult? result = null; var failures = new List<Exception>();
            try
            {
                foreach (var field in XaiSttProtocol.Fields(controls)) body.Add(new StringContent(field.Value), field.Key);
                switch (admitted)
                {
                    case BinarySpeechInput binary:
                        AddFile(body, new ByteArrayContent(binary.Data.ToArray()), binary.ContentType, binary.FileName); break;
                    case UrlSpeechInput url: body.Add(new StringContent(url.Url.OriginalString), "url"); break;
                    case ResourceSpeechInput resource:
                        reading = LlmResourcePreparation.ForNamedReads();
                        (InferenceResourceDescriptor Descriptor, LlmResourcePreparation.CountingReadStream Content) opened;
                        try
                        {
                            opened = await reading.OpenNamedAsync(_resources!, resource.Resource,
                                descriptor => XaiSttProtocol.ValidateFileMetadata(descriptor.ContentType, descriptor.FileName, controls), Check, token).ConfigureAwait(false);
                        }
                        catch (Exception error) { ExceptionDispatchInfo.Capture(ResourceFailure(error, InferenceResourceOperationPhase.OpenRead, resource.Resource, null, token)).Throw(); throw; }
                        AddFile(body, new ResourceContent(reading, _resources!, opened.Descriptor, opened.Content, Check, token), opened.Descriptor.ContentType!, opened.Descriptor.FileName);
                        break;
                }
                long started = Stopwatch.GetTimestamp(); unhanded = null;
                result = await SendAsync(snapshot, body, async (response, current) =>
                {
                    using JsonDocument document = JsonDocument.Parse(await SdkHttpConsumption.ReadAsStringAsync(response.Content, current).ConfigureAwait(false));
                    Check(); return XaiSttProtocol.Result(document.RootElement, new()
                    { ImplementationId = _provider!.SetupDefinition.Implementation.Id, ModelId = null, Runtime = "HTTP", Elapsed = Stopwatch.GetElapsedTime(started) });
                }, Check, token).ConfigureAwait(false);
            }
            catch (Exception error) { failures.Add(error); }
            if (unhanded is not null)
            {
                try { unhanded.Dispose(); } catch (Exception error) { var cleanup = LlmResourcePreparation.AsCleanupFailure(error, refusedOwner: unhanded); RecordCleanup(cleanup); failures.Add(cleanup); }
            }
            if (reading is not null)
            {
                try { await reading.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { RecordCleanup(error); failures.Add(error); }
            }
            Throw(failures); Check(); return consumption.Publish(result!);
        }
        private static void AddFile(MultipartFormDataContent body, HttpContent file, string type, string? name)
        {
            // Transfer content ownership before a refusing header setter can throw.
            if (name is null) body.Add(file, "file"); else body.Add(file, "file", name);
            file.Headers.ContentType = new(type);
        }
        private async Task<T> SendAsync<T>(LlmConnection snapshot, HttpContent body,
            Func<HttpResponseMessage, CancellationToken, Task<T>> read, Action fence, CancellationToken token)
        {
            var request = new HttpRequestMessage { Content = body }; HttpResponseMessage? response = null;
            var errors = new List<Exception>(); T result = default!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                fence(); request.Method = HttpMethod.Post; request.RequestUri = ProviderHttp.BuildUri(snapshot, "stt");
                XaiRequestHeaders.Apply(request, snapshot, false);
                foreach (var header in _connection!.AdditionalHeaders) if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value)) throw XaiSttProtocol.Invalid();
                timeout.CancelAfter(_connection.RequestTimeout);
                response = await _provider!._http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); fence();
                await XaiErrorClassifier.ThrowIfUnsuccessfulSpeechAsync(response, LlmCredential.Parse(snapshot.Credential), timeout.Token).ConfigureAwait(false); fence();
                result = await read(response, timeout.Token).ConfigureAwait(false); fence();
            }
            catch (JsonException) { errors.Add(XaiSttProtocol.Protocol()); }
            catch (Exception error) { errors.Add(error); }
            try { response?.Dispose(); } catch (Exception error) { var cleanup = LlmResourcePreparation.AsCleanupFailure(error, refusedOwner: response); RecordCleanup(cleanup); errors.Add(cleanup); }
            try { request.Dispose(); } catch (Exception error) { var cleanup = LlmResourcePreparation.AsCleanupFailure(error, refusedOwner: request); RecordCleanup(cleanup); errors.Add(cleanup); }
            Throw(errors); fence(); return result;
        }
        public async Task<ISpeechTranscriptionSession> OpenStreamAsync(SpeechStreamOptions options, InferenceStreamLimits limits, CancellationToken cancellationToken = default)
        {
            using var operation = _lifetime.Enter(cancellationToken);
            return await OpenCoreAsync(options, limits, operation.Token).ConfigureAwait(false);
        }
        private async Task<ISpeechTranscriptionSession> OpenCoreAsync(SpeechStreamOptions options, InferenceStreamLimits limits, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(limits);
            var controls = XaiSttProtocol.Snapshot(options);
            if (limits.MaximumBufferedEvents <= 0 || limits.MaximumBufferedPayloadBytes <= 0 || limits.MaximumMessageBytes <= 0) throw XaiSttProtocol.Invalid();
            using var consumption = SdkHttpConsumption.Enter(_provider!._http, token);
            LlmConnection snapshot = await AcquireAsync(consumption.Token).ConfigureAwait(false);
            var session = await XaiSttSession.OpenAsync(_provider!._connect, snapshot, _connection!, controls, limits,
                () => { Fence(consumption.Token); consumption.Fence(); }, RecordCleanup, consumption.Token).ConfigureAwait(false);
            try { Fence(consumption.Token); return consumption.Publish<ISpeechTranscriptionSession>(session); }
            catch (Exception original)
            {
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { RecordCleanup(error); throw new AggregateException(original, error); }
                throw;
            }
        }
        public async IAsyncEnumerable<SpeechTranscriptEvent> TranscribeStreamAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> audio,
            SpeechStreamOptions options, InferenceStreamLimits limits, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(audio); LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
            ISpeechTranscriptionSession session;
            try { session = await OpenCoreAsync(options, limits, operation.Token).ConfigureAwait(false); }
            catch { operation.Dispose(); throw; }
            CancellationToken token = operation.Token;
            var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration signal = stop.Token.Register(() => finished.TrySetResult());
            Task sender = SendInputAsync(); Task<Exception?> completion = CompleteAsync();
            IAsyncEnumerator<SpeechTranscriptEvent> reader = session.Events.GetAsyncEnumerator(cancellationToken); Exception? original = null;
            try
            {
                while (await MoveNextAsync().ConfigureAwait(false)) { PublicationFence(); yield return reader.Current; }
                finished.TrySetResult(); Exception? failure = await completion.ConfigureAwait(false);
                if (failure is not null) { original = failure; ExceptionDispatchInfo.Capture(failure).Throw(); }
                PublicationFence();
            }
            finally { await DrainAsync().ConfigureAwait(false); }
            async Task DrainAsync()
            {
                finished.TrySetResult(); var failures = new List<Exception>(); if (original is not null) failures.Add(original);
                if (await completion.ConfigureAwait(false) is { } failure) Add(failures, failure);
                try { await reader.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { Add(failures, error); }
                if (failures.Count > 1) throw new AggregateException(failures);
                if (failures.Count == 1 && !ReferenceEquals(failures[0], original)) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }
            async Task<bool> MoveNextAsync()
            { try { return await reader.MoveNextAsync().ConfigureAwait(false); } catch (Exception error) { original = error; throw; } }
            void PublicationFence()
            { try { Fence(token); } catch (Exception error) { original = error; throw; } }
            async Task<Exception?> CompleteAsync()
            {
                await finished.Task.ConfigureAwait(false);
                var failures = new List<Exception>();
                try
                {
                    Task cancellation = stop.CancelAsync(); Task retirement = session.DisposeAsync().AsTask();
                    try { await cancellation.ConfigureAwait(false); } catch (Exception error) { RecordCleanup(error); Add(failures, error); }
                    try { await retirement.ConfigureAwait(false); } catch (Exception error) { RecordCleanup(error); Add(failures, error); }
                    try { await sender.ConfigureAwait(false); }
                    catch (OperationCanceledException error) when (stop.IsCancellationRequested && !LlmResourcePreparation.IsResourceFailure(error)) { }
                    catch (Exception error) { Add(failures, error); }
                    return failures.Count switch { 0 => null, 1 => failures[0], _ => new AggregateException(failures) };
                }
                finally { audio = null!; signal.Dispose(); stop.Dispose(); operation.Dispose(); }
            }
            async Task SendInputAsync()
            {
                IAsyncEnumerator<ReadOnlyMemory<byte>>? input = null; Exception? failure = null;
                try
                {
                    input = audio.GetAsyncEnumerator(stop.Token); Fence(token);
                    while (await input.MoveNextAsync().ConfigureAwait(false))
                    { Fence(token); byte[] chunk = input.Current.ToArray(); Fence(token); await session.AppendAudioAsync(chunk, stop.Token).ConfigureAwait(false); Fence(token); }
                    Fence(token); await session.CompleteAudioAsync(stop.Token).ConfigureAwait(false); Fence(token);
                }
                catch (Exception error) { failure = error; }
                if (input is not null)
                {
                    try { await input.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error) { var cleanup = LlmResourcePreparation.AsCleanupFailure(error, refusedOwner: input); RecordCleanup(cleanup); failure = failure is null ? cleanup : new AggregateException(failure, cleanup); }
                }
                if (failure is not null) { finished.TrySetResult(); ExceptionDispatchInfo.Capture(failure).Throw(); }
            }
        }
        private void Fence(CancellationToken token) { token.ThrowIfCancellationRequested(); try { _lifetime.ThrowIfStopping(); } catch (ObjectDisposedException) { throw new OperationCanceledException(token); } }
        private void RecordCleanup(Exception failure) { lock (_cleanup) Add(_cleanup, failure); }
        public void Dispose() => RetireAsync().GetAwaiter().GetResult();
        public ValueTask DisposeAsync() => new(RetireAsync());
        private Task RetireAsync() => _lifetime.RetireAsync(() =>
        {
            _provider = null; _binding = null; _connection = null; _resources = null;
            return _cleanup.Count switch { 0 => Task.CompletedTask, 1 => Task.FromException(_cleanup[0]), _ => Task.FromException(new AggregateException(_cleanup)) };
        });
    }
    internal static void Add(List<Exception> errors, Exception error)
    {
        if (errors.Any(existing => Contains(existing, error))) return;
        errors.RemoveAll(existing => Contains(error, existing)); errors.Add(error);
    }
    internal static bool Contains(Exception error, Exception candidate) => ReferenceEquals(error, candidate)
        || error is AggregateException aggregate && aggregate.InnerExceptions.Any(inner => Contains(inner, candidate));
    private static void Throw(List<Exception> failures)
    { if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw(); if (failures.Count > 1) throw new AggregateException(failures); }
    private static Exception ResourceFailure(Exception error, InferenceResourceOperationPhase phase, InferenceResourceReference reference, long? bytes, CancellationToken token)
    {
        if (LlmResourcePreparation.IsResourceFailure(error)) return error;
        var facts = new InferenceResourceOperationFailure(phase, reference, InferenceResourceCommitOutcome.NotAttempted, bytes);
        return error is OperationCanceledException canceled ? new InferenceResourceOperationCanceledException(facts, canceled.CancellationToken, error)
            : new InferenceResourceOperationException(InferenceFailureCode.ExecutionFailed, "The named transcription resource could not be read.", facts, error);
    }
    private sealed class ResourceContent(LlmResourcePreparation reading, IInferenceResourceAccess access, InferenceResourceDescriptor descriptor,
        LlmResourcePreparation.CountingReadStream content, Action fence, CancellationToken token) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = descriptor.Bytes ?? 0; return descriptor.Bytes is not null; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, token);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            try
            { fence(); await content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false); fence(); reading.VerifyRead(access, descriptor, content, fence, token); }
            catch (Exception error) { ExceptionDispatchInfo.Capture(ResourceFailure(error, InferenceResourceOperationPhase.Read, descriptor.Reference, content.BytesRead, token)).Throw(); throw; }
        }
    }
}
