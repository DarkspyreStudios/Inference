using Darkspyre.Inference.Common.Setup;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Darkspyre.Inference.UI.Settings;

/// <summary>
/// Walks a person through an inference provider's setup steps, fields, sign-in, actions and save, and shows its status
/// and issues. The host places it; nothing reaches the app until <see cref="SaveAsync"/>. Its controls take the host's
/// look through their style classes and the theme's resource keys.
/// </summary>
public sealed class InferenceSettingsEditor : UserControl
{
    private readonly InferenceSettings settings;
    private readonly IInferenceSettingsManager manager;
    private readonly IInferenceSettingsInteractionHost host;
    private readonly IInferenceSettingsStatusSource? statusSource;
    private readonly SetupEditorInputState inputs = new();
    private readonly Dictionary<string, SetupFieldInput> fields = new(StringComparer.Ordinal);
    private readonly HashSet<string> openSections = new(StringComparer.Ordinal);
    private SetupFieldInput? callback;
    private SetupEditorState? lastPublishedState;
    private int lastPublishedStep;
    private bool saving;
    private bool running;
    private long editVersion;
    private int stepIndex;
    private SetupProgress? progress;
    // The device code already copied to the clipboard when it was shown, so each code is copied once.
    private string? copiedCode;
    private SetupActionResult? actionResult;
    // The action whose result the tail shows, or null for a result no action produced.
    private SetupActionDescriptor? resultAction;
    private SetupCommitResult? commitResult;
    // Steps whose checklist has run its automatic check.
    private readonly HashSet<string> checkedSteps = new(StringComparer.Ordinal);
    private CancellationTokenSource? actionCancellation;
    private SetupValidation validation = SetupValidation.Valid;
    private bool attached;
    private bool closed;
    // The editor's regions. The step and the interaction keep their views while their shape stays, so a field that
    // holds focus keeps it while the person types.
    private readonly ContentControl stepRegion = new();
    private readonly ContentControl interactionRegion = new();
    private readonly ContentControl headRegion = new();
    private readonly ContentControl issuesRegion = new();
    private readonly ContentControl tailRegion = new();
    private string? stepShape;
    private string? interactionShape;

