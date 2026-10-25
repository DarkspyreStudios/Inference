using Darkspyre.Inference.Common.Setup;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.UI.Settings.Avalonia;

/// <summary>
/// One setup field with the input that fits its kind. Each edit is applied to the settings, and the field's first error
/// shows under it. The view is kept while the field stays in the editor, so typed text and focus survive a redraw.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A control is never disposed: the editor calls Release when the field leaves it or the editor closes, which cancels the field's work.")]
public sealed class SetupFieldInput : UserControl
{
    private readonly IAvaloniaSettingsInteractionHost host;
    private readonly IInferenceSettingsManager manager;
    private readonly InferenceSettings settings;
    private readonly SetupEditorInputState inputs;
    private readonly Func<SetupEditResult, Task> edited;
    private readonly Func<SetupFieldInput, Task>? chosen;
    private readonly bool sensitiveInput;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock error = Text(string.Empty, "inference-settings-field-error");
    private readonly ContentControl body = new();
    private readonly Dictionary<string, SetupFieldInput> children = new(StringComparer.Ordinal);
    private SetupField field;
    private IReadOnlyList<SetupIssue> issues = [];
    private long editVersion;
    private bool disabled;
    private bool syncing;
    private bool picking;
    private bool disposed;
    // Built once for the field's kind and updated on each redraw.
    private TextBox? text;
    private SetupNumberInput<long>? integer;
    private SetupNumberInput<decimal>? number;
    private CheckBox? boolean;
    private ComboBox? fixedChoices;
    private AutoCompleteBox? openChoices;
    private TextBox? manualChoice;
    private TextBox? search;
    private TextBox? secret;
    private StackPanel? choiceActions;
    private WrapPanel? packCards;
    private TextBlock? choiceStatus;
    private TextBlock? secretState;
    private Button? browse;
    private Button? replace;
    // Choice loading.
    private List<SetupChoice> choices = [];
    private string? continuation;
    private bool loading;
    private string? choiceIssue;
    private CancellationTokenSource? choiceCancellation;
    private long choiceGeneration;
    private long requestedVersion = -1;
    private long loadedVersion = -1;
    private bool wasUnavailable;

    /// <summary>Creates the input for <paramref name="field"/>.</summary>
    /// <param name="field">The setup field this input shows and edits.</param>
    /// <param name="settings">The provider settings the field belongs to.</param>
    /// <param name="manager">The manager that loads the field's choices.</param>
    /// <param name="host">The host that authorizes choice loading and, when it picks paths, shows a Browse button.</param>
    /// <param name="inputs">The editor's typed input, kept across steps.</param>
    /// <param name="edited">Receives the result after each edit is applied to the field.</param>
    /// <param name="sensitiveInput">True masks a text field's input and keeps the typed text when the field is redrawn.</param>
    /// <param name="chosen">Receives the input after a choice is picked from the list, or typed and committed, and applied.
    /// Text typed into the manual box applies on each change and does not count as a choice.</param>
    internal SetupFieldInput(SetupField field, InferenceSettings settings, IInferenceSettingsManager manager, IAvaloniaSettingsInteractionHost host,
        SetupEditorInputState inputs, Func<SetupEditResult, Task> edited, bool sensitiveInput = false, Func<SetupFieldInput, Task>? chosen = null)
    {
        this.field = field;
        this.settings = settings;
        this.manager = manager;
        this.host = host;
        this.inputs = inputs;
        this.edited = edited;
        this.chosen = chosen;
        this.sensitiveInput = sensitiveInput;
        this.error.IsVisible = false;
        this.Classes.Add("inference-settings-field");
        this.Content = this.Build();
    }

    /// <summary>The field the input shows.</summary>
    public SetupField Field => this.field;

    private SetupEditorInputState.Input Input => this.inputs.Get(this.field.Description.Id);

    private string Value
    {
        get => this.Input.Text;
        set
        {
            this.Input.Text = value;
            this.Input.Pending = true;
        }
    }

