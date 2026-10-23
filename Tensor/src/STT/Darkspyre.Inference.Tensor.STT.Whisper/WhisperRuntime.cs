using System.Globalization;
using Darkspyre.Inference.Tensor.Common;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace Darkspyre.Inference.Tensor.STT.Whisper;

internal sealed record WhisperTranscript(IReadOnlyList<SpeechSegment> Segments, string? Language);

internal interface IWhisperModel : IAsyncDisposable
{
    WhisperModelMetadata Metadata { get; }
    TensorBackendKind Backend { get; }
    Task<WhisperTranscript> TranscribeAsync(float[] samples, string language, bool translate, CancellationToken token);
}

internal interface IWhisperLoader
{
    Task<IWhisperModel> LoadAsync(TensorModelPackDefinition definition, TensorModelPackLease lease,
        TensorBackendKind backend, int gpuDevice, CancellationToken token);
}

internal sealed class WhisperLoader : IWhisperLoader
{
    public async Task<IWhisperModel> LoadAsync(TensorModelPackDefinition definition, TensorModelPackLease lease,
        TensorBackendKind backend, int gpuDevice, CancellationToken token)
    {
        WhisperModelFile file = await WhisperModelFile.OpenAsync(lease.GetRequiredArtifact("model"), definition.Artifacts[0].Length, token).ConfigureAwait(false);
        WhisperFactory? factory = null;
        try
        {
            // Join native initialization before cancellation can release the installed model file.
            WhisperFactoryAllocation allocation = await Task.Run(() => WhisperRuntime.Load(file.Path, backend, gpuDevice, token), CancellationToken.None).ConfigureAwait(false);
            factory = allocation.Factory;
            token.ThrowIfCancellationRequested();
            return new Model(factory, file, allocation.Backend);
        }
        catch
        {
            factory?.Dispose();
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Model(WhisperFactory factory, WhisperModelFile file, TensorBackendKind backend) : IWhisperModel
    {
        public WhisperModelMetadata Metadata => file.Metadata;
        public TensorBackendKind Backend => backend;
        public async Task<WhisperTranscript> TranscribeAsync(float[] samples, string language, bool translate, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var builder = factory.CreateBuilder().WithLanguage(language);
            if (translate) builder.WithTranslate();
            // Async disposal joins native processing before the request releases its model lease.
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

internal sealed record WhisperFactoryAllocation(WhisperFactory Factory, TensorBackendKind Backend);
internal sealed record WhisperRuntimeState(string? Info, RuntimeLibrary? Library);

/// <summary>Serializes the process-wide Whisper.net native selection and model initialization observations.</summary>
internal static class WhisperRuntime
{
    private static readonly Lock Gate = new();

    internal static WhisperRuntimeState Inspect()
    {
        lock (Gate) return InspectCore();
    }

    private static WhisperRuntimeState InspectCore()
    {
        // This precedes every first native call, including a settings probe. Cpu remains an execution choice.
        // The trailing Cpu entry prevents Whisper.net's special last-CUDA-entry CPU acceptance rule.
        if (RuntimeOptions.LoadedLibrary is null) RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda12, RuntimeLibrary.Cpu];
        string? info = WhisperFactory.GetRuntimeInfo();
        return new(info, RuntimeOptions.LoadedLibrary);
    }

    internal static WhisperFactoryAllocation Load(string path, TensorBackendKind backend, int gpuDevice, CancellationToken token)
    {
        lock (Gate)
        {
            token.ThrowIfCancellationRequested();
            WhisperRuntimeState runtime = InspectCore();
            bool useGpu = backend != TensorBackendKind.Cpu && runtime.Library == RuntimeLibrary.Cuda12;
            return Allocate(backend == TensorBackendKind.Auto, useGpu, gpu =>
            {
                token.ThrowIfCancellationRequested();
                var observation = new WhisperBackendObservation(gpuDevice);
                using IDisposable observer = LogProvider.AddLogger((_, message) => observation.Observe(message));
                WhisperFactory? factory = null;
                try
                {
                    factory = WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = gpu, GpuDevice = gpuDevice, DelayInitialization = false });
                    using var processor = factory.CreateBuilder().Build();
                    // Whisper.net loads weights without execution state. One second of silence initializes
                    // that state under this observer before Loaded publishes its actual backend.
                    if (processor.DetectLanguage(new float[WhisperPcm.TargetRate]) is null)
                        throw new WhisperModelLoadException("Whisper could not initialize an executable model.");
                    token.ThrowIfCancellationRequested();
                    return new WhisperFactoryAllocation(factory, gpu ? observation.Backend : TensorBackendKind.Cpu);
                }
                catch { factory?.Dispose(); throw; }
            });
        }
    }

    // Only automatic GPU allocation has a second CPU attempt. A successful CPU allocation stays resident.
    internal static T Allocate<T>(bool allowCpuFallback, bool useGpu, Func<bool, T> allocate)
    {
        try { return allocate(useGpu); }
        catch (Exception exception) when (allowCpuFallback && useGpu && exception is WhisperModelLoadException or OutOfMemoryException)
        { return allocate(false); }
    }
}

/// <summary>Observes the pinned whisper.cpp initialization outcome without persisting native log contents.</summary>
internal sealed class WhisperBackendObservation(int gpuDevice)
{
    private readonly string selected = "whisper_backend_init_gpu: using CUDA" + gpuDevice.ToString(CultureInfo.InvariantCulture) + " backend";
    private int gpuSelected;
    private int gpuFailed;
    internal TensorBackendKind Backend => Volatile.Read(ref gpuSelected) != 0 && Volatile.Read(ref gpuFailed) == 0
        ? TensorBackendKind.GgmlCuda : TensorBackendKind.Cpu;
    internal void Observe(string? message)
    {
        if (message is null) return;
        // Whisper.net 1.9.1 pins whisper.cpp f24588a272ae8e23280d9c220536437164e6ed28.
        if (message.Contains(selected, StringComparison.Ordinal)) Volatile.Write(ref gpuSelected, 1);
        if (message.Contains("whisper_backend_init_gpu: failed to initialize", StringComparison.Ordinal)) Volatile.Write(ref gpuFailed, 1);
    }
}

internal sealed class WhisperBackendProbe(Func<WhisperRuntimeState>? runtimeInfo = null) : ITensorBackendProbe
{
    public Task<TensorBackendAvailability> CheckAsync(TensorBackendProbeRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TensorBackendProbes.ValidateLocalRequest(request, WhisperSetup.Definition.Implementation.Id) is { } invalid) return invalid;
            TensorBackendKind backend = request.RequestedBackend;
            if (backend is not (TensorBackendKind.Cpu or TensorBackendKind.Auto or TensorBackendKind.GgmlCuda))
                return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unsupported, "This adapter supports CPU and GGML CUDA execution.");
            if (TensorBackendProbes.RejectUnboundNativePlan(request) is { } unbound) return unbound;
            bool? available;
            string reason;
            try
            {
                WhisperRuntimeState runtime = (runtimeInfo ?? WhisperRuntime.Inspect)();
                available = string.IsNullOrWhiteSpace(runtime.Info) ? null : backend != TensorBackendKind.GgmlCuda || runtime.Library == RuntimeLibrary.Cuda12;
                reason = available switch
                {
                    null => "The Whisper native runtime returned no availability information.",
                    false => "The CUDA 12 Whisper runtime is unavailable. Automatic selection permits CPU execution.",
                    _ when backend == TensorBackendKind.Cpu => "The Whisper native runtime is callable for CPU execution. Model allocation is not guaranteed.",
                    _ when runtime.Library == RuntimeLibrary.Cuda12 => "The CUDA 12 Whisper runtime is callable. GPU model allocation is not guaranteed; automatic selection permits CPU fallback.",
                    _ => "Automatic Whisper selection uses the callable CPU runtime. Model allocation is not guaranteed."
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            { available = false; reason = $"The Whisper native runtime could not be initialized on this machine ({exception.GetType().Name}): {exception.Message}"; }
            cancellationToken.ThrowIfCancellationRequested();
            return TensorBackendAvailability.Observe(request, available switch
            {
                true => TensorBackendAvailabilityState.Available,
                false => TensorBackendAvailabilityState.Unavailable,
                null => TensorBackendAvailabilityState.Unknown
            }, reason);
        }, cancellationToken);
}
