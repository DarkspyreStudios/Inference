using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

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
    Completed,
    Failed,
    Cancelled
}

public readonly record struct TensorModelInstallProgress(
    TensorModelInstallStage Stage,
    string? ArtifactRole = null,
    long? BytesTransferred = null,
    long? TotalBytes = null,
    long? OverallBytesTransferred = null,
    long? OverallTotalBytes = null,
    bool SupportsResume = false);

public interface ITensorModelPackManager
{
    IReadOnlyList<TensorModelPackDefinition> Definitions { get; }

    TensorModelPackDefinition Register(TensorModelPackDefinition definition);

    TensorModelPackDefinition GetRequiredDefinition(TensorModelKey key);

    Task<TensorModelPackStatus> GetStatusAsync(
        TensorModelKey key,
        CancellationToken cancellationToken = default);

    Task InstallAsync(
        TensorModelKey key,
        IProgress<TensorModelInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<TensorModelImportResult> ImportAsync(TensorModelImportRequest request,
        IProgress<TensorModelInstallProgress>? progress = null, CancellationToken cancellationToken = default);

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
    private readonly TensorModelCatalog _catalog;
    private readonly TensorPackDirectory _directory;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TensorSourceHttp _sourceHttp;
    private readonly ConcurrentDictionary<TensorModelKey, SemaphoreSlim> _packGates = new();
    private readonly ConcurrentDictionary<TensorModelKey, int> _activeLeases = new();
    private bool _disposed;

    /// <param name="catalog">The shared definition catalog.</param>
    /// <param name="packDirectory">
    /// The local directory that holds installed packs. Null selects
    /// LocalApplicationData/Darkspyre Studios/Inference/Tensor.
    /// </param>
    /// <param name="httpClient">The client for downloads; null creates and owns one.</param>
    /// <param name="sourceAccess">Resolves credentials for restricted download origins.</param>
    public TensorModelPackManager(
        TensorModelCatalog catalog,
        string? packDirectory = null,
        HttpClient? httpClient = null,
        TensorSourceAccess? sourceAccess = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _directory = new TensorPackDirectory(packDirectory ?? TensorPackDirectory.DefaultRootDirectory);
        _httpClient = httpClient ?? TensorSourceHttp.CreateClient();
        _ownsHttpClient = httpClient is null;
        _sourceHttp = new(_httpClient, sourceAccess);
    }

    public IReadOnlyList<TensorModelPackDefinition> Definitions { get { ThrowIfDisposed(); return _catalog.Definitions; } }

    public TensorModelPackDefinition Register(TensorModelPackDefinition definition)
    {
        ThrowIfDisposed();
        return _catalog.Register(definition);
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
        await using Stream? content = await _directory.OpenReadAsync(
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

    public Task InstallAsync(
        TensorModelKey key,
        IProgress<TensorModelInstallProgress>? progress = null,
        CancellationToken cancellationToken = default) => InstallCoreAsync(key, progress, null, cancellationToken);

    private async Task InstallCoreAsync(TensorModelKey key, IProgress<TensorModelInstallProgress>? progress,
        IReadOnlyDictionary<Uri, string>? staged, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        TensorModelPackDefinition definition = _catalog.GetRequired(key);
        SemaphoreSlim gate = _packGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        bool entered = false;
        long total = definition.Artifacts.Sum(artifact => artifact.Length);
        long completed = 0;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            Report(progress, new(TensorModelInstallStage.Checking, OverallBytesTransferred: 0, OverallTotalBytes: total));
            TensorModelPackStatus existing = await VerifyAsync(key, cancellationToken).ConfigureAwait(false);
            if (existing.State == TensorModelPackState.Installed)
            {
                Report(progress, new(TensorModelInstallStage.Completed, OverallBytesTransferred: total, OverallTotalBytes: total));
                return;
            }

            if (_activeLeases.TryGetValue(key, out int leases) && leases > 0)
            {
                throw new TensorModelPackException(key, "A model pack in use cannot be repaired.");
            }
            // A previous manifest must not advertise an interrupted repair as installed.
            if (existing.State == TensorModelPackState.Invalid)
            {
                await _directory.DeleteAsync(ManifestName(key), cancellationToken).ConfigureAwait(false);
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
                    await DownloadAsync(key, artifact, temporaryPath, progress, completed, total, staged, cancellationToken)
                        .ConfigureAwait(false);
                    Report(progress, new TensorModelInstallProgress(
                        TensorModelInstallStage.Saving,
                        artifact.Role,
                        artifact.Length,
                        artifact.Length,
                        completed + artifact.Length,
                        total));
                    await using var input = new FileStream(
                        temporaryPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await _directory.SaveAsync(
                        ArtifactName(definition.Key, artifact.RelativePath),
                        input,
                        cancellationToken).ConfigureAwait(false);
                    completed += artifact.Length;
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
            await _directory.SaveAsync(
                ManifestName(definition.Key),
                manifestContent,
                cancellationToken).ConfigureAwait(false);
            Report(progress, new(TensorModelInstallStage.Completed, OverallBytesTransferred: total, OverallTotalBytes: total));
        }
        catch (OperationCanceledException)
        {
            Report(progress, new(TensorModelInstallStage.Cancelled, OverallBytesTransferred: completed, OverallTotalBytes: total));
            throw;
        }
        catch
        {
            Report(progress, new(TensorModelInstallStage.Failed, OverallBytesTransferred: completed, OverallTotalBytes: total));
            throw;
        }
        finally
        {
            if (entered) { gate.Release(); }
        }
    }

    /// <summary>Explicit full acquisition. Final validation is metadata-only; no engine is started or loaded.</summary>
    public async Task<TensorModelImportResult> ImportAsync(TensorModelImportRequest request,
        IProgress<TensorModelInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        var staged = new Dictionary<Uri, string>();
        try
        {
            Report(progress, new(TensorModelInstallStage.Checking));
            var discovery = await request.Resolver.DiscoverAsync(request.Selection, cancellationToken).ConfigureAwait(false);
            if (discovery.State != TensorSourceState.Available || discovery.Value is null)
            {
                Report(progress, new(TensorModelInstallStage.Failed));
                return new(new(new(TensorCompatibilityState.Unverified, discovery.Issues ?? [], [], discovery.State)));
            }
            TensorModelCandidate candidate = discovery.Value;
            if (string.IsNullOrWhiteSpace(candidate.License) || candidate.Artifacts.Any(item => string.IsNullOrWhiteSpace(item.Provenance.License)))
            {
                Report(progress, new(TensorModelInstallStage.Failed));
                return new(new(new(TensorCompatibilityState.Unverified,
                    [new("license-required", "Supply the actual license for each artifact before importing.")], [])));
            }
            var artifacts = new List<TensorSourceArtifact>();
            long completed = 0;
            long? total = candidate.Artifacts.All(item => item.Length.HasValue) ? candidate.Artifacts.Sum(item => item.Length!.Value) : null;
            foreach (TensorSourceArtifact artifact in candidate.Artifacts)
            {
                string path = Path.Combine(Path.GetTempPath(), $"darkspyre-tensor-import-{Guid.NewGuid():N}.download");
                // Every staging path is retained for cleanup, including a failed partial copy.
                if (!staged.TryAdd(artifact.DownloadUri, path))
                    throw new ArgumentException("A model import must not assign one source URI to multiple artifacts.", nameof(request));
                (long length, string hash) = await CopySourceAsync(artifact.DownloadUri, path, artifact.Path,
                    artifact.Length, artifact.Sha256, progress, completed, total, cancellationToken).ConfigureAwait(false);
                artifacts.Add(artifact with { Length = length, Sha256 = hash });
                completed = checked(completed + length);
            }
            candidate = candidate with { Artifacts = artifacts.ToArray() };
            TensorModelResolution resolution = await request.Resolver.ResolveCandidateAsync(candidate, request.Selection.Backend,
                new StagedReader(staged), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (resolution.Evaluation.State != TensorCompatibilityState.Supported || resolution.Definition is null)
            {
                Report(progress, new(TensorModelInstallStage.Failed));
                return new(resolution);
            }
            resolution = resolution with { Definition = Register(resolution.Definition) };
            // Reuse installation's hash checks, per-key serialization, lease protection and manifest-last publication.
            await InstallCoreAsync(resolution.Definition.Key, progress, staged, cancellationToken).ConfigureAwait(false);
            return new(resolution);
        }
        catch (OperationCanceledException) { Report(progress, new(TensorModelInstallStage.Cancelled)); throw; }
        catch { Report(progress, new(TensorModelInstallStage.Failed)); throw; }
        finally { foreach (string path in staged.Values) TryDelete(path); }
    }

    private sealed class StagedReader(IReadOnlyDictionary<Uri, string> staged) : ITensorArtifactReader
    {
        private readonly TensorArtifactReader _reader = new();
        public Task<TensorSourceResult<TensorMetadataContent>> ReadMetadataAsync(TensorSourceArtifact artifact,
            TensorMetadataRead read, CancellationToken cancellationToken = default) =>
            _reader.ReadMetadataAsync(artifact with
            {
                DownloadUri = new Uri(staged[artifact.DownloadUri]),
                Provenance = artifact.Provenance with { Repository = null, Revision = null }
            }, read, cancellationToken);
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
            await using Stream? content = await _directory.OpenReadAsync(
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

            IReadOnlyDictionary<string, string> artifacts = definition.Artifacts.ToDictionary(
                static artifact => artifact.Role,
                artifact => _directory.GetPath(ArtifactName(definition.Key, artifact.RelativePath)),
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

            await _directory.DeleteAsync(ManifestName(key), cancellationToken).ConfigureAwait(false);
            foreach (TensorModelArtifactDefinition artifact in definition.Artifacts)
            {
                await _directory.DeleteAsync(
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
        long completed,
        long total,
        IReadOnlyDictionary<Uri, string>? staged,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        foreach (Uri source in artifact.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Uri input = staged is null ? source : staged.TryGetValue(source, out string? local) ? new Uri(local)
                    : throw new InvalidDataException("The resolved artifact was not included in the staged import.");
                await CopySourceAsync(input, destination, artifact.Role, artifact.Length, artifact.Sha256,
                    progress, completed, total, cancellationToken).ConfigureAwait(false);

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
                lastFailure = exception is TensorSourceException ? exception : new IOException("The artifact could not be downloaded or failed integrity verification.");
                TryDelete(destination);
            }
        }

        throw new TensorModelPackException(
            key,
            $"No source could provide artifact '{artifact.Role}'.",
            lastFailure);
    }

    private async Task<(long Length, string Hash)> CopySourceAsync(Uri source, string destination, string role,
        long? expectedLength, string? expectedHash, IProgress<TensorModelInstallProgress>? progress,
        long completed, long? total, CancellationToken token)
    {
        await using Stream input = await OpenSourceAsync(source, token).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        long transferred = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if (expectedLength is long size && read > size - transferred) throw new InvalidDataException("The artifact exceeds its declared length.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            transferred = checked(transferred + read);
            Report(progress, new(TensorModelInstallStage.Downloading, role, transferred, expectedLength, completed + transferred, total));
        }
        await output.FlushAsync(token).ConfigureAwait(false);
        string actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (expectedLength is long length && transferred != length
            || expectedHash is not null && !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The artifact did not match its expected length or SHA-256.");
        return (transferred, actualHash);
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

        HttpResponseMessage response = await _sourceHttp.GetAsync(source, cancellationToken).ConfigureAwait(false);
        try
        {
            Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new ResponseStream(content, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static void Report(IProgress<TensorModelInstallProgress>? progress, TensorModelInstallProgress value)
    {
        // An observer cannot turn successful storage writes into a failed installation.
        try { progress?.Report(value); } catch { }
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
