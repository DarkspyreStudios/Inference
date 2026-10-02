using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed class XaiFileClient(HttpClient http) : IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    private HttpClient? _http = http;

    internal async Task<LlmProviderFile> UploadAsync(LlmConnection connection, Stream source, string fileName,
        LlmFileUploadOptions? options, Action fence, CancellationToken cancellationToken)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        Check(fence, cancellationToken);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName.Any(char.IsControl)) throw new ArgumentException("The filename contains control characters.", nameof(fileName));
        if (!source.CanRead) throw new ArgumentException("Upload source must be readable.", nameof(source));
        long? expiry = Expiry(options?.ExpiresAfter);
        connection = EmbeddingRequestData.Snapshot(connection);
        _ = XaiRequestHeaders.Require(connection);
        Check(fence, cancellationToken);
        LlmProviderFile result;
        using var consumption = SdkHttpConsumption.Enter(_http!, cancellationToken);
        cancellationToken = consumption.Token;
        {
            using var request = Request(connection, HttpMethod.Post, "files");
            using var multipart = new MultipartFormDataContent();
            if (expiry is { } seconds) multipart.Add(new StringContent(seconds.ToString(CultureInfo.InvariantCulture)), "expires_after");
            multipart.Add(new UploadContent(source, () => Check(fence, cancellationToken), cancellationToken), "file", fileName);
            request.Content = multipart;
            Check(fence, cancellationToken);
            using HttpResponseMessage response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            Check(fence, cancellationToken);
            result = await ReadAsync(connection, response, ReadFile, fence, cancellationToken).ConfigureAwait(false);
            Check(fence, cancellationToken);
        }
        Check(fence, cancellationToken);
        return consumption.Publish(result);
    }

    internal Task<LlmFilePage> ListAsync(LlmConnection connection, LlmFileListRequest request,
        Action fence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        int limit = FileContractData.Limit(request.Limit);
        var query = new List<KeyValuePair<string, string>> { new("limit", limit.ToString(CultureInfo.InvariantCulture)) };
        if (request.PaginationToken is { } token) query.Add(new("pagination_token", token));
        if (request.Order is { } order) query.Add(new("order", order switch
        {
            LlmFileOrder.Ascending => "asc",
            LlmFileOrder.Descending => "desc",
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        }));
        if (request.Sort is { } sort) query.Add(new("sort_by", sort switch
        {
            LlmFileSort.CreatedAt => "created_at",
            LlmFileSort.FileName => "filename",
            LlmFileSort.Size => "size",
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        }));
        if (request.Filter is { } filter) query.Add(new("filter", filter));
        string path = "files?" + string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        return JsonAsync(connection, HttpMethod.Get, path, null, root =>
        {
            JsonElement data = Required(root, "data");
            if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > limit) throw Invalid();
            return new LlmFilePage(data.EnumerateArray().Select(ReadFile).ToArray(), OptionalString(root, "pagination_token"), data.GetArrayLength() < limit);
        }, fence, cancellationToken);
    }

    internal Task<LlmProviderFile> GetAsync(LlmConnection connection, string fileId, Action fence, CancellationToken cancellationToken) =>
        JsonAsync(connection, HttpMethod.Get, Path(fileId), null, ReadFile, fence, cancellationToken);

    internal Task<LlmFileDeletion> DeleteAsync(LlmConnection connection, string fileId, Action fence, CancellationToken cancellationToken) =>
        JsonAsync(connection, HttpMethod.Delete, Path(fileId), null, root =>
        {
            if (Text(root, "object") != "file") throw Invalid();
            return new LlmFileDeletion(Identity(Text(root, "id")), Boolean(root, "deleted"));
        }, fence, cancellationToken);

    internal Task<XaiPublicUrl> CreatePublicUrlAsync(LlmConnection connection, string fileId,
        XaiPublicUrlOptions? options, Action fence, CancellationToken cancellationToken)
    {
        var body = new JsonObject();
        if (Expiry(options?.ExpiresAfter) is { } seconds) body["expires_after"] = seconds;
        return JsonAsync(connection, HttpMethod.Post, Path(fileId) + "/public-url", body.ToJsonString(),
            root => new XaiPublicUrl(Text(root, "public_url"), OptionalDate(root, "expires_at")), fence, cancellationToken);
    }

    internal Task<XaiPublicUrlRevocation> RevokePublicUrlAsync(LlmConnection connection, string fileId, Action fence, CancellationToken cancellationToken) =>
        JsonAsync(connection, HttpMethod.Post, Path(fileId) + "/public-url/revoke", null,
            root => new XaiPublicUrlRevocation(Identity(Text(root, "id")), Boolean(root, "revoked"), OptionalString(root, "public_url")), fence, cancellationToken);

    internal async Task<ResourceDownloadReceipt> DownloadAsync(LlmConnection connection, string fileId, Stream destination,
        LlmFileDownloadOptions? options, Action fence, CancellationToken cancellationToken)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        Check(fence, cancellationToken);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("Download destination must be writable.", nameof(destination));
        string path = Path(fileId) + "/content";
        if (options?.Format is { } format) path += "?format=" + (format switch
        {
            LlmFileContentFormat.Original => "original",
            LlmFileContentFormat.Text => "text",
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        });
        connection = EmbeddingRequestData.Snapshot(connection);
        _ = XaiRequestHeaders.Require(connection);
        ResourceDownloadReceipt result;
        using var consumption = SdkHttpConsumption.Enter(_http!, cancellationToken);
        cancellationToken = consumption.Token;
        {
            using var request = Request(connection, HttpMethod.Get, path);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            Check(fence, cancellationToken);
            using HttpResponseMessage response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            Check(fence, cancellationToken);
            await XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, LlmCredential.Parse(connection.Credential), [], cancellationToken, diagnosticBody: false).ConfigureAwait(false);
            Check(fence, cancellationToken);
            Stream content = await SdkHttpConsumption.ReadAsStreamAsync(response.Content, cancellationToken).ConfigureAwait(false);
            Check(fence, cancellationToken);
            long written = await TransferAsync(content, destination, () => Check(fence, cancellationToken), cancellationToken).ConfigureAwait(false);
            Check(fence, cancellationToken);
            result = new(written, response.Content.Headers.ContentType?.ToString(), response.Content.Headers.ContentLength);
        }
        Check(fence, cancellationToken);
        return consumption.Publish(result);
    }

    private async Task<T> JsonAsync<T>(LlmConnection connection, HttpMethod method, string path, string? body,
        Func<JsonElement, T> parse, Action fence, CancellationToken cancellationToken)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        Check(fence, cancellationToken);
        connection = EmbeddingRequestData.Snapshot(connection);
        _ = XaiRequestHeaders.Require(connection);
        using var consumption = SdkHttpConsumption.Enter(_http!, cancellationToken);
        cancellationToken = consumption.Token;
        T result;
        {
            using var request = Request(connection, method, path);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            Check(fence, cancellationToken);
            using HttpResponseMessage response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            Check(fence, cancellationToken);
            result = await ReadAsync(connection, response, parse, fence, cancellationToken).ConfigureAwait(false);
            Check(fence, cancellationToken);
        }
        Check(fence, cancellationToken);
        return consumption.Publish(result);
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

    private static HttpRequestMessage Request(LlmConnection connection, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, ProviderHttp.BuildUri(connection, path));
        try { XaiRequestHeaders.Apply(request, connection, false); return request; }
        catch { request.Dispose(); throw; }
    }

    private static string Identity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value is "." or ".." || value.Any(char.IsControl)) throw new ArgumentException("Invalid literal file identifier.", nameof(value));
        return value;
    }
    private static string Path(string id) => "files/" + Uri.EscapeDataString(Identity(id));
    private static long? Expiry(TimeSpan? value) => FileContractData.Expiry(value) is { } duration ? duration.Ticks / TimeSpan.TicksPerSecond : null;
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
    private static LlmProviderFile ReadFile(JsonElement root)
    {
        if (Text(root, "object") != "file") throw Invalid();
        long bytes = Integer(Required(root, "bytes"));
        if (bytes < 0) throw Invalid();
        return new(Identity(Text(root, "id")), Text(root, "filename"), bytes,
            DateTimeOffset.FromUnixTimeSeconds(Integer(Required(root, "created_at"))), OptionalDate(root, "expires_at"))
        { PublicUrl = OptionalString(root, "public_url"), PublicUrlExpiresAt = OptionalDate(root, "public_url_expires_at") };
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
            if (disposing) { _source = null; _fence = null; }
            base.Dispose(disposing);
        }
    }

    public void Dispose() => _lifetime.RetireAsync(ReleaseAsync).GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(_lifetime.RetireAsync(ReleaseAsync));
    private Task ReleaseAsync() { _http = null; return Task.CompletedTask; }
}
