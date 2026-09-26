namespace Darkspyre.Inference.Tensor.STT.Whisper;

/// <summary>
/// The installed Whisper weights, held open while Whisper.net loads them from <see cref="Path"/>.
/// </summary>
internal sealed class WhisperModelFile : IAsyncDisposable
{
    private readonly FileStream _source;
    private bool _disposed;
    public string Path { get; }
    public WhisperModelMetadata Metadata { get; }

    private WhisperModelFile(FileStream source, WhisperModelMetadata metadata)
    { _source = source; Path = source.Name; Metadata = metadata; }

    public static async Task<WhisperModelFile> OpenAsync(string path, long expectedLength, CancellationToken token)
    {
        if (!File.Exists(path)) throw new InvalidDataException("The installed Whisper artifact is missing.");
        var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (source.Length != expectedLength) throw new InvalidDataException("The Whisper artifact length changed.");
            WhisperModelMetadata metadata = WhisperCompatibility.Inspect(source);
            token.ThrowIfCancellationRequested();
            return new(source, metadata);
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _source.DisposeAsync().ConfigureAwait(false);
    }
}
