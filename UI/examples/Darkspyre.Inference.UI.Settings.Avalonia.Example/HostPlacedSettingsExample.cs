using Avalonia.Controls;
using Avalonia.Layout;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.Image;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Setup.Examples;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.UI.Settings.Avalonia.Example;

/// <summary>
/// An example host: a list of configured provider editors and the editor of the one chosen. The host chooses how to
/// list and place editors; the library shows one settings object.
/// </summary>
public sealed class HostPlacedSettingsExample : UserControl
{
    private readonly IAvaloniaSettingsInteractionHost interactions;
    private readonly ContentControl editor = new();
    private readonly StackPanel providers = new() { Orientation = Orientation.Horizontal, Spacing = 8 };

    /// <summary>Creates the example; <paramref name="committed"/> receives each save's result.</summary>
    public HostPlacedSettingsExample(IAvaloniaSettingsInteractionHost interactions, Action<SetupCommitResult> committed)
    {
        this.interactions = interactions;
        this.Committed = committed;
        global::Avalonia.Automation.AutomationProperties.SetName(this.providers, "Configured provider editors");
        StackPanel layout = new() { Spacing = 16 };
        layout.Children.Add(this.providers);
        layout.Children.Add(this.editor);
        global::Avalonia.Automation.AutomationProperties.SetName(layout, "Example host provider settings");
        this.Content = layout;
    }

    private Action<SetupCommitResult> Committed { get; }

    /// <summary>Lists the example drafts and opens the first.</summary>
    public async Task LoadAsync(LlmProviderService llmProviders, ImageProviderService imageProviders, ITensorModelPackManager packs,
        IImageWorkflowChoiceSource workflows, ISetupCommitTarget<LlmSetupChanges> llmCommit, ISetupCommitTarget<ImageSetupChanges> imageCommit,
        ISetupCommitTarget<TensorSetupChanges> tensorCommit)
    {
        IReadOnlyList<SetupDraft> drafts = await SetupCompositionExample.CreateDraftsAsync(llmProviders, imageProviders, packs, workflows,
            llmCommit, imageCommit, tensorCommit);
        this.providers.Children.Clear();
        foreach (SetupDraft draft in drafts)
        {
            Button choose = new() { Content = draft.Settings.Definition.Implementation.DisplayName };
            choose.Classes.Add("ghost");
            choose.Click += (_, _) => this.Show(draft);
            this.providers.Children.Add(choose);
        }
        if (drafts.Count > 0) this.Show(drafts[0]);
    }

    private void Show(SetupDraft draft)
    {
        InferenceSettingsEditor shown = new(draft.Settings, draft.Manager, this.interactions);
        shown.CommitCompleted += (_, result) => this.Committed(result);
        this.editor.Content = shown;
    }
}
