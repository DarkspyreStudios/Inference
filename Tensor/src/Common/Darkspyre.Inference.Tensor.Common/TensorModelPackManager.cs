using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Darkspyre.Persistence;
using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.Common;

public enum TensorModelPackState
{
    NotInstalled,
    Installed,
    Invalid
}

public sealed record TensorModelPackStatus(
    TensorModelKey Key,
    TensorModelPackState State,
    string? Detail = null);

public enum TensorModelInstallStage
{
    Checking,
    Downloading,
    Saving,
    Completed
}

public readonly record struct TensorModelInstallProgress(
    TensorModelInstallStage Stage,
    string? ArtifactRole = null,
    long? BytesTransferred = null,
    long? TotalBytes = null);

public interface ITensorModelPackManager
{
    TensorModelPackDefinition GetRequiredDefinition(TensorModelKey key);

    Task<TensorModelPackStatus> GetStatusAsync(
        TensorModelKey key,
        CancellationToken cancellationToken = default);

    Task InstallAsync(
        TensorModelKey key,
        IProgress<TensorModelInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<TensorModelPackStatus> VerifyAsync(
        TensorModelKey key,
        CancellationToken cancellationToken = default);

    Task<TensorModelPackLease> AcquireAsync(
        TensorModelKey key,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(TensorModelKey key, CancellationToken cancellationToken = default);
}

public sealed class TensorModelPackManager : ITensorModelPackManager, IDisposable
{
    public const string DefaultStoreName = "Darkspyre.Inference.Tensor.Models";

    private readonly TensorModelCatalog _catalog;
    private readonly IPersistenceStore _persistence;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _storeName;
    private readonly ConcurrentDictionary<TensorModelKey, SemaphoreSlim> _packGates = new();
    private readonly ConcurrentDictionary<TensorModelKey, int> _activeLeases = new();
    private bool _disposed;

    public TensorModelPackManager(
        TensorModelCatalog catalog,
        IPersistenceStore? persistence = null,
        HttpClient? httpClient = null,
        string storeName = DefaultStoreName)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        _catalog = catalog;
        _persistence = persistence ?? new TensorDirectFilePersistStore();
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _storeName = storeName;
    }

    public TensorModelPackDefinition GetRequiredDefinition(TensorModelKey key)
    {
        ThrowIfDisposed();
        return _catalog.GetRequired(key);
    }

    public async Task<TensorModelPackStatus> GetStatusAsync(
        TensorModelKey key,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        TensorModelPackDefinition definition = _catalog.GetRequired(key);
        string manifestName = ManifestName(definition.Key);
        await using Stream? content = await _persistence.OpenReadAsync(
            _storeName,
            manifestName,
            cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return new TensorModelPackStatus(key, TensorModelPackState.NotInstalled);
        }

        try
        {
            PersistedModelManifest? manifest = await JsonSerializer.DeserializeAsync<PersistedModelManifest>(
                content,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return manifest is not null && ManifestMatches(definition, manifest)
                ? new TensorModelPackStatus(key, TensorModelPackState.Installed)
                : new TensorModelPackStatus(key, TensorModelPackState.Invalid, "The installed manifest does not match the registered pack.");
        }
        catch (JsonException exception)
        {
            return new TensorModelPackStatus(key, TensorModelPackState.Invalid, exception.Message);
        }
    }

    public async Task InstallAsync(
        TensorModelKey key,
        IProgress<TensorModelInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        TensorModelPackDefinition definition = _catalog.GetRequired(key);
        SemaphoreSlim gate = _packGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            progress?.Report(new TensorModelInstallProgress(TensorModelInstallStage.Checking));
            TensorModelPackStatus existing = await VerifyAsync(key, cancellationToken).ConfigureAwait(false);
            if (existing.State == TensorModelPackState.Installed)
            {
                progress?.Report(new TensorModelInstallProgress(TensorModelInstallStage.Completed));
                return;
            }

            foreach (TensorModelArtifactDefinition artifact in definition.Artifacts)
            {
                if (artifact.Sources.Count == 0)
                {
                    throw new TensorModelPackException(
                        key,
                        $"Artifact '{artifact.Role}' has no registered download source.");
                }

                string temporaryPath = Path.Combine(
                    Path.GetTempPath(),
                    $"darkspyre-tensor-{Guid.NewGuid():N}.download");
                try
                {
                    await DownloadAsync(key, artifact, temporaryPath, progress, cancellationToken)
                        .ConfigureAwait(false);
                    progress?.Report(new TensorModelInstallProgress(
                        TensorModelInstallStage.Saving,
                        artifact.Role,
                        artifact.Length,
                        artifact.Length));
                    await using var input = new FileStream(
                        temporaryPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await _persistence.SaveAsync(
                        _storeName,
                        ArtifactName(definition.Key, artifact.RelativePath),
                        input,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    TryDelete(temporaryPath);
                }
            }

            var manifest = PersistedModelManifest.Create(definition);
            await using var manifestContent = new MemoryStream();
            await JsonSerializer.SerializeAsync(
                manifestContent,
                manifest,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            manifestContent.Position = 0;
            await _persistence.SaveAsync(
                _storeName,
                ManifestName(definition.Key),
                manifestContent,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new TensorModelInstallProgress(TensorModelInstallStage.Completed));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<TensorModelPackStatus> VerifyAsync(
        TensorModelKey key,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        TensorModelPackDefinition definition = _catalog.GetRequired(key);
        TensorModelPackStatus status = await GetStatusAsync(key, cancellationToken).ConfigureAwait(false);
        if (status.State != TensorModelPackState.Installed)
        {
            return status;
        }

        foreach (TensorModelArtifactDefinition artifact in definition.Artifacts)
        {
            await using Stream? content = await _persistence.OpenReadAsync(
                _storeName,
                ArtifactName(key, artifact.RelativePath),
                cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                return new TensorModelPackStatus(key, TensorModelPackState.Invalid, $"Artifact '{artifact.Role}' is missing.");
            }

            (long length, string hash) = await HashAsync(content, cancellationToken).ConfigureAwait(false);
            if (length != artifact.Length
                || !string.Equals(hash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new TensorModelPackStatus(key, TensorModelPackState.Invalid, $"Artifact '{artifact.Role}' failed verification.");
            }
        }

        return status;
    }

    public async Task<TensorModelPackLease> AcquireAsync(
        TensorModelKey key,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        TensorModelPackDefinition definition = _catalog.GetRequired(key);
        SemaphoreSlim gate = _packGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TensorModelPackStatus status = await VerifyAsync(key, cancellationToken).ConfigureAwait(false);
            if (status.State != TensorModelPackState.Installed)
            {
                throw new TensorModelPackException(key, status.Detail ?? "The tensor model pack is not installed and verified.");
            }

            IReadOnlyDictionary<string, PersistenceFileReference> artifacts = definition.Artifacts.ToDictionary(
                static artifact => artifact.Role,
                artifact => new PersistenceFileReference(
                    _persistence,
                    _storeName,
                    ArtifactName(definition.Key, artifact.RelativePath)),
                StringComparer.Ordinal);
            _activeLeases.AddOrUpdate(key, 1, static (_, count) => checked(count + 1));
            return new TensorModelPackLease(
                key,
                artifacts,
                () =>
                {
                    _activeLeases.AddOrUpdate(key, 0, static (_, count) => Math.Max(0, count - 1));
                    return ValueTask.CompletedTask;
                });
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RemoveAsync(TensorModelKey key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        TensorModelPackDefinition definition = _catalog.GetRequired(key);
        SemaphoreSlim gate = _packGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeLeases.TryGetValue(key, out int count) && count > 0)
            {
                throw new TensorModelPackException(key, "The tensor model pack is currently in use.");
            }

            await _persistence.DeleteAsync(_storeName, ManifestName(key), cancellationToken).ConfigureAwait(false);
            foreach (TensorModelArtifactDefinition artifact in definition.Artifacts)
            {
                await _persistence.DeleteAsync(
                    _storeName,
                    ArtifactName(key, artifact.RelativePath),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        foreach (SemaphoreSlim gate in _packGates.Values)
        {
            gate.Dispose();
        }
    }

    private async Task DownloadAsync(
        TensorModelKey key,
        TensorModelArtifactDefinition artifact,
        string destination,
        IProgress<TensorModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        foreach (Uri source in artifact.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using Stream input = await OpenSourceAsync(source, cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(
                    destination,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[1024 * 1024];
                long transferred = 0;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    transferred += read;
                    progress?.Report(new TensorModelInstallProgress(
                        TensorModelInstallStage.Downloading,
                        artifact.Role,
                        transferred,
                        artifact.Length));
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                string actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (transferred != artifact.Length
                    || !string.Equals(actualHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Downloaded artifact '{artifact.Role}' did not match its pinned length and SHA-256 hash.");
                }

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException
                                              or IOException
                                              or InvalidDataException
                                              or UnauthorizedAccessException)
            {
                lastFailure = exception;
                TryDelete(destination);
            }
        }

        throw new TensorModelPackException(
            key,
            $"No source could provide artifact '{artifact.Role}'.",
            lastFailure);
    }

    private async Task<Stream> OpenSourceAsync(Uri source, CancellationToken cancellationToken)
    {
        if (source.IsFile)
        {
            return new FileStream(
                source.LocalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        if (!string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !(string.Equals(source.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                 && IPAddress.TryParse(source.Host, out IPAddress? address)
                 && IPAddress.IsLoopback(address)))
        {
            throw new IOException($"Model source '{source}' must use HTTPS, a loopback HTTP address, or a local file URI.");
        }

        HttpResponseMessage response = await _httpClient.GetAsync(
            source,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new ResponseStream(content, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static async Task<(long Length, string Hash)> HashAsync(
        Stream content,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        long length = 0;
        while (true)
        {
            int read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            length += read;
        }

        return (length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static bool ManifestMatches(
        TensorModelPackDefinition definition,
        PersistedModelManifest manifest) =>
        string.Equals(manifest.ModelId, definition.Key.ModelId, StringComparison.Ordinal)
        && string.Equals(manifest.Revision, definition.Key.Revision, StringComparison.Ordinal)
        && manifest.Artifacts.Count == definition.Artifacts.Count
        && definition.Artifacts.All(artifact => manifest.Artifacts.Any(
            installed => string.Equals(installed.Role, artifact.Role, StringComparison.Ordinal)
                         && string.Equals(installed.RelativePath, artifact.RelativePath, StringComparison.Ordinal)
                         && installed.Length == artifact.Length
                         && string.Equals(installed.Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase)));

    internal static string ArtifactName(TensorModelKey key, string relativePath) =>
        $"packs/{EncodeSegment(key.ModelId)}/{EncodeSegment(key.Revision)}/artifacts/{relativePath}";

    internal static string ManifestName(TensorModelKey key) =>
        $"packs/{EncodeSegment(key.ModelId)}/{EncodeSegment(key.Revision)}/install.json";

    internal static string EncodeSegment(string value) => Uri.EscapeDataString(value).Replace("%", "_", StringComparison.Ordinal);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record PersistedArtifact(string Role, string RelativePath, long Length, string Sha256);

    private sealed record PersistedModelManifest(
        string ModelId,
        string Revision,
        IReadOnlyList<PersistedArtifact> Artifacts)
    {
        public static PersistedModelManifest Create(TensorModelPackDefinition definition) =>
            new(
                definition.Key.ModelId,
                definition.Key.Revision,
                definition.Artifacts.Select(static artifact => new PersistedArtifact(
                    artifact.Role,
                    artifact.RelativePath,
                    artifact.Length,
                    artifact.Sha256.ToLowerInvariant())).ToArray());
    }

    private sealed class ResponseStream(Stream content, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => content.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => content.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            content.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                content.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await content.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}

public sealed class TensorModelPackException : Exception
{
    public TensorModelPackException(TensorModelKey key, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Key = key;
    }

    public TensorModelKey Key { get; }
}
