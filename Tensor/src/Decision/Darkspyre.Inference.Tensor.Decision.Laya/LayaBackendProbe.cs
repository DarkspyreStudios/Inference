using Darkspyre.Inference.Tensor.Common;
using Microsoft.ML.OnnxRuntime;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

internal sealed class LayaBackendProbe : ITensorBackendProbe
{
    private const string CoreMlProvider = "CoreMLExecutionProvider";

    public Task<TensorBackendAvailability> CheckAsync(TensorBackendKind backend, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TensorBackendKind actual = LayaDecisionInference.ResolveBackend(backend);
        bool? available;
        try
        {
            available = actual switch
            {
                TensorBackendKind.Cpu => true,
                TensorBackendKind.CoreMl => OrtEnv.Instance().GetAvailableProviders().Contains(CoreMlProvider, StringComparer.Ordinal),
                _ => false
            };
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or PlatformNotSupportedException or TypeInitializationException or OnnxRuntimeException)
        { available = false; }
        return Task.FromResult(new TensorBackendAvailability(backend, available,
            available == true ? $"{actual} runtime prerequisites are available. Model allocation is not guaranteed."
                : $"{actual} runtime/device prerequisites are unavailable.", DateTimeOffset.UtcNow));
    }
}