    private bool Unavailable => this.disabled || this.field.Description.IsReadOnly || !this.field.Description.IsEnabled || !this.field.Description.IsVisible;

    private string? FieldError => this.Input.Issues.FirstOrDefault(issue => issue.Severity == SetupIssueSeverity.Error)?.Message
        ?? this.issues.FirstOrDefault(issue => issue.FieldId == this.field.Description.Id && issue.Severity == SetupIssueSeverity.Error)?.Message;

    /// <summary>
    /// Redraws the field for its latest <paramref name="current"/> state. <paramref name="version"/> is the editor's count
    /// of local edits; a choice result requested at an earlier count is dropped. <paramref name="editorIssues"/> are the
    /// editor's issues, and <paramref name="isDisabled"/> makes the field read only.
    /// </summary>
    public void Update(SetupField current, long version, IReadOnlyList<SetupIssue> editorIssues, bool isDisabled)
    {
        this.field = current;
        this.editVersion = version;
        this.issues = editorIssues;
        this.disabled = isDisabled;
        this.IsVisible = current.Description.IsVisible;
        if (!this.Input.Initialized || !this.Input.Pending && !this.sensitiveInput)
        {
            this.Input.Text = current switch
            {
                SetupTextField value => value.Value,
                SetupUriField value => value.Value?.ToString() ?? string.Empty,
                SetupExecutableField value => value.Value.Value,
                SetupIntegerField value => value.Value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                SetupDecimalField value => value.Value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                SetupChoiceField value => value.Value,
                _ => string.Empty,
            };
            this.Input.Initialized = true;
        }
        this.Render();
        bool becameAvailable = this.wasUnavailable && !this.Unavailable;
        this.wasUnavailable = this.Unavailable;
        if (current is SetupChoiceField && (this.requestedVersion != version || becameAvailable))
        {
            this.requestedVersion = version;
            _ = this.LoadChoicesAsync(reset: true);
        }
    }

    /// <summary>Stops the field's work, as when it leaves the editor or the editor closes. A view moved between panels keeps working.</summary>
    internal void Release()
    {
        if (this.disposed) return;
        this.disposed = true;
        this.lifetime.Cancel();
        this.choiceCancellation?.Cancel();
        foreach (SetupFieldInput child in this.children.Values) child.Release();
    }

