using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

internal static class VertexCacheFiles
{
    internal const long MaximumObjectBytes = 5L * 1024 * 1024 * 1024 * 1024;
    internal sealed record File(string Bucket, string Name, string Generation, long Bytes, string MimeType)
    {
        internal string Uri => "gs://" + Bucket + "/" + Name;
        public override string ToString() => "VertexCacheFile { <derived-object> }";
    }

    internal static void ValidateDescriptor(InferenceResourceDescriptor descriptor, bool toolResult)
    {
        ValidateMedia(descriptor.ContentType, descriptor.Bytes, toolResult);
    }

    internal static void ValidateMedia(string? mime, long? bytes, bool toolResult)
    {
        bool image = mime is not null && (toolResult
            ? RichContentRoutes.GeminiFunctionResponse.ImageMediaTypes.Contains(mime)
            : GeminiCapabilities.AcceptsImageMediaType(mime, vertex: true));
        if (!image && mime is not ("text/plain" or "application/pdf"))
            throw new NotSupportedException("Vertex cache staging requires an observed supported MIME type.");
        if (bytes is { } known) ArgumentOutOfRangeException.ThrowIfGreaterThan(known, MaximumObjectBytes);
    }

    [SuppressMessage("Design", "CA1031", Justification = "The Google boundary sanitizes failures, attempts owned cleanup and retains a refused response owner.")]
    internal static async Task<File> UploadAsync(HttpClient http, VertexAccess access, LlmVertexCacheStaging staging,
        string mimeType, long? length, InferenceResourceReference? reference, Stream source,
        Action<CancellationToken> fence, CancellationToken token)
    {
        ValidateMedia(mimeType, length, toolResult: false); token.ThrowIfCancellationRequested(); fence(token);
        using var consumption = SdkHttpConsumption.Enter(http, token); token = consumption.Token;
        void Check() { token.ThrowIfCancellationRequested(); fence(token); consumption.Fence(); }
        Check();
        string name = staging.ObjectPrefix + Guid.NewGuid().ToString("N");
        var content = new GoogleCacheMediaContent(source, mimeType, length, MaximumObjectBytes, fence);
        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://storage.googleapis.com/upload/storage/v1/b/" + Uri.EscapeDataString(staging.Bucket)
            + "/o?uploadType=media&name=" + Uri.EscapeDataString(name) + "&ifGenerationMatch=0")
        { Content = content };
        if (length is null) request.Headers.TransferEncodingChunked = true;
        HttpResponseMessage? response = null;
        HttpStatusCode? status = null; string? requestId = null; TimeSpan? retry = null;
        File? observed = null; Exception? failure = null; bool canceled = false;
        try
        {
            access.Authorize(request); Check();
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            status = response.StatusCode; requestId = ProviderHttp.ReadRequestId(response); retry = response.Headers.RetryAfter?.Delta;
            Check();
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(null, null, status);
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, token).ConfigureAwait(false);
            Check();
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            string bucket = root.GetProperty("bucket").GetString() ?? throw new JsonException();
            string actualName = root.GetProperty("name").GetString() ?? throw new JsonException();
            string generation = root.GetProperty("generation").GetString() ?? throw new JsonException();
            string size = root.GetProperty("size").GetString() ?? throw new JsonException();
            if (bucket != staging.Bucket || actualName != name || generation.Length == 0
                || generation.Any(character => !char.IsAsciiDigit(character)) || generation.All(character => character == '0')
                || !long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out long bytes)
                || bytes != content.BytesWritten || length is { } expected && bytes != expected)
                throw new JsonException();
            if (root.TryGetProperty("contentType", out JsonElement mime) && mime.ValueKind != JsonValueKind.Null
                && (mime.ValueKind != JsonValueKind.String || mime.GetString() != mimeType)) throw new JsonException();
            observed = new(bucket, actualName, generation, bytes, mimeType);
            Check();
        }
        catch (Exception error) { canceled = error is OperationCanceledException; failure = Safe(error, "Vertex cache staging failed."); }
        if (response is not null)
        {
            try { response.Dispose(); }
            catch (Exception cleanup)
            {
                Exception owned = LlmResourcePreparation.AsCleanupFailure(Safe(cleanup, "Vertex cache staging response cleanup failed."), reference, response);
                failure = failure is null ? owned : new AggregateException(failure, owned);
                canceled |= cleanup is OperationCanceledException;
            }
        }
        try { request.Dispose(); }
        catch (Exception cleanup)
        {
            Exception owned = LlmResourcePreparation.AsCleanupFailure(Safe(cleanup, "Vertex cache staging request cleanup failed."), reference, request);
            failure = failure is null ? owned : new AggregateException(failure, owned);
            canceled |= cleanup is OperationCanceledException;
        }
        finally { content.DetachBorrowed(); }
        try { Check(); }
        catch (Exception error) { canceled |= error is OperationCanceledException; failure ??= Safe(error, "Vertex cache staging was interrupted."); }
        if (failure is not null)
        {
            var facts = new InferenceResourceOperationFailure(InferenceResourceOperationPhase.Materialize,
                reference, InferenceResourceCommitOutcome.NotAttempted, content.BytesWritten);
            if (canceled) throw new InferenceResourceOperationCanceledException(facts, token, failure);
            throw new InferenceResourceOperationException(Common.InferenceFailureCode.ExecutionFailed,
                "Vertex cache prefix staging failed; a derived object may remain.", facts, failure);
        }
        return observed!;

        Exception Safe(Exception error, string category) => error is OperationCanceledException
            ? new OperationCanceledException("Vertex cache staging was interrupted.", status is null ? null
                : new LlmProviderException("Vertex cache staging response observed before interruption.", LlmTransport.GoogleVertexAi,
                    status, requestId: requestId, retryAfter: retry), token)
            : new LlmProviderException(category, LlmTransport.GoogleVertexAi, status, requestId: requestId, retryAfter: retry,
                isContextOverflow: error is LlmProviderException { IsContextOverflow: true });
    }
}
