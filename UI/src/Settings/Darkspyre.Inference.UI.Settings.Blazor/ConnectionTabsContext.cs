namespace Darkspyre.Inference.UI.Settings.Blazor;

/// <summary>
/// Passed from <see cref="ConnectionTabs"/> to the editor it shows. The editor then hides its step list and its Back and
/// Next buttons, shows the step's title as its heading, and registers itself so the tabs can choose its step and save it.
/// </summary>
internal sealed class ConnectionTabsContext
{
    /// <summary>The editor the tabs show, once it has started.</summary>
    public InferenceSettingsEditor? Editor { get; set; }
}
