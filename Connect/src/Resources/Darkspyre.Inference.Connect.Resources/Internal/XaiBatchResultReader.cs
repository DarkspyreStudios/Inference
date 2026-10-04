using System.Net;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.Image.Internal;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.Inference.Connect.Video.Internal;

namespace Darkspyre.Inference.Connect.Resources.Internal;

/// <summary>One bounded provider page and its ordered actual resource effects.</summary>
internal sealed class XaiBatchResultReader(HttpClient http, LlmConnection connection, string connectionId,
    LlmMediaMaterializationOptions limits, IInferenceResourceAccess? resources,
    XaiFileClient files, LlmProviderService core, XaiResourceHttp.Observation observation, Action fence)
{
    private InferenceResourceSelectionVersion? _scope;
    private long _inlineImageBytes;

    internal async Task<LlmBatchResultPage> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is { } length && length > limits.MaximumInlineResultBytes)
            throw TooLarge();
        using var page = new BoundedPageBuffer(limits.MaximumInlineResultBytes);
        await SdkHttpConsumption.CopyToAsync(response.Content, page, token).ConfigureAwait(false);
        fence();
        using JsonDocument document = JsonDocument.Parse(page.GetBuffer().AsMemory(0, checked((int)page.Length)));
        JsonElement root = document.RootElement;
        JsonElement rows = Property(root, "results");
        if (rows.ValueKind != JsonValueKind.Array) throw XaiMediaData.Malformed();
        string? pagination = OptionalString(root, "pagination_token");
        observation.ReserveResultCapacity(rows.GetArrayLength());
        var results = new List<LlmBatchResult>(rows.GetArrayLength());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var chat = new XaiDeferredChatCodec(http);
        foreach (JsonElement row in rows.EnumerateArray())
        {
            fence();
            string id = RequiredString(row, "batch_request_id");
            if (!ids.Add(id)) throw XaiMediaData.Malformed();
            JsonElement outcome = Property(row, "batch_result");
            JsonElement error = Property(outcome, "error");
            JsonElement nativeResponse = Property(outcome, "response");
            LlmBatchResult result;
            if (error.ValueKind == JsonValueKind.String)
            {
                if (nativeResponse.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) throw XaiMediaData.Malformed();
                string message = SecretRedactor.Sanitize(error.GetString()!, LlmCredential.Parse(connection.Credential), connection.AdditionalHeaders.Values);
                result = new LlmBatchFailure(id, new(InferenceFailureCode.ExecutionFailed, null, message));
                observation.ObserveResult(result);
            }
            else
            {
                if (error.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) throw XaiMediaData.Malformed();
                if (nativeResponse.ValueKind == JsonValueKind.String && nativeResponse.GetString() == "unknown")
                {
                    result = new LlmBatchFailure(id, new(InferenceFailureCode.ProtocolFailure, null, "xAI returned an unknown batch response."));
                    observation.ObserveResult(result);
                }
                else
                {
                    JsonElement chatBody = Property(nativeResponse, "chat_get_completion");
                    JsonElement imageBody = Property(nativeResponse, "image_generation");
                    JsonElement videoBody = Property(nativeResponse, "video_generation");
                    int kinds = Present(chatBody) + Present(imageBody) + Present(videoBody);
                    if (kinds != 1) throw XaiMediaData.Malformed();
                    result = Present(chatBody) == 1
                        ? await ReadChatAsync(id, chatBody, chat, response, token).ConfigureAwait(false)
                        : Present(imageBody) == 1
                            ? await ReadImageAsync(id, imageBody, token).ConfigureAwait(false)
                            : await ReadVideoAsync(id, videoBody, token).ConfigureAwait(false);
                }
            }
            results.Add(result);
            fence();
        }
        fence();
        return new(results, pagination);
    }

    private async Task<LlmBatchChatResult> ReadChatAsync(string id, JsonElement body, XaiDeferredChatCodec codec,
        HttpResponseMessage response, CancellationToken token)
    {
        var decoded = codec.DecodeCompleteResponse(body.GetRawText(), response, connection);
        var result = new LlmBatchChatResult(id, decoded.Result);
        int index = observation.ObserveResult(result);
        foreach (var file in decoded.OutputFiles) XaiFileRequestData.Identity(file.Id);
        if (decoded.OutputFiles.Count == 0) return result;
        InferenceResourceSelectionVersion scope = await ScopeAsync(token).ConfigureAwait(false);
        var commits = new List<InferenceResourceCommitResult>(decoded.OutputFiles.Count);
        foreach (var file in decoded.OutputFiles)
        {
            fence();
            var writer = new XaiGeneratedFileWriter(resources!, scope, file.Name, fence, observation, receipt =>
            {
                commits.Add(receipt);
                result = new(id, decoded.Result with { GeneratedResources = commits.ToArray() });
                observation.UpdateResult(index, result);
            });
            await writer.MaterializeAsync(files, connection, file.Id, token).ConfigureAwait(false);
            fence();
        }
        return result;
    }

    private async Task<LlmBatchImageResult> ReadImageAsync(string id, JsonElement body, CancellationToken token)
    {
        XaiImageResult decoded = XaiImageProtocol.Parse(body.GetRawText(), Protected, fence);
        long measured = _inlineImageBytes;
        foreach (XaiImageSource image in decoded.Images)
            if (image.Base64 is { } encoded)
            {
                int bytes = XaiImageProtocol.MeasureBase64(encoded);
                if (bytes > limits.MaximumInlineImageBytes || bytes > limits.MaximumInlineResultBytes - measured) throw TooLarge();
                measured = checked(measured + bytes);
            }
        var provenance = Provenance(decoded.ModelId);
        GeneratedImage[] generated = decoded.Images.Select(image =>
        {
            return new GeneratedImage
            {
                ContentType = image.ContentType,
                Data = ReadOnlyMemory<byte>.Empty,
                File = image.FileId is { } fileId ? new(fileId) : null,
                Width = image.Width,
                Height = image.Height
            };
        }).ToArray();
        var result = new LlmBatchImageResult(id, new() { Provenance = provenance, Images = generated.ToArray() }) { Usage = XaiUsage.Media(decoded.Usage) };
        int index = observation.ObserveResult(result);
        InferenceResourceSelectionVersion scope = await ScopeAsync(token).ConfigureAwait(false);
        for (int outputIndex = 0; outputIndex < decoded.Images.Count; outputIndex++)
        {
            XaiImageSource image = decoded.Images[outputIndex];
            fence();
            (ReadOnlyMemory<byte> bytes, string? contentType) = await ImageDataAsync(image, token).ConfigureAwait(false);
            var writer = new XaiGeneratedFileWriter(resources!, scope, image.FileName, fence, observation, receipt =>
            {
                generated[outputIndex] = generated[outputIndex] with
                {
                    ContentType = contentType,
                    Data = bytes,
                    Resource = receipt.Resource,
                    Selection = receipt.Selection
                };
                result = result with { Result = new() { Provenance = provenance, Images = generated.ToArray() } };
                observation.UpdateResult(index, result);
            });
            await writer.MaterializeAsync(bytes, contentType, token).ConfigureAwait(false);
            fence();
        }
        return result;
    }

    private async Task<(ReadOnlyMemory<byte> Bytes, string? ContentType)> ImageDataAsync(XaiImageSource image, CancellationToken token)
    {
        long maximum = Math.Min(limits.MaximumInlineImageBytes, limits.MaximumInlineResultBytes - _inlineImageBytes);
        if (maximum <= 0) throw TooLarge();
        if (image.Base64 is { } encoded)
        {
            int length = XaiImageProtocol.MeasureBase64(encoded);
            if (length > maximum) throw TooLarge();
            byte[] inline = XaiImageProtocol.DecodeBase64(encoded, length);
            _inlineImageBytes = checked(_inlineImageBytes + length);
            return (inline, image.ContentType);
        }
        using var buffer = new BoundedPageBuffer(maximum);
        string? receivedType = null;
        Task<Stream> Open(string? mime, long? length, CancellationToken bodyToken)
        {
            bodyToken.ThrowIfCancellationRequested();
            fence();
            if (length > maximum) throw TooLarge();
            if (XaiMediaData.ContentTypesConflict(mime, image.ContentType)) throw XaiMediaData.Malformed();
            receivedType = mime;
            return Task.FromResult<Stream>(buffer);
        }
        if (image.FileId is { } fileId)
        {
            observation.BeginStage("GET generated image file");
            await files.DownloadToAsync(connection, fileId, Open, fence,
                () => observation.RequestDispatched = true,
                response => observation.ObserveResponse(response, connection), token).ConfigureAwait(false);
        }
        else if (image.Url is { } url)
        {
            observation.BeginStage("GET unsigned image");
            await core.RunXaiMediaSnapshotAsync(connection, fence, (media, ownerFence, current) =>
                media.DownloadToAsync(url, ProviderHttp.GenerationTimeout, (mime, length, bodyToken) =>
            {
                ownerFence();
                return Open(mime, length, bodyToken);
            }, ownerFence, () => observation.RequestDispatched = true,
                response => observation.ObserveResponse(response, connection), current), token).ConfigureAwait(false);
        }
        else throw XaiMediaData.Malformed();
        fence();
        if (buffer.Length == 0) throw XaiMediaData.Malformed();
        byte[] bytes = buffer.ToArray();
        _inlineImageBytes = checked(_inlineImageBytes + bytes.LongLength);
        return (bytes, receivedType ?? image.ContentType);
    }

    private async Task<LlmBatchResult> ReadVideoAsync(string id, JsonElement body, CancellationToken token)
    {
        XaiVideoPoll decoded = XaiVideoProtocol.ParsePoll(HttpStatusCode.OK, body.GetRawText(), Protected, fence);
        if (decoded.State is VideoJobState.Failed or VideoJobState.Expired)
        {
            JsonElement error = Property(body, "error");
            string? message = Present(error) == 1 ? XaiMediaData.OptionalString(error, "message", static _ => false) : null;
            var failure = new LlmBatchFailure(id, new(InferenceFailureCode.ExecutionFailed, decoded.ErrorCode,
                message is null ? $"The provider video state is {decoded.State}."
                    : SecretRedactor.Sanitize(message, LlmCredential.Parse(connection.Credential), connection.AdditionalHeaders.Values)));
            observation.ObserveResult(failure);
            return failure;
        }
        if (decoded.State != VideoJobState.Completed || decoded.Output is not { } output) throw XaiMediaData.Malformed();
        if (!Property(output, "respect_moderation").GetBoolean())
        {
            var failure = new LlmBatchFailure(id, new(InferenceFailureCode.ExecutionFailed, null, "The video did not pass provider moderation."));
            observation.ObserveResult(failure);
            return failure;
        }
        JsonElement file = Property(output, "file_output");
        string? fileId = Present(file) == 1 ? RequiredString(file, "file_id") : null;
        string? name = Present(file) == 1 ? RequiredString(file, "filename") : null;
        if (fileId is not null) XaiFileRequestData.Identity(fileId);
        string? url = OptionalString(output, "url");
        if (fileId is null && string.IsNullOrWhiteSpace(url)) throw XaiMediaData.Malformed();
        TimeSpan duration = TimeSpan.FromSeconds(Property(output, "duration").GetInt32());
        InferenceResourceSelectionVersion scope = await ScopeAsync(token).ConfigureAwait(false);
        var nativeScope = new InferenceResourceScope(ProviderHttp.BuildUri(connection, "videos").AbsoluteUri, connectionId);
        LlmBatchVideoResult? result = null;
        var writer = new XaiGeneratedFileWriter(resources!, scope, name, fence, observation, receipt =>
        {
            result = new(id,
                new(nativeScope, output, receipt.Resource.ContentType, duration, FileName: name,
                    File: fileId is null ? null : new(fileId), Resource: receipt.Resource),
                new(receipt.Resource, duration, Provenance(decoded.ModelId)) { Selection = receipt.Selection })
            { Usage = XaiUsage.Media(decoded.Usage) };
            observation.ObserveResult(result);
        });
        if (fileId is not null)
            await writer.MaterializeAsync(files, connection, fileId, token).ConfigureAwait(false);
        else
            await core.RunXaiMediaSnapshotAsync(connection, fence,
                (media, ownerFence, current) => writer.MaterializeAsync(media, connection, url!, current), token).ConfigureAwait(false);
        // A terminal batch output has no observed native request_id. CustomId is never used as a job ID.
        fence();
        return result!;
    }

    private async Task<InferenceResourceSelectionVersion> ScopeAsync(CancellationToken token)
    {
        if (resources is null) throw new ArgumentException("Generated batch artifacts require explicit resource access.");
        _scope ??= await SpeechResourceWriter.ReadScopeAsync(resources, fence, token).ConfigureAwait(false);
        LlmResourcePreparation.ValidateScope(resources, _scope, fence, token);
        return _scope;
    }

    private static InferenceProvenance Provenance(string? model) => new()
    { ImplementationId = ProviderDefinitions.GrokSubscription.Id, ModelId = model, Runtime = "xAI", Backend = "connect" };

    private bool Protected(string value) => value != SecretRedactor.Sanitize(value,
        LlmCredential.Parse(connection.Credential), connection.AdditionalHeaders.Values);
    private string RequiredString(JsonElement root, string name) => XaiMediaData.RequiredString(root, name, Protected);
    private string? OptionalString(JsonElement root, string name) => XaiMediaData.OptionalString(root, name, Protected);
    private static int Present(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? 0 : 1;
    private static JsonElement Property(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) throw XaiMediaData.Malformed();
        JsonElement result = default;
        bool found = false;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name != name) continue;
            if (found) throw XaiMediaData.Malformed();
            result = property.Value; found = true;
        }
        return result;
    }

    private static ArgumentException TooLarge() => new("The batch output exceeds the admitted retention limit.");
    private sealed class BoundedPageBuffer(long maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { Check(count); return base.WriteAsync(buffer, offset, count, cancellationToken); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Check(buffer.Length); return base.WriteAsync(buffer, cancellationToken); }
        private void Check(int count) { if (count > maximum - Length) throw TooLarge(); }
    }
}
