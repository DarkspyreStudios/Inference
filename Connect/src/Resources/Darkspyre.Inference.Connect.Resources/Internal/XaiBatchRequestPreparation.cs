using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.Image;
using Darkspyre.Inference.Connect.Image.Internal;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.Inference.Connect.Video;
using Darkspyre.Inference.Connect.Video.Internal;

namespace Darkspyre.Inference.Connect.Resources.Internal;

/// <summary>One ordered batch staging owner. Core, Files and caller resource access remain borrowed.</summary>
internal sealed class XaiBatchRequestPreparation : IAsyncDisposable
{
    internal const long MaximumRequestBytes = 25_000_000;
    internal const long MaximumJsonlBytes = 200_000_000;
    internal const int MaximumJsonlRows = 50_000;
    private readonly List<Row> _rows = [];
    private readonly List<LlmResourcePreparation> _reads = [];
    private readonly List<XaiHostedToolPreparation> _hosted = [];
    private readonly List<Stream> _streams = [];
    private readonly XaiHostedToolPreparation.AuthorizationMemo _authorization;
    private readonly XaiResourceHttp.Observation _observation;
    private readonly Action _fence;
    private readonly CancellationToken _token;
    private IInferenceResourceAccess? _resources;
    private Task? _retirement;
    internal long InlineUtf8Bytes { get; private set; } = Measure(new JsonObject { ["batch_requests"] = new JsonArray() });
    internal long JsonlUtf8Bytes { get; private set; }
    internal IReadOnlyList<string> Secrets => _authorization.Secrets;

    internal XaiBatchRequestPreparation(IReadOnlyList<LlmBatchRequest> requests, IInferenceResourceAccess? resources,
        XaiResourceHttp.Observation observation, Action fence, CancellationToken token)
    {
        _resources = resources;
        _observation = observation;
        _fence = fence;
        _token = token;
        _authorization = new(fence, token);
        _rows.EnsureCapacity(requests.Count);
        _reads.EnsureCapacity(requests.Count);
        _hosted.EnsureCapacity(requests.Count);
        foreach (LlmBatchChatOperation chat in requests.Select(row => row.Operation).OfType<LlmBatchChatOperation>())
            _authorization.AddStatic(chat.Request);
        observation.SecretContext = Secrets;
    }

