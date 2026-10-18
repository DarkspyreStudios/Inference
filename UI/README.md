# Darkspyre.Inference.UI.Settings

Source package version **11.16.0-rc.1**, synchronized with Common, Connect and Tensor. Its exact Common NuGet
dependency is `[11.16.0-rc.1]`; the package does not embed Common, Connect, Tensor or Avalonia DLLs.

This package provides one host-placed Avalonia editor control for a provider's `InferenceSettings` object and
its `IInferenceSettingsManager`. It renders the settings' fields, validation, status, and setup actions
with standard Avalonia controls. The host chooses where to show the editor and owns its connection list, persistence,
credentials, consent, browser interaction, and long-running operation lifetime.

Among Inference products, the package references `Darkspyre.Inference.Common` only. The host composes Connect or Tensor
settings managers and passes the Common types to `InferenceSettingsEditor`. No provider selection or
connection listing is built into the library.

Capability support uses Common's `InferenceSupport`: `Unknown`, `Supported`, or `Unsupported`.
Unknown leaves an explicit attempt available; it does not claim qualification. Connect's capability
override choices distinguish Default (inherit) from Unknown, Enabled (Supported), and Disabled
(Unsupported). Ordinary binary settings remain checkboxes.

The host supplies `IInferenceSettingsInteractionHost` to authorize declared side effects and
run provider actions through `ExecuteAsync(manager, settings, action, progress, token)`. The component
does not own the settings, the manager, or any engine behind it. The editor carries its own styles,
`InferenceSettingsStyles`. They read the host theme's brushes, so the editor follows the host's light or
dark theme.

```csharp
InferenceSettingsEditor editor = new(settings, manager, interactionHost);
editor.CommitCompleted += (_, result) => OnCommitCompleted(result);
dialog.Content = editor;
```

The host creates `manager` with the owning Connect or Tensor factory and a typed
`ISetupCommitTarget<TChanges>`, then calls the manager's `OpenAsync` for `settings`. Field edits stay
in `settings`. Only Save reaches the host. A Committed `CommitCompleted` result tells the host to close
the dialog and refresh its own list. Closing without Save discards the object. The editor exposes
`SaveAsync()` for a host-owned Save control; set `ShowSaveButton = false` when using that option.
When the manager implements `IInferenceSettingsStatusSource`, the editor listens to `StatusChanged`
while it is shown. After each refresh it renders the new status and reloads its choice fields, so
install chips follow the change.
The status line shows each applicable `SetupStatus` observation as a chip and is absent when none
applies: "Signed in", "Not signed in" or "Sign-in not checked", and the same for Configured,
Installed, Verified, Loaded and Ready. A stale observation reads as not checked; stored sign-in
configuration or a successful Save does not establish a fresh check. Action and save results read
as sentences, never as enum names.
A `SetupInstallStatusField` renders as a status card under the choice it follows: Installed in the
store, with the file count, size and licence and a **Show** button that calls the host's
`RevealFolderAsync` with the store folder; Not downloaded, with a Download button; Files missing,
naming the missing file, with Download again and Delete download; or Not supported, with the
status's `UnsupportedReason` as a line under the chip and the actions the status gives. A disabled
card action shows its reason beside it. The card's buttons run through the manager like any other
action, with the editor's progress and Cancel. A choice whose `SetupChoice.Install` is set shows a
chip for its `State`: "Installed", "Not downloaded" with its download size, or "Not supported".
An Installed chip uses the success style with a check mark, a Not downloaded chip the warning style
with a download arrow, and Files missing and Not supported chips the destructive style with no icon.
When every loaded choice of a field carries `Install`, the field shows the choices as model pack
cards in place of the list: name, download size and install chip ("Installed", "Not downloaded" or
"Not supported"), chosen on click. A `SetupChoiceField` with `ShowValue` false keeps its value but renders no selection, and no card shows as selected; the
status card shows the value instead. A choice's `Tags` show as chips beside its name, and its
description as a line under it.
`IntroContent` and `SupplementaryContent` accept optional host guidance without changing the
provider-owned fields or actions. `MoreOptionsContent` is host content drawn on the first step: at
the end of its advanced section titled "More options", or in a "More options" expander of its own
when the step has none.

## Step actions, the tool checklist and the model test

The tail under a step draws the actions the step names in `SetupStep.ActionIds`, in that order.
When no step of the settings names any action, every action shows on every step. The first of
several steps is the connection step: its instructions show under "About this connection", and its
troubleshooting text only when the person opens "Trouble signing in?".

