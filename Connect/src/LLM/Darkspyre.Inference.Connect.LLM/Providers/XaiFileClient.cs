using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed class XaiFileClient(HttpClient http) : IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly List<Exception> _cleanupFailures = [];
    private HttpClient? _http = http;

    internal async Task<LlmProviderFile> UploadAsync(LlmConnection connection, Stream source, string fileName,
        LlmFileUploadOptions? options, Action fence, CancellationToken cancellationToken,
        Action? onDispatched = null, Action<LlmProviderFile>? onObserved = null)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        Check(fence, cancellationToken);
        long? expiry = XaiFileRequestData.Upload(source, fileName, options);
        connection = EmbeddingRequestData.Snapshot(connection);
        XaiFileRequestData.Connection(connection);
        Check(fence, cancellationToken);
        using var consumption = SdkHttpConsumption.Enter(_http!, cancellationToken);
        cancellationToken = consumption.Token;
        UploadContent? upload = null;
        try
        {
            LlmProviderFile result = await ExchangeAsync(connection, HttpMethod.Post, "files", request =>
            {
                var multipart = new MultipartFormDataContent();
                request.Content = multipart;
                if (expiry is { } seconds) multipart.Add(new StringContent(seconds.ToString(CultureInfo.InvariantCulture)), "expires_after");
                upload = new UploadContent(source, () => Check(fence, cancellationToken), cancellationToken);
                multipart.Add(upload, "file", fileName);
            }, response => ReadAsync(connection, response, root =>
            {
                LlmProviderFile observed = ReadFile(root);
                onObserved?.Invoke(observed);
                return observed;
            }, fence, cancellationToken), fence, cancellationToken, onDispatched).ConfigureAwait(false);
            return consumption.Publish(result);
        }
        finally { upload?.DetachBorrowed(); }
    }

    internal Task<LlmFilePage> ListAsync(LlmConnection connection, LlmFileListRequest request,
        Action fence, CancellationToken cancellationToken)
    {
        string path = XaiFileRequestData.List(request);
        int limit = request.Limit;
        return JsonAsync(connection, HttpMethod.Get, path, null, root =>
        {
            JsonElement data = Required(root, "data");
            if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > limit) throw Invalid();
            return new LlmFilePage(data.EnumerateArray().Select(ReadFile).ToArray(), OptionalString(root, "pagination_token"), data.GetArrayLength() < limit);
        }, fence, cancellationToken);
    }

    internal Task<LlmProviderFile> GetAsync(LlmConnection connection, string fileId, Action fence, CancellationToken cancellationToken) =>
        JsonAsync(connection, HttpMethod.Get, XaiFileRequestData.Path(fileId), null, ReadFile, fence, cancellationToken);

    internal Task<LlmFileDeletion> DeleteAsync(LlmConnection connection, string fileId, Action fence, CancellationToken cancellationToken) =>
        JsonAsync(connection, HttpMethod.Delete, XaiFileRequestData.Path(fileId), null, root =>
        {
            if (Text(root, "object") != "file") throw Invalid();
            return new LlmFileDeletion(XaiFileRequestData.Identity(Text(root, "id")), Boolean(root, "deleted"));
        }, fence, cancellationToken);

    internal Task<XaiPublicUrl> CreatePublicUrlAsync(LlmConnection connection, string fileId,
        XaiPublicUrlOptions? options, Action fence, CancellationToken cancellationToken)
    {
        var body = new JsonObject();
        if (XaiFileRequestData.Expiry(options?.ExpiresAfter) is { } seconds) body["expires_after"] = seconds;
        return JsonAsync(connection, HttpMethod.Post, XaiFileRequestData.Path(fileId) + "/public-url", body.ToJsonString(),
            root => new XaiPublicUrl(PublicUrl(Text(root, "public_url"))!, OptionalDate(root, "expires_at")), fence, cancellationToken);
    }

    internal Task<XaiPublicUrlRevocation> RevokePublicUrlAsync(LlmConnection connection, string fileId, Action fence, CancellationToken cancellationToken) =>
        JsonAsync(connection, HttpMethod.Post, XaiFileRequestData.Path(fileId) + "/public-url/revoke", null,
            root => new XaiPublicUrlRevocation(XaiFileRequestData.Identity(Text(root, "id")), Boolean(root, "revoked"), PublicUrl(OptionalString(root, "public_url"))), fence, cancellationToken);

    internal async Task<ResourceDownloadReceipt> DownloadAsync(LlmConnection connection, string fileId, Stream destination,
        LlmFileDownloadOptions? options, Action fence, CancellationToken cancellationToken)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        Check(fence, cancellationToken);
        string path = XaiFileRequestData.Download(fileId, destination, options);
        connection = EmbeddingRequestData.Snapshot(connection);
        XaiFileRequestData.Connection(connection);
        using var consumption = SdkHttpConsumption.Enter(_http!, cancellationToken);
        cancellationToken = consumption.Token;
        ResourceDownloadReceipt result = await ExchangeAsync(connection, HttpMethod.Get, path, request =>
        {
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        }, async response =>
        {
            await XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, LlmCredential.Parse(connection.Credential), [], cancellationToken, diagnosticBody: false).ConfigureAwait(false);
            Check(fence, cancellationToken);
            Stream content = await SdkHttpConsumption.ReadAsStreamAsync(response.Content, cancellationToken).ConfigureAwait(false);
            Check(fence, cancellationToken);
            long written = await TransferAsync(content, destination, () => Check(fence, cancellationToken), cancellationToken).ConfigureAwait(false);
            Check(fence, cancellationToken);
            if (response.Content.Headers.ContentLength is { } declared && written < declared) throw Invalid();
            return new ResourceDownloadReceipt(written, response.Content.Headers.ContentType?.ToString(), response.Content.Headers.ContentLength);
        }, fence, cancellationToken).ConfigureAwait(false);
        return consumption.Publish(result);
    }

    private async Task<T> JsonAsync<T>(LlmConnection connection, HttpMethod method, string path, string? body,
        Func<JsonElement, T> parse, Action fence, CancellationToken cancellationToken)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        Check(fence, cancellationToken);
        connection = EmbeddingRequestData.Snapshot(connection);
        XaiFileRequestData.Connection(connection);
        using var consumption = SdkHttpConsumption.Enter(_http!, cancellationToken);
        cancellationToken = consumption.Token;
        T result = await ExchangeAsync(connection, method, path, request =>
        {
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }, response => ReadAsync(connection, response, parse, fence, cancellationToken), fence, cancellationToken).ConfigureAwait(false);
        return consumption.Publish(result);
    }

    private async Task<T> ExchangeAsync<T>(LlmConnection connection, HttpMethod method, string path,
        Action<HttpRequestMessage> configure, Func<HttpResponseMessage, Task<T>> read, Action fence, CancellationToken token,
        Action? onDispatched = null)
    {
        HttpRequestMessage? request = null;
        HttpResponseMessage? response = null;
        T result = default!;
        var failures = new List<Exception>();
        try
        {
            request = new HttpRequestMessage(method, ProviderHttp.BuildUri(connection, path));
            XaiRequestHeaders.Apply(request, connection, false);
            configure(request);
            Check(fence, token);
            // The internal sink records possible effects at the same boundary as SendAsync.
            onDispatched?.Invoke();
            response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            Check(fence, token);
            result = await read(response).ConfigureAwait(false);
            Check(fence, token);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            try { response?.Dispose(); }
            catch (Exception cleanup) { failures.Add(RecordCleanup(cleanup, response!)); }
            try { request?.Dispose(); }
            catch (Exception cleanup) { failures.Add(RecordCleanup(cleanup, request!)); }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Count > 1) throw new AggregateException("Grok Files operation and cleanup failed.", failures);
        Check(fence, token);
        return result;
    }

    private async Task<T> ReadAsync<T>(LlmConnection connection, HttpResponseMessage response,
        Func<JsonElement, T> parse, Action fence, CancellationToken token)
    {
        await XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, LlmCredential.Parse(connection.Credential), [], token, diagnosticBody: false).ConfigureAwait(false);
        Check(fence, token);
        string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, token).ConfigureAwait(false);
        Check(fence, token);
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return parse(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw Invalid();
        }
    }

    private void Check(Action fence, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(fence);
        token.ThrowIfCancellationRequested();
        try { _lifetime.ThrowIfStopping(); }
        catch (ObjectDisposedException error) { throw new OperationCanceledException("Files operation retired.", error, token); }
        fence();
        token.ThrowIfCancellationRequested();
    }

    private static LlmProviderException Invalid() => new("Grok Files returned invalid protocol data.", LlmTransport.XaiResponses);

    private static JsonElement Required(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) throw Invalid();
        JsonProperty[] properties = root.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        if (properties.Length != 1) throw Invalid();
        return properties[0].Value;
    }
    private static string Text(JsonElement root, string name) => Required(root, name) is { ValueKind: JsonValueKind.String } value ? value.GetString()! : throw Invalid();
    private static bool Boolean(JsonElement root, string name) => Required(root, name).ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Invalid() };
    private static long Integer(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long integer) ? integer : throw Invalid();
    private static JsonElement? Optional(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out _)) return null;
        JsonElement value = Required(root, name);
        return value.ValueKind == JsonValueKind.Null ? null : value;
    }
    private static string? OptionalString(JsonElement root, string name) => Optional(root, name) is { } value
        ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw Invalid() : null;
    private static DateTimeOffset? OptionalDate(JsonElement root, string name) => Optional(root, name) is { } value
        ? DateTimeOffset.FromUnixTimeSeconds(Integer(value)) : null;
    private static string? PublicUrl(string? value)
    {
        if (value is not null && (value.Any(char.IsControl) || !Uri.TryCreate(value, UriKind.Absolute, out _))) throw Invalid();
        return value;
    }
    private static LlmProviderFile ReadFile(JsonElement root)
    {
        if (Text(root, "object") != "file") throw Invalid();
        long bytes = Integer(Required(root, "bytes"));
        if (bytes < 0) throw Invalid();
        return new(XaiFileRequestData.Identity(Text(root, "id")), Text(root, "filename"), bytes,
            DateTimeOffset.FromUnixTimeSeconds(Integer(Required(root, "created_at"))), OptionalDate(root, "expires_at"))
        { PublicUrl = PublicUrl(OptionalString(root, "public_url")), PublicUrlExpiresAt = OptionalDate(root, "public_url_expires_at") };
    }

    private static async Task<long> TransferAsync(Stream source, Stream destination, Action fence, CancellationToken token)
    {
        byte[] buffer = new byte[81920];
        long written = 0;
        while (true)
        {
            fence();
            int count = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            fence();
            if (count == 0) return written;
            await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            written = checked(written + count);
            fence();
        }
    }

    private sealed class UploadContent(Stream source, Action fence, CancellationToken ownerToken) : HttpContent
    {
        private Stream? _source = source;
        private Action? _fence = fence;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, ownerToken);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ownerToken, cancellationToken);
            Stream borrowedSource = _source ?? throw new ObjectDisposedException(nameof(UploadContent));
            Action operationFence = _fence ?? throw new ObjectDisposedException(nameof(UploadContent));
            await TransferAsync(borrowedSource, stream, operationFence, linked.Token).ConfigureAwait(false);
            operationFence();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DetachBorrowed();
            base.Dispose(disposing);
        }

        internal void DetachBorrowed() { _source = null; _fence = null; }
    }

    public void Dispose() => _lifetime.RetireAsync(ReleaseAsync).GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(_lifetime.RetireAsync(ReleaseAsync));
    private Exception RecordCleanup(Exception cause, object refusedOwner)
    {
        Exception failure = LlmResourcePreparation.AsCleanupFailure(cause, refusedOwner: refusedOwner);
        lock (_cleanupFailures) { _cleanupFailures.Add(failure); }
        return failure;
    }

    private Task ReleaseAsync()
    {
        _http = null;
        if (_cleanupFailures.Count == 1) return Task.FromException(_cleanupFailures[0]);
        if (_cleanupFailures.Count > 1) return Task.FromException(new AggregateException("Grok Files cleanup failed.", _cleanupFailures));
        return Task.CompletedTask;
    }
}
