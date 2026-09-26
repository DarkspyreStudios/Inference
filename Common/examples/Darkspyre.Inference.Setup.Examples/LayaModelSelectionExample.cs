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
    public static async Task<SetupDraft> CreateAndRenderAsync(TensorModelCatalog catalog, ITensorModelPackManager packs,
        ISetupCommitTarget<TensorSetupChanges> target, ISetupEditorControls controls, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        TensorModelPackDefinition[] offered = catalog.Definitions.Where(definition =>
            definition.Capability == TensorCapability.Decision && definition.RuntimeArchitecture == TensorDecisionModels.LayaRuntimeArchitecture).ToArray();
        TensorSettingsManager manager = LayaSetup.CreateSettingsManager(target, packs, offered);
        TensorSettings settings = await manager.OpenAsync(new(SetupChangeIntent.Create, Guid.NewGuid().ToString("N"), null),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var draft = new SetupDraft(manager, settings);
        SetupEditorExample.Render(draft, controls);
        return draft;
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
        await AcceptAndWaitAsync(engine, () => engine.LoadAsync(first, cancellationToken), TensorEngineState.Loaded, cancellationToken).ConfigureAwait(false);
        await engine.DecideAsync(request with { ModelId = first.ModelId }, cancellationToken).ConfigureAwait(false);
        await AcceptAndWaitAsync(engine, () => engine.UnloadAsync(CancellationToken.None), TensorEngineState.Unloaded, cancellationToken).ConfigureAwait(false);
        await AcceptAndWaitAsync(engine, () => engine.LoadAsync(second, cancellationToken), TensorEngineState.Loaded, cancellationToken).ConfigureAwait(false);
        return await engine.DecideAsync(request with { ModelId = second.ModelId }, cancellationToken).ConfigureAwait(false);
        // await using performs terminal Stop after the final request (also on error).
    }

    /// <summary>Lifecycle calls return once accepted; the outcome arrives through EngineStatusChanged.</summary>
    private static async Task AcceptAndWaitAsync(ITensorModelInstance engine, Func<Task> accept, TensorEngineState target,
        CancellationToken cancellationToken)
    {
        var outcome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatus(object? sender, TensorEngineStatus status)
        {
            if (status.State == target) { outcome.TrySetResult(); }
            else if (status.State == TensorEngineState.Failed) { outcome.TrySetException(status.Error!); }
        }

        engine.EngineStatusChanged += OnStatus;
        try
        {
            await accept().ConfigureAwait(false);
            await outcome.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            engine.EngineStatusChanged -= OnStatus;
        }
    }
}
