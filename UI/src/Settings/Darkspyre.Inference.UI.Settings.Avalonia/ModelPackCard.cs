using Darkspyre.Inference.Common.Setup;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Darkspyre.Inference.UI.Settings.Avalonia;

/// <summary>A local model pack as a card: name, download size, install state; selected on click.</summary>
internal sealed class ModelPackCard : UserControl
{
    /// <summary>Creates the card for <paramref name="choice"/>, a choice that carries <see cref="SetupChoice.Install"/>.</summary>
    /// <param name="choice">The model pack the card shows.</param>
    /// <param name="selected">True marks the card as the chosen pack.</param>
    /// <param name="choose">Runs when the person clicks the card.</param>
    public ModelPackCard(SetupChoice choice, bool selected, Action choose)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(choose);
        SetupChoiceInstall install = choice.Install ?? throw new ArgumentException("A model pack card needs the choice's install facts.", nameof(choice));
        StackPanel body = new() { Spacing = 6 };
        body.Children.Add(new TextBlock { Text = choice.DisplayName, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        TextBlock size = new() { Text = $"{SetupByteSize.Format(install.Size)} download" };
        size.Classes.Add("inference-settings-help");
        body.Children.Add(size);
        body.Children.Add(SetupInstallChip.ForChoice(install, "Not downloaded"));
        Button card = new() { Content = body, HorizontalContentAlignment = HorizontalAlignment.Left };
        card.Classes.Add("inference-model-pack-card");
        card.Classes.Set("selected", selected);
        card.Click += (_, _) => choose();
        global::Avalonia.Automation.AutomationProperties.SetName(card, choice.DisplayName);
        this.Content = card;
    }
}