    internal static TextBlock Text(string value, string? kind = null)
    {
        TextBlock block = new() { Text = value, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
        if (kind is not null) block.Classes.Add(kind);
        return block;
    }

    private StackPanel Build()
    {
        SetupFieldDescription description = this.field.Description;
        if (this.field is SetupRepeatableGroupField)
        {
            StackPanel group = new() { Spacing = 14 };
            group.Classes.Add("inference-settings-repeatable");
            return group;
        }
        if (this.field is SetupBooleanField)
        {
            this.boolean = new CheckBox { Content = description.DisplayName };
            this.boolean.IsCheckedChanged += (_, _) =>
            {
                if (!this.syncing && this.field is SetupBooleanField current) _ = this.ApplyAsync(current.SetValue(this.boolean.IsChecked == true));
            };
            StackPanel layout = new() { Spacing = 4 };
            layout.Children.Add(this.boolean);
            if (!string.IsNullOrWhiteSpace(description.HelpText)) layout.Children.Add(Text(description.HelpText, "inference-settings-help"));
            layout.Children.Add(this.error);
            return layout;
        }
        StackPanel field = new() { Spacing = 6 };
        field.Classes.Add("field");
        TextBlock label = Text(description.DisplayName + (description.IsRequired ? " *" : string.Empty), "label");
        field.Children.Add(label);
        field.Children.Add(this.body);
        if (!string.IsNullOrWhiteSpace(description.HelpText)) field.Children.Add(Text(description.HelpText, "inference-settings-help"));
        field.Children.Add(this.error);
        this.body.Content = this.field switch
        {
            SetupTextField textField => this.TextBoxInput(textField.PathSelection is not null, this.sensitiveInput, textField.MaximumLength),
            SetupUriField => this.TextBoxInput(false, false, null),
            SetupExecutableField executable => this.TextBoxInput(true, false, null, executable.DefaultValue.Value),
            SetupIntegerField whole => this.Integer(whole),
            SetupDecimalField fraction => this.Decimal(fraction),
            SetupChoiceField choice => this.Choice(choice),
            SetupSecretField => this.Secret(),
            _ => null,
        };
        return field;
    }

    private Control TextBoxInput(bool picker, bool masked, int? maximumLength, string? placeholder = null)
    {
        this.text = new TextBox { PlaceholderText = placeholder, MaxLength = maximumLength ?? 0 };
        if (masked) this.text.PasswordChar = '•';
        global::Avalonia.Automation.AutomationProperties.SetName(this.text, this.field.Description.DisplayName);
        this.text.TextChanged += (_, _) =>
        {
            string value = this.text.Text ?? string.Empty;
            // A text box reports a redraw's text after the redraw ends; only text that differs from the input is an edit.
            if (this.syncing || value == this.Value) return;
            _ = this.field switch
            {
                SetupTextField current => this.SetTextAsync(current, value),
                SetupUriField current => this.SetUriAsync(current, value),
                SetupExecutableField current => this.SetExecutableAsync(current, value),
                _ => Task.CompletedTask,
            };
        };
        if (!picker || this.host is not IInferenceSettingsPathPicker) return this.text;
        this.browse = InferenceSettingsEditor.Button("Browse…", "outline", this.PickPathAsync);
        DockPanel row = new();
        DockPanel.SetDock(this.browse, Dock.Right);
        this.browse.Margin = new global::Avalonia.Thickness(8, 0, 0, 0);
        row.Children.Add(this.browse);
        row.Children.Add(this.text);
        return row;
    }

    private SetupNumberInput<long> Integer(SetupIntegerField whole)
    {
        this.integer = new SetupNumberInput<long>(whole.Description.DisplayName, whole.Minimum, whole.Maximum, 1, whole.Description.IsRequired);
        this.integer.Edited += (_, edit) =>
        {
            if (this.field is SetupIntegerField current) _ = this.SetNumberAsync(current, edit);
        };
        return this.integer;
    }

    private SetupNumberInput<decimal> Decimal(SetupDecimalField fraction)
    {
        this.number = new SetupNumberInput<decimal>(fraction.Description.DisplayName, fraction.Minimum, fraction.Maximum, fraction.Step,
            fraction.Description.IsRequired);
        this.number.Edited += (_, edit) =>
        {
            if (this.field is SetupDecimalField current) _ = this.SetNumberAsync(current, edit);
        };
        return this.number;
    }

    private StackPanel Choice(SetupChoiceField choice)
    {
        StackPanel layout = new() { Spacing = 6 };
        // The list filters loaded choices itself; provider search matters only while more remain.
        // A list can select the shown value after a redraw has finished; only a different value is an edit.
        if (choice.Capabilities.SupportsSearch != InferenceSupport.Unsupported)
        {
            this.search = new TextBox { PlaceholderText = "Search choices" };
            Button run = InferenceSettingsEditor.Button("Search", "ghost", () => this.LoadChoicesAsync(reset: true));
            DockPanel row = new();
            DockPanel.SetDock(run, Dock.Right);
            row.Children.Add(run);
            row.Children.Add(this.search);
            row.Classes.Add("inference-settings-choice-search");
            layout.Children.Add(row);
        }
        bool fixedList = choice.Capabilities.SupportsSearch == InferenceSupport.Unsupported
            && choice.Capabilities.SupportsPaging == InferenceSupport.Unsupported && !choice.AllowsManualEntry;
        FuncDataTemplate<SetupChoice> template = new((option, _) => ChoiceView(option));
        if (fixedList)
        {
            this.fixedChoices = new ComboBox { ItemTemplate = template, HorizontalAlignment = HorizontalAlignment.Stretch };
            this.fixedChoices.SelectionBoxItemTemplate = new FuncDataTemplate<SetupChoice>((option, _) => new TextBlock { Text = option?.DisplayName });
            global::Avalonia.Automation.AutomationProperties.SetName(this.fixedChoices, choice.Description.DisplayName);
            this.fixedChoices.SelectionChanged += (_, _) =>
            {
                if (!this.syncing && this.fixedChoices.SelectedItem is SetupChoice picked && picked.Value != this.Value && this.field is SetupChoiceField current)
                    _ = this.ChooseAsync(current, picked.Value);
            };
            layout.Children.Add(this.fixedChoices);
        }
        else
        {
            // Providers can give distinct values one display name, so the value names the choice.
            this.openChoices = new AutoCompleteBox
            {
                ItemTemplate = template,
                MinimumPrefixLength = 0,
                FilterMode = AutoCompleteFilterMode.Custom,
                ItemFilter = (typed, item) => item is SetupChoice option && (string.IsNullOrEmpty(typed)
                    || ChoiceSearchText(option).Contains(typed, StringComparison.OrdinalIgnoreCase)),
                ItemSelector = (_, item) => item is SetupChoice option ? option.Value : string.Empty,
                PlaceholderText = "Choose or search",
            };
            global::Avalonia.Automation.AutomationProperties.SetName(this.openChoices, choice.Description.DisplayName);
            this.openChoices.SelectionChanged += (_, _) =>
            {
                if (!this.syncing && this.openChoices.SelectedItem is SetupChoice picked && picked.Value != this.Value && this.field is SetupChoiceField current)
                    _ = this.ChooseAsync(current, picked.Value);
            };
            // A value typed by hand is taken when the person leaves the box or presses Enter.
            void Commit()
            {
                if (this.syncing || this.field is not SetupChoiceField { AllowsManualEntry: true } current) return;
                string typed = this.openChoices.Text ?? string.Empty;
                if (typed != this.Value) _ = this.ChooseAsync(current, typed);
            }
            this.openChoices.LostFocus += (_, _) => Commit();
            this.openChoices.AddHandler(KeyDownEvent, (_, args) =>
            {
                if (args.Key == Key.Enter) Commit();
            }, RoutingStrategies.Tunnel);
            layout.Children.Add(this.openChoices);
            if (choice.AllowsManualEntry)
            {
                this.manualChoice = new TextBox();
                global::Avalonia.Automation.AutomationProperties.SetName(this.manualChoice, choice.Description.DisplayName);
                this.manualChoice.TextChanged += (_, _) =>
                {
                    if (!this.syncing && (this.manualChoice.Text ?? string.Empty) != this.Value && this.field is SetupChoiceField current)
                        _ = this.SetChoiceAsync(current, this.manualChoice.Text ?? string.Empty);
                };
                layout.Children.Add(this.manualChoice);
            }
        }
        // Model packs show as cards in place of the list once every loaded choice has local files.
        this.packCards = new WrapPanel { ItemSpacing = 12, LineSpacing = 12, IsVisible = false };
        this.packCards.Classes.Add("inference-model-pack-cards");
        global::Avalonia.Automation.AutomationProperties.SetName(this.packCards, choice.Description.DisplayName);
        layout.Children.Add(this.packCards);
        this.choiceActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        this.choiceActions.Classes.Add("inference-settings-choice-actions");
        layout.Children.Add(this.choiceActions);
        this.choiceStatus = Text(string.Empty, "inference-settings-status");
        layout.Children.Add(this.choiceStatus);
        return layout;
    }

    private StackPanel Secret()
    {
        StackPanel layout = new() { Spacing = 6 };
        this.secretState = Text(string.Empty);
        layout.Children.Add(this.secretState);
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Classes.Add("inference-settings-secret-actions");
        actions.Children.Add(InferenceSettingsEditor.Button("Keep", "ghost", () => this.SetSecretAsync(SetupSecretEdit.Keep)));
        actions.Children.Add(InferenceSettingsEditor.Button("Clear", "ghost", () => this.SetSecretAsync(SetupSecretEdit.Clear)));
        layout.Children.Add(actions);
        this.secret = new TextBox { PasswordChar = '•', PlaceholderText = "New secret" };
        global::Avalonia.Automation.AutomationProperties.SetName(this.secret, this.field.Description.DisplayName);
        this.secret.TextChanged += (_, _) =>
        {
            if (this.syncing || (this.secret.Text ?? string.Empty) == this.Input.Secret) return;
            this.Input.Secret = this.secret.Text ?? string.Empty;
            this.Input.Pending = this.Input.Secret.Length != 0;
            this.Input.Issues = [];
            this.replace!.IsEnabled = !this.Unavailable && !(this.field.Description.IsRequired && this.Input.Secret.Length == 0);
            _ = this.edited(new(SetupEditState.Applied, []));
        };
        layout.Children.Add(this.secret);
        this.replace = InferenceSettingsEditor.Button("Replace secret", "outline", this.ReplaceSecretAsync);
        this.replace.HorizontalAlignment = HorizontalAlignment.Left;
        layout.Children.Add(this.replace);
        return layout;
    }

    private void Render()
    {
        bool unavailable = this.Unavailable;
        this.syncing = true;
        try
        {
            switch (this.field)
            {
                case SetupRepeatableGroupField group:
                    this.RenderGroup(group, unavailable);
                    break;
                case SetupBooleanField value:
                    this.boolean!.IsChecked = value.Value;
                    this.boolean.IsEnabled = !unavailable;
                    break;
                case SetupIntegerField:
                    this.integer!.Show(this.Value, unavailable, this.FieldError is not null);
                    break;
                case SetupDecimalField:
                    this.number!.Show(this.Value, unavailable, this.FieldError is not null);
                    break;
                case SetupChoiceField choice:
                    this.RenderChoice(choice, unavailable);
                    break;
                case SetupSecretField secretField:
                    this.secretState!.Text = secretField.Change switch
                    {
                        SetupSecretEditKind.Clear => "The saved secret will be cleared when settings are saved.",
                        SetupSecretEditKind.Replace => "The secret will be replaced when settings are saved.",
                        _ => secretField.HasValue ? "A secret is saved." : "No secret is saved.",
                    };
                    if (this.secret!.Text != this.Input.Secret) this.secret.Text = this.Input.Secret;
                    this.secret.IsEnabled = !unavailable;
                    this.replace!.IsEnabled = !unavailable && !(secretField.Description.IsRequired && this.Input.Secret.Length == 0);
                    foreach (Button action in ((StackPanel)this.secret.Parent!).Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<Button>()))
                        action.IsEnabled = !unavailable;
                    break;
                default:
                    if (this.text is not null)
                    {
                        if (this.text.Text != this.Value) this.text.Text = this.Value;
                        this.text.IsEnabled = !unavailable;
                    }
                    this.browse?.IsEnabled = !unavailable && !this.picking;
                    break;
            }
        }
        finally { this.syncing = false; }
        this.error.Text = this.FieldError ?? string.Empty;
        this.error.IsVisible = this.FieldError is not null;
    }

