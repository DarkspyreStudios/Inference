using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.UI.Settings;

/// <summary>Host-owned consent, external interaction, and operation lifetime for a setup editor.</summary>
public interface IInferenceSettingsInteractionHost
{
    ValueTask<bool> AuthorizeAsync(string operation, SetupSideEffect sideEffects,
        CancellationToken cancellationToken = default);

    Task<SetupActionResult> ExecuteAsync(IInferenceSetupSession session, ISetupAction action,
        IProgress<SetupProgress> progress, CancellationToken cancellationToken = default);

    ValueTask OpenBrowserAsync(Uri uri, CancellationToken cancellationToken = default);
}

/// <summary>Optional host-native picker. Null means cancelled; the host owns permissions and returns an absolute path.
/// Hosts without this capability retain manual path and executable-name entry.</summary>
public interface IInferenceSettingsPathPicker
{
    ValueTask<string?> SelectPathAsync(SetupPathSelectionKind kind, string currentValue,
        CancellationToken cancellationToken = default);
}
