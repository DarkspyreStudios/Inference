using Darkspyre.Inference.Tensor.Common;
using TensorSharp;
using TensorSharp.Cuda;
using TensorSharp.MLX;
using Darkspyre.Inference.Tensor.Ggml;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

internal sealed class QwenBackendProbe(GgmlRuntimeOwner? runtimeOwner = null) : ITensorBackendProbe
{
    // Loading a native library or runtime to answer is synchronous work.
    public Task<TensorBackendAvailability> CheckAsync(TensorBackendProbeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (TensorBackendProbes.ValidateLocalRequest(request, QwenSetup.Definition.Implementation.Id) is { } invalid)
            return Task.FromResult(invalid);
        if (GgmlRuntimeOwner.IsGgml(request.RequestedBackend))
            return (runtimeOwner ?? GgmlRuntimeOwner.GetOrCreate()).CheckAsync(request, cancellationToken);
        return Task.Run(() =>
        {
            using IDisposable? admission = request.RequestedBackend == TensorBackendKind.Mlx
                ? (runtimeOwner ?? GgmlRuntimeOwner.GetOrCreate()).EnterMlx(MlxBackend.Shutdown) : null;
            return Check(request, IsBackendAvailable,
                static error => NativeRuntimeQuarantine.TryGetFailure(error, out _), cancellationToken);
        }, cancellationToken);
    }

    internal static TensorBackendAvailability Check(TensorBackendProbeRequest request,
        Func<TensorBackendKind, bool> isBackendAvailable, Func<Exception, bool> isRecordedFailure, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TensorBackendProbes.ValidateLocalRequest(request, QwenSetup.Definition.Implementation.Id) is { } invalid) return invalid;
        TensorBackendKind actual = request.RequestedBackend;
        if (actual == TensorBackendKind.CoreMl
            || !OperatingSystem.IsMacOS() && actual is TensorBackendKind.Mlx or TensorBackendKind.GgmlMetal
            || OperatingSystem.IsMacOS() && actual is TensorBackendKind.Cuda or TensorBackendKind.GgmlCuda or TensorBackendKind.GgmlVulkan)
            return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unsupported, "This backend is unsupported by Qwen on the target platform.");
        if (actual != TensorBackendKind.Cpu && TensorBackendProbes.RejectUnboundNativePlan(request) is { } unbound) return unbound;
        bool? available;
        try
        {
            available = isBackendAvailable(actual);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && isRecordedFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            const string reason = "Native resource cleanup failed. Restart the application process before using this runtime.";
            return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.RequiresProcessRestart, reason,
                candidateDiagnostics: [new(null, TensorBackendProbePhase.Initialization, "runtime-requires-process-exit", reason,
                    NativeStateMayRemain: true)]);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException)
        { available = false; }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        { available = null; }
        cancellationToken.ThrowIfCancellationRequested();
        return TensorBackendAvailability.Observe(request, available switch
        {
            true => TensorBackendAvailabilityState.Available,
            false => TensorBackendAvailabilityState.Unavailable,
            null => TensorBackendAvailabilityState.Unknown
        },
            available == true ? $"{actual} runtime prerequisites are available. Model allocation is not guaranteed."
                : available == false ? $"{actual} runtime/device prerequisites are unavailable." : $"{actual} availability is unknown.",
            actual is TensorBackendKind.Cpu or TensorBackendKind.Cuda ? [new(actual, available)] : []);
    }

    private static bool IsBackendAvailable(TensorBackendKind backend) => backend switch
    {
        TensorBackendKind.Cpu => true,
        TensorBackendKind.Cuda => CudaBackend.IsAvailable(),
        TensorBackendKind.Mlx => MlxBackend.IsAvailable(),
        _ => false
    };
}
