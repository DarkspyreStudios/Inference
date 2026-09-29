using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Registry;

/// <summary>
/// The registered Tensor engine implementations. It reads each provider's definition once, validates
/// the set and derives every view at construction. It owns registrations and derived metadata only:
/// it holds no pack manager, store, credential or live instance.
/// </summary>
public sealed class TensorEngineRegistry
{
    private readonly FrozenDictionary<string, ITensorEngineProvider> _providers;
    private readonly FrozenDictionary<InferenceTask, IReadOnlyList<TensorEngineDefinition>> _byTask;

    /// <exception cref="ArgumentException">
    /// A provider or its definition is missing, two providers share an implementation ID, or two store
    /// keys are equal ignoring case.
    /// </exception>
    public TensorEngineRegistry(IEnumerable<ITensorEngineProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var byId = new Dictionary<string, ITensorEngineProvider>(StringComparer.Ordinal);
        var storeKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var definitions = new List<TensorEngineDefinition>();
        foreach (ITensorEngineProvider? provider in providers)
        {
            if (provider?.Definition is not { } definition)
            {
                throw new ArgumentException("Every registered Tensor engine provider requires a definition.", nameof(providers));
            }
            string id = definition.Setup.Implementation.Id;
            if (!byId.TryAdd(id, provider))
            {
                throw new ArgumentException($"Multiple Tensor engine providers are registered for implementation '{id}'.", nameof(providers));
            }
            if (!storeKeys.TryAdd(definition.ModelStoreKey, id))
            {
                throw new ArgumentException($"Tensor engines '{storeKeys[definition.ModelStoreKey]}' and '{id}' use colliding model store keys.",
                    nameof(providers));
            }
            definitions.Add(definition);
        }

        Definitions = Array.AsReadOnly(definitions
            .OrderBy(definition => definition.Setup.DisplayOrder)
            .ThenBy(definition => definition.Setup.Implementation.Id, StringComparer.Ordinal)
            .ToArray());
        _providers = byId.ToFrozenDictionary(StringComparer.Ordinal);
        _byTask = Enum.GetValues<InferenceTask>().ToFrozenDictionary(task => task, task => (IReadOnlyList<TensorEngineDefinition>)
            Array.AsReadOnly(Definitions.Where(definition => definition.Setup.Implementation.Tasks.Contains(task)).ToArray()));
    }

    /// <summary>Every registered definition, ordered by display order and then ordinal implementation ID.</summary>
    public IReadOnlyList<TensorEngineDefinition> Definitions { get; }

    /// <summary>The registered definitions that declare <paramref name="task"/>, in <see cref="Definitions"/> order.</summary>
    public IReadOnlyList<TensorEngineDefinition> GetDefinitions(InferenceTask task) =>
        _byTask.TryGetValue(task, out IReadOnlyList<TensorEngineDefinition>? definitions)
            ? definitions
            : throw new ArgumentOutOfRangeException(nameof(task), task, "Unknown inference task.");

    /// <summary>
    /// Resolves the provider registered for the exact, case-sensitive implementation ID. A missing
    /// provider returns false; the caller keeps its saved record and reports it unavailable.
    /// </summary>
    public bool TryGetProvider(string implementationId, [NotNullWhen(true)] out ITensorEngineProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(implementationId);
        return _providers.TryGetValue(implementationId, out provider);
    }
}
