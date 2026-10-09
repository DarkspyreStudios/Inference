using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Derived Google staging copies, never the workspace's original content.</summary>
internal static class GeminiCacheFiles
{
    internal const long MaximumBytes = 2_147_483_648;
    internal sealed record File(string Name, string Uri, string MimeType, string State, long? Bytes = null);

    internal static void ValidateDescriptor(InferenceResourceDescriptor descriptor, bool toolResult)
    {
        if (descriptor.FileName is { } name && name.EnumerateRunes().Count() > 512)
            throw new ArgumentException("The staging display name exceeds the documented Unicode-character bound.", nameof(descriptor));
        bool image = descriptor.ContentType is { } mime && (toolResult
            ? RichContentRoutes.GeminiFunctionResponse.ImageMediaTypes.Contains(mime)
            : GeminiCapabilities.AcceptsImageMediaType(mime, vertex: false));
        if (!image && descriptor.ContentType is not ("text/plain" or "application/pdf"))
            throw new NotSupportedException("The cache prefix destination requires an observed supported MIME type.");
        if (descriptor.Bytes is { } size) ArgumentOutOfRangeException.ThrowIfGreaterThan(size, MaximumBytes);
        if (descriptor.ContentType == "application/pdf" && descriptor.Bytes is { } pdfBytes)
            ArgumentOutOfRangeException.ThrowIfGreaterThan(pdfBytes, 50L * 1024 * 1024);
    }

    internal static async Task<File> UploadAsync(HttpClient http, LlmConnection connection,
        InferenceResourceDescriptor descriptor, Stream source, Action<CancellationToken> fence, CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        ValidateDescriptor(descriptor, toolResult: false);
        string key = GeminiApiLlmClient.Key(connection);
        var metadata = new JsonObject();
        if (descriptor.FileName is { Length: > 0 } name) metadata["displayName"] = name;
        var body = new MultipartContent("related");
        body.Add(RequestJson.Content(new JsonObject { ["file"] = metadata }));
        var media = new GoogleCacheMediaContent(source, descriptor.ContentType!, descriptor.Bytes,
            descriptor.ContentType == "application/pdf" ? 50L * 1024 * 1024 : MaximumBytes, fence);
        body.Add(media);
        var request = new HttpRequestMessage(HttpMethod.Post,
            GeminiApiLlmClient.DefaultEndpoint + "/upload/v1beta/files?uploadType=multipart")
        { Content = body };
        File observed;
        try { observed = await ExchangeAsync(http, request, key, descriptor, fence, true, token).ConfigureAwait(false); }
        finally { media.DetachBorrowed(); }
        while (observed.State == "PROCESSING")
        {
            token.ThrowIfCancellationRequested(); fence(token);
            await (wait is null ? Task.Delay(TimeSpan.FromSeconds(5), token) : wait(TimeSpan.FromSeconds(5), token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); fence(token);
            var get = new HttpRequestMessage(HttpMethod.Get, observed.Uri);
            File ready = await ExchangeAsync(http, get, key, descriptor, fence, false, token).ConfigureAwait(false);
            if (ready.Name != observed.Name || ready.Uri != observed.Uri || ready.MimeType != observed.MimeType
                || ready.Bytes is { } readyBytes && observed.Bytes is { } previousBytes && readyBytes != previousBytes)
                throw new LlmProviderException("Google staging readiness returned contradictory file identity.", LlmTransport.GoogleGeminiApi);
            observed = ready;
        }
        return observed;
    }

    [SuppressMessage("Design", "CA1031", Justification = "Untrusted transport and response cleanup diagnostics are replaced by fixed safe facts.")]
    private static async Task<File> ExchangeAsync(HttpClient http, HttpRequestMessage request, string key,
        InferenceResourceDescriptor descriptor, Action<CancellationToken> fence, bool envelope, CancellationToken token)
    {
        SdkHttpConsumption.Operation? consumption = null;
        void Fence() { token.ThrowIfCancellationRequested(); fence(token); consumption?.Fence(); }
        HttpResponseMessage? response = null;
        File? result = null;
        Exception? failure = null;
        HttpStatusCode? status = null;
        string? requestId = null;
        TimeSpan? retryAfter = null;
        try
        {
            consumption = SdkHttpConsumption.Enter(http, token); token = consumption.Token;
            request.Headers.Add("x-goog-api-key", key);
            Fence();
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            status = response.StatusCode; requestId = ProviderHttp.ReadRequestId(response);
            retryAfter = response.Headers.RetryAfter?.Delta;
            Fence();
            if (!response.IsSuccessStatusCode)
                throw new LlmProviderException("Google rejected cache prefix file staging.", LlmTransport.GoogleGeminiApi, status, requestId: requestId, retryAfter: retryAfter);
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, token).ConfigureAwait(false);
            Fence();
            using JsonDocument document = JsonDocument.Parse(body);
            result = Parse(envelope ? document.RootElement.GetProperty("file") : document.RootElement, descriptor);
            Fence();
        }
        catch (Exception error)
        {
            failure = error is OperationCanceledException ? new OperationCanceledException(token)
                : new LlmProviderException("Google cache prefix file staging failed.", LlmTransport.GoogleGeminiApi, status, requestId: requestId, retryAfter: retryAfter);
        }
        using var ownedConsumption = consumption;
        if (response is not null)
        {
            try { response.Dispose(); }
            catch (Exception)
            {
                Exception cleanup = LlmResourcePreparation.AsCleanupFailure(
                    new LlmProviderException("Google cache prefix file response cleanup failed.", LlmTransport.GoogleGeminiApi,
                        status, requestId: requestId, retryAfter: retryAfter), descriptor.Reference, response);
                failure = failure is null ? cleanup : new AggregateException(failure, cleanup);
            }
        }
        try { request.Dispose(); }
        catch (Exception)
        {
            Exception cleanup = LlmResourcePreparation.AsCleanupFailure(
                new LlmProviderException("Google cache prefix file request cleanup failed.", LlmTransport.GoogleGeminiApi,
                    status, requestId: requestId, retryAfter: retryAfter), descriptor.Reference, request);
            failure = failure is null ? cleanup : new AggregateException(failure, cleanup);
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Fence();
        return result!;
    }

    private static File Parse(JsonElement file, InferenceResourceDescriptor descriptor)
    {
        string name = file.GetProperty("name").GetString() ?? throw new JsonException();
        const string prefix = "files/";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) throw new JsonException();
        string id = name[prefix.Length..];
        if (id.Length is < 1 or > 40 || id[0] == '-' || id[^1] == '-' || id.Any(character => character != '-' && !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character)))
            throw new JsonException();
        string uri = file.GetProperty("uri").GetString() ?? throw new JsonException();
        if (uri != GeminiApiLlmClient.DefaultEndpoint + "/v1beta/files/" + id) throw new JsonException();
        string state = file.GetProperty("state").GetString() ?? throw new JsonException();
        if (state is not ("ACTIVE" or "PROCESSING")) throw new JsonException();
        string mime = file.GetProperty("mimeType").GetString() ?? throw new JsonException();
        if (mime != descriptor.ContentType) throw new JsonException();
        long? observedBytes = null;
        if (file.TryGetProperty("sizeBytes", out JsonElement size))
        {
            if (size.ValueKind != JsonValueKind.String || !long.TryParse(size.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out long bytes)
                || bytes < 0 || descriptor.Bytes is { } expected && bytes != expected) throw new JsonException();
            observedBytes = bytes;
        }
        return new(name, uri, mime, state, observedBytes);
    }

}
