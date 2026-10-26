using Avalonia;

namespace Darkspyre.Inference.UI.Settings.Avalonia;

/// <summary>The interaction host of the Avalonia setup editor: the shared host contract and the window's clipboard.</summary>
public interface IAvaloniaSettingsInteractionHost : IInferenceSettingsInteractionHost
{
    /// <summary>Writes <paramref name="text"/> to the clipboard of the window that shows <paramref name="source"/> and
    /// tells the person the result with the host's transient notice. A clipboard failure is reported to the person, not thrown.</summary>
    Task CopyToClipboardAsync(Visual source, string text);
}
