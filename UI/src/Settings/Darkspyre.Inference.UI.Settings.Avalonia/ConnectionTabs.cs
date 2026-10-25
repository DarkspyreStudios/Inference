using Darkspyre.Inference.Common.Setup;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Darkspyre.Inference.UI.Settings.Avalonia;

/// <summary>
/// Editing an existing connection: tabs labelled "Connection" and "Model", one per step; each step's own title stays as
/// its heading. One editor shows the chosen tab's step, so typed input and sign-in progress carry across tabs. The host
/// frames it with the connection's name and owns Save through <see cref="SaveAsync"/>.
/// </summary>
public sealed class ConnectionTabs : UserControl
{
    private static readonly string[] Labels = ["Connection", "Model"];
    private readonly InferenceSettings settings;
    private readonly IInferenceSettingsManager manager;
    private readonly IAvaloniaSettingsInteractionHost host;
    private InferenceSettingsEditor? editor;

    /// <summary>Creates the tabs for <paramref name="settings"/>, whose steps are the connection step and then the model step.</summary>
    /// <param name="settings">The settings of the connection being edited.</param>
    /// <param name="manager">The manager that runs the settings' actions and saves them.</param>
    /// <param name="host">The host that authorizes and runs actions, opens browser pages and shows folders.</param>
    public ConnectionTabs(InferenceSettings settings, IInferenceSettingsManager manager, IAvaloniaSettingsInteractionHost host)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(host);
        this.settings = settings;
        this.manager = manager;
        this.host = host;
        this.Classes.Add("inference-connection-tabs");
    }

    /// <summary>Passed to the Connection tab's editor as its MoreOptionsContent.</summary>
    public Control? MoreOptionsContent { get; init; }

    /// <summary>Raised after Save. A Committed result means the host closes its sheet.</summary>
    public event EventHandler<SetupCommitResult>? CommitCompleted;

    /// <summary>Raised each time the editor's unsaved, pending, error or can-save state changes, as <see cref="InferenceSettingsEditor.StateChanged"/>.</summary>
    public event EventHandler<SetupEditorState>? StateChanged;

    /// <summary>The editor's current unsaved, pending input, error, can-save and uncertain-save state; the host enables Save from CanSave.</summary>
    public SetupEditorState State => this.Editor().State;

    /// <summary>Saves the settings through the manager; <see cref="CommitCompleted"/> reports the result.</summary>
    public async Task SaveAsync() => await this.Editor().SaveAsync();

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (this.Content is not null) return;
        InferenceSettingsEditor shown = this.Editor();
        TabStrip tabs = new() { ItemsSource = Labels.Take(Math.Max(1, this.settings.Steps.Count)).ToArray(), SelectedIndex = 0 };
        tabs.Classes.Add("inference-connection-tab-strip");
        tabs.SelectionChanged += (_, _) => shown.ShowStep(Math.Max(0, tabs.SelectedIndex));
        // A refused save shows the step with the problem; the tabs follow it.
        shown.CommitCompleted += (_, _) => tabs.SelectedIndex = shown.CurrentStepIndex;
        StackPanel layout = new() { Spacing = 16 };
        layout.Children.Add(tabs);
        layout.Children.Add(shown);
        this.Content = layout;
    }

    // The host's click is consent for every effect, and the host's frame names the connection.
    private InferenceSettingsEditor Editor()
    {
        if (this.editor is { } existing) return existing;
        InferenceSettingsEditor created = new(this.settings, this.manager, this.host)
        {
            ShowSaveButton = false,
            ShowHeading = false,
            ShowActionEffects = false,
            StepNavigation = false,
            MoreOptionsContent = this.MoreOptionsContent,
        };
        created.CommitCompleted += (_, result) => this.CommitCompleted?.Invoke(this, result);
        created.StateChanged += (_, state) => this.StateChanged?.Invoke(this, state);
        this.editor = created;
        return created;
    }
}
