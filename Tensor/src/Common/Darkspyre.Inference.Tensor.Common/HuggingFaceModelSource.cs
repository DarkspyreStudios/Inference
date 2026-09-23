using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>Bounded, metadata-only Hugging Face discovery. No repository code or model weights are executed or downloaded.
/// Caller-owned HTTP clients must disable automatic redirects and default credentials; use the exact-origin access callback.</summary>
public sealed class HuggingFaceModelSource : ITensorModelSource, IDisposable
{
    private const int MaximumMetadataBytes = 4 * 1024 * 1024;
    private static readonly Uri Hub = new("https://huggingface.co/");
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TensorSourceHttp _http;
    private bool _disposed;

    public HuggingFaceModelSource(HttpClient? httpClient = null, TensorSourceAccess? access = null)
    {
        _client = httpClient ?? TensorSourceHttp.CreateClient();
        _ownsClient = httpClient is null;
        _http = new(_client, access);
    }

    public string Id => "hugging-face";

    public Task<TensorSourceResult<TensorSourcePage<TensorSourceModel>>> SearchAsync(TensorSourceQuery query, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(query);
            ValidatePage(query.PageSize, query.Continuation);
            if (query.Search is null || query.Search.Length > 512 || query.Search.Any(char.IsControl)) { throw new ArgumentException("Invalid search."); }
            var uri = new Uri(Hub, "api/models?full=true&cardData=true&limit=" + query.PageSize.ToString(CultureInfo.InvariantCulture)
                + "&search=" + Uri.EscapeDataString(query.Search) + Cursor(query.Continuation));
            using HttpResponseMessage response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            using JsonDocument json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            TensorSourceModel[] models = json.RootElement.EnumerateArray().Select(item => new TensorSourceModel(
                RequiredRepository(item, "id"), Text(item, "sha"), License(item), Boolean(item, "private"), Gated(item))).ToArray();
            return new TensorSourcePage<TensorSourceModel>(Array.AsReadOnly(models), NextCursor(response, uri));
        }, cancellationToken);

    public Task<TensorSourceResult<TensorSourcePage<TensorSourceRevision>>> ListRevisionsAsync(TensorRepositoryQuery query, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(query);
            ValidateRepository(query.Repository);
            ValidatePage(query.PageSize, query.Continuation);
            var uri = new Uri(Hub, "api/models/" + EscapePath(query.Repository) + "/commits/main?limit="
                + query.PageSize.ToString(CultureInfo.InvariantCulture) + Cursor(query.Continuation));
            using HttpResponseMessage response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            using JsonDocument json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            TensorSourceRevision[] revisions = json.RootElement.EnumerateArray().Select(item =>
            {
                string revision = Text(item, "id") ?? throw new JsonException();
                if (!IsCommit(revision)) { throw new JsonException(); }
                DateTimeOffset? date = DateTimeOffset.TryParse(Text(item, "date"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed) ? parsed : null;
                return new TensorSourceRevision(revision, date);
            }).ToArray();
            return new TensorSourcePage<TensorSourceRevision>(Array.AsReadOnly(revisions), NextCursor(response, uri));
        }, cancellationToken);

    public Task<TensorSourceResult<TensorModelCandidate>> GetCandidateAsync(TensorCandidateSelection selection, CancellationToken cancellationToken = default) =>
        FetchCandidateAsync(selection, true, cancellationToken);

    public async Task<TensorSourceResult<TensorSourcePage<TensorSourceArtifact>>> ListVariantsAsync(TensorVariantQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        TensorSourceResult<TensorModelCandidate> candidate = await FetchCandidateAsync(new(query.Repository, query.Revision, string.Empty), false, cancellationToken).ConfigureAwait(false);
        return new(candidate.State, candidate.Value is null ? null : new(candidate.Value.Artifacts), candidate.Issues);
    }

    private Task<TensorSourceResult<TensorModelCandidate>> FetchCandidateAsync(TensorCandidateSelection selection, bool requireVariant, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(selection);
            ArgumentException.ThrowIfNullOrWhiteSpace(selection.Repository);
            ValidateRepository(selection.Repository);
            if (string.IsNullOrWhiteSpace(selection.Revision) || selection.Revision.Length > 256 || selection.Revision.Any(char.IsControl)) { throw new ArgumentException("Invalid revision."); }
            if (requireVariant) { TensorModelDefinitionValidator.ValidateRelativePath(selection.Variant); }
            var uri = new Uri(Hub, "api/models/" + EscapePath(selection.Repository) + "/revision/" + Uri.EscapeDataString(selection.Revision) + "?blobs=true");
            using HttpResponseMessage response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            using JsonDocument json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            JsonElement root = json.RootElement;
            string repository = RequiredRepository(root, "id");
            if (!string.Equals(repository, selection.Repository, StringComparison.OrdinalIgnoreCase)) { throw new JsonException(); }
            string revision = Text(root, "sha") ?? throw new JsonException();
            if (!IsCommit(revision) || (IsCommit(selection.Revision) && !string.Equals(selection.Revision, revision, StringComparison.OrdinalIgnoreCase))) { throw new JsonException(); }
            string? license = License(root);
            var artifacts = new List<TensorSourceArtifact>();
            foreach (JsonElement item in root.GetProperty("siblings").EnumerateArray())
            {
                string path = Text(item, "rfilename") ?? throw new JsonException();
                TensorModelDefinitionValidator.ValidateRelativePath(path);
                JsonElement lfs = Property(item, "lfs");
                long? length = Number(item, "size") ?? Number(lfs, "size");
                string? sha = Text(lfs, "sha256");
                // blobId is a Git object identity, not the SHA-256 of the artifact bytes.
                if (sha is not null && (sha.Length != 64 || !sha.All(Uri.IsHexDigit))) { sha = null; }
                if (length < 0) { length = null; }
                artifacts.Add(new(path, length, sha?.ToLowerInvariant(), new(Hub, repository, revision, path, license),
                    new Uri(Hub, EscapePath(repository) + "/resolve/" + revision + "/" + EscapePath(path))));
            }
            if (requireVariant && !artifacts.Any(item => item.Path == selection.Variant))
            {
                throw new TensorSourceException(TensorSourceState.NotFound, "variant-not-found", "The selected artifact is not listed at this revision.");
            }
            if (artifacts.Select(item => item.Path).Distinct(StringComparer.Ordinal).Count() != artifacts.Count) { throw new JsonException(); }
            JsonElement gguf = Property(root, "gguf");
            string? architecture = Text(gguf, "architecture") ?? Text(Property(root, "config"), "model_type");
            long? context = Number(gguf, "context_length");
            bool? chat = Text(gguf, "chat_template") is string template ? !string.IsNullOrWhiteSpace(template) : null;
            return new TensorModelCandidate(Id, new(repository, revision, selection.Variant), new(architecture, context, chat),
                license, Boolean(root, "private"), Gated(root), artifacts.AsReadOnly());
        }, cancellationToken);

    public Task<TensorSourceResult<TensorMetadataContent>> ReadMetadataAsync(TensorSourceArtifact artifact, TensorMetadataRead read, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(artifact);
            ArgumentNullException.ThrowIfNull(read);
            ArgumentException.ThrowIfNullOrWhiteSpace(artifact.Provenance.Repository);
            ArgumentException.ThrowIfNullOrWhiteSpace(artifact.Provenance.Revision);
            ValidateRepository(artifact.Provenance.Repository);
            TensorModelDefinitionValidator.ValidateRelativePath(artifact.Path);
            if (artifact.Provenance.Origin != Hub || !IsCommit(artifact.Provenance.Revision)
                || artifact.Provenance.Path != artifact.Path || read.Offset < 0 || read.Length is < 1 or > 16 * 1024 * 1024
                || read.Offset > long.MaxValue - read.Length || !Enum.IsDefined(read.Kind)
                || (read.Kind == TensorMetadataReadKind.CompleteMetadataFile && (read.Offset != 0 || read.Length > MaximumMetadataBytes)))
            { throw new ArgumentException("Invalid metadata inspection request."); }
            var uri = new Uri(Hub, EscapePath(artifact.Provenance.Repository) + "/resolve/" + artifact.Provenance.Revision + "/" + EscapePath(artifact.Path));
            if (artifact.DownloadUri != uri) { throw new ArgumentException("Artifact provenance does not match its source URI."); }
            using HttpResponseMessage response = await _http.GetAsync(uri, cancellationToken,
                new RangeHeaderValue(read.Offset, read.Offset + read.Length - 1)).ConfigureAwait(false);
            ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
            bool whole = response.StatusCode == HttpStatusCode.OK && read.Kind == TensorMetadataReadKind.CompleteMetadataFile;
            if (response.Content.Headers.ContentEncoding.Count != 0
                || (!whole && (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes"
                    || range.From != read.Offset || range.To is null || range.To < range.From || range.To >= read.Offset + read.Length
                    || range.Length is null || range.Length <= range.To))
                || response.Content.Headers.ContentLength > read.Length)
            { throw new TensorSourceException(TensorSourceState.Unverified, "metadata-range", "The source did not honor the bounded metadata request."); }
            byte[] bytes = await ReadBoundedAsync(response, read.Length, cancellationToken).ConfigureAwait(false);
            long total = whole ? bytes.LongLength : range!.Length!.Value;
            if ((!whole && bytes.LongLength != range!.To - range.From + 1)
                || (artifact.Length is long expected && expected != total)
                || (read.Kind == TensorMetadataReadKind.CompleteMetadataFile && bytes.LongLength != total))
            { throw new TensorSourceException(TensorSourceState.Unverified, "metadata-length", "The metadata response length is inconsistent or incomplete."); }
            return new TensorMetadataContent(bytes, read.Offset, total);
        }, cancellationToken);

    private async Task<TensorSourceResult<T>> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken token) where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        try { return new(TensorSourceState.Available, await action().ConfigureAwait(false)); }
        catch (TensorSourceException exception) { return new(exception.State, Issues: [new(exception.Code, exception.Message)]); }
        catch (ArgumentException) { return new(TensorSourceState.InvalidRequest, Issues: [new("source-query", "The source query or artifact path is invalid.")]); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return new(TensorSourceState.Unverified, Issues: [new("source-metadata", "The source metadata is missing, malformed or unsupported.")]); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(TensorSourceState.TransientFailure, Issues: [new("source-timeout", "The source request timed out.")]); }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        { return new(TensorSourceState.TransientFailure, Issues: [new("source-read", "The source response could not be read.")]); }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        return JsonDocument.Parse(await ReadBoundedAsync(response, MaximumMetadataBytes, token).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 32 });
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximumBytes, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximumBytes) { throw new JsonException(); }
        await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maximumBytes - bytes.Length + 1)), token).ConfigureAwait(false);
            if (read == 0) { break; }
            if (bytes.Length + read > maximumBytes) { throw new JsonException(); }
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }

    private static string? NextCursor(HttpResponseMessage response, Uri requested)
    {
        if (!response.Headers.TryGetValues("Link", out IEnumerable<string>? links)) { return null; }
        foreach (string link in links.SelectMany(value => value.Split(',')))
        {
            if (!link.Contains("rel=\"next\"", StringComparison.Ordinal) && !link.Contains("rel=next", StringComparison.Ordinal)) { continue; }
            int start = link.IndexOf('<'), end = link.IndexOf('>');
            if (start < 0 || end <= start || !Uri.TryCreate(link[(start + 1)..end], UriKind.Absolute, out Uri? next)
                || TensorSourceHttp.Origin(next) != Hub || next.AbsolutePath != requested.AbsolutePath || next.UserInfo.Length != 0)
            { throw new JsonException(); }
            // Only the cursor is accepted as data. Subsequent requests rebuild our known endpoint and query.
            string? cursor = next.Query.TrimStart('?').Split('&').Where(pair => pair.StartsWith("cursor=", StringComparison.Ordinal))
                .Select(pair => Uri.UnescapeDataString(pair[7..])).SingleOrDefault();
            if (string.IsNullOrEmpty(cursor) || cursor.Length > 8192 || cursor.Any(char.IsControl)) { throw new JsonException(); }
            return cursor;
        }
        return null;
    }

    private static void ValidatePage(int size, string? cursor)
    {
        if (size is < 1 or > 100 || cursor is { Length: > 8192 } || (cursor?.Any(char.IsControl) ?? false)) { throw new ArgumentException("Invalid page."); }
    }
    private static string Cursor(string? cursor) => cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor);
    private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    private static void ValidateRepository(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository) || repository.Length > 256 || repository.Split('/').Length is < 1 or > 2
            || repository.Split('/').Any(part => part is "" or "." or ".." || part.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.')))
        { throw new ArgumentException("Invalid repository."); }
    }
    private static string RequiredRepository(JsonElement item, string key)
    {
        string repository = Text(item, key) ?? throw new JsonException();
        ValidateRepository(repository);
        return repository;
    }
    private static bool IsCommit(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);
    private static JsonElement Property(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement value) ? value : default;
    private static string? Text(JsonElement item, string name) => Property(item, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static long? Number(JsonElement item, string name) => Property(item, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out long number) ? number : null;
    private static bool? Boolean(JsonElement item, string name) => Property(item, name).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    private static string? License(JsonElement item) => Text(Property(item, "cardData"), "license");
    private static bool? Gated(JsonElement item) => Text(item, "gated") is "auto" or "manual" ? true : Boolean(item, "gated");

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        if (_ownsClient) { _client.Dispose(); }
    }
}