    internal static IReadOnlyList<LlmBatchRequest> Snapshot(IReadOnlyList<LlmBatchRequest> requests,
        IInferenceResourceAccess? resources)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<LlmBatchRequest>(requests.Count);
        foreach (LlmBatchRequest row in requests)
        {
            ArgumentNullException.ThrowIfNull(row);
            BatchData.Identity(row.CustomId);
            if (!ids.Add(row.CustomId)) throw new ArgumentException("Batch request CustomIds must be unique.", nameof(requests));
            rows.Add(row with
            {
                Operation = row.Operation switch
                {
                    LlmBatchChatOperation chat => new LlmBatchChatOperation(ChatSnapshot(chat.Request, resources)),
                    LlmBatchImageOperation image => new LlmBatchImageOperation(image.Request with
                    { References = Array.AsReadOnly(image.Request.References.Select(CopySource).ToArray()) },
                        image.Workflow with { TransportConfiguration = image.Workflow.TransportConfiguration }),
                    LlmBatchVideoGenerationOperation video => new LlmBatchVideoGenerationOperation((VideoGenerationRequest)CopyVideo(video.Request)),
                    LlmBatchVideoEditOperation video => new LlmBatchVideoEditOperation((VideoEditRequest)CopyVideo(video.Request)),
                    LlmBatchVideoExtensionOperation video => new LlmBatchVideoExtensionOperation((VideoExtensionRequest)CopyVideo(video.Request)),
                    _ => throw new NotSupportedException("Unknown batch operation.")
                }
            });
        }
        return rows.AsReadOnly();
    }

    internal static IReadOnlyList<LlmBatchRequest> Project(LlmConnection connection, IReadOnlyList<LlmBatchRequest> requests,
        XaiResponsesLlmClient client, IInferenceResourceAccess? resources)
    {
        var rows = new List<LlmBatchRequest>(requests.Count);
        foreach (LlmBatchRequest row in requests)
        {
            LlmBatchOperation operation = row.Operation;
            if (operation is LlmBatchChatOperation chat)
                operation = new LlmBatchChatOperation(client.ProjectBatchRequest(connection, chat.Request));
            else if (operation is LlmBatchImageOperation image)
            {
                XaiImageProtocol.Validate(image.Request, image.Workflow);
                ValidateResources(image.Request.References.Select(source => (Source: source, Audio: false)), resources);
            }
            else
            {
                VideoRequest video = Video(operation);
                XaiVideoProtocol.Validate(video);
                ValidateResources(Sources(video), resources);
            }
            rows.Add(row with { Operation = operation });
        }
        return rows.AsReadOnly();
    }

    [SuppressMessage("Design", "CA1031", Justification = "A named media read retains its actual source, input phase and byte observations before the outer boundary handles failure.")]
    internal async Task PrepareAsync(LlmConnection connection, string connectionId, IReadOnlyList<LlmBatchRequest> requests, XaiFileClient files,
        Action<Action<XaiResponsesLlmClient>> withSelectedClient, Action<Exception> recordCleanup)
    {
        var scope = new InferenceResourceScope(ProviderHttp.BuildUri(connection, "files").AbsoluteUri,
            connectionId);
        foreach (LlmBatchRequest row in requests)
        {
            Check();
            Row prepared;
            if (row.Operation is LlmBatchChatOperation chat)
            {
                LlmChatRequest request = chat.Request;
                IInferenceResourceAccess? access = request.Resources;
                LlmResourcePreparation? reading = null;
                if (access is not null)
                {
                    reading = await LlmResourcePreparation.PrepareAsync(connection, request, files, _fence, _token,
                        source => UploadObserver(scope, row.CustomId, source, false), Secrets,
                        response => _observation.ObserveResponse(response, connection)).ConfigureAwait(false);
                    _reads.Add(reading);
                    request = reading.Request;
                    Check();
                }
                JsonObject? body = null;
                withSelectedClient(client => body = client.BuildBatchPayload(connection, request, reading));
                Check();
                XaiHostedToolPreparation hosted = await XaiHostedToolPreparation.PrepareAsync(body!, request,
                    access, reading?.SelectionVersion, _fence, recordCleanup, _token, _authorization).ConfigureAwait(false);
                _hosted.Add(hosted);
                Check();
                prepared = new(row.CustomId, "responses", "/v1/responses", body!);
            }
            else
            {
                LlmResourcePreparation reading = LlmResourcePreparation.ForNamedReads();
                _reads.Add(reading);
                var media = new Dictionary<(InferenceMediaSource Source, bool Audio), JsonObject>();
                long remainingAudio = MaximumRequestBytes;
                async Task<JsonObject> ResolveAsync(InferenceMediaSource source, bool audio)
                {
                    Check();
                    if (media.TryGetValue((source, audio), out JsonObject? existing)) return (JsonObject)existing.DeepClone();
                    JsonObject value;
                    if (source is UrlMediaSource url) value = new() { ["url"] = url.Url.AbsoluteUri };
                    else if (source is FileMediaSource file) value = new() { ["file_id"] = file.File.FileId };
                    else
                    {
                        Stream content;
                        string? contentType;
                        string? fileName;
                        InferenceResourceDescriptor? descriptor = null;
                        LlmResourcePreparation.CountingReadStream? namedContent = null;
                        InferenceResourceOperationPhase phase = InferenceResourceOperationPhase.OpenRead;
                        try
                        {
                            if (source is ResourceMediaSource resource)
                            {
                                var opened = await reading.OpenNamedAsync(_resources!, resource.Resource,
                                    observed =>
                                    {
                                        if (!audio && observed.FileName is { } suppliedName) XaiFileRequestData.FileName(suppliedName);
                                    }, _fence, _token).ConfigureAwait(false);
                                descriptor = opened.Descriptor;
                                content = opened.Content;
                                namedContent = opened.Content;
                                contentType = descriptor.ContentType;
                                fileName = descriptor.FileName;
                            }
                            else if (source is BinaryMediaSource binary)
                            {
                                _streams.EnsureCapacity(checked(_streams.Count + 1));
                                content = new MemoryStream(binary.Data.ToArray(), writable: false);
                                _streams.Add(content);
                                contentType = binary.ContentType;
                                fileName = binary.FileName;
                            }
                            else throw new NotSupportedException("Unknown batch media source.");
                            phase = InferenceResourceOperationPhase.Materialize;
                            Check();
                            if (audio)
                            {
                                string urlValue = await XaiMediaData.AudioDataUrlAsync(content, contentType, remainingAudio,
                                    _fence, _token).ConfigureAwait(false);
                                remainingAudio = checked(remainingAudio - Encoding.UTF8.GetByteCount(urlValue));
                                value = new() { ["url"] = urlValue };
                            }
                            else
                            {
                                var observer = UploadObserver(scope, row.CustomId, (source as ResourceMediaSource)?.Resource, false);
                                LlmProviderFile uploaded = await files.UploadAsync(connection, content, fileName ?? "batch-input.bin",
                                null, _fence, _token, observer.OnDispatched, observer.OnObserved, Secrets,
                                response => _observation.ObserveResponse(response, connection)).ConfigureAwait(false);
                                value = new() { ["file_id"] = uploaded.Id };
                            }
                            if (descriptor is not null)
                                reading.VerifyRead(_resources!, descriptor, (LlmResourcePreparation.CountingReadStream)content, _fence, _token);
                        }
                        catch (Exception error) when (source is ResourceMediaSource)
                        {
                            throw LlmResourcePreparation.AsInputFailure(error, phase,
                                ((ResourceMediaSource)source).Resource, namedContent?.BytesRead, _token);
                        }
                    }
                    Check();
                    media.Add((source, audio), value);
                    return (JsonObject)value.DeepClone();
                }
                if (row.Operation is LlmBatchImageOperation image)
                {
                    var references = new List<JsonObject>(image.Request.References.Count);
                    foreach (InferenceMediaSource source in image.Request.References)
                        references.Add(await ResolveAsync(source, false).ConfigureAwait(false));
                    var native = XaiImageProtocol.Build(image.Request, image.Workflow, references);
                    prepared = new(row.CustomId, image.Request.References.Count == 0 ? "image_generation" : "image_edit",
                        "/v1/" + native.Route, native.Body);
                }
                else
                {
                    VideoRequest video = Video(row.Operation);
                    var inputs = new List<(InferenceMediaSource Source, JsonObject Value)>();
                    foreach (var source in Sources(video))
                        inputs.Add((source.Source, await ResolveAsync(source.Source, source.Audio).ConfigureAwait(false)));
                    int nextInput = 0;
                    // The canonical codec resolves ordered occurrences, including one source used as both image and audio.
                    var native = XaiVideoProtocol.Build(video, source =>
                    {
                        if (nextInput >= inputs.Count || inputs[nextInput].Source != source)
                            throw new InvalidOperationException("The ordered batch media projection changed.");
                        return (JsonObject)inputs[nextInput++].Value.DeepClone();
                    }, _fence);
                    if (nextInput != inputs.Count) throw new InvalidOperationException("A batch media input was not projected.");
                    prepared = new(row.CustomId, video is VideoExtensionRequest ? "video_extension" : "video_generation",
                        "/v1/" + native.Route, native.Body);
                }
            }
            Check();
            long individual = Measure(InlineRow(prepared));
            if (individual > MaximumRequestBytes) throw new ArgumentException("A native batch request exceeds 25,000,000 UTF-8 bytes.");
            InlineUtf8Bytes = checked(InlineUtf8Bytes + individual + (_rows.Count == 0 ? 0 : 1));
            JsonlUtf8Bytes = checked(JsonlUtf8Bytes + Measure(JsonlRow(prepared)) + 1);
            _rows.Add(prepared);
        }
        Check();
    }

    internal (Action OnDispatched, Action<LlmProviderFile> OnObserved) UploadObserver(InferenceResourceScope scope,
        string? customId, InferenceResourceReference? source, bool isJsonl)
    {
        _observation.ReserveUploadCapacity();
        int attempt = -1;
        return (() => attempt = _observation.BeginUpload(scope, customId, source, isJsonl),
            file => _observation.ObserveUpload(attempt, file));
    }

    internal JsonObject BuildInlineBody()
    {
        Check();
        return new() { ["batch_requests"] = new JsonArray(_rows.Select(row => (JsonNode)InlineRow(row)).ToArray()) };
    }

    internal Stream CreateJsonl()
    {
        Check();
        if (_rows.Count > MaximumJsonlRows || JsonlUtf8Bytes > MaximumJsonlBytes)
            throw new ArgumentException("A batch JSONL file exceeds 50,000 rows or 200,000,000 UTF-8 bytes.");
        _streams.EnsureCapacity(checked(_streams.Count + 1));
        var stream = new MemoryStream(checked((int)JsonlUtf8Bytes));
        _streams.Add(stream);
        foreach (Row row in _rows)
        {
            Check();
            using (var writer = new Utf8JsonWriter(stream)) { JsonlRow(row).WriteTo(writer); writer.Flush(); }
            stream.WriteByte((byte)'\n');
        }
        Check();
        if (stream.Length != JsonlUtf8Bytes) throw new InvalidOperationException("The staged JSONL byte count changed.");
        stream.Position = 0;
        return stream;
    }

    private static LlmChatRequest ChatSnapshot(LlmChatRequest request, IInferenceResourceAccess? resources)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (resources is not null && request.Resources is not null && !ReferenceEquals(resources, request.Resources))
            throw new ArgumentException("The chat row and batch must borrow the same resource access.", nameof(resources));
        return ClientTools.Snapshot(request) with
        {
            Resources = resources ?? request.Resources,
            HostedTools = HostedTools.Snapshot(request.HostedTools)
        };
    }

    private static InferenceMediaSource CopySource(InferenceMediaSource source) => source switch
    {
        BinaryMediaSource binary => binary with { Data = binary.Data.ToArray() },
        UrlMediaSource url => url with { },
        FileMediaSource file => file with { File = file.File with { } },
        ResourceMediaSource resource => resource with { Resource = LlmAttachments.CopyResource(resource.Resource) },
        _ => throw new NotSupportedException("Unknown batch media source.")
    };

    private static VideoRequest CopyVideo(VideoRequest request)
    {
        VideoRequest copied = MapVideo(request, (source, _) => CopySource(source));
        return copied with
        {
            ProviderOptions = request.ProviderOptions switch
            {
                null => null,
                XaiVideoOptions options => options with
                {
                    Storage = options.Storage is { } storage ? storage with { } : null,
                    Output = options.Output is { } output ? output with { } : null
                },
                _ => throw new NotSupportedException("Unknown batch video controls.")
            }
        };
    }

    private static VideoRequest MapVideo(VideoRequest request, Func<InferenceMediaSource, bool, InferenceMediaSource> copy) => request switch
    {
        VideoGenerationRequest generation => generation with
        {
            FirstFrame = generation.FirstFrame is { } first ? copy(first, false) : null,
            LastFrame = generation.LastFrame is { } last ? copy(last, false) : null,
            Keyframes = generation.Keyframes?.Select(frame => frame with { Image = copy(frame.Image, false) }).ToArray(),
            ReferenceImages = generation.ReferenceImages?.Select(source => copy(source, false)).ToArray(),
            ReferenceAudio = generation.ReferenceAudio?.Select(audio => audio switch
            {
                VideoVoiceReference voice => (VideoReferenceAudio)(voice with { }),
                VideoAudioReference supplied => supplied with { Source = copy(supplied.Source, true) },
                _ => throw new NotSupportedException("Unknown batch audio reference.")
            }).ToArray()
        },
        VideoEditRequest edit => edit with { SourceVideo = copy(edit.SourceVideo, false) },
        VideoExtensionRequest extension => extension with { SourceVideo = copy(extension.SourceVideo, false) },
        _ => throw new NotSupportedException("Unknown batch video request.")
    };

    private static VideoRequest Video(LlmBatchOperation operation) => operation switch
    {
        LlmBatchVideoGenerationOperation video => video.Request,
        LlmBatchVideoEditOperation video => video.Request,
        LlmBatchVideoExtensionOperation video => video.Request,
        _ => throw new NotSupportedException("Unknown batch video operation.")
    };

    private static IEnumerable<(InferenceMediaSource Source, bool Audio)> Sources(VideoRequest request)
    {
        if (request is VideoGenerationRequest generation)
        {
            if (generation.FirstFrame is { } first) yield return (first, false);
            if (generation.LastFrame is { } last) yield return (last, false);
            foreach (VideoKeyframe frame in generation.Keyframes ?? []) yield return (frame.Image, false);
            foreach (InferenceMediaSource source in generation.ReferenceImages ?? []) yield return (source, false);
            foreach (VideoAudioReference audio in (generation.ReferenceAudio ?? []).OfType<VideoAudioReference>()) yield return (audio.Source, true);
        }
        else if (request is VideoEditRequest edit) yield return (edit.SourceVideo, false);
        else if (request is VideoExtensionRequest extension) yield return (extension.SourceVideo, false);
    }

    private static void ValidateResources(IEnumerable<(InferenceMediaSource Source, bool Audio)> sources, IInferenceResourceAccess? resources)
    {
        foreach (var source in sources)
        {
            if (source.Source is ResourceMediaSource resource)
                LlmResourcePreparation.ValidateNamedReference(resource.Resource, resources);
            else if (source.Source is FileMediaSource file) XaiFileRequestData.Identity(file.File.FileId);
            else if (!source.Audio && source.Source is BinaryMediaSource { FileName: { } fileName })
                XaiFileRequestData.FileName(fileName);
        }
    }

    private static JsonObject InlineRow(Row row) => new()
    { ["batch_request_id"] = row.CustomId, ["batch_request"] = new JsonObject { [row.Wrapper] = row.Body.DeepClone() } };
    private static JsonObject JsonlRow(Row row) => new()
    { ["custom_id"] = row.CustomId, ["method"] = "POST", ["url"] = row.Route, ["body"] = row.Body.DeepClone() };

    private static long Measure(JsonNode value)
    {
        using var stream = new CountingWriteStream();
        using var writer = new Utf8JsonWriter(stream);
        value.WriteTo(writer);
        writer.Flush();
        return stream.BytesWritten;
    }

    private void Check() => LlmResourcePreparation.Check(_fence, _token);

    public ValueTask DisposeAsync() => new(_retirement ??= RetireAsync());

    [SuppressMessage("Design", "CA1031", Justification = "Each independent staged owner releases after success or failure; refused owners remain in their cleanup evidence.")]
    private async Task RetireAsync()
    {
        var failures = new List<Exception>();
        foreach (LlmResourcePreparation read in _reads)
            try { await read.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(XaiResourceHttp.SafeFailure(error, _observation, forceSafe: true)); }
        foreach (Stream stream in _streams)
            try { await stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                failures.Add(LlmResourcePreparation.AsCleanupFailure(
                XaiResourceHttp.SafeFailure(error, _observation, forceSafe: true), refusedOwner: stream));
            }
        foreach (XaiHostedToolPreparation frame in _hosted)
            try { frame.Dispose(); }
            catch (Exception error)
            {
                failures.Add(LlmResourcePreparation.AsCleanupFailure(
                XaiResourceHttp.SafeFailure(error, _observation, forceSafe: true), refusedOwner: frame));
            }
        _resources = null;
        _rows.Clear();
        _reads.Clear();
        _streams.Clear();
        _hosted.Clear();
        _authorization.Dispose();
        _observation.SecretContext = [];
        if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Count > 1) throw XaiResourceHttp.CombineFailures(failures);
    }

    private sealed record Row(string CustomId, string Wrapper, string Route, JsonObject Body);

    private sealed class CountingWriteStream : Stream
    {
        internal long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => BytesWritten = checked(BytesWritten + count);
        public override void Write(ReadOnlySpan<byte> buffer) => BytesWritten = checked(BytesWritten + buffer.Length);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
