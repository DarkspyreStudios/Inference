# Darkspyre.Inference.UI.Settings.Avalonia

The Avalonia setup editor for Darkspyre.Inference providers. `InferenceSettingsEditor` shows a provider's settings
object through its settings manager: its fields, validation, status, sign-in panels and setup actions.
`ConnectionTabs` shows an existing connection on a Connection tab and a Model tab. The host decides where the editor
appears.

## Install

```bash
dotnet add package Darkspyre.Inference.UI.Settings.Avalonia
```

## Register

The package needs no registration. The host creates the settings manager with the owning Connect or Tensor factory,
opens the settings with the manager's `OpenAsync`, and implements `IAvaloniaSettingsInteractionHost`, which adds the
window's clipboard to the shared interaction host.

## Example

```csharp
InferenceSettingsEditor editor = new(settings, manager, interactionHost) { ShowSaveButton = false };
editor.StateChanged += (_, state) => saveButton.IsEnabled = state.CanSave;
editor.CommitCompleted += (_, result) => OnCommitCompleted(result);
dialog.Content = editor;
```

The host's Save control calls `editor.SaveAsync()`. A Committed result tells the host to close the dialog and refresh
its own list. Closing without Save discards the edits.

## Settings

- Field edits stay in the settings object; only Save reaches the host.
- `ShowHeading`, `ShowActionEffects` and `ShowStepList` let a host that frames the editor hide the provider heading,
  the effect labels and the step list. `MoreOptionsContent` adds host content under More options.
- A device sign-in copies its code to the clipboard the first time it shows, with **Copy code** and **Copy link**
  buttons.
- A model install card shows the install state with **Download**, **Download again**, **Delete download** and **Show**.

## Limits

- The editor follows the host theme's brushes through its own `InferenceSettingsStyles`.
- The editor owns neither the settings, the manager nor any engine behind it. The host owns persistence,
  credentials, consent and the browser.
