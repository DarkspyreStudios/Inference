using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Persist;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>
/// The immutable description of one registered Tensor engine implementation. <see cref="Setup"/> is the
/// sole identity, display, task and prerequisite description. <see cref="ModelStoreKey"/> names the
/// engine's pack namespace as one portable path segment.
/// </summary>
public sealed class TensorEngineDefinition
{
    private const int MaximumStoreKeyLength = 64;
    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    /// <summary>Validates the setup definition and store key and snapshots the prerequisite list.</summary>
    /// <param name="setup">The engine's existing setup definition. It must describe a Tensor implementation.</param>
    /// <param name="modelStoreKey">
    /// A stable single path segment of at most 64 characters: ASCII letters, digits, '-', '_' and '.',
    /// starting with a letter or digit and not ending with '.'. Windows device names are rejected.
    /// </param>
    public TensorEngineDefinition(InferenceSetupDefinition setup, string modelStoreKey)
    {
        ArgumentNullException.ThrowIfNull(setup);
        if (setup.Implementation is null || setup.Implementation.Kind != InferenceImplementationKind.Tensor)
        {
            throw new ArgumentException("A Tensor engine requires a Tensor implementation descriptor.", nameof(setup));
        }
        if (setup.Prerequisites is null)
        {
            throw new ArgumentException("A Tensor engine setup definition requires a prerequisite list.", nameof(setup));
        }
        if (!IsValidStoreKey(modelStoreKey))
        {
            throw new ArgumentException("The model store key must be one portable path segment.", nameof(modelStoreKey));
        }

        Setup = setup with { Prerequisites = Array.AsReadOnly(setup.Prerequisites.ToArray()) };
        ModelStoreKey = modelStoreKey;
    }

    /// <summary>The engine's setup definition, with its prerequisite list copied into a read-only snapshot.</summary>
    public InferenceSetupDefinition Setup { get; }

    /// <summary>Opaque namespace metadata. The host joins it beneath its own approved storage root.</summary>
    public string ModelStoreKey { get; }

    private static bool IsValidStoreKey(string? key) =>
        key is { Length: > 0 and <= MaximumStoreKeyLength }
        && char.IsAsciiLetterOrDigit(key[0])
        && key[^1] != '.'
        && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
        && !ReservedDeviceNames.Contains(key.Split('.')[0], StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One registered Tensor engine implementation. <see cref="Definition"/> is pure metadata: reading it
/// constructs no engine, touches no native backend, opens no store and reads no credential.
/// </summary>
public interface ITensorEngineProvider
{
    /// <summary>The engine's immutable definition.</summary>
    TensorEngineDefinition Definition { get; }

    /// <summary>The family's built-in packs; empty when models arrive only by import. Touches no file and loads no native code.</summary>
    IReadOnlyList<TensorModelPackDefinition> BuiltInModels { get; }

    /// <summary>
    /// The models this family can load from <paramref name="modelStore"/>, read under its own
    /// <see cref="TensorEngineDefinition.ModelStoreKey"/>: its built-ins matched by key and artifact hashes, and other packs
    /// through its own format check, with every file present at its recorded length. Loads no native code.
    /// </summary>
    Task<IReadOnlyList<TensorModelPackDefinition>> GetInstalledModelsAsync(IPersist modelStore,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an instance over the host's pack manager. The instance implements the engine's typed
    /// capability interface. Creation does not Start, Load, download or run inference.
    /// </summary>
    ITensorModelInstance CreateInstance(ITensorModelPackManager packs, TensorBackendKind backend);

    /// <summary>
    /// Creates the engine's settings manager through its existing setup factory. The live instance is
    /// optional; a supplied instance must belong to this implementation and capability.
    /// A supplied backend probe replaces the local probe. Probe creation does not load native code;
    /// only the explicit backend-check action invokes it. The manager borrows the probe.
    /// <paramref name="models"/> is the host's live list of this family's models in the model store; the editor follows it.
    /// </summary>
    TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs,
        ITensorModelList models, ITensorModelInstance? instance = null,
        ITensorModelSource? source = null, ITensorArtifactReader? reader = null, ITensorBackendProbe? backendProbe = null);
}