    private void RenderGroup(SetupRepeatableGroupField group, bool unavailable)
    {
        StackPanel layout = (StackPanel)this.Content!;
        layout.Children.Clear();
        layout.Children.Add(Text(group.Description.DisplayName, "label"));
        HashSet<string> live = new(StringComparer.Ordinal);
        foreach (SetupFieldRow row in group.Rows)
        {
            WrapPanel line = new() { ItemSpacing = 14, LineSpacing = 14 };
            line.Classes.Add("inference-settings-row");
            foreach (SetupField child in row.Fields.Where(item => item.Description.IsVisible))
            {
                string key = row.Id + "/" + child.Description.Id;
                live.Add(key);
                if (!this.children.TryGetValue(key, out SetupFieldInput? view))
                    this.children[key] = view = new SetupFieldInput(child, this.settings, this.manager, this.host, this.inputs, this.edited);
                (view.Parent as Panel)?.Children.Remove(view);
                view.MinWidth = 224;
                view.Update(child, this.editVersion, this.issues, this.disabled);
                line.Children.Add(view);
            }
            string id = row.Id;
            Button remove = InferenceSettingsEditor.Button("Remove row", "ghost", () => this.ApplyAsync(group.Remove(id)));
            remove.IsEnabled = !unavailable;
            remove.VerticalAlignment = VerticalAlignment.Bottom;
            line.Children.Add(remove);
            layout.Children.Add(line);
        }
        foreach (string gone in this.children.Keys.Where(key => !live.Contains(key)).ToArray())
        {
            this.children[gone].Release();
            this.children.Remove(gone);
        }
        Button add = InferenceSettingsEditor.Button("Add row", "outline", () => this.ApplyAsync(group.Add()));
        add.IsEnabled = !unavailable;
        add.HorizontalAlignment = HorizontalAlignment.Left;
        layout.Children.Add(add);
    }

