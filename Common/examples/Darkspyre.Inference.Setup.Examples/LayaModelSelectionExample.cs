using Darkspyre.Inference.Common.Decision;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Decision.Laya;

namespace Darkspyre.Inference.Setup.Examples;

public static class LayaModelSelectionExample
{
    /// <summary>
    /// Host composition supplies its own catalog/manager, including private or fine-tuned packs.
    /// The same generic editor renders Laya without any provider-specific controls.
    /// </summary>
    public static TensorSetupSession CreateAndRender(TensorModelCatalog catalog, ITensorModelPackManager packs,
        ISetupCommitTarget<TensorSetupChanges> target, ISetupEditorControls controls)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        TensorModelPackDefinition[] offered = catalog.Definitions.Where(definition =>
            definition.Capability == TensorCapability.Decision && definition.RuntimeArchitecture == TensorDecisionModels.LayaRuntimeArchitecture).ToArray();
        TensorSetupSession setup = LayaSetup.CreateSession(new(SetupChangeIntent.Create, Guid.NewGuid().ToString("N"), null), target, packs, offered);
        SetupEditorExample.Render(setup, controls);
        return setup; // The host owns the session and disposes it after finishing/cancelling the edit.
    }

    /// <summary>The host has explicitly installed both chosen packs before calling this example.</summary>
    public static async Task<DecisionInferenceResult> SwitchModelsAsync(ITensorModelPackManager packs,
        TensorModelKey first, TensorModelKey second, DecisionInferenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(request);
        await using var engine = new LayaDecisionInference(packs, TensorBackendKind.Cpu);
        await engine.StartAsync(cancellationToken).ConfigureAwait(false);
        await engine.LoadAsync(first, cancellationToken).ConfigureAwait(false);
        await engine.DecideAsync(request with { ModelId = first.ModelId }, cancellationToken).ConfigureAwait(false);
        await engine.UnloadAsync(CancellationToken.None).ConfigureAwait(false);
        await engine.LoadAsync(second, cancellationToken).ConfigureAwait(false);
        return await engine.DecideAsync(request with { ModelId = second.ModelId }, cancellationToken).ConfigureAwait(false);
        // await using performs terminal Stop after the final request (also on error).
    }
}
