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
        foreach (string role in new[] { "model", "tokenizer" })
        {
            if (!definition.Artifacts.Any(artifact => artifact.Role == role))
            {
                throw new ArgumentException($"Laya pack '{definition.Key}' requires artifact role '{role}'.", nameof(definition));
            }
        }
        if (definition.SupportedBackends.Count == 0 || definition.SupportedBackends.Any(backend => backend is not (TensorBackendKind.Cpu or TensorBackendKind.Cuda)))
        {
            throw new ArgumentException("Laya pack backends must be CPU and/or CUDA.", nameof(definition));
        }
    }
}
