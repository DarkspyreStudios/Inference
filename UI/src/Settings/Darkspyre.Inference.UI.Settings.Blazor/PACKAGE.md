# Darkspyre.Inference.UI.Settings.Blazor

The Blazor setup editor for Darkspyre.Inference providers, drawn with Lumeo components. `InferenceSettingsEditor` shows
a provider's settings object through its settings manager: its fields, validation, status, sign-in panels and setup
actions. `ConnectionTabs` shows an existing connection on a Connection tab and a Model tab. It shows the same regions,
wording and states as the Avalonia editor.

## Install

```bash
dotnet add package Darkspyre.Inference.UI.Settings.Blazor
```

## Register

The package needs no registration of its own; the host's Blazor application sets up Lumeo. The host creates the
settings manager with the owning Connect or Tensor factory, opens the settings with the manager's `OpenAsync`, and
implements `IInferenceSettingsInteractionHost`.

## Example

```razor
<InferenceSettingsEditor Settings="settings" Manager="manager" Host="interactionHost"
                         CommitCompleted="OnCommitCompleted" StateChanged="OnStateChanged" />
```

## Settings

- Field edits stay in the settings object; only Save reaches the host.
- `ShowSaveButton`, `ShowHeading`, `ShowActionEffects` and `ShowStepList` are parameters. `IntroContent`,
  `SupplementaryContent` and `MoreOptionsContent` are render fragments for host content.
- A device sign-in copies its code with the browser's clipboard and shows **Copy code** when the browser refuses an
  automatic copy.

## Limits

- The editor's scoped CSS reads the Lumeo theme's colours.
- The editor owns neither the settings, the manager nor any engine behind it. The host owns persistence,
  credentials, consent and the browser.
- The package carries `THIRD-PARTY-NOTICES.md` for the adapted Lumeo number input.