    /// <summary>Creates the editor.</summary>
    /// <param name="settings">The provider settings being edited, with their steps, fields, status and actions.</param>
    /// <param name="manager">The manager that validates and saves the settings and refreshes a running engine's status.</param>
    /// <param name="host">The host that authorizes and runs provider actions and opens authorization pages in a browser.</param>
    public InferenceSettingsEditor(InferenceSettings settings, IInferenceSettingsManager manager, IInferenceSettingsInteractionHost host)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(host);
        this.settings = settings;
        this.manager = manager;
        this.host = host;
        this.statusSource = manager as IInferenceSettingsStatusSource;
        this.Styles.Add(new InferenceSettingsStyles());
        this.Classes.Add("inference-settings-editor");
        Avalonia.Automation.AutomationProperties.SetName(this, $"{settings.Definition.Implementation.DisplayName} settings");
    }

    /// <summary>True shows a Save button on the last step; false leaves saving to the host through <see cref="SaveAsync"/>.</summary>
    public bool ShowSaveButton { get; init; } = true;

    /// <summary>False when the host already shows the provider's name and description in its own frame.</summary>
    public bool ShowHeading { get; init; } = true;

    /// <summary>False when the host treats the person's click as consent, so declared side effects need no label.</summary>
    public bool ShowActionEffects { get; init; } = true;

    /// <summary>Host content shown below the heading and above the status line.</summary>
    public Control? IntroContent { get; init; }

    /// <summary>Host content shown below the current step and above the issue list.</summary>
    public Control? SupplementaryContent { get; init; }

    /// <summary>
    /// Host content drawn on the first step: at the end of its advanced section titled "More options", or in a
    /// "More options" expander of its own when the step has none.
    /// </summary>
    public Control? MoreOptionsContent { get; init; }

    /// <summary>
    /// False hides the step list and the Back and Next buttons and shows the current step's title as its heading;
    /// <see cref="ShowStep"/> chooses the step. <see cref="ConnectionTabs"/> uses this for its tabs.
    /// </summary>
    internal bool StepNavigation { get; init; } = true;

    /// <summary>
    /// False hides the numbered step list and shows the current step's title as its heading, while Back and Next stay.
    /// A host that draws its own step strip from <see cref="CurrentStepIndex"/> and <see cref="StepChanged"/> sets it.
    /// </summary>
    public bool ShowStepList { get; init; } = true;

    /// <summary>Raised after Save. A Committed result means the host closes the dialog.</summary>
    public event EventHandler<SetupCommitResult>? CommitCompleted;

    /// <summary>Raised with the new step index each time the editor shows another step: Back, Next, a tab, or a refused save.</summary>
    public event EventHandler<int>? StepChanged;

    /// <summary>Raised each time the editor's unsaved, pending, error or can-save state changes.</summary>
    public event EventHandler<SetupEditorState>? StateChanged;

    /// <summary>The editor's current unsaved, pending input, error, can-save and uncertain-save state.</summary>
    public SetupEditorState State => new(this.settings.HasUnsavedChanges || this.inputs.HasPendingInput || this.settings.SaveOutcomeUncertain,
        this.inputs.HasPendingInput, this.inputs.Issues.Any(issue => issue.Severity == SetupIssueSeverity.Error),
        !this.Busy && !this.inputs.HasPendingInput && this.settings.CanSave && !this.AllIssues.Any(issue => issue.Severity == SetupIssueSeverity.Error),
        this.settings.SaveOutcomeUncertain);

    private bool Busy => this.running || this.saving || this.settings.IsSaved;

    private IReadOnlyList<SetupStep> Steps => this.settings.Steps;

    /// <summary>The index in <see cref="InferenceSettings.Steps"/> of the step the editor shows.</summary>
    public int CurrentStepIndex => this.stepIndex;

    /// <summary>Shows the step at <paramref name="index"/>, as a tab does.</summary>
    internal void ShowStep(int index)
    {
        this.stepIndex = Math.Clamp(index, 0, Math.Max(0, this.Steps.Count - 1));
        this.Changed();
    }

    private bool ShowsStepList => this.StepNavigation && this.ShowStepList && this.Steps.Count > 1;

    private SetupStep? CurrentStep => this.stepIndex < this.Steps.Count ? this.Steps[this.stepIndex] : null;

    private SetupIssue[] AllIssues => [.. this.validation.Issues.Concat(this.inputs.Issues).Distinct()];

    private SetupIssue[] VisibleIssues => [.. this.AllIssues.Where(issue => issue.StepId is null || issue.StepId == this.CurrentStep?.Id)];

    /// <summary>A button in the host's <paramref name="variant"/> look that runs <paramref name="click"/>.</summary>
    internal static Button Button(string label, string variant, Func<Task> click)
    {
        Button button = new() { Content = label };
        button.Classes.Add(variant);
        // A failure surfaces on the UI thread, where the host reports it.
        button.Click += async (_, _) => await click();
        return button;
    }

    /// <summary>Saves the settings through the manager. Nothing reaches the app without this call.</summary>
    public async Task<SetupCommitResult> SaveAsync()
    {
        if (this.saving || this.running) return new(SetupCommitState.Invalid, Issues: [new("editor-busy", "Wait for the running operation to finish.")]);
        IReadOnlyList<SetupIssue> inputIssues = this.inputs.Issues;
        SetupCommitResult result;
        if (inputIssues.Any(issue => issue.Severity == SetupIssueSeverity.Error)) result = new(SetupCommitState.Invalid, Issues: inputIssues);
        else
        {
            this.saving = true;
            this.Changed();
            try { result = await this.manager.SaveAsync(this.settings); }
            finally { this.saving = false; }
        }
        if (!this.closed)
        {
            this.commitResult = result;
            if (result.State == SetupCommitState.Committed) this.inputs.Clear();
            if (result.State == SetupCommitState.Invalid)
            {
                this.validation = new(result.Issues ?? []);
                int step = this.Steps.ToList().FindIndex(item => (result.Issues ?? []).Any(issue => issue.StepId == item.Id
                    || item.Sections.SelectMany(section => section.Fields).SelectMany(SetupEditorInputState.Flatten).Any(field => field.Description.Id == issue.FieldId)));
                if (step >= 0) this.stepIndex = step;
            }
            this.Changed();
            this.CommitCompleted?.Invoke(this, result);
        }
        return result;
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (this.attached || this.closed) return;
        this.attached = true;
        if (this.statusSource is not null) this.statusSource.StatusChanged += this.OnStatusChanged;
        this.Changed();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // The editor closes with its host's dialog; its fields stop loading choices.
        this.closed = true;
        this.inputs.Clear();
        if (this.statusSource is not null) this.statusSource.StatusChanged -= this.OnStatusChanged;
        foreach (SetupFieldInput field in this.fields.Values) field.Release();
        this.callback?.Release();
    }

    // The provider reported a change, such as a running engine or its model list; the editor reads the new state and draws it.
    private void OnStatusChanged(object? sender, EventArgs args)
    {
        if (this.closed || this.statusSource is not { } source) return;
        Dispatcher.UIThread.Post(async () =>
        {
            if (this.closed) return;
            try { await source.RefreshStatusAsync(this.settings); }
            catch (Exception) { return; }
            if (this.closed) return;
            // A refresh can change the fields, such as a model choice's install chips; the choices reload as after an action.
            this.editVersion++;
            this.inputs.Reconcile(this.settings);
            this.Changed();
        });
    }

    // Draws the editor and tells the host when its state changed.
    private void Changed()
    {
        if (this.closed) return;
        this.Render();
        if (this.stepIndex != this.lastPublishedStep)
        {
            this.lastPublishedStep = this.stepIndex;
            this.StepChanged?.Invoke(this, this.stepIndex);
        }
        SetupEditorState state = this.State;
        if (state == this.lastPublishedState) return;
        this.lastPublishedState = state;
        this.StateChanged?.Invoke(this, state);
    }

    // Each region is redrawn on its own; the step and interaction regions stay in the window, so a field that holds
    // focus keeps it.
    private void Render()
    {
        if (this.Content is null)
        {
            StackPanel layout = new() { Spacing = 24 };
            layout.Children.Add(this.headRegion);
            layout.Children.Add(this.stepRegion);
            if (this.SupplementaryContent is { } supplementary) layout.Children.Add(supplementary);
            layout.Children.Add(this.issuesRegion);
            layout.Children.Add(this.interactionRegion);
            layout.Children.Add(this.tailRegion);
            this.Content = layout;
        }
        StackPanel head = new() { Spacing = 24 };
        if (this.ShowHeading)
        {
            StackPanel heading = new() { Spacing = 12 };
            heading.Children.Add(SetupFieldInput.Text(this.settings.Definition.Implementation.DisplayName, "h2"));
            heading.Children.Add(SetupFieldInput.Text(this.settings.Definition.Description));
            head.Children.Add(heading);
        }
        if (this.IntroContent is { } intro)
        {
            Detach(intro);
            head.Children.Add(intro);
        }
        (string Text, bool? Value)[] status = [.. this.StatusChips()];
        if (status.Length > 0)
        {
            WrapPanel line = new() { ItemSpacing = 8, LineSpacing = 6 };
            line.Classes.Add("inference-settings-status");
            foreach ((string text, bool? value) in status) line.Children.Add(Chip(text, value switch { true => "success", false => "warning", _ => null }));
            head.Children.Add(line);
        }
        if (this.ShowsStepList)
        {
            WrapPanel steps = new() { ItemSpacing = 12, LineSpacing = 6 };
            steps.Classes.Add("inference-settings-steps");
            Avalonia.Automation.AutomationProperties.SetName(steps, "Setup steps");
            for (int index = 0; index < this.Steps.Count; index++)
            {
                TextBlock item = new() { Text = $"{index + 1}. {this.Steps[index].Title}" };
                item.Classes.Set("current", index == this.stepIndex);
                steps.Children.Add(item);
            }
            head.Children.Add(steps);
        }
        this.headRegion.Content = head;
        this.headRegion.IsVisible = head.Children.Count > 0;
        this.RenderStep();
        SetupIssue[] issues = this.VisibleIssues;
        StackPanel list = new() { Spacing = 4 };
        list.Classes.Add("inference-settings-issues");
        foreach (SetupIssue issue in issues) list.Children.Add(SetupFieldInput.Text(issue.Message));
        this.issuesRegion.Content = list;
        this.issuesRegion.IsVisible = issues.Length > 0;
        SetupStep? step = this.CurrentStep;
        bool checklist = this.ShowsChecklist(step);
        SetupActionDescriptor[] stepActions = [.. this.StepActions(step)];
        SetupActionDescriptor? signIn = stepActions.FirstOrDefault(action => action.Purpose == SetupActionPurpose.Authenticate);
        // The checklist draws the prerequisite itself when the step has no sign-in action of its own.
        bool prerequisiteInChecklist = checklist && signIn is null && this.settings.Interaction is SetupExternalPrerequisite;
        this.RenderInteraction();
        this.interactionRegion.IsVisible = this.settings.Interaction is not null && !prerequisiteInChecklist;
        StackPanel tail = new() { Spacing = 24 };
        if (checklist) tail.Children.Add(this.Checklist(step!, stepActions, signIn));
        // The checklist draws its sign-in action, and for a step without one its check; a test with a shown result offers "Test again".
        SetupActionDescriptor[] tailActions = [.. stepActions.Where(action => !(checklist && (action.Purpose == SetupActionPurpose.Authenticate
            || signIn is null && action.Purpose == SetupActionPurpose.CheckPrerequisites))
            && !(this.actionResult is not null && this.resultAction?.Id == action.Id && action.Purpose == SetupActionPurpose.Test))];
        if (tailActions.Length > 0) tail.Children.Add(this.Actions(tailActions));
        if (this.progress is { } current)
            tail.Children.Add(SetupFieldInput.Text($"{current.Stage}: {current.Message}" + (current.Total is > 0 ? $" ({current.Completed} / {current.Total})" : string.Empty),
                "inference-settings-progress"));
        if (this.actionCancellation is not null)
        {
            Button cancel = Button("Cancel operation", "ghost", () =>
            {
                this.actionCancellation?.Cancel();
                return Task.CompletedTask;
            });
            cancel.HorizontalAlignment = HorizontalAlignment.Left;
            tail.Children.Add(cancel);
        }
        if (this.actionResult is { } action) tail.Children.Add(this.Result(action));
        if (this.commitResult is { } commit) tail.Children.Add(SetupFieldInput.Text(commit.Issues is { Count: > 0 } refused ? refused[0].Message : Describe(commit.State)));
        WrapPanel navigation = new() { ItemSpacing = 12, LineSpacing = 8 };
        navigation.Classes.Add("inference-settings-navigation");
        if (this.StepNavigation && this.stepIndex > 0) navigation.Children.Add(Enabled(Button("Back", "ghost", this.PreviousStep), !this.Busy));
        if (this.StepNavigation && this.stepIndex < this.Steps.Count - 1) navigation.Children.Add(Enabled(Button("Next", "primary", this.NextStep), !this.Busy));
        if (this.ShowSaveButton && this.stepIndex >= this.Steps.Count - 1)
            navigation.Children.Add(Enabled(Button("Save settings", "primary", async () => await this.SaveAsync()), this.State.CanSave));
        if (navigation.Children.Count > 0) tail.Children.Add(navigation);
        this.tailRegion.Content = tail;
        this.tailRegion.IsVisible = tail.Children.Count > 0;
        if (checklist && this.attached && !this.Busy && this.checkedSteps.Add(step!.Id)) this.RunCheckLater(stepActions);
    }

    // The step is rebuilt only when its shape changes: another step, or other visible fields. Otherwise each field
    // view takes its latest state in place.
    private void RenderStep()
    {
        SetupStep? step = this.CurrentStep;
        string shape = step is null ? string.Empty : this.stepIndex + "\n" + string.Join("\n", step.Sections.Select(section => section.Title + ":"
            + string.Join(",", section.Fields.Where(field => field.Description.IsVisible).Select(field => field.Description.Id + "#" + field.GetType().Name)))) + "\n" + this.Busy;
        HashSet<string> live = new(StringComparer.Ordinal);
        if (shape != this.stepShape || step is null)
        {
            this.stepShape = shape;
            this.stepRegion.Content = step is null ? null : this.Step(step, live);
        }
        else
        {
            foreach (SetupField field in step.Sections.SelectMany(section => section.Fields).Where(field => field.Description.IsVisible && field is not SetupInstallStatusField))
            {
                live.Add(field.Description.Id);
                if (this.fields.TryGetValue(field.Description.Id, out SetupFieldInput? view)) view.Update(field, this.editVersion, this.AllIssues, this.Busy);
            }
        }
        foreach (string gone in this.fields.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            this.fields[gone].Release();
            this.fields.Remove(gone);
        }
    }

    // The interaction is rebuilt only when it changes or the editor's busy state changes. A device code is copied to
    // the clipboard the first time it is shown.
    private void RenderInteraction()
    {
        SetupInteraction? interaction = this.settings.Interaction;
        if (interaction is null)
        {
            this.callback?.Release();
            this.callback = null;
            this.interactionShape = null;
            this.interactionRegion.Content = null;
            return;
        }
        string shape = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(interaction) + "\n" + this.Busy;
        if (shape != this.interactionShape)
        {
            this.interactionShape = shape;
            this.interactionRegion.Content = this.Interaction(interaction);
        }
        if (interaction is SetupDeviceAuthorization device && device.UserCode != this.copiedCode)
        {
            this.copiedCode = device.UserCode;
            string code = device.UserCode;
            Dispatcher.UIThread.Post(async () => await this.CopyCodeAsync(code));
        }
        else if (interaction is SetupBrowserAuthorization browser) this.callback?.Update(browser.Callback, this.editVersion, this.AllIssues, this.Busy);
    }

    private static Button Enabled(Button button, bool enabled)
    {
        button.IsEnabled = enabled;
        return button;
    }

    // Host content moves between redraws; it leaves its previous place first.
    private static void Detach(Control content) => (content.Parent as Panel)?.Children.Remove(content);

    private StackPanel Step(SetupStep step, HashSet<string> live)
    {
        StackPanel view = new() { Spacing = 12 };
        view.Classes.Add("inference-settings-step");
        // The step list above names and marks the current step; a single step, a step shown as a tab, or a step under the
        // host's own strip has no list.
        if (!this.ShowsStepList) view.Children.Add(SetupFieldInput.Text(step.Title, "h3"));
        // The first of several steps is the connection step: its instructions say what the connection is.
        bool connection = this.stepIndex == 0 && this.Steps.Count > 1;
        if (!string.IsNullOrWhiteSpace(step.Instructions))
        {
            if (connection) view.Children.Add(SetupFieldInput.Text("About this connection", "h4"));
            view.Children.Add(SetupFieldInput.Text(step.Instructions));
        }
        if (step.HasPlatformInstructions) view.Children.Add(SetupFieldInput.Text(step.PlatformInstructions));
        Control? moreOptions = this.stepIndex == 0 ? this.MoreOptionsContent : null;
        if (moreOptions is not null) Detach(moreOptions);
        bool moreOptionsPlaced = false;
        foreach (SetupSection section in step.Sections)
        {
            StackPanel body = new() { Spacing = 14 };
            body.Classes.Add("inference-settings-fields");
            foreach (SetupField field in section.Fields.Where(value => value.Description.IsVisible)) body.Children.Add(this.Field(field, live));
            if (section.IsAdvanced)
            {
                if (moreOptions is not null && section.Title == MoreOptionsTitle)
                {
                    body.Children.Add(moreOptions);
                    moreOptionsPlaced = true;
                }
                view.Children.Add(this.Advanced(step.Id + "/" + section.Title, section.Title, body, this.HasErrors(section)));
            }
            else
            {
                StackPanel group = new() { Spacing = 12 };
                group.Classes.Add("inference-settings-section");
                Avalonia.Automation.AutomationProperties.SetName(group, section.Title);
                group.Children.Add(SetupFieldInput.Text(section.Title, "h4"));
                group.Children.Add(body);
                view.Children.Add(group);
            }
        }
        if (moreOptions is not null && !moreOptionsPlaced)
        {
            StackPanel body = new() { Spacing = 14 };
            body.Classes.Add("inference-settings-fields");
            body.Children.Add(moreOptions);
            view.Children.Add(this.Advanced(step.Id + "/" + MoreOptionsTitle, MoreOptionsTitle, body, false));
        }
        if (step.HasCompletionRequirement) view.Children.Add(SetupFieldInput.Text(step.CompletionRequirement));
        if (step.HasTroubleshooting)
        {
            // The connection step's troubleshooting shows only when asked.
            if (connection) view.Children.Add(this.Advanced(step.Id + "/troubleshooting", "Trouble signing in?", SetupFieldInput.Text(step.Troubleshooting), false));
            else view.Children.Add(SetupFieldInput.Text(step.Troubleshooting));
        }
        return view;
    }

    private const string MoreOptionsTitle = "More options";

    // A collapsed section that stays open across redraws once the person opens it, and opens itself for an error.
    private Expander Advanced(string key, string title, Control body, bool hasErrors)
    {
        Expander advanced = new() { Header = title, Content = body, HorizontalAlignment = HorizontalAlignment.Stretch };
        advanced.IsExpanded = this.openSections.Contains(key) || hasErrors;
        advanced.Expanded += (_, _) => this.openSections.Add(key);
        advanced.Collapsed += (_, _) => this.openSections.Remove(key);
        advanced.Classes.Add("inference-settings-section");
        return advanced;
    }

    // An install status field shows as its card; every other field shows its input.
    private Control Field(SetupField field, HashSet<string> live)
    {
        if (field is SetupInstallStatusField install)
            return new SetupInstallStatusCard(install.Status, install.Description.DisplayName, this.Busy, this.RunActionAsync, this.RevealAsync);
        string id = field.Description.Id;
        live.Add(id);
        if (!this.fields.TryGetValue(id, out SetupFieldInput? view))
            this.fields[id] = view = new SetupFieldInput(field, this.settings, this.manager, this.host, this.inputs, this.OnFieldEditedAsync, chosen: this.OnChoiceChosenAsync);
        Detach(view);
        view.Update(field, this.editVersion, this.AllIssues, this.Busy);
        return view;
    }

    private StackPanel Interaction(SetupInteraction interaction)
    {
        StackPanel view = new() { Spacing = 12 };
        view.Classes.Add("inference-settings-interaction");
        Avalonia.Automation.AutomationProperties.SetName(view, "Connection instructions");
        WrapPanel actions = new() { ItemSpacing = 12, LineSpacing = 8 };
        switch (interaction)
        {
            case SetupDeviceAuthorization device:
                SelectableTextBlock code = new() { Text = device.UserCode, FontWeight = Avalonia.Media.FontWeight.Bold };
                code.Classes.Add("inference-settings-user-code");
                StackPanel line = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
                line.Children.Add(new TextBlock { Text = "Use code" });
                line.Children.Add(code);
                line.Children.Add(new TextBlock { Text = "to finish connecting." });
                view.Children.Add(line);
                actions.Children.Add(Button("Copy code", "outline", () => this.CopyCodeAsync(device.UserCode)));
                actions.Children.Add(Button("Open authorization page", "primary", () => this.OpenBrowserAsync(device.VerificationUri)));
                actions.Children.Add(Enabled(Button("Continue", "primary", () => this.RunActionAsync(device.Continue)), !this.Busy));
                actions.Children.Add(Enabled(Button("Cancel connection", "outline", () => this.RunActionAsync(device.Cancel)), !this.Busy));
                view.Children.Add(actions);
                break;
            case SetupBrowserAuthorization browser:
                Button open = Button("Open authorization page", "primary", () => this.OpenBrowserAsync(browser.AuthorizationUri));
                open.HorizontalAlignment = HorizontalAlignment.Left;
                view.Children.Add(open);
                this.callback ??= new SetupFieldInput(browser.Callback, this.settings, this.manager, this.host, this.inputs, this.OnFieldEditedAsync,
                    sensitiveInput: true);
                Detach(this.callback);
                this.callback.Update(browser.Callback, this.editVersion, this.AllIssues, this.Busy);
                view.Children.Add(this.callback);
                actions.Children.Add(Enabled(Button("Continue", "primary", () => this.RunActionAsync(browser.Continue)), !this.Busy));
                actions.Children.Add(Enabled(Button("Cancel connection", "outline", () => this.RunActionAsync(browser.Cancel)), !this.Busy));
                view.Children.Add(actions);
                break;
            case SetupExternalPrerequisite prerequisite:
                view.Children.Add(SetupFieldInput.Text(prerequisite.Instructions));
                Button again = Enabled(Button("Check again", "primary", () => this.RunActionAsync(prerequisite.Recheck)), !this.Busy);
                again.HorizontalAlignment = HorizontalAlignment.Left;
                view.Children.Add(again);
                break;
        }
        return view;
    }

    private StackPanel Actions(IEnumerable<SetupActionDescriptor> actions)
    {
        StackPanel view = new() { Spacing = 8 };
        view.Classes.Add("inference-settings-actions");
        Avalonia.Automation.AutomationProperties.SetName(view, "Provider actions");
        foreach (SetupActionDescriptor action in actions)
        {
            WrapPanel row = new() { ItemSpacing = 12, LineSpacing = 6 };
            row.Children.Add(Enabled(Button(action.DisplayName, "outline", () => this.RunActionAsync(action)), !this.Busy && action.IsEnabled));
            if (!action.IsEnabled && !string.IsNullOrWhiteSpace(action.UnavailableReason)) row.Children.Add(SetupFieldInput.Text(action.UnavailableReason));
            if (this.ShowActionEffects && action.SideEffects != SetupSideEffect.None)
                row.Children.Add(SetupFieldInput.Text(action.SideEffects.ToString(), "inference-settings-effects"));
            view.Children.Add(row);
        }
        return view;
    }

    // The actions the step names, in order; when no step names any action, every action shows on every step.
    private IEnumerable<SetupActionDescriptor> StepActions(SetupStep? step)
    {
        IReadOnlyList<SetupActionDescriptor> actions = this.settings.Actions;
        if (!this.Steps.Any(item => item.ActionIds.Count > 0)) return actions;
        return step is null ? [] : step.ActionIds.Select(id => actions.FirstOrDefault(action => action.Id == id)).OfType<SetupActionDescriptor>();
    }

    // A step that checks an installed tool shows whether the tool is installed and signed in.
    private bool ShowsChecklist(SetupStep? step) => this.settings.Status.Installed.IsApplicable
        && this.StepActions(step).Any(action => action.Purpose == SetupActionPurpose.CheckPrerequisites);

    private StackPanel Checklist(SetupStep step, SetupActionDescriptor[] stepActions, SetupActionDescriptor? signIn)
    {
        StackPanel view = new() { Spacing = 8 };
        view.Classes.Add("inference-settings-checklist");
        Avalonia.Automation.AutomationProperties.SetName(view, "Setup checklist");
        SetupStatus status = this.settings.Status;
        SetupExecutableField? executable = step.Sections.SelectMany(section => section.Fields).SelectMany(SetupEditorInputState.Flatten)
            .OfType<SetupExecutableField>().FirstOrDefault();
        string tool = executable is null ? string.Empty
            : string.IsNullOrWhiteSpace(executable.Value.Value) ? executable.DefaultValue.Value : executable.Value.Value;
        view.Children.Add(SetupFieldInput.Text(Known(status.Installed) switch
        {
            true => $"✓ Installed: {tool}",
            false => "Not installed",
            null => "Not checked yet",
        }, "inference-settings-checklist-item"));
        if (Known(status.Authenticated) == true) view.Children.Add(SetupFieldInput.Text("✓ Signed in", "inference-settings-checklist-item"));
        else if (signIn is not null)
        {
            Button button = Enabled(Button(signIn.DisplayName, "primary", () => this.RunActionAsync(signIn)), !this.Busy && signIn.IsEnabled);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            view.Children.Add(button);
        }
        else if (this.settings.Interaction is SetupExternalPrerequisite prerequisite)
        {
            view.Children.Add(SetupFieldInput.Text(prerequisite.Instructions));
            Button again = Enabled(Button("Check again", "primary", () => this.RunActionAsync(prerequisite.Recheck)), !this.Busy);
            again.HorizontalAlignment = HorizontalAlignment.Left;
            view.Children.Add(again);
        }
        else if (stepActions.FirstOrDefault(action => action.Purpose == SetupActionPurpose.CheckPrerequisites) is { } check)
        {
            Button button = Enabled(Button(check.DisplayName, "outline", () => this.RunActionAsync(check)), !this.Busy && check.IsEnabled);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            view.Children.Add(button);
        }
        return view;
    }

    // A fresh observed value, or null while it is unknown or stale.
    private static bool? Known(SetupObservation observation) => observation.IsStale ? null : observation.Value;

    // Runs the step's check after the current redraw, as when a checklist step is first shown or a sign-in completes.
    private void RunCheckLater(IEnumerable<SetupActionDescriptor> stepActions)
    {
        if (stepActions.FirstOrDefault(action => action.Purpose == SetupActionPurpose.CheckPrerequisites) is not { IsEnabled: true } check) return;
        Dispatcher.UIThread.Post(async () =>
        {
            if (!this.closed && !this.Busy) await this.RunActionAsync(check);
        });
    }

    // Choosing a model on a step that tests runs the test, so the person sees a reply without another click.
    private async Task OnChoiceChosenAsync(SetupFieldInput view)
    {
        if (this.closed || this.Busy || view.Field.Description.Id != "model" || this.CurrentStep is not { } step
            || !step.ActionIds.Contains("test")) return;
        if (this.settings.Actions.FirstOrDefault(action => action.Id == "test") is { IsEnabled: true } test) await this.RunActionAsync(test);
    }

    private StackPanel Result(SetupActionResult result)
    {
        StackPanel view = new() { Spacing = 8 };
        view.Classes.Add("inference-settings-result");
        bool test = this.resultAction?.Purpose == SetupActionPurpose.Test;
        if (result.Issues is { Count: > 0 } failed) view.Children.Add(SetupFieldInput.Text(failed[0].Message));
        else if (test && result.State == SetupActionState.Completed && result.Elapsed is { } elapsed)
            view.Children.Add(SetupFieldInput.Text(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Test reply received in {elapsed.TotalSeconds:0.0} s"), "inference-settings-result-title"));
        else if (result.Message is null) view.Children.Add(SetupFieldInput.Text(Describe(result.State)));
        if (result.Message is { } message) view.Children.Add(SetupFieldInput.Text(message));
        if (test && this.resultAction is { } again
            && this.settings.Actions.FirstOrDefault(action => action.Id == again.Id) is { } current)
        {
            Button retry = Enabled(Button("Test again", "outline", () => this.RunActionAsync(current)), !this.Busy && current.IsEnabled);
            retry.HorizontalAlignment = HorizontalAlignment.Left;
            view.Children.Add(retry);
        }
        return view;
    }

    private async Task RevealAsync(string folder)
    {
        try { await this.host.RevealFolderAsync(folder, CancellationToken.None); }
        catch (Exception error) when (!this.closed)
        {
            this.resultAction = null;
            this.actionResult = new(SetupActionState.Failed, [new("reveal-failed", error.Message)]);
            this.Changed();
        }
    }

    private Task OnFieldEditedAsync(SetupEditResult result)
    {
        this.commitResult = null;
        this.editVersion++;
        this.inputs.Reconcile(this.settings);
        // Shown validation follows the current values; an edit or action can resolve or raise an issue.
        if (this.validation.Issues.Count > 0) this.validation = this.settings.Validate();
        this.Changed();
        return Task.CompletedTask;
    }

    private bool HasErrors(SetupSection section) => this.AllIssues.Any(issue => issue.Severity == SetupIssueSeverity.Error
        && section.Fields.SelectMany(SetupEditorInputState.Flatten).Any(field => field.Description.Id == issue.FieldId));

    private Task PreviousStep()
    {
        this.stepIndex--;
        this.Changed();
        return Task.CompletedTask;
    }

    private Task NextStep()
    {
        if (this.inputs.Issues.Any(this.IsCurrentStepError)) return Task.CompletedTask;
        this.validation = this.settings.Validate();
        if (!this.validation.Issues.Any(this.IsCurrentStepError)) this.stepIndex++;
        this.Changed();
        return Task.CompletedTask;
    }

    private bool IsCurrentStepError(SetupIssue issue) => issue.Severity == SetupIssueSeverity.Error
        && ((issue.StepId is null && issue.FieldId is null) || issue.StepId == this.CurrentStep?.Id || this.CurrentStep?.Sections.Any(section =>
            section.Fields.SelectMany(SetupEditorInputState.Flatten).Any(field => field.Description.Id == issue.FieldId)) == true);

    private async Task RunActionAsync(SetupActionDescriptor action)
    {
        bool authorized;
        try { authorized = await this.host.AuthorizeAsync(action.DisplayName, action.SideEffects); }
        catch (Exception error)
        {
            this.actionResult = Failed("authorization-failed", "The host could not authorize the action", error);
            this.Changed();
            return;
        }
        if (!authorized || this.closed) return;
        using CancellationTokenSource cancellation = new();
        this.actionCancellation = cancellation;
        this.progress = null;
        this.actionResult = null;
        this.resultAction = action;
        this.running = true;
        this.Changed();
        Progress<SetupProgress> reported = new(value => Dispatcher.UIThread.Post(() =>
        {
            if (this.closed) return;
            this.progress = value;
            this.Render();
        }));
        try { this.actionResult = await this.host.ExecuteAsync(this.manager, this.settings, action, reported, cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { this.actionResult = new(SetupActionState.Cancelled); }
        catch (Exception error) { this.actionResult = Failed("action-failed", "The host could not run the action", error); }
        finally
        {
            if (ReferenceEquals(this.actionCancellation, cancellation)) this.actionCancellation = null;
            this.running = false;
        }
        if (this.closed) return;
        // An action can change values, such as a sign-in credential or a resolved pack.
        this.editVersion++;
        this.inputs.Reconcile(this.settings);
        if (this.validation.Issues.Count > 0) this.validation = this.settings.Validate();
        this.Changed();
        // A completed sign-in is checked again, so the checklist shows the signed-in tool.
        if (action.Purpose is SetupActionPurpose.Authenticate or SetupActionPurpose.ContinueAuthentication
            && this.actionResult?.State == SetupActionState.Completed && this.ShowsChecklist(this.CurrentStep))
            this.RunCheckLater(this.StepActions(this.CurrentStep));
    }

    private async Task OpenBrowserAsync(Uri uri)
    {
        try
        {
            if (await this.host.AuthorizeAsync("Open authorization page", SetupSideEffect.BrowserInteraction) && !this.closed)
                await this.host.OpenBrowserAsync(uri);
        }
        catch (Exception error) when (!this.closed)
        {
            this.resultAction = null;
            this.actionResult = Failed("browser-failed", "The authorization page could not be opened", error);
            this.Changed();
        }
    }

    // A failed action whose issue names what failed, then the exception's type and message.
    private static SetupActionResult Failed(string code, string what, Exception error) =>
        new(SetupActionState.Failed, [new(code, $"{what} ({error.GetType().Name}): {error.Message}")]);

    // The clipboard write needs no provider effect or host consent; the code is shown on screen already.
    private Task CopyCodeAsync(string code) => this.closed ? Task.CompletedTask : this.host.CopyToClipboardAsync(this, code, "Sign-in code copied to clipboard.");

    // Each applicable status fact as a chip: what is true, what is not, or that it is not checked.
    private IEnumerable<(string Text, bool? Value)> StatusChips()
    {
        SetupStatus status = this.settings.Status;
        (SetupObservation Observation, string Yes, string No, string Unknown)[] facts =
        [
            (status.Configured, "Configured", "Not configured", "Configuration not checked"),
            (status.Authenticated, "Signed in", "Not signed in", "Sign-in not checked"),
            (status.Installed, "Installed", "Not installed", "Install not checked"),
            (status.Verified, "Verified", "Not verified", "Not verified yet"),
            (status.Loaded, "Loaded", "Not loaded", "Load state not checked"),
            (status.Ready, "Ready", "Not ready", "Readiness not checked"),
        ];
        return facts.Where(fact => fact.Observation.IsApplicable).Select(fact => Known(fact.Observation) switch
        {
            true => (fact.Yes, (bool?)true),
            false => (fact.No, (bool?)false),
            null => (fact.Unknown, (bool?)null),
        });
    }

    /// <summary>A chip in the host's badge look; <paramref name="tone"/> is success, warning or null for a neutral chip.</summary>
    internal static ContentControl Chip(string text, string? tone)
    {
        ContentControl chip = new() { Content = new TextBlock { Text = text }, HorizontalAlignment = HorizontalAlignment.Left };
        chip.Classes.Add("badge");
        chip.Classes.Add("inference-settings-chip");
        if (tone is not null) chip.Classes.Add(tone);
        return chip;
    }

    private static string Describe(SetupActionState state) => state switch
    {
        SetupActionState.Completed => "Done.",
        SetupActionState.NeedsInteraction => "Finish the step above to continue.",
        SetupActionState.Failed => "That did not work. Try again.",
        SetupActionState.Cancelled => "Cancelled.",
        _ => "This is not available right now.",
    };

    private static string Describe(SetupCommitState state) => state switch
    {
        SetupCommitState.Committed => "Saved.",
        SetupCommitState.Conflict => "These settings changed elsewhere. Close them and open them again.",
        SetupCommitState.Invalid => "Fix the problems above, then save.",
        SetupCommitState.Cancelled => "Not saved.",
        SetupCommitState.Failed => "The settings could not be saved.",
        _ => "It is not known whether the settings were saved. Open them again to check.",
    };
}
