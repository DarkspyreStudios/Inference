using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.Inference.Connect.Video.Internal;

namespace Darkspyre.Inference.Connect.Video.Providers;

internal sealed class XaiImagineVideoTransport : IVideoTransport, IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly LlmCleanupOutcomes _cleanup = new();

    public VideoTransport Transport => VideoTransport.XaiImagine;
    public InferenceSetupDefinition SetupDefinition { get; } = new(
        new("darkspyre.connect.xai.video", "Grok Video", InferenceImplementationKind.Connect, InferenceTask.VideoGeneration),
        "Generate, edit and extend video through the selected xAI connection.");

    public Task<VideoCapabilities> GetCapabilitiesAsync(VideoConnection connection, string? modelId = null,
        XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
        => RunAsync(connection, xai, async (bound, snapshot, fence, token) =>
        {
            IReadOnlyList<LlmModel> models = await bound.GetModelsAsync(snapshot, ModelCatalogFamily.Video, fence, token).ConfigureAwait(false);
            fence();
            if (modelId is not null && !models.Any(model => model.Id == modelId))
                throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The selected connection does not list this video model.");
            if (models.Count == 0) return new VideoCapabilities();
            bool modern = modelId == "grok-imagine-video-1.5";
            bool classic = modelId == "grok-imagine-video";
            return new VideoCapabilities
            {
                Operations = new[] { VideoOperation.Generation, VideoOperation.Edit, VideoOperation.Extension }.ToFrozenSet(),
                SourceKinds = new[] { InferenceMediaSourceKind.Binary, InferenceMediaSourceKind.Url,
                    InferenceMediaSourceKind.ProviderFile, InferenceMediaSourceKind.Resource }.ToFrozenSet(),
                MinimumGenerationDuration = TimeSpan.FromSeconds(1), MaximumGenerationDuration = TimeSpan.FromSeconds(15),
                MinimumExtensionDuration = TimeSpan.FromSeconds(2), MaximumExtensionDuration = TimeSpan.FromSeconds(10),
                ResolutionHeights = modern ? [480, 720, 1080] : classic ? [480, 720] : null,
                MaximumKeyframes = modern ? 4 : classic ? 0 : null,
                MaximumReferenceAudio = 3, SupportsLastFrame = modern,
                SupportsAudioControl = true, SupportsPresetVoiceReferences = true, SupportsSuppliedAudioReferences = true,
                SupportsProgress = true
            };
        }, cancellationToken);

    public Task<VideoJob> SubmitAsync(VideoConnection connection, VideoRequest request,
        XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default)
    {
        using var admission = _lifetime.Enter(cancellationToken);
        ValidateConnection(connection, xai);
        request = ConnectVideoInference.CaptureRequest(request);
        ConnectVideoInference.ValidateRequest(connection, request, resources, () => Fence(admission.Token, xai), admission.Token);
        return RunAsync(connection, xai, async (bound, snapshot, fence, token) =>
        {
            var prepared = await PrepareAsync(connection, request, resources, bound, snapshot, fence, token).ConfigureAwait(false);
            string id = await bound.RunCatalogAsync(snapshot, fence, (http, selected, ownerFence, current) =>
                SendAsync(http, connection, selected, HttpMethod.Post, prepared.Route, prepared.Body,
                    (_, text) => XaiVideoProtocol.ParseSubmission(text, value => Protected(value, connection, selected)), ownerFence, current), token).ConfigureAwait(false);
            fence();
            return new VideoJob(id, Scope(connection, bound));
        }, cancellationToken);
    }

    public Task<VideoJobSnapshot> GetAsync(VideoConnection connection, VideoJob job,
        XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ValidateConnection(connection, xai);
        RequireScope(connection, xai!, job.Scope);
        string route = JobRoute(job.ProviderJobId);
        return RunAsync(connection, xai, (bound, snapshot, fence, token) =>
            PollAsync(connection, job, route, bound, snapshot, fence, token), cancellationToken);
    }

    public async Task<Stream> DownloadAsync(VideoConnection connection, VideoOutput output,
        XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ValidateConnection(connection, xai);
        RequireScope(connection, xai!, output.Scope);
        string? fileId = output.File?.FileId;
        if (fileId is not null) XaiFileRequestData.Identity(fileId);
        string? jobId = XaiMediaData.OptionalString(output.ProviderState, "request_id", static _ => false);
        if (jobId is not null) _ = JobRoute(jobId);
        if (fileId is null && jobId is null)
            throw new ArgumentException("A native video download requires a Files receipt or native request ID; an arbitrary output URL is not authority.", nameof(output));
        LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        XaiFamilyBinding? bound = null;
        XaiHttpDownload? download = null;
        VideoDownloadStream? stream = null;
        try
        {
            bound = await xai!.AcquireBoundAsync(selected => ConnectVideoInference.ValidateSelected(connection, selected),
                () => Fence(operation.Token, xai), operation.Token).ConfigureAwait(false);
            LlmConnection snapshot = await bound.AcquireAsync(operation.Token).ConfigureAwait(false);
            void Check() => Fence(operation.Token, bound);
            Check();
            string? fileName = output.FileName;
            if (fileId is not null)
            {
                LlmProviderFile observed = await AuthorizeFileAsync(connection, bound, snapshot, fileId, Check, operation.Token).ConfigureAwait(false);
                fileName ??= observed.FileName;
                download = await bound.OpenFileDownloadAsync(snapshot, fileId, connection.RequestTimeout, Check, operation.Token,
                    request => ConnectVideoInference.ApplyAdditionalHeaders(request, connection, snapshot)).ConfigureAwait(false);
            }
            else
            {
                var job = new VideoJob(jobId!, output.Scope);
                VideoJobSnapshot observed = await PollAsync(connection, job, JobRoute(jobId!), bound, snapshot, Check, operation.Token).ConfigureAwait(false);
                if (observed.State != VideoJobState.Completed || observed.Output is not { } current)
                    throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The native video output is not available.");
                if (output.Duration is { } duration && current.Duration is { } actualDuration && duration != actualDuration)
                    throw XaiMediaData.Malformed();
                fileName ??= current.FileName;
                if (current.File is { } nativeFile)
                    download = await bound.OpenFileDownloadAsync(snapshot, nativeFile.FileId, connection.RequestTimeout, Check, operation.Token,
                        request => ConnectVideoInference.ApplyAdditionalHeaders(request, connection, snapshot)).ConfigureAwait(false);
                else
                {
                    string url = XaiMediaData.RequiredString(current.ProviderState, "url", value => Protected(value, connection, snapshot));
                    download = await bound.OpenMediaDownloadAsync(snapshot, url, connection.RequestTimeout, Check, operation.Token).ConfigureAwait(false);
                }
            }
            Check();
            if (XaiMediaData.ContentTypesConflict(output.ContentType, download.ContentType)) throw XaiMediaData.Malformed();
            Stream body = await download.OpenBodyAsync().ConfigureAwait(false);
            Check();
            XaiFamilyBinding creator = bound;
            stream = new VideoDownloadStream(body, download, error => _cleanup.Record(SafeFailure(error)), () =>
            {
                creator.DetachOperationFence(); operation.Dispose();
            }, fileName);
            Check();
            return stream;
        }
        catch (Exception original)
        {
            var failures = new List<Exception> { original };
            try
            {
                if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
                else if (download is not null) await download.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception cleanup) { failures.Add(cleanup); }
            finally
            {
                if (stream is null) download?.ReleaseAfterDrain();
                bound?.DetachOperationFence(); operation.Dispose();
            }
            Exception failure = SafeFailure(failures.Count == 1 ? failures[0] : new AggregateException(failures));
            _cleanup.Record(failure);
            ExceptionDispatchInfo.Throw(failure);
            throw;
        }
    }

    private static Task<VideoJobSnapshot> PollAsync(VideoConnection connection, VideoJob job, string route,
        XaiFamilyBinding bound, LlmConnection snapshot, Action fence, CancellationToken token)
        => bound.RunCatalogAsync(snapshot, fence, (http, selected, ownerFence, current) =>
            SendAsync(http, connection, selected, HttpMethod.Get, route, null, (status, text) =>
            {
                XaiVideoPoll poll = XaiVideoProtocol.ParsePoll(status, text,
                    value => Protected(value, connection, selected), ownerFence);
                InferenceResourceFailure? failure = poll.State is VideoJobState.Failed or VideoJobState.Expired
                    ? new(InferenceFailureCode.ExecutionFailed, poll.ErrorCode, poll.ErrorMessage is { } message
                        ? SecretRedactor.Sanitize(message, LlmCredential.Parse(selected.Credential), Secrets(connection, selected))
                        : $"The provider video state is {poll.State}.") : null;
                VideoOutput? output = null;
                VideoJobState state = poll.State;
                if (poll.Output is { } native)
                {
                    if (!native.GetProperty("respect_moderation").GetBoolean())
                    {
                        state = VideoJobState.Failed;
                        failure = new(InferenceFailureCode.ExecutionFailed, null, "The video did not pass provider moderation.");
                    }
                    else
                    {
                        JsonObject data = (JsonObject)JsonNode.Parse(native.GetRawText())!;
                        data["request_id"] = job.ProviderJobId;
                        string? fileId = null, fileName = null;
                        DateTimeOffset? expiry = null;
                        if (native.TryGetProperty("file_output", out JsonElement file))
                        {
                            fileId = XaiMediaData.RequiredString(file, "file_id", static _ => false);
                            XaiFileRequestData.Identity(fileId);
                            fileName = XaiMediaData.RequiredString(file, "filename", static _ => false);
                            if (file.TryGetProperty("expires_at", out JsonElement expires)) expiry = DateTimeOffset.FromUnixTimeSeconds(expires.GetInt64());
                        }
                        output = new(job.Scope, XaiMediaData.Element(data), Duration: TimeSpan.FromSeconds(native.GetProperty("duration").GetInt32()),
                            ExpiresAt: expiry, FileName: fileName, File: fileId is null ? null : new(fileId));
                    }
                }
                return new VideoJobSnapshot(job, state, poll.ProgressPercent, output, failure) { ModelId = poll.ModelId };
            }, ownerFence, current), token);

    private static IEnumerable<(InferenceMediaSource Source, bool Audio)> Sources(VideoRequest request)
    {
        if (request is VideoGenerationRequest generation)
        {
            if (generation.FirstFrame is { } first) yield return (first, false);
            if (generation.LastFrame is { } last) yield return (last, false);
            foreach (VideoKeyframe frame in generation.Keyframes ?? []) yield return (frame.Image, false);
            foreach (InferenceMediaSource image in generation.ReferenceImages ?? []) yield return (image, false);
            foreach (VideoAudioReference audio in generation.ReferenceAudio?.OfType<VideoAudioReference>() ?? []) yield return (audio.Source, true);
        }
        else if (request is VideoEditRequest edit) yield return (edit.SourceVideo, false);
        else if (request is VideoExtensionRequest extension) yield return (extension.SourceVideo, false);
    }

    [SuppressMessage("Design", "CA1031", Justification = "Preparation awaits owned source cleanup before creating the native job and preserves independent failures.")]
    private static async Task<(string Route, JsonObject Body)> PrepareAsync(VideoConnection connection, VideoRequest request,
        IInferenceResourceAccess? resources, XaiFamilyBinding bound, LlmConnection snapshot, Action fence, CancellationToken token)
    {
        var reads = LlmResourcePreparation.ForNamedReads();
        var inputs = new List<(InferenceMediaSource Source, JsonObject Value)>();
        var cache = new Dictionary<(InferenceMediaSource Source, bool Audio), JsonObject>();
        long remainingAudio = connection.MaximumEncodedReferenceAudioBytes ?? 0;
        (string Route, JsonObject Body) result = default;
        var errors = new List<Exception>();
        try
        {
            foreach (var item in Sources(request))
            {
                fence();
                if (!cache.TryGetValue(item, out JsonObject? value))
                {
                    if (item.Source is UrlMediaSource url) value = new() { ["url"] = url.Url.OriginalString };
                    else if (item.Source is FileMediaSource native)
                    {
                        LlmProviderFile file = await AuthorizeFileAsync(connection, bound, snapshot, native.File.FileId, fence, token).ConfigureAwait(false);
                        value = new() { ["file_id"] = file.Id };
                    }
                    else
                    {
                        LlmResourcePreparation.CountingReadStream? named = null;
                        InferenceResourceDescriptor? descriptor = null;
                        string? contentType, fileName;
                        Stream content;
                        if (item.Source is ResourceMediaSource logical)
                        {
                            try
                            {
                                var opened = await reads.OpenNamedAsync(resources!, logical.Resource, observed =>
                                {
                                    if (!item.Audio && observed.FileName is { } name) XaiFileRequestData.FileName(name);
                                }, fence, token).ConfigureAwait(false);
                                descriptor = opened.Descriptor; named = opened.Content; content = named;
                            }
                            catch (Exception error)
                            { throw LlmResourcePreparation.AsInputFailure(error, InferenceResourceOperationPhase.OpenRead, logical.Resource, null, token); }
                            contentType = descriptor.ContentType; fileName = descriptor.FileName;
                        }
                        else
                        {
                            var binary = (BinaryMediaSource)item.Source;
                            content = new MemoryStream(binary.Data.ToArray(), writable: false);
                            contentType = binary.ContentType; fileName = binary.FileName;
                        }
                        try
                        {
                            if (item.Audio)
                                value = new() { ["url"] = await XaiMediaData.AudioDataUrlAsync(content, contentType, remainingAudio, fence, token).ConfigureAwait(false) };
                            else
                            {
                                LlmProviderFile uploaded = await bound.RunFilesAsync(snapshot, fence, (files, selected, ownerFence, current) =>
                                    files.UploadAsync(selected, content, fileName ?? "video-input.bin", null, ownerFence, current,
                                        requestTimeout: connection.RequestTimeout, configureHeaders: message =>
                                            ConnectVideoInference.ApplyAdditionalHeaders(message, connection, selected)), token).ConfigureAwait(false);
                                if (Protected(uploaded.Id, connection, snapshot)) throw XaiMediaData.Malformed();
                                value = new() { ["file_id"] = uploaded.Id };
                            }
                            if (named is not null) reads.VerifyRead(resources!, descriptor!, named, fence, token);
                        }
                        catch (Exception error) when (item.Source is ResourceMediaSource resource)
                        { throw LlmResourcePreparation.AsInputFailure(error, InferenceResourceOperationPhase.Materialize, resource.Resource, named?.BytesRead, token); }
                        finally { if (named is null) await content.DisposeAsync().ConfigureAwait(false); }
                    }
                    cache.Add(item, value);
                }
                if (item.Audio && item.Source is not UrlMediaSource)
                {
                    remainingAudio = checked(remainingAudio - Encoding.UTF8.GetByteCount(value["url"]!.GetValue<string>()));
                    if (remainingAudio < 0) throw new ArgumentException("Supplied audio exceeds its encoded reference byte limit.", nameof(request));
                }
                inputs.Add((item.Source, value));
            }
            int next = 0;
            result = XaiVideoProtocol.Build(request, source =>
            {
                if (next >= inputs.Count || inputs[next].Source != source) throw XaiMediaData.Malformed();
                return inputs[next++].Value;
            }, fence);
            if (next != inputs.Count) throw XaiMediaData.Malformed();
        }
        catch (Exception error) { errors.Add(error); }
        try { await reads.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { errors.Add(cleanup); }
        ThrowFailures(errors);
        fence();
        return result;
    }

    private static async Task<LlmProviderFile> AuthorizeFileAsync(VideoConnection connection, XaiFamilyBinding bound,
        LlmConnection snapshot, string id, Action fence, CancellationToken token)
    {
        LlmProviderFile file = await bound.RunFilesAsync(snapshot, fence, (files, selected, ownerFence, current) =>
            files.GetAsync(selected, id, ownerFence, current, connection.RequestTimeout,
                request => ConnectVideoInference.ApplyAdditionalHeaders(request, connection, selected)), token).ConfigureAwait(false);
        fence();
        if (file.Id != id || Protected(file.Id, connection, snapshot) || Protected(file.FileName, connection, snapshot)) throw XaiMediaData.Malformed();
        return file;
    }

    private async Task<T> RunAsync<T>(VideoConnection connection, XaiFamilyBinding? xai,
        Func<XaiFamilyBinding, LlmConnection, Action, CancellationToken, Task<T>> action, CancellationToken token)
    {
        using var operation = _lifetime.Enter(token);
        ValidateConnection(connection, xai);
        XaiFamilyBinding? bound = null;
        try
        {
            bound = await xai!.AcquireBoundAsync(selected => ConnectVideoInference.ValidateSelected(connection, selected),
                () => Fence(operation.Token, xai), operation.Token).ConfigureAwait(false);
            LlmConnection snapshot = await bound.AcquireAsync(operation.Token).ConfigureAwait(false);
            void Check() => Fence(operation.Token, bound);
            Check();
            T result = await action(bound, snapshot, Check, operation.Token).ConfigureAwait(false);
            Check();
            return result;
        }
        catch (Exception original)
        {
            Exception failure = SafeFailure(original);
            _cleanup.Record(failure);
            ExceptionDispatchInfo.Throw(failure);
            throw;
        }
        finally { bound?.DetachOperationFence(); }
    }

    private static async Task<T> SendAsync<T>(HttpClient http, VideoConnection connection, LlmConnection snapshot,
        HttpMethod method, string route, JsonObject? body, Func<HttpStatusCode, string, T> parse, Action fence, CancellationToken token)
    {
        HttpRequestMessage? request = null;
        HttpResponseMessage? response = null;
        T result = default!;
        var errors = new List<Exception>();
        try
        {
            fence();
            request = new(method, ProviderHttp.BuildUri(snapshot, route));
            ConnectVideoInference.ApplyHeaders(request, connection, snapshot);
            if (body is not null) request.Content = JsonContent.Create(body);
            fence();
            using (var headers = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                headers.CancelAfter(connection.RequestTimeout);
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
            }
            fence();
            await XaiErrorClassifier.ThrowIfUnsuccessfulVideoAsync(response, LlmCredential.Parse(snapshot.Credential), Secrets(connection, snapshot), token).ConfigureAwait(false);
            fence();
            string text = await SdkHttpConsumption.ReadAsStringAsync(response.Content, token).ConfigureAwait(false);
            fence();
            result = parse(response.StatusCode, text);
            fence();
        }
        catch (Exception error) { errors.Add(SafeFailure(error)); }
        if (response is not null)
            try { response.Dispose(); }
            catch (Exception error) { errors.Add(LlmResourcePreparation.AsCleanupFailure(SafeFailure(error), refusedOwner: response)); }
        if (request is not null)
            try { request.Dispose(); }
            catch (Exception error) { errors.Add(LlmResourcePreparation.AsCleanupFailure(SafeFailure(error), refusedOwner: request)); }
        ThrowFailures(errors);
        fence();
        return result;
    }

    private static IEnumerable<string> Secrets(VideoConnection connection, LlmConnection snapshot)
        => snapshot.AdditionalHeaders.Values.Concat(connection.AdditionalHeaders.Values);
    private static bool Protected(string value, VideoConnection connection, LlmConnection snapshot)
        => SecretRedactor.ContainsProtectedValue(value, LlmCredential.Parse(snapshot.Credential), Secrets(connection, snapshot));
    private static string JobRoute(string id) => "videos/" + Uri.EscapeDataString(XaiFileRequestData.Identity(id));
    private static InferenceResourceScope Scope(VideoConnection connection, XaiFamilyBinding binding)
        => new(new Uri(connection.Endpoint.TrimEnd('/') + "/videos").AbsoluteUri, binding.Selection.ConnectionId);
    private static void RequireScope(VideoConnection connection, XaiFamilyBinding binding, InferenceResourceScope scope)
    {
        if (scope != Scope(connection, binding)) throw new ArgumentException("The native video receipt belongs to another endpoint or selected connection.", nameof(scope));
    }
    private static void ValidateConnection(VideoConnection connection, XaiFamilyBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ConnectVideoInference.ValidateBinding(connection, binding);
        binding!.RequireCatalogService();
    }
    private void Fence(CancellationToken token, XaiFamilyBinding? binding = null)
    {
        token.ThrowIfCancellationRequested();
        try { _lifetime.ThrowIfStopping(); }
        catch (ObjectDisposedException error) { throw new OperationCanceledException("The video transport is retiring.", error, token); }
        binding?.FenceOperation();
        token.ThrowIfCancellationRequested();
    }
    private static void ThrowFailures(List<Exception> errors)
    {
        if (errors.Count == 1) ExceptionDispatchInfo.Throw(errors[0]);
        if (errors.Count > 1) throw new AggregateException(errors);
    }
    private static Exception SafeFailure(Exception error) => error switch
    {
        VideoEvidence => error,
        { InnerException: VideoEvidence } => error,
        AggregateException aggregate => new AggregateException("The video operation and cleanup failed.", aggregate.InnerExceptions.Select(SafeFailure)),
        InferenceResourceOperationException resource => RetainResource(resource, new InferenceResourceOperationException(resource.Code,
            "The video resource operation failed.", resource.Failure, new VideoEvidence(resource))),
        InferenceResourceOperationCanceledException resource => RetainResource(resource, new InferenceResourceOperationCanceledException(resource.Failure,
            resource.CancellationToken, new VideoEvidence(resource))),
        OperationCanceledException canceled => new OperationCanceledException("The video operation was interrupted.", new VideoEvidence(canceled), canceled.CancellationToken),
        InferenceException { InnerException: LlmProviderException provider } inference => new InferenceException(inference.Code,
            inference.Message, SafeFailure(provider)),
        LlmProviderException provider => new LlmProviderException(provider.Message, LlmTransport.XaiResponses, provider.StatusCode, provider.ProviderCode,
            provider.RequestId, provider.RetryAfter, provider.SanitizedResponseBody, new VideoEvidence(provider), provider.IsContextOverflow),
        InferenceException inference => new InferenceException(inference.Code, "The video operation failed.", new VideoEvidence(inference)),
        _ => new InferenceException(error is JsonException ? InferenceFailureCode.ProtocolFailure : InferenceFailureCode.ExecutionFailed,
            "The video operation failed.", new VideoEvidence(error))
    };
    private static Exception RetainResource(Exception original, Exception safe) => LlmResourcePreparation.IsCleanupFailure(original)
        ? LlmResourcePreparation.AsCleanupFailure(safe, refusedOwner: original) : safe;
    private sealed class VideoEvidence(Exception original) : Exception("The video boundary reported a failure.")
    { internal Exception Original { get; } = original; }
    public void Dispose() => _lifetime.RetireAsync(_cleanup.ReleaseAsync).GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(_lifetime.RetireAsync(_cleanup.ReleaseAsync));
}
