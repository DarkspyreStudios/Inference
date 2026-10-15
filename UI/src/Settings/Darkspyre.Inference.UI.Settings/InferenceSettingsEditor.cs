using Darkspyre.Inference.Common.Setup;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
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
    private bool saving;
    private bool running;
    private long editVersion;
    private int stepIndex;
    private SetupProgress? progress;
    private string? copiedCode;
    private string? copyStatus;
    private SetupActionResult? actionResult;
    private SetupCommitResult? commitResult;
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

    /// <summary>Raised after Save. A Committed result means the host closes the dialog.</summary>
    public event EventHandler<SetupCommitResult>? CommitCompleted;

    /// <summary>Raised each time the editor's unsaved, pending, error or can-save state changes.</summary>
    public event EventHandler<SetupEditorState>? StateChanged;

    /// <summary>The editor's current unsaved, pending input, error, can-save and uncertain-save state.</summary>
    public SetupEditorState State => new(this.settings.HasUnsavedChanges || this.inputs.HasPendingInput || this.settings.SaveOutcomeUncertain,
        this.inputs.HasPendingInput, this.inputs.Issues.Any(issue => issue.Severity == SetupIssueSeverity.Error),
        !this.Busy && !this.inputs.HasPendingInput && this.settings.CanSave && !this.AllIssues.Any(issue => issue.Severity == SetupIssueSeverity.Error),
        this.settings.SaveOutcomeUncertain);

    private bool Busy => this.running || this.saving || this.settings.IsSaved;

    private IReadOnlyList<SetupStep> Steps => this.settings.Steps;

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
        (string Label, SetupObservation Observation)[] status = [.. this.StatusItems()];
        if (status.Length > 0)
        {
            WrapPanel line = new() { ItemSpacing = 12, LineSpacing = 6 };
            line.Classes.Add("inference-settings-status");
            foreach ((string label, SetupObservation observation) in status) line.Children.Add(new TextBlock { Text = $"{label}: {Describe(observation)}" });
            head.Children.Add(line);
        }
        if (this.Steps.Count > 1)
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
        this.RenderInteraction();
        this.interactionRegion.IsVisible = this.settings.Interaction is not null;
        StackPanel tail = new() { Spacing = 24 };
        if (this.settings.Actions.Count > 0) tail.Children.Add(this.Actions());
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
        if (this.actionResult is { } action) tail.Children.Add(SetupFieldInput.Text(action.Issues is { Count: > 0 } failed ? failed[0].Message : action.State.ToString()));
        if (this.commitResult is { } commit) tail.Children.Add(SetupFieldInput.Text(commit.Issues is { Count: > 0 } refused ? refused[0].Message : commit.State.ToString()));
        WrapPanel navigation = new() { ItemSpacing = 12, LineSpacing = 8 };
        navigation.Classes.Add("inference-settings-navigation");
        if (this.stepIndex > 0) navigation.Children.Add(Enabled(Button("Back", "ghost", this.PreviousStep), !this.Busy));
        if (this.stepIndex < this.Steps.Count - 1) navigation.Children.Add(Enabled(Button("Next", "primary", this.NextStep), !this.Busy));
        if (this.ShowSaveButton && this.stepIndex >= this.Steps.Count - 1)
            navigation.Children.Add(Enabled(Button("Save settings", "primary", async () => await this.SaveAsync()), this.State.CanSave));
        if (navigation.Children.Count > 0) tail.Children.Add(navigation);
        this.tailRegion.Content = tail;
        this.tailRegion.IsVisible = tail.Children.Count > 0;
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

    // The interaction is rebuilt only when it changes, the editor's busy state changes or the copy status changes.
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
        string shape = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(interaction) + "\n" + this.Busy + "\n" + this.copyStatus;
        if (shape != this.interactionShape)
        {
            this.interactionShape = shape;
            this.interactionRegion.Content = this.Interaction(interaction);
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
        // The step list above names and marks the current step; a single step has no list.
        if (this.Steps.Count <= 1) view.Children.Add(SetupFieldInput.Text(step.Title, "h3"));
        if (!string.IsNullOrWhiteSpace(step.Instructions)) view.Children.Add(SetupFieldInput.Text(step.Instructions));
        if (step.HasPlatformInstructions) view.Children.Add(SetupFieldInput.Text(step.PlatformInstructions));
        foreach (SetupSection section in step.Sections)
        {
            StackPanel body = new() { Spacing = 14 };
            body.Classes.Add("inference-settings-fields");
            foreach (SetupField field in section.Fields.Where(value => value.Description.IsVisible)) body.Children.Add(this.Field(field, live));
            if (section.IsAdvanced)
            {
                string key = step.Id + "/" + section.Title;
                Expander advanced = new() { Header = section.Title, Content = body, HorizontalAlignment = HorizontalAlignment.Stretch };
                advanced.IsExpanded = this.openSections.Contains(key) || this.HasErrors(section);
                advanced.Expanded += (_, _) => this.openSections.Add(key);
                advanced.Collapsed += (_, _) => this.openSections.Remove(key);
                advanced.Classes.Add("inference-settings-section");
                view.Children.Add(advanced);
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
        if (step.HasCompletionRequirement) view.Children.Add(SetupFieldInput.Text(step.CompletionRequirement));
        if (step.HasTroubleshooting) view.Children.Add(SetupFieldInput.Text(step.Troubleshooting));
        return view;
    }

    // An install status field shows as its card; every other field shows its input.
    private Control Field(SetupField field, HashSet<string> live)
    {
        if (field is SetupInstallStatusField install)
            return new SetupInstallStatusCard(install.Status, install.Description.DisplayName, this.Busy, this.RunActionAsync);
        string id = field.Description.Id;
        live.Add(id);
        if (!this.fields.TryGetValue(id, out SetupFieldInput? view))
            this.fields[id] = view = new SetupFieldInput(field, this.settings, this.manager, this.host, this.inputs, this.OnFieldEditedAsync);
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
                if (this.copiedCode == device.UserCode && this.copyStatus is not null) view.Children.Add(SetupFieldInput.Text(this.copyStatus));
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

    private StackPanel Actions()
    {
        StackPanel view = new() { Spacing = 8 };
        view.Classes.Add("inference-settings-actions");
        Avalonia.Automation.AutomationProperties.SetName(view, "Provider actions");
        foreach (SetupActionDescriptor action in this.settings.Actions)
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
        catch (Exception)
        {
            this.actionResult = new(SetupActionState.Failed);
            this.Changed();
            return;
        }
        if (!authorized || this.closed) return;
        using CancellationTokenSource cancellation = new();
        this.actionCancellation = cancellation;
        this.progress = null;
        this.actionResult = null;
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
        catch (Exception) { this.actionResult = new(SetupActionState.Failed); }
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
    }

    private async Task OpenBrowserAsync(Uri uri)
    {
        try
        {
            if (await this.host.AuthorizeAsync("Open authorization page", SetupSideEffect.BrowserInteraction) && !this.closed)
                await this.host.OpenBrowserAsync(uri);
        }
        catch (Exception) when (!this.closed)
        {
            this.actionResult = new(SetupActionState.Failed);
            this.Changed();
        }
    }

    // The clipboard write needs no provider effect or host consent; the code is shown on screen already.
    private async Task CopyCodeAsync(string code)
    {
        this.copiedCode = code;
        try
        {
            IClipboard clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("The editor is not in a window.");
            await clipboard.SetTextAsync(code);
            this.copyStatus = "Code copied.";
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            this.copyStatus = "The code could not be copied. Select it and copy it instead.";
        }
        this.Changed();
    }

    private IEnumerable<(string Label, SetupObservation Observation)> StatusItems()
    {
        SetupStatus status = this.settings.Status;
        return new[]
        {
            ("Configured", status.Configured), ("Authentication checked", status.Authenticated), ("Installed", status.Installed),
            ("Verified", status.Verified), ("Loaded", status.Loaded), ("Ready", status.Ready),
        }.Where(item => item.Item2.IsApplicable);
    }

    private static string Describe(SetupObservation value) => value.IsStale ? "Stale" : value.Value is null ? "Unknown" : value.Value.Value ? "Yes" : "No";
}
