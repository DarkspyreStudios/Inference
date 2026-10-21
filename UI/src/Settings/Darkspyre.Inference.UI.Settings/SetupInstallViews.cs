using Darkspyre.Inference.Common.Setup;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace Darkspyre.Inference.UI.Settings;

/// <summary>
/// One install-state chip: Installed (success, check), Not downloaded (warning, download), or Files missing and Not supported
/// (destructive, no icon).
/// </summary>
public sealed class SetupInstallChip : ContentControl
{
    // A check mark and a download arrow on a 24 unit grid.
    private const string CheckPath = "M20 6 9 17l-5-5";
    private const string DownloadPath = "M12 3v12 M7 10l5 5 5-5 M5 21h14";

    /// <summary>Creates the chip. The <paramref name="text"/> carries the meaning; the icon is decoration.</summary>
    public SetupInstallChip(SetupInstallState state, string text)
    {
        StackPanel line = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (state is SetupInstallState.Installed or SetupInstallState.NotDownloaded)
        {
            Avalonia.Controls.Shapes.Path icon = new()
            {
                Data = Geometry.Parse(state == SetupInstallState.Installed ? CheckPath : DownloadPath),
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform,
                StrokeThickness = 2,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                VerticalAlignment = VerticalAlignment.Center,
            };
            icon.Classes.Add("inference-install-chip-icon");
            line.Children.Add(icon);
        }
        line.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        this.Content = line;
        this.HorizontalAlignment = HorizontalAlignment.Left;
        this.Classes.Add("badge");
        this.Classes.Add("inference-install-chip");
        this.Classes.Add(state switch
        {
            SetupInstallState.Installed => "success",
            SetupInstallState.NotDownloaded => "warning",
            _ => "destructive",
        });
    }

    /// <summary>Creates a choice's chip: "Installed", "Not supported", or <paramref name="notDownloaded"/> for a choice that is not
    /// downloaded.</summary>
    internal static SetupInstallChip ForChoice(SetupChoiceInstall install, string notDownloaded) => new(install.State, install.State switch
    {
        SetupInstallState.Installed => "Installed",
        SetupInstallState.NotSupported => "Not supported",
        _ => notDownloaded,
    });

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(ContentControl);
}

/// <summary>The selected model's install state under its choice, with the actions that change it.</summary>
public sealed class SetupInstallStatusCard : UserControl
{
    /// <summary>
    /// Creates the card for <paramref name="status"/>, named <paramref name="label"/>; <paramref name="run"/> runs an action its buttons
    /// name, and <paramref name="reveal"/> shows the store folder of an installed model in the file manager.
    /// </summary>
    public SetupInstallStatusCard(SetupInstallStatus status, string label, bool disabled, Func<SetupActionDescriptor, Task> run,
        Func<string, Task> reveal)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(reveal);
        StackPanel body = new() { Spacing = 8 };
        TextBlock title = new() { Text = status.Name, FontWeight = FontWeight.SemiBold };
        body.Children.Add(title);
        switch (status.State)
        {
            case SetupInstallState.Installed:
                body.Children.Add(new SetupInstallChip(SetupInstallState.Installed, $"Installed in {status.Location}"));
                string files = status.FileCount == 1 ? "1 file" : $"{status.FileCount} files";
                WrapPanel detail = new() { ItemSpacing = 12, LineSpacing = 6 };
                detail.Children.Add(Text($"Ready to load. {files}, {SetupByteSize.Format(status.Size)}, {status.License}.", "inference-install-card-detail"));
                Button show = InferenceSettingsEditor.Button("Show", "ghost", () => reveal(status.Location));
                Avalonia.Automation.AutomationProperties.SetName(show, "Show the model folder");
                detail.Children.Add(show);
                body.Children.Add(detail);
                break;
            case SetupInstallState.NotDownloaded:
                body.Children.Add(new SetupInstallChip(SetupInstallState.NotDownloaded, "Not downloaded"));
                body.Children.Add(Text($"This model is not in {status.Location}. The engine cannot load it until it is downloaded."));
                break;
            case SetupInstallState.NotSupported:
                body.Children.Add(new SetupInstallChip(SetupInstallState.NotSupported, "Not supported"));
                body.Children.Add(Text(status.UnsupportedReason ?? string.Empty));
                break;
            default:
                body.Children.Add(new SetupInstallChip(SetupInstallState.FilesMissing, "Files missing"));
                body.Children.Add(Text($"{status.MissingFile} is missing from the model store."));
                break;
        }
        if (status.Actions.Count > 0)
        {
            WrapPanel actions = new() { ItemSpacing = 12, LineSpacing = 8 };
            foreach (SetupActionDescriptor action in status.Actions)
            {
                Button button = InferenceSettingsEditor.Button(action.DisplayName, "outline", () => run(action));
                button.IsEnabled = !disabled && action.IsEnabled;
                actions.Children.Add(button);
                if (!action.IsEnabled && !string.IsNullOrWhiteSpace(action.UnavailableReason))
                    actions.Children.Add(Text(action.UnavailableReason, "inference-install-card-detail"));
            }
            body.Children.Add(actions);
        }
        Border card = new() { Child = body };
        card.Classes.Add("inference-install-card");
        card.Classes.Add(status.State switch
        {
            SetupInstallState.Installed => "success",
            SetupInstallState.NotDownloaded => "warning",
            _ => "destructive",
        });
        Avalonia.Automation.AutomationProperties.SetName(card, label);
        this.Content = card;
    }

    private static TextBlock Text(string text, string? kind = null)
    {
        TextBlock block = new() { Text = text, TextWrapping = TextWrapping.Wrap };
        if (kind is not null) block.Classes.Add(kind);
        return block;
    }
}