A step that names a `CheckPrerequisites` action, in settings whose `Status.Installed` applies, shows
a checklist above its actions. The first item reads "✓ Installed: <executable>" once the check has
found the tool, "Not installed" when it has not, and "Not checked yet" while the observation is
unknown or stale; a missing tool's prerequisite shows as an interaction. The second item reads
"✓ Signed in", or offers the step's sign-in action. A step with no sign-in action offers the
prerequisite instructions with their recheck instead. The tail leaves out the actions the checklist
draws. The editor runs the check once when such a step is first shown, and again after a sign-in
completes.

On a step whose `ActionIds` contains `test`, choosing a model from the `model` field's list, or
typing one and pressing Enter or leaving the box, runs the test. Text typed into the manual box,
which shows while no choice is listed, applies on each change and starts no test. A test result
shows "Test reply received in 1.4 s", the result's `Message`, and a **Test again** button.

## Editing an existing connection

`ConnectionTabs` shows a connection's settings on two tabs, "Connection" and "Model", one per step;
each step's own title is its heading, and no step list or Back and Next buttons show. One editor
serves both tabs, so typed input and a sign-in in progress carry across them. Its
`MoreOptionsContent` goes to the Connection tab. The host frames the tabs with the connection's name
and saves through `SaveAsync`; `CommitCompleted` reports the result. A refused save selects the tab
with the problem. `State` and `StateChanged` report the editor's `SetupEditorState`, so the host
enables its Save from `CanSave` and asks before discarding unsaved changes.

## Showing a folder

`IInferenceSettingsInteractionHost.RevealFolderAsync(folder, token)` shows a folder in the platform's
file manager. The install card's **Show** button calls it with the model store folder; an exception
shows its message as the action result.

## Optional native path selection

An interaction host may also implement `IInferenceSettingsPathPicker.SelectPathAsync`. Text fields
with a `SetupPathSelectionKind.File` or `.Folder` hint, plus executable fields, then offer Browse.
Return an absolute path or null for cancellation. The host owns the native dialog and access grants;
manual entry remains available when no picker is supplied. A completion that arrives after a later
edit cannot overwrite that edit. This capability is included starting with 11.4.0-rc.1.
An empty executable input shows the field's default as a placeholder. Rendering this hint does not
change the field value or apply an edit.

## Host-framed presentation

A host that frames the editor with its own title sets `ShowHeading = false`; the editor then omits
its provider name and description. A host that treats the person's click as consent for declared
side effects sets `ShowActionEffects = false` to omit the effect labels beside each action. Both
default to `true`.

When the settings have several steps, the step list names and marks the current step (class `current`),
and the step's own title is not repeated; single-step settings keep their title. A host that draws its
own step strip sets `ShowStepList = false`: the list is hidden, each step shows its title, and Back and
Next stay. `CurrentStepIndex` is the shown step, and `StepChanged` reports each move to another step. A device
authorization copies its user code to the clipboard the first time the code is shown, once per code, and shows a
**Copy code** button that copies it again. Both go through
`IInferenceSettingsInteractionHost.CopyToClipboardAsync(source, text, copied)`, which writes the clipboard and tells
the person the result with the host's transient notice; the editor passes `Sign-in code copied to clipboard.`. The
code itself selects in one click. A searchable choice list shows its separate
provider-search box only while the loaded list is incomplete or a search is active; otherwise the
auto-complete box filters the loaded choices. A choice whose value differs from its display name shows the
value on a second line, and the filter matches either one.

## Editor state and Save

Subscribe to `StateChanged` for a safe `SetupEditorState` (also available as `editor.State`).
`HasUnsavedChanges` combines unsaved settings changes with locally invalid/unapplied input and an
uncertain save. Use this signal for host-owned discard confirmation. `HasPendingInput` and
`HasInputErrors` explain local blockers. `CanSave` is true when every required field has a value, any
required sign-in has succeeded, no action or save is running, and no local input is invalid.
`CommitOutcomeUncertain` requires host readback, not an automatic save retry.

```csharp
InferenceSettingsEditor editor = new(settings, manager, interactionHost) { ShowSaveButton = false };
editor.StateChanged += (_, state) => saveButton.IsEnabled = state.CanSave;
editor.CommitCompleted += (_, result) => OnCommitCompleted(result);
```

The host may enable its Save control from `state.CanSave` and invoke `editor.SaveAsync()`.
That method and the built-in Save share the same validation gate. Invalid URL/number text and
unapplied secret text cannot silently save a previous valid value. Back preserves pending input;
replacing the settings clears it. The host must ask about discarding edits before replacing the
settings. Saved settings are not saved again; open the entry again for a subsequent edit.

Bounded choices use a combo box without search or refresh controls. Choice capabilities opt into discovery
controls. Numeric fields use UI.Settings-owned exact `long`/`decimal` parsing and stepping
(`SetupNumberInput<T>`). Invalid or out-of-range typed text is preserved and blocks Save; only explicit
stepping clamps to bounds. A text box reports text the editor sets during a redraw; the inputs treat only
text that differs from the shown value as an edit.
