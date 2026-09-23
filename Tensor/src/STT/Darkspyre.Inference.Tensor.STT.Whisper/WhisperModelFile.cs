using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.STT.Whisper;

/// <summary>Native adapter only. The host continues to address weights through its persistence store.</summary>
internal sealed class WhisperModelFile : IAsyncDisposable
{
    private readonly Stream _source;
    private readonly string? _temporaryDirectory;
    private bool _disposed;
    public string Path { get; }
    public WhisperModelMetadata Metadata { get; }

    private WhisperModelFile(Stream source, string path, string? directory, WhisperModelMetadata metadata)
    { _source = source; Path = path; _temporaryDirectory = directory; Metadata = metadata; }

    public static async Task<WhisperModelFile> OpenAsync(PersistenceFileReference reference, long expectedLength, CancellationToken token)
    {
        Stream source = await reference.Store.OpenReadAsync(reference.StoreName, reference.AssetName, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The installed Whisper artifact is missing.");
        string? directory = null;
        string? path = null;
        try
        {
            if (source is FileStream file)
            {
                if (file.Length != expectedLength) throw new InvalidDataException("The Whisper artifact length changed.");
                file.Position = 0;
                WhisperModelMetadata metadata = WhisperCompatibility.Inspect(file);
                token.ThrowIfCancellationRequested();
                return new(source, file.Name, null, metadata);
            }
            directory = Directory.CreateTempSubdirectory("darkspyre-whisper-").FullName;
            path = System.IO.Path.Combine(directory, "model.bin");
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var target = new FileStream(path, options))
            {
                byte[] buffer = new byte[65536];
                long copied = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    copied = checked(copied + count);
                    if (copied > expectedLength) throw new InvalidDataException("The Whisper artifact exceeds its declared length.");
                    await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
                if (copied != expectedLength) throw new InvalidDataException("The Whisper artifact is truncated.");
            }
            using var header = File.OpenRead(path);
            WhisperModelMetadata parsed = WhisperCompatibility.Inspect(header);
            token.ThrowIfCancellationRequested();
            return new(source, path, directory, parsed);
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            if (path is not null && File.Exists(path)) File.Delete(path);
            if (directory is not null) Directory.Delete(directory);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await _source.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            if (_temporaryDirectory is not null)
            {
                File.Delete(Path);
                Directory.Delete(_temporaryDirectory);
            }
        }
    }
}
