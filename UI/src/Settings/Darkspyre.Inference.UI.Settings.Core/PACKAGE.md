# Darkspyre.Inference.UI.Settings.Core

The UI-neutral pieces the Avalonia and Blazor setup editors share: the host contracts `IInferenceSettingsInteractionHost`
and `IInferenceSettingsPathPicker`, the editor's `SetupEditorState`, and exact number editing with
`SetupNumberEdit<T>`. A host references the editor for its UI framework, which brings this package with it.

## Install

```bash
dotnet add package Darkspyre.Inference.UI.Settings.Core
```

## Register

The package needs no registration. The host implements `IInferenceSettingsInteractionHost` and passes it to the editor.

## Example

```csharp
public sealed class MyInteractionHost(IConsentPrompt consent, IShell shell) : IInferenceSettingsInteractionHost
{
    public ValueTask<bool> AuthorizeAsync(string operation, SetupSideEffect sideEffects,
        CancellationToken cancellationToken = default) => consent.AskAsync(operation, sideEffects, cancellationToken);

    public Task<SetupActionResult> ExecuteAsync(IInferenceSettingsManager manager, InferenceSettings settings,
        SetupActionDescriptor action, IProgress<SetupProgress> progress, CancellationToken cancellationToken = default) =>
        manager.RunAsync(settings, action.Id, progress, cancellationToken);

    public ValueTask OpenBrowserAsync(Uri uri, CancellationToken cancellationToken = default) =>
        shell.OpenAsync(uri, cancellationToken);

    public Task RevealFolderAsync(string folder, CancellationToken cancellationToken) =>
        shell.RevealAsync(folder, cancellationToken);
}
```

An interaction host that also implements `IInferenceSettingsPathPicker.SelectPathAsync` adds Browse to file, folder
and executable fields; it returns an absolute path, or null when the person cancels.

## Limits

- `SetupEditorState.CanSave` is true when every required field has a value, any required sign-in has succeeded, no
  action or save is running and no local input is invalid.
- `CommitOutcomeUncertain` asks the host to read back what was saved before opening another edit; it never asks for
  an automatic retry.
- The package references no UI framework and no Connect or Tensor package.
