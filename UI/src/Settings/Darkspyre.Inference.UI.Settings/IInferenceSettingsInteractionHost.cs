using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.UI.Settings;

/// <summary>Host-owned consent, external interaction, and operation lifetime for a setup editor.</summary>
public interface IInferenceSettingsInteractionHost
{
    /// <summary>Asks whether the editor may run <paramref name="operation"/> with the given side effects; true allows it.</summary>
    ValueTask<bool> AuthorizeAsync(string operation, SetupSideEffect sideEffects,
        CancellationToken cancellationToken = default);

    /// <summary>Runs one action; a host without extra policy returns <c>manager.RunAsync(settings, action.Id, progress, cancellationToken)</c>.</summary>
    Task<SetupActionResult> ExecuteAsync(IInferenceSettingsManager manager, InferenceSettings settings, SetupActionDescriptor action,
        IProgress<SetupProgress> progress, CancellationToken cancellationToken = default);

    /// <summary>Opens <paramref name="uri"/> in the person's browser.</summary>
    ValueTask OpenBrowserAsync(Uri uri, CancellationToken cancellationToken = default);

    /// <summary>Shows a folder in the platform's file manager.</summary>
    Task RevealFolderAsync(string folder, CancellationToken cancellationToken);
}

/// <summary>Optional host-native picker. Null means cancelled; the host owns permissions and returns an absolute path.
/// Hosts without this capability retain manual path and executable-name entry.</summary>
public interface IInferenceSettingsPathPicker
{
    /// <summary>Shows the host's picker for <paramref name="kind"/>, starting at <paramref name="currentValue"/>, and returns the chosen absolute path or null when cancelled.</summary>
    ValueTask<string?> SelectPathAsync(SetupPathSelectionKind kind, string currentValue,
        CancellationToken cancellationToken = default);
}
