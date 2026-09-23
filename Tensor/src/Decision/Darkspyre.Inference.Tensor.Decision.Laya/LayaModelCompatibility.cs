using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

internal static class LayaModelCompatibility
{
    public static void Validate(TensorModelPackDefinition definition)
    {
        TensorModelDefinitionValidator.Validate(definition);
        if (definition.Capability != TensorCapability.Decision)
        {
            throw new ArgumentException($"Laya requires capability '{TensorCapability.Decision}', not '{definition.Capability}'.", nameof(definition));
        }
        if (definition.RuntimeArchitecture != TensorDecisionModels.LayaRuntimeArchitecture)
        {
            throw new ArgumentException($"Laya requires architecture '{TensorDecisionModels.LayaRuntimeArchitecture}', not '{definition.RuntimeArchitecture}'.", nameof(definition));
        }
        foreach (string role in new[] { "model", "tokenizer", "agent-config" })
        {
            if (!definition.Artifacts.Any(artifact => artifact.Role == role))
            {
                throw new ArgumentException($"Laya pack '{definition.Key}' requires artifact role '{role}'.", nameof(definition));
            }
        }
        if (definition.SupportedBackends.Contains(TensorBackendKind.Cuda))
        {
            throw new ArgumentException("Laya pack backends cannot include CUDA: ONNX CUDA execution is not yet available.", nameof(definition));
        }
        if (definition.SupportedBackends.Count == 0 || definition.SupportedBackends.Any(backend => backend is not (TensorBackendKind.CoreMl or TensorBackendKind.Cpu)))
        {
            throw new ArgumentException("Laya pack backends must be Core ML and/or CPU.", nameof(definition));
        }
    }
}
