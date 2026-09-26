# Darkspyre.Inference.UI.Settings

Version **11.10.4-rc.1**, synchronized with Common, Connect and Tensor. Its exact Common NuGet
dependency is `[11.10.4-rc.1]`; the package does not embed Common, Connect, Tensor or Lumeo DLLs.
The release-candidate suffix reflects the .NET 11 RC ASP.NET component dependency.

This package provides one host-placed Blazor editor for a provider's `InferenceSettings` object and
its `IInferenceSettingsManager`. It renders the settings' fields, validation, status, and setup actions
with Lumeo. The host chooses where to show the editor and owns its connection list, persistence,
credentials, consent, browser interaction, and long-running operation lifetime.

Among Inference products, the package references `Darkspyre.Inference.Common` only. The host composes Connect or Tensor
settings managers and passes the Common types to `InferenceSettingsEditor`. No provider selection or
connection listing is built into the library.

The host supplies `IInferenceSettingsInteractionHost` to authorize declared side effects and
run provider actions through `ExecuteAsync(manager, settings, action, progress, token)`. The component
does not own the settings, the manager, or any engine behind it. The host must register
Lumeo and include its theme assets and this package's scoped CSS through the normal Razor Class
Library static-web-assets pipeline.

```razor
<InferenceSettingsEditor Settings="@settings"
                         Manager="@manager"
                         Host="@interactionHost"
                         CommitCompleted="OnCommitCompletedAsync" />
```

The host creates `manager` with the owning Connect or Tensor factory and a typed
`ISetupCommitTarget<TChanges>`, then calls the manager's `OpenAsync` for `settings`. Field edits stay
in `settings`. Only Save reaches the host. A Committed `CommitCompleted` result tells the host to close
the dialog and refresh its own list. Closing without Save discards the object. The editor exposes
`SaveAsync()` for a host-owned Save control; set `ShowSaveButton="false"` when using that option.
When the manager implements `IInferenceSettingsStatusSource`, the editor listens to `StatusChanged`
while it is shown and renders the refreshed status.
`IntroContent` and `SupplementaryContent` accept optional host guidance without changing the
provider-owned fields or actions.

The canonical technical design is [the Settings UI design](../docs/settings.ui.tech-design.md).

## Unpublished source: optional native path selection

An interaction host may also implement `IInferenceSettingsPathPicker.SelectPathAsync`. Text fields
with a `SetupPathSelectionKind.File` or `.Folder` hint, plus executable fields, then offer Browse.
Return an absolute path or null for cancellation. The host owns the native dialog and access grants;
manual entry remains available when no picker is supplied. A completion that arrives after a later
edit cannot overwrite that edit. This capability is included starting with 11.4.0-rc.1.

## Unpublished source: host-framed presentation

A host that frames the editor with its own title sets `ShowHeading="false"`; the editor then omits
its provider name and description. A host that treats the person's click as consent for declared
side effects sets `ShowActionEffects="false"` to omit the effect labels beside each action. Both
default to `true`.

When the settings have several steps, the step list names and marks the current step (`aria-current`),
and the step's own title is not repeated; single-step settings keep their title. A device
authorization shows a **Copy code** button that writes the user code to the clipboard and reports
the outcome; the code itself selects in one click. A searchable choice list shows its separate
provider-search box only while the loaded list is incomplete or a search is active; otherwise the
combobox filters the loaded choices. A choice whose value differs from its display name shows the
value on a second line, and the combobox filter matches either one.

## Editor state and Save

Subscribe to `StateChanged` for a safe `SetupEditorState` (also available as `editor.State`).
`HasUnsavedChanges` combines unsaved settings changes with locally invalid/unapplied input and an
uncertain save. Use this signal for host-owned discard confirmation. `HasPendingInput` and
`HasInputErrors` explain local blockers. `CanSave` is true when every required field has a value, any
required sign-in has succeeded, no action or save is running, and no local input is invalid.
`CommitOutcomeUncertain` requires host readback, not an automatic save retry.

```razor
<InferenceSettingsEditor @ref="editor" Settings="@settings" Manager="@manager" Host="@interactionHost"
                         ShowSaveButton="false" StateChanged="@(state => editorState = state)"
                         CommitCompleted="OnCommitCompletedAsync" />
```

The host may enable its Save control from `editorState.CanSave` and invoke `editor.SaveAsync()`.
That method and the built-in Save share the same validation gate. Invalid URL/number text and
unapplied secret text cannot silently save a previous valid value. Back preserves pending input;
replacing the settings clears it. The host must ask about discarding edits before replacing the
settings. Saved settings are not saved again; open the entry again for a subsequent edit.

Bounded choices use Select without search/refresh chrome. Choice capabilities opt into discovery
controls. Numeric fields use UI.Settings-owned exact `long`/`decimal` parsing and stepping, with
Lumeo-themed layout/accessibility adapted under its MIT license. Invalid/out-of-range typed text is
preserved and blocks Save; only explicit stepping clamps to bounds. Lumeo source is neither modified
nor forked. Attribution ships in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
