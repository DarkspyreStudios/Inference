using Darkspyre.Inference.Tensor.Common;
using Microsoft.ML.OnnxRuntime;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

internal sealed class LayaBackendProbe : ITensorBackendProbe
{
    private const string CoreMlProvider = "CoreMLExecutionProvider";

    // Loading a native library or runtime to answer is synchronous work.
    public Task<TensorBackendAvailability> CheckAsync(TensorBackendProbeRequest request, CancellationToken cancellationToken = default) =>
        Task.Run(() => Check(request, cancellationToken), cancellationToken);

    private static TensorBackendAvailability Check(TensorBackendProbeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TensorBackendProbes.ValidateLocalRequest(request, LayaSetup.Definition.Implementation.Id) is { } invalid) return invalid;
        TensorBackendKind actual = LayaDecisionInference.ResolveBackend(request.RequestedBackend);
        if (actual is not (TensorBackendKind.Cpu or TensorBackendKind.CoreMl) || actual == TensorBackendKind.CoreMl && !OperatingSystem.IsMacOS())
            return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unsupported, "This backend is unsupported by Laya on the target platform.");
        if (TensorBackendProbes.RejectUnboundNativePlan(request) is { } unbound) return unbound;
        bool? available;
        try
        {
            available = actual switch
            {
                TensorBackendKind.Cpu => OrtEnv.Instance().GetAvailableProviders().Contains("CPUExecutionProvider", StringComparer.Ordinal),
                TensorBackendKind.CoreMl => OrtEnv.Instance().GetAvailableProviders().Contains(CoreMlProvider, StringComparer.Ordinal),
                _ => false
            };
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or PlatformNotSupportedException or TypeInitializationException or OnnxRuntimeException)
        { available = false; }
        cancellationToken.ThrowIfCancellationRequested();
        return TensorBackendAvailability.Observe(request, available == true ? TensorBackendAvailabilityState.Available : TensorBackendAvailabilityState.Unavailable,
            available == true ? $"{actual} runtime prerequisites are available. Model allocation is not guaranteed."
                : $"{actual} runtime/device prerequisites are unavailable.");
    }
}
