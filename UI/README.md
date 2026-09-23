# Darkspyre.Inference.UI.Settings

Version **11.3.0-rc.1**, synchronized with Common, Connect and Tensor. Its exact Common NuGet
dependency is `[11.3.0-rc.1]`; the package does not embed Common, Connect, Tensor or Lumeo DLLs.
The release-candidate suffix reflects the .NET 11 RC ASP.NET component dependency.

This package provides one host-placed Blazor editor for a provider-owned
`IInferenceSetupSession`. It renders the session's fields, validation, status, and setup actions
with Lumeo. The host chooses where to show the editor and owns its connection list, persistence,
credentials, consent, browser interaction, and long-running operation lifetime.

Among Inference products, the package references `Darkspyre.Inference.Common` only. The host composes Connect or Tensor
sessions and passes the Common interface to `InferenceSettingsEditor`. No provider selection or
connection listing is built into the library.

The host supplies `IInferenceSettingsInteractionHost` to authorize declared side effects and
retain provider actions. The component does not own or dispose the session. The host must register
Lumeo and include its theme assets and this package's scoped CSS through the normal Razor Class
Library static-web-assets pipeline.

```razor
<InferenceSettingsEditor Session="@selectedSession"
                         Host="@interactionHost"
                         CommitCompleted="OnCommitCompletedAsync" />
```

The host creates `selectedSession` with the owning Connect or Tensor factory and a typed
`ISetupCommitTarget<TChanges>`. `CommitCompleted` tells the host when to refresh its own list.
The editor exposes `SaveAsync()` for a host-owned Save control; set `ShowSaveButton="false"` when
using that option. The host keeps the session alive for any operation that should continue after
this view disappears and disposes it when that work is finished.
`IntroContent` and `SupplementaryContent` accept optional host guidance without changing the
provider-owned fields or actions.

The canonical technical design is [the Settings UI design](../docs/settings.ui.tech-design.md).
