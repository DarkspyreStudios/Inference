using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Darkspyre.Inference.UI.Settings;

/// <summary>The editor's own look: spacing, install chips and cards, status and issue lines. Colours come from the theme's resource keys.</summary>
public sealed class InferenceSettingsStyles : Styles
{
    /// <summary>Loads the styles.</summary>
    public InferenceSettingsStyles() => AvaloniaXamlLoader.Load(this);
}
