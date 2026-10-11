using System.Net;
using System.Net.Http.Headers;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>Bounded local/HTTP reads. The optional repository adapter retains its own pinned-source validation.</summary>
public sealed class TensorArtifactReader(ITensorModelSource? repository = null, HttpClient? httpClient = null,
    TensorSourceAccess? access = null) : ITensorArtifactReader
{
    private static readonly HttpClient SharedClient = TensorSourceHttp.CreateClient();
    private readonly TensorSourceHttp _http = new(httpClient ?? SharedClient, access);

    /// <inheritdoc />
    public async Task<TensorSourceResult<TensorMetadataContent>> ReadMetadataAsync(TensorSourceArtifact artifact,
        TensorMetadataRead read, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read.Offset < 0 || read.Length is < 1 or > 16 * 1024 * 1024 || read.Offset > long.MaxValue - read.Length
                || !Enum.IsDefined(read.Kind) || (read.Kind == TensorMetadataReadKind.CompleteMetadataFile
                    && (read.Offset != 0 || read.Length > 4 * 1024 * 1024)))
                throw new ArgumentException("Invalid metadata range.");
            if (artifact.Provenance.Repository is not null && repository is not null)
                return await repository.ReadMetadataAsync(artifact with { Path = artifact.Provenance.Path }, read, cancellationToken).ConfigureAwait(false);
            if (artifact.DownloadUri.IsFile)
            {
                await using var file = new FileStream(artifact.DownloadUri.LocalPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
                if (read.Offset > file.Length || (artifact.Length is long expected && expected != file.Length))
                    throw new TensorSourceException(TensorSourceState.Unverified, "source-changed", "The artifact size changed after discovery.");
                file.Position = read.Offset;
                byte[] bytes = new byte[(int)Math.Min(read.Length, file.Length - read.Offset)];
                await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (read.Kind == TensorMetadataReadKind.CompleteMetadataFile && bytes.LongLength != file.Length)
                    throw new TensorSourceException(TensorSourceState.Unverified, "metadata-size", "The metadata file exceeds its inspection bound.");
                return new(TensorSourceState.Available, new(bytes, read.Offset, file.Length));
            }
            using HttpResponseMessage response = await _http.GetAsync(artifact.DownloadUri, cancellationToken,
                new RangeHeaderValue(read.Offset, read.Offset + read.Length - 1)).ConfigureAwait(false);
            ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
            bool whole = response.StatusCode == HttpStatusCode.OK && read.Kind == TensorMetadataReadKind.CompleteMetadataFile;
            if (response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength > read.Length
                || (!whole && (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes"
                    || range.From != read.Offset || range.To is null || range.To < range.From
                    || range.To >= read.Offset + read.Length || range.Length is null || range.Length <= range.To)))
                throw new TensorSourceException(TensorSourceState.Unverified, "metadata-range", "The source did not honor the bounded metadata request. Explicit import is available.");
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            byte[] buffer = new byte[read.Length + 1];
            int count = 0, received;
            while (count < buffer.Length && (received = await input.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false)) != 0)
                count += received;
            long total = whole ? count : range!.Length!.Value;
            if (count > read.Length || (!whole && count != range!.To - range.From + 1)
                || (artifact.Length is long length && length != total)
                || (read.Kind == TensorMetadataReadKind.CompleteMetadataFile && count != total))
                throw new TensorSourceException(TensorSourceState.Unverified, "metadata-length", "The source returned inconsistent or incomplete metadata.");
            return new(TensorSourceState.Available, new(buffer.AsMemory(0, count), read.Offset, total));
        }
        catch (TensorSourceException exception) { return new(exception.State, Issues: [new(exception.Code, exception.Message)]); }
        catch (UnauthorizedAccessException) { return new(TensorSourceState.AccessDenied, Issues: [new("source-access", "The host does not have access to this location.")]); }
        catch (FileNotFoundException) { return new(TensorSourceState.NotFound, Issues: [new("source-missing", "The selected artifact is missing.")]); }
        catch (ArgumentException) { return new(TensorSourceState.InvalidRequest, Issues: [new("source-input", "The artifact location or inspection range is invalid.")]); }
        catch (IOException) { return new(TensorSourceState.Unverified, Issues: [new("source-read", "The artifact could not be inspected.")]); }
    }
}
