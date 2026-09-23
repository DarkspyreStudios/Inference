using Darkspyre.Inference.Tensor.Common;
using Whisper.net;

namespace Darkspyre.Inference.Tensor.STT.Whisper;

internal sealed record WhisperTranscript(IReadOnlyList<SpeechSegment> Segments, string? Language);

internal interface IWhisperModel : IAsyncDisposable
{
    WhisperModelMetadata Metadata { get; }
    Task<WhisperTranscript> TranscribeAsync(float[] samples, string language, bool translate, CancellationToken token);
}

internal interface IWhisperLoader
{
    Task<IWhisperModel> LoadAsync(TensorModelPackDefinition definition, TensorModelPackLease lease, CancellationToken token);
}

internal sealed class WhisperLoader : IWhisperLoader
{
    public async Task<IWhisperModel> LoadAsync(TensorModelPackDefinition definition, TensorModelPackLease lease, CancellationToken token)
    {
        WhisperModelFile file = await WhisperModelFile.OpenAsync(lease.GetRequiredArtifact("model"), definition.Artifacts[0].Length, token).ConfigureAwait(false);
        WhisperFactory? factory = null;
        try
        {
            // Do not abandon native initialization on cancellation: wait, then dispose the completed allocation.
            factory = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return WhisperFactory.FromPath(file.Path, new WhisperFactoryOptions { UseGpu = false, DelayInitialization = false });
            }, CancellationToken.None).ConfigureAwait(false);
            _ = factory.CreateBuilder(); // FromPath may return a factory with a null native context; force validation during Load.
            token.ThrowIfCancellationRequested();
            return new Model(factory, file);
        }
        catch
        {
            factory?.Dispose();
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Model(WhisperFactory factory, WhisperModelFile file) : IWhisperModel
    {
        public WhisperModelMetadata Metadata => file.Metadata;
        public async Task<WhisperTranscript> TranscribeAsync(float[] samples, string language, bool translate, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var builder = factory.CreateBuilder().WithLanguage(language);
            if (translate) builder.WithTranslate();
            // Async disposal waits for native processing even when ProcessAsync observes cancellation first.
            await using WhisperProcessor processor = builder.Build();
            var segments = new List<SpeechSegment>();
            string? detected = null;
            await foreach (SegmentData segment in processor.ProcessAsync(samples, token).ConfigureAwait(false))
            {
                segments.Add(new(segment.Text.Trim(), segment.Start, segment.End));
                if (!string.IsNullOrWhiteSpace(segment.Language)) detected ??= segment.Language;
            }
            token.ThrowIfCancellationRequested();
            return new(segments.AsReadOnly(), detected);
        }
        public async ValueTask DisposeAsync()
        {
            try { factory.Dispose(); }
            finally { await file.DisposeAsync().ConfigureAwait(false); }
        }
    }
}

internal sealed class WhisperBackendProbe(Func<string?>? runtimeInfo = null) : ITensorBackendProbe
{
    public Task<TensorBackendAvailability> CheckAsync(TensorBackendKind backend, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (backend is not (TensorBackendKind.Cpu or TensorBackendKind.Auto))
                return new TensorBackendAvailability(backend, false, "This adapter supplies CPU inference only.", DateTimeOffset.UtcNow);
            bool? available;
            string reason;
            try
            {
                available = !string.IsNullOrWhiteSpace((runtimeInfo ?? WhisperFactory.GetRuntimeInfo)());
                reason = available == true ? "The Whisper native runtime is callable. CPU model allocation is not guaranteed."
                    : "The Whisper native runtime returned no availability information.";
                if (available == false) available = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            { available = false; reason = "The Whisper native runtime could not be initialized on this machine."; }
            cancellationToken.ThrowIfCancellationRequested();
            return new TensorBackendAvailability(backend, available, reason, DateTimeOffset.UtcNow);
        }, cancellationToken);
}