    private void RenderChoice(SetupChoiceField choice, bool unavailable)
    {
        string shown = choice.ShowValue ? this.Value : string.Empty;
        bool cards = this.choices.Count > 0 && this.choices.All(option => option.Install is not null);
        if (this.fixedChoices is { } list)
        {
            if (!ReferenceEquals(list.ItemsSource, this.choices)) list.ItemsSource = this.choices;
            list.SelectedItem = this.choices.FirstOrDefault(option => option.Value == shown);
            list.IsEnabled = !unavailable;
            list.IsVisible = !cards;
        }
        if (this.openChoices is { } open)
        {
            if (!ReferenceEquals(open.ItemsSource, this.choices)) open.ItemsSource = this.choices;
            if (open.Text != shown && !open.IsKeyboardFocusWithin) open.Text = shown;
            open.IsEnabled = !unavailable;
            open.IsVisible = !cards && (this.choices.Count > 0 || !choice.AllowsManualEntry);
        }
        if (this.manualChoice is { } manual)
        {
            manual.IsVisible = this.choices.Count == 0;
            if (manual.Text != this.Value) manual.Text = this.Value;
            manual.IsEnabled = !unavailable;
        }
        if (this.search is { } box)
        {
            box.IsEnabled = !unavailable;
            ((Control)box.Parent!).IsVisible = !cards && (this.continuation is not null || (box.Text?.Length ?? 0) > 0);
        }
        this.packCards!.Children.Clear();
        this.packCards.IsVisible = cards;
        if (cards)
        {
            foreach (SetupChoice option in this.choices)
            {
                ModelPackCard card = new(option, option.Value == shown, () =>
                {
                    if (!this.Unavailable && option.Value != this.Value && this.field is SetupChoiceField current) _ = this.ChooseAsync(current, option.Value);
                })
                { IsEnabled = !unavailable };
                this.packCards.Children.Add(card);
            }
        }
        this.choiceActions!.Children.Clear();
        if (choice.Capabilities.SupportsRefresh != InferenceSupport.Unsupported)
        {
            Button refresh = InferenceSettingsEditor.Button("Refresh choices", "ghost", () => this.LoadChoicesAsync(reset: true));
            refresh.IsEnabled = !this.loading && !unavailable;
            this.choiceActions.Children.Add(refresh);
        }
        if (this.continuation is not null && choice.Capabilities.SupportsPaging != InferenceSupport.Unsupported)
        {
            Button more = InferenceSettingsEditor.Button("Load more", "ghost", this.LoadMoreAsync);
            more.IsEnabled = !this.loading && !unavailable;
            this.choiceActions.Children.Add(more);
        }
        string? status = this.loading ? "Loading choices…" : this.choiceIssue;
        this.choiceStatus!.Text = status ?? string.Empty;
        this.choiceStatus.IsVisible = status is not null;
    }

