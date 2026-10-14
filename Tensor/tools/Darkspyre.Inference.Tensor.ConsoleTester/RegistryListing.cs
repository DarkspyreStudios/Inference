using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Registry;
using Darkspyre.Persist;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Tensor.ConsoleTester;

/// <summary>
/// Lists the engines <see cref="ServiceCollectionExtensions.AddTensorEngines"/> registers and opens each
/// engine's settings through its provider, as a host does. It reads metadata only: it installs, loads
/// and saves nothing, writes no file and makes no network call.
/// </summary>
internal static class RegistryListing
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 3 || args[1] != "--store")
        {
            throw new ArgumentException("registry needs --store <dir>.");
        }

        string store = Path.GetFullPath(args[2]);
        await using ServiceProvider services = new ServiceCollection().AddTensorEngines().BuildServiceProvider();
        TensorEngineRegistry registry = services.GetRequiredService<TensorEngineRegistry>();
        Console.WriteLine($"store   : {store}");
        var modelStore = new OsUserPersist(store);
        foreach (TensorEngineDefinition definition in registry.Definitions)
        {
            if (!registry.TryGetProvider(definition.Setup.Implementation.Id, out ITensorEngineProvider? provider))
            {
                throw new InvalidOperationException($"The registry did not resolve '{definition.Setup.Implementation.Id}'.");
            }

            IReadOnlyList<TensorModelPackDefinition> installed = await provider.GetInstalledModelsAsync(modelStore, cancellationToken);
            TensorModelListing listing = await TensorInstalledPacks.ListAsync(modelStore, store, definition.ModelStoreKey,
                provider.BuiltInModels, installed, saved: null, cancellationToken);
            // One pack manager per engine kind, rooted at the engine's store key beneath the host's root.
            using var packs = new TensorModelPackManager(new TensorModelCatalog(listing.Models.Select(model => model.Definition).ToArray()),
                Path.Combine(store, definition.ModelStoreKey));
            TensorSettingsManager manager = provider.CreateSettingsManager(new ListingTarget(), packs, TensorModelList.Fixed(listing));
            TensorSettings settings = await manager.OpenAsync(new(SetupChangeIntent.Create, "registry-listing", null), cancellationToken: cancellationToken);

            Console.WriteLine();
            Console.WriteLine($"engine  : {definition.Setup.Implementation.Id} ({definition.Setup.Implementation.DisplayName})");
            Console.WriteLine($"  tasks   : {string.Join(", ", definition.Setup.Implementation.Tasks)}");
            Console.WriteLine($"  store   : {definition.ModelStoreKey}");
            Console.WriteLine($"  models  : {provider.BuiltInModels.Count} built in, {installed.Count} installed");
            foreach (TensorListedModel model in listing.Models)
            {
                string state = model.State switch
                {
                    SetupInstallState.Installed => "installed",
                    SetupInstallState.FilesMissing => $"files-missing ({model.MissingFile})",
                    _ => "not-downloaded"
                };
                Console.WriteLine($"    {state} {model.Definition.Key.ModelId} @ {model.Definition.Key.Revision}");
            }
            Console.WriteLine($"  setup   : {settings.Steps.Sum(step => step.Sections.Count)} sections");
            foreach (SetupActionDescriptor action in settings.Actions)
            {
                Console.WriteLine($"    {(action.IsEnabled ? "enabled " : "disabled")} {action.Id,-13} {action.Purpose}");
            }
        }

        return 0;
    }

    private sealed class ListingTarget : ISetupCommitTarget<TensorSetupChanges>
    {
        public Task<SetupCommitResult> CommitAsync(TensorSetupChanges changes, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SetupCommitResult(SetupCommitState.Invalid, Issues: [new("listing", "The registry listing never saves.")]));
    }
}