    // An option with local files shows its install state and, when not downloaded, its download size. Its tags show as chips,
    // and its description as a line under the name.
    private static StackPanel ChoiceView(SetupChoice? option)
    {
        StackPanel line = new() { Spacing = 2 };
        if (option is null) return line;
        WrapPanel head = new() { ItemSpacing = 8, LineSpacing = 4 };
        head.Children.Add(new TextBlock { Text = option.DisplayName, VerticalAlignment = VerticalAlignment.Center });
        if (option.Install is { } install)
            head.Children.Add(SetupInstallChip.ForChoice(install, $"Not downloaded · {SetupByteSize.Format(install.Size)}"));
        foreach (string tag in option.Tags) head.Children.Add(InferenceSettingsEditor.Chip(tag, null));
        line.Children.Add(head);
        if (option.DisplayName != option.Value) line.Children.Add(Text(option.Value, "inference-settings-choice-value"));
        if (!string.IsNullOrWhiteSpace(option.Description)) line.Children.Add(Text(option.Description, "inference-settings-help"));
        return line;
    }

    private static string ChoiceSearchText(SetupChoice choice) =>
        choice.DisplayName == choice.Value ? choice.Value : choice.DisplayName + " " + choice.Value;

    private async Task PickPathAsync()
    {
        if (this.Unavailable || this.picking || this.host is not IInferenceSettingsPathPicker picker) return;
        SetupField current = this.field;
        long version = this.editVersion;
        string previous = this.Value;
        SetupPathSelectionKind kind = current is SetupTextField pathField ? pathField.PathSelection!.Value : SetupPathSelectionKind.File;
        this.picking = true;
        this.Render();
        try
        {
            string? value = await picker.SelectPathAsync(kind, previous, this.lifetime.Token);
            if (value is null || this.disposed || this.Unavailable || version != this.editVersion || !ReferenceEquals(current, this.field) || this.Value != previous) return;
            if (current is SetupTextField path) await this.SetTextAsync(path, value);
            else if (current is SetupExecutableField executable) await this.SetExecutableAsync(executable, value);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (!this.disposed)
        {
            await this.InvalidAsync($"The host could not select a path ({error.GetType().Name}): {error.Message} Enter it manually or try again.");
        }
        finally
        {
            this.picking = false;
            if (!this.disposed) this.Render();
        }
    }

    private async Task ApplyAsync(SetupEditResult result)
    {
        if (this.disposed) return;
        this.Input.Pending = result.State != SetupEditState.Applied;
        this.Input.Issues = result.Issues.Count > 0 ? result.Issues : this.Input.Pending
            ? [new("input-" + result.State.ToString().ToLowerInvariant(), "The edit was not applied. Review the current settings and try again.", this.field.Description.Id)]
            : [];
        await this.edited(result);
    }

    private Task SetTextAsync(SetupTextField current, string value)
    {
        this.Value = value;
        return this.ApplyAsync(current.SetValue(value));
    }

    private Task SetExecutableAsync(SetupExecutableField current, string value)
    {
        this.Value = value;
        return this.ApplyAsync(current.SetValue(new(value)));
    }

    private Task SetChoiceAsync(SetupChoiceField current, string value)
    {
        this.Value = value;
        return this.ApplyAsync(current.SetValue(value));
    }

    // A choice picked from the list, or typed and committed: applied, then reported as chosen.
    private async Task ChooseAsync(SetupChoiceField current, string value)
    {
        await this.SetChoiceAsync(current, value);
        if (!this.disposed && !this.Input.Pending && this.chosen is { } report) await report(this);
    }

    private Task SetUriAsync(SetupUriField current, string value)
    {
        this.Value = value;
        if (value.Length > 0 && !Uri.TryCreate(value, UriKind.Absolute, out _)) return this.InvalidAsync("Enter an absolute URL.");
        return this.ApplyAsync(current.SetValue(value.Length == 0 ? null : new Uri(value, UriKind.Absolute)));
    }

    private Task SetNumberAsync<T>(SetupValueField<T?> current, SetupNumberEdit<T> edit) where T : struct
    {
        this.Value = edit.Text;
        return edit.Error is null ? this.ApplyAsync(current.SetValue(edit.Value)) : this.InvalidAsync(edit.Error);
    }

    private Task InvalidAsync(string message) =>
        this.ApplyAsync(new(SetupEditState.Invalid, [new("input-invalid", message, this.field.Description.Id)]));

    private async Task ReplaceSecretAsync()
    {
        if (this.field is not SetupSecretField current) return;
        SetupEditResult result = current.SetValue(SetupSecretEdit.Replace(this.Input.Secret));
        if (result.State == SetupEditState.Applied) this.Input.Secret = string.Empty;
        await this.ApplyAsync(result);
    }

    private async Task SetSecretAsync(SetupSecretEdit change)
    {
        if (this.field is not SetupSecretField current) return;
        this.Input.Secret = string.Empty;
        await this.ApplyAsync(current.SetValue(change));
    }

    private Task LoadMoreAsync() => this.loading || this.loadedVersion != this.editVersion ? Task.CompletedTask : this.LoadChoicesAsync(reset: false);

    private async Task LoadChoicesAsync(bool reset)
    {
        long generation = ++this.choiceGeneration;
        this.choiceCancellation?.Cancel();
        if (reset)
        {
            this.choices = [];
            this.continuation = null;
            this.loadedVersion = -1;
        }
        this.loading = false;
        if (this.field is not SetupChoiceField choice || this.Unavailable || this.disposed) return;
        // The values this query reads; a result that arrives after a later edit is dropped.
        long version = this.editVersion;
        string query = this.search?.Text ?? string.Empty;
        string? after = this.continuation;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token);
        this.choiceCancellation = cancellation;
        this.loading = true;
        this.choiceIssue = null;
        this.Render();
        bool IsCurrent() => !this.disposed && !cancellation.IsCancellationRequested && generation == this.choiceGeneration && version == this.editVersion;
        try
        {
            if (choice.ChoiceSideEffects != SetupSideEffect.None
                && !await this.host.AuthorizeAsync("Load " + choice.Description.DisplayName, choice.ChoiceSideEffects, cancellation.Token))
            {
                if (IsCurrent()) this.choiceIssue = "Choice loading was not authorized.";
                return;
            }
            if (!IsCurrent()) return;
            SetupChoicePage page = await this.manager.QueryChoicesAsync(this.settings, choice.Description.Id, new(query, after,
                choice.Capabilities.SupportsPaging != InferenceSupport.Unsupported ? 50 : 200), cancellation.Token);
            if (!IsCurrent()) return;
            if (page.State == SetupChoiceState.Completed)
            {
                this.choices = [.. this.choices.Concat(page.Choices).DistinctBy(item => item.Value)];
                this.continuation = page.Continuation;
                this.loadedVersion = version;
                if (page.Continuation is not null && choice.Capabilities.SupportsPaging == InferenceSupport.Unsupported)
                    this.choiceIssue = "The provider returned an incomplete bounded list.";
            }
            else this.choiceIssue = page.Issues is { Count: > 0 } issues ? issues[0].Message : page.State.ToString();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (IsCurrent()) { this.choiceIssue = $"Choices could not be loaded ({error.GetType().Name}): {error.Message}"; }
        finally
        {
            if (generation == this.choiceGeneration) this.loading = false;
            if (ReferenceEquals(this.choiceCancellation, cancellation)) this.choiceCancellation = null;
            if (!this.disposed) Dispatcher.UIThread.Post(this.Render);
        }
    }
}
