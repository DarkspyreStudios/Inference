# Inference Settings editor UI: technical design

Date: 2026-09-22

Status: **Implementation authorized on 2026-09-22; build, test and coordinated private publication
authorized on 2026-09-23.** The published correction release is 11.3.1-rc.1, retaining the .NET 11 RC
dependency. Consuming-host migration remains separate.

## 1. Scope and ownership

`Darkspyre.Inference.UI.Settings` is a reusable **editor surface** for a single Connect or Tensor
provider's settings object and its settings manager. It renders provider-authored fields, sections,
steps, validation, provider-owned connection/readiness status, connection/authentication actions,
tests, and other setup operations. It is a Blazor Razor Class Library using Lumeo controls. It does not decide
how existing connections are listed or
where the editor is displayed. The host may put the same editor in a page, inline panel, dialog,
drawer, or another host-owned frame. UI.Settings instantiates no Lumeo `Dialog`, `Drawer`, or
collection surface and owns no route or application shell.

DarkspyreCE and Agent-DS each own discovery and listing, Add/Edit entry points, the editor's
placement, frame, close behavior, navigation, theme, persistence, protected credentials, consent,
and application operation lifetime. The host creates the appropriate provider settings manager, opens
the settings, and passes only the Common types to the editor. The editor never opens settings, loads
saved configuration, writes settings directly, or retrieves a saved secret.

The [provider setup and audio technical design](provider-setup-and-audio-technical-design.md) owns
the Common/Connect/Tensor contracts and provider behavior. This document owns the reusable editor
presentation and its host integration. It does not expand protocols, model discovery, model
compatibility, persistence, or credential implementation. It preserves the required Text, Image,
and Audio provider coverage by rendering any valid settings object from those capabilities; it does
not own a Text/Image/Audio navigation or grouping UI.

## 2. Repository, package, and dependency boundary

UI is a product workspace in this repository, following the existing Connect and Tensor layout:

```text
UI/
  Darkspyre.Inference.UI.slnx
  README.md
  src/Settings/Darkspyre.Inference.UI.Settings/
  tests/Settings/Darkspyre.Inference.UI.Settings.Tests/
  examples/Darkspyre.Inference.UI.Settings.Example/
```

The Settings project has matching assembly, root namespace, and `PackageId`. Its NuGet contains
the assembly, XML documentation, static web assets, README, and repository license. The example
is non-packable host composition, not another product package. UI.Settings is the fourth NuGet in
the synchronized Inference family. Common, Connect, Tensor, and UI.Settings use the one version in
`Directory.Build.props` and release together at an immutable semantic version.

UI.Settings references `Darkspyre.Inference.Common` and Lumeo. It does not reference Connect,
Tensor, DarkspyreCE, or Agent-DS assemblies. The NuGet declares an exact synchronized Common
dependency and does not embed Common. Lumeo and test dependency versions are centrally managed.
Common, Connect, and Tensor gain no Blazor or Lumeo dependency. If a required editor flow truly
needs a Connect- or Tensor-specific interface, return to design and split the UI into family
libraries; do not add either dependency or provider-name dispatch to this shared package.

This package decision supersedes older Settings assessment text that calls `UI/` a sibling
repository or says the package remains outside the synchronized family. In this implementation unit, reconcile
`.roles/project.md`, `.roles/design-rules.md`, package verification, and that assessment. The
approved runtime design's three-package wording described its own earlier implementation scope;
it did not authorize this UI unit.

## 3. Composition and public editor contract

The request path is:

```text
Host collection / Add or Edit action
  -> host resolves existing configuration, creates the provider settings manager, calls OpenAsync
  -> UI.Settings editor receives InferenceSettings and IInferenceSettingsManager
  -> editor edits fields locally; RunAsync, QueryChoicesAsync and SaveAsync go through the manager
  -> provider-owned Connect or Tensor manager
  -> ISetupCommitTarget<TChanges> implemented by the host
  -> host settings and protected credential storage
```

The central public component is one provider-independent `InferenceSettingsEditor`. Its required
inputs are an `InferenceSettings` object and the `IInferenceSettingsManager` that opened it, supplied
by the host. The page is event based: field edits change the settings object synchronously, and an
async button click runs manager calls in order and then updates the page state. When the manager
implements `IInferenceSettingsStatusSource`, the editor subscribes to `StatusChanged` while mounted,
calls `RefreshStatusAsync`, and renders the result. It reports state and outcomes through callbacks
so the host can render its own title, save/close controls, notifications, and frame. Replacing the
settings resets field, choice, step, validation, and progress view state; callbacks for older settings
cannot update the new editor.

The integration surface has these semantics; exact signatures are finalized against the completed
Common API:

- **Settings input:** one `InferenceSettings` object and its manager; the object provides definition,
  sections, fields, actions, validation, interaction and status. The UI does not accept a
  configuration bag.
- **State output:** `StateChanged` supplies a safe `SetupEditorState`, also readable through
  `State`. `HasUnsavedChanges` combines the settings' `HasUnsavedChanges`, transient pending input
  and uncertain save state. `HasPendingInput`, `HasInputErrors`, `CanSave` and
  `CommitOutcomeUncertain` support the host-owned frame. State callbacks contain no input or secret
  values.
- **Save command and result:** a host control may ask the editor to validate and commit; an editor
  embedded without a host save control may render its own Lumeo Save/Finish button. Both paths call
  the same controller method and report the exact `SetupCommitResult`.
- **Action/interaction host:** a host-supplied UI capability authorizes declared side effects,
  handles browser opening or other external interaction, and retains operations that should
  continue after the view closes. It receives only Common action and interaction types.

The host obtains definitions and settings managers from Connect/Tensor factories at its composition
root. That family-specific composition stays outside UI.Settings. A configured instance's host ID and
opaque saved version are resolved there; the editor sees only the safe settings object. Multiple
configured instances of one implementation are distinguished and listed by the host, not by this
component. Merely constructing the editor does not probe devices, resolve secrets, connect,
authenticate, test, download, or load a model.

## 4. Field and wizard behavior

The unpublished model-location extension adds `SetupTextField.PathSelection` (file or folder).
When the interaction host also implements `IInferenceSettingsPathPicker`, the renderer offers
Browse for these fields and for existing Connect executable fields. Native picker implementation
and permissions remain host-owned; manual entry, including executable command names, is preserved.
Cancel does not edit. A result that returns after the field was replaced or edited again is
discarded rather than replayed. Picker failure reports a generic local input error. No Tensor reference or provider
name dispatch is introduced. See [the location design](tensor-model-locations.tech-design.md).

The renderer uses Common `SetupStep`, `SetupSection`, and `SetupField` variants. It maps text,
URI, and executable reference to Lumeo single-line inputs; integer and decimal to numeric input;
submitted Boolean values to checkbox; bounded choices to select; searchable/paged or manually
enterable choices to combobox; secret to masked input plus Keep/Replace/Clear; and repeatable groups
to ordered rows with Add/Remove controls. The actual Lumeo API is verified against the centrally
pinned version during implementation. Labels, help, defaults, constraints, required/read-only
state, visibility, and enablement come from each read of the settings' `Steps`. The UI never invents provider
fields, evaluates scripts, or dispatches by provider name or transport ID. `SetupChoiceCapabilities`
declares search/paging/refresh separately from manual entry. Bounded lists use Select without
search/refresh controls; discovery fields render only their advertised controls.

Numeric controls are owned by UI.Settings, with Lumeo NumberInput's themed layout/accessibility
adapted under MIT attribution. Lumeo remains an unmodified dependency. Its double-based parsing and
automatic clamping cannot preserve these typed contracts, so a wrapper around that parser is not
used. Integer and decimal controls accept nullable `long` and `decimal`, expose declared bounds and
decimal step, preserve invalid/out-of-range text, and reject decimal precision loss. Explicit
buttons/arrow keys step and clamp to declared or type bounds; typed input is never silently clamped.
Labels, error associations, disabled state, density and spinbutton semantics are retained.

An edit calls the field's typed edit method. It changes only the settings object and never calls a
provider. Applied re-renders from the settings and reloads dependent choices. Invalid retains the
user's visible input and shows the returned issue. The settings object owns accepted values; the
component keeps only ephemeral control input and view state. The editor keeps an edit version that
increases on each local edit and after each action.

One editor-owned input scope retains pending text, secret replacement input and per-field errors
across renders and wizard steps. Successful edits clear only that field's pending/error state.
Correcting a different field never clears its errors. Explicitly removed fields, completed callback
attempts, settings replacement, successful save and editor disposal release their transient input.
The settings object owns accepted values; no configuration or saved-secret copy is kept by the
renderer.

Choices come from `IInferenceSettingsManager.QueryChoicesAsync` with the current settings and a
`SetupChoiceQuery`. They support search, bounded paging, continuation, loading, empty, failed and
cancelled outcomes. The UI drops pages that return after a later edit and never treats a displayed
choice as proof that selection is valid. The provider validates
the selected value. Manual entry appears only when `AllowsManualEntry` is true. When a choice
source fails or returns no choices, manual entry remains available only when the provider allows
it. Natural-prerequisite loading occurs automatically only when declared side effects and the
host's consent policy permit it.

Every choice request captures its field, edit version, search and continuation before awaiting
consent or the manager. A newer generation cancels the older request and starts the newest load; an
old completion (including an error) cannot publish. Requested and successfully loaded edit versions
are separate, so an in-flight older query cannot mark a newer version loaded. Only current pages are
appended. Settings replacement/removal cancels view-owned queries, not host-retained actions.

The editor follows the provider's ordered steps and sections. Advanced sections remain
discoverable and expand when they contain a blocking issue. Next validates the current settings and
shows current-step issues before advancing. Once validation is shown, it runs again after each edit
and each action, so a fixed value clears its issue and re-enables Save. Back preserves edits. Save/Finish validates the whole
object and calls `SaveAsync`. A descriptive completion instruction or successful test is
not proof of a committed configuration. Field and step issues have accessible summaries beside
their targets; warnings remain warnings. If final Common validation cannot identify current-step
blocking issues, resolve that contract gap with the provider settings owner instead of deriving logic
from IDs, labels, or prose.

Next first checks local current-step input errors, then provider validation. Built-in Save and public
`SaveAsync` share one whole-editor gate: invalid/unapplied input returns Invalid without calling
the manager. Save is enabled when every required field has a value, any required sign-in has
succeeded, and no action or save is running. A Committed save closes the host's dialog; closing
without Save discards the settings. A host cannot bypass that gate by drawing its own button. Invalid Save selects the
first affected step, including errors retained on a previously visited step.

## 5. Connect, test, and other bound actions

The editor renders the settings' `Actions` descriptors. Actions may include
authentication/connection, prerequisite checks, model discovery, provider tests, readiness checks,
install, verify, load, unload, and stop, as actually supplied by the provider. The descriptor
determines display name, availability, purpose, and declared side effects. There is no UI-side
provider switch and no placeholder successful action. Unavailable actions show their provider
reason. Actions are never invoked merely by opening the editor or changing steps.

The editor determines the displayed connection state from the settings' `SetupStatus` observations.
It can run the CheckReadiness or Test action on user request and re-render the settings to update
that state. The host does not supply or maintain a second
"connected" flag. Configured, Authenticated, Verified, Installed, Loaded, and Ready retain their
separate meanings; unknown or stale evidence is displayed as such. For a Tensor provider, Loaded
and Ready describe local model state rather than a network connection. The editor never infers
current connectivity merely from saved settings or a previously successful test.

Before an action that opens a browser, invokes a CLI, contacts a provider, may bill, downloads
weights, loads a model, unloads it, or stops an instance, the host's consent capability decides
whether to proceed and the editor shows the effect clearly. After consent the editor runs the action
through `IInferenceSettingsInteractionHost.ExecuteAsync(manager, settings, action, progress, token)`.
It displays `SetupProgress` by stage and bounded amount, with cancellation when supported.
Completed, NeedsInteraction, Failed, Cancelled, and Unavailable produce distinct visible outcomes. No paid test or installation is retried automatically.

Typed browser/device authorization, callback, and external-prerequisite interactions render from
Common. Provider OAuth verifier/state and protocol payloads remain inside the provider settings.
`InferenceSettings.Interaction` is the sole current interaction source. Its Continue, Cancel and
Recheck descriptors run through the manager by ID. CLI prerequisite instructions remain until the
provider clears them. Browser
callback text is retained as masked one-way input under its attempt-scoped field ID; the provider
never needs to echo the code to keep the field stable. Completion, cancellation or restarting the
attempt clears its transient input.
The host performs browser opening or another external interaction; the UI does not execute remote
shell text. Only structured, safe issues are shown. Raw provider responses and exceptions are
never logged or displayed as diagnostic detail by default.

The host retains the task, progress sink, and cancellation source whenever the operation must
outlive the editor's placement. Removing the editor view does not automatically cancel a download,
a paid test, or an application-owned Tensor instance. The editor unsubscribes from `StatusChanged`
when unmounted.

## 6. Save, conflicts, and secrets

Save invokes `IInferenceSettingsManager.SaveAsync` once for the current settings. The manager
validates them and sends a capability-specific typed change set to the host's
`ISetupCommitTarget<TChanges>`.
The host compares the expected opaque version and publishes settings with protected secret
changes. UI.Settings never sees that typed change set or performs a separate settings or
credential write. Create requires absence; edit requires the saved version. The host owns vault
staging and cleanup when settings and secrets cannot share one transaction.

Secret fields expose only `HasValue` and Keep/Replace/Clear. The editor never prepopulates a saved
secret, stores replacement input in durable UI state, logs it, or echoes it in an error. It clears
transient replacement input after applying the field edit and on editor removal. Closing without
save does not clear a saved credential. External effects already performed while editing are not
rolled back by dismissing the surface.

On Committed, the editor reports success and the host refreshes its own connection list and shows
transient feedback. Conflict keeps the editor and settings available, reports the saved version
changed, and asks the host to open the entry again; it never overwrites or replays credentials
automatically. Invalid stays on the relevant fields. Failed is retryable only when the
host reports a definite non-commit. Uncertain requires host readback before another save; it is
never reported as Cancelled or retried automatically. The host decides whether to close, relocate,
or keep showing the editor after each outcome.

## 7. Presentation, theme, and accessibility

The component owns only editor-internal layout: sections, field groups, wizard navigation,
validation summaries, status, actions, interaction prompts, and progress. The host owns outer
width tier, page padding, frame, header, footer, overlay, scroll boundary, and close/navigation
behavior. CE may compose the surface inside its existing `EditorLayer`, but this library does not
require or instantiate that dialog. Agent-DS can place the same surface in its own layout.

Tracked, scoped CSS may define internal grid flow, spacing, alignment, and responsive collapse.
It defines no palette, literal foreground/background colors, typography family, radii, shadows,
control border treatment, or application shell layout. It exposes documented structural CSS
custom properties where a host needs to adjust density. Lumeo and the host theme supply colors
and control appearance; a host need not copy or fork the editor stylesheet.

The editor uses labeled controls, associated help/issues, keyboard-operable wizard and action
controls, announced progress and status changes, and reduced-motion-safe transitions. If a host
chooses a dialog, that host owns the focus trap, initial focus, Escape/backdrop behavior, dirty
close confirmation, and focus restoration. UI.Settings exposes the state needed for those host
decisions but does not impose a dialog lifecycle. Common supplies unsaved-change state through
`InferenceSettings.HasUnsavedChanges`. Hosts use `State.HasUnsavedChanges` for close confirmation,
and `State.CommitOutcomeUncertain` to require readback. Status-only updates do not trigger discard
prompts, and invalid visible input cannot be lost merely because it never entered the settings.

Presentation extension slots may add host guidance or surrounding context without replacing
provider-owned fields, validation, action availability, status, or commit logic. The same shared
renderer and settings controller serve both hosts under their distinct themes.

## 8. Contract readiness and implementation gates

The completed Common contract has setup records, typed fields, settings objects, settings managers,
status, and typed commit targets. Connect and Tensor settings managers cover the approved provider
capabilities,
including Audio. The editor source is reconciled against these contracts. Debug/Release compilation
and its regression suite passes; the former build hold was lifted by the release request. The initial
published archive had three component tests; the source correction coverage is recorded below.

Required gates:

1. A host can supply real create/edit settings and their manager for each supported provider
   without UI-side Connect/Tensor references or provider-name branches.
2. The final Common fields and validation supply the state needed to render dependent controls,
   choices, step issues, and accessible errors. Missing semantics return to the provider-contract
   design instead of becoming a UI-local metadata model.
3. Consent, action continuation, cancellation, progress, and host retention can be implemented
   with Common action/interaction types alone.
4. Text, Image, and Audio provider settings are real and accurately report unavailable actions;
   no family or modality is represented by a placeholder success.
5. A host that requires dirty-close behavior obtains `SetupEditorState.HasUnsavedChanges`, combining
   provider-owned unsaved-change state and pending UI input.

If any required editor flow truly needs a Connect- or Tensor-specific UI interface, stop and
redesign as family-specific UI libraries before implementation. The requirement cannot be met by
silently adding a family dependency to UI.Settings.

## 9. Verification and delivery

Deterministic component tests use fake Common settings and managers and controlled host
capabilities. They cover fields and dependent visibility, advanced sections, paged choices and
manual fallback, late choice pages, warning/error placement, step navigation, secret
Keep/Replace/Clear, action availability and side-effect consent, interaction continuation,
progress/cancellation, save enablement, conflict and uncertain outcomes, status refresh, settings
replacement, and removal during retained work. A non-packable example composes at least one real
Connect and one real Tensor settings manager behind host
contracts without network, paid inference, model weights, or native execution. Package inspection
confirms no Connect or Tensor dependency.

Render the same editor under CE-like and Agent-DS-like themes in an inline host and a host-owned
frame at wide and narrow viewports. Check field density, long labels, step navigation, progress,
keyboard flow, focus visibility, contrast under each host theme, and reduced-motion behavior.
CE-specific placement is checked against `DarkspyreCE/docs/ui-design-language.md` when CE
integration is separately approved; this package itself does not define a CE Settings page.

After the user's build hold is lifted, run authenticated restore, zero-warning Debug/Release
builds, deterministic tests, format verification, and the transitive dependency vulnerability
audit for the UI subset and synchronized family. Verify package contents and exact Common
dependency before a separately authorized four-package publication. Commit whole files with
explicit pathspecs and update affected canonical documents in the same implementation unit.
Normal gates require no paid calls, full model downloads, or stored production credentials.

## 10. Authorization boundary

The original implementation authorization deferred builds. On 2026-09-23 the user authorized the
coordinated four-package release, including builds and a version correction for prerelease framework
dependencies. CE/Agent-DS migration, a particular host placement, changes to configured providers,
model downloads and paid provider actions remain excluded.

## 11. Release validation (2026-09-23)

UI.Settings 11.3.0-rc.1 builds without warnings in Debug and Release. Its three current component
tests pass (provider status, explicit action/consent and save through the session), alongside 22
Common tests. bUnit's current context and the actual Lumeo registration namespace are used, and a
redundant event-callback render was removed. Formatting and the transitive vulnerability audit pass.

The archive contains only UI.Settings' DLL/XML, scoped CSS/static-web-asset imports, README and
license. Common is an exact `[11.3.0-rc.1]` NuGet dependency; Connect/Tensor are neither dependencies
nor embedded assemblies. The prerelease suffix keeps NuGet validation enabled for the required
ASP.NET 11 RC package. A clean four-package consumer compiles and loads the component type.
The package was published to the private DarkspyreStudios feed from release source `1e431c5`.
GitHub API readback confirms private visibility; a fresh feed-only consumer restores the identical
archive and loads the component at that source identity.
This package verification does not certify the CE/Agent-DS themed host/browser matrix or migrate
either application; those integration checks remain separately scoped.

## 12. Post-release Settings corrections (2026-09-23)

The user approved implementation of the five reported defects: local-input Save validation,
authentication interactions/callback retention, reliable dirty-close state, dependent-choice races
and metadata-driven control selection. The numeric control is library-owned; no Lumeo source or
fork is changed. The initial source-only unit kept version 11.3.0-rc.1 and built/published no
packages. The user subsequently approved the minor bump and coordinated 11.3.1-rc.1 publication;
the old archives remain immutable.

Common now supplies dirty/uncertain-commit state, choice capabilities and decimal step. Every
Connect/Tensor setup implementation supplies the new semantics. UI.Settings owns transient input
and request generations while provider sessions remain the only draft owners. The shipped notice
and future package-verification gate retain Lumeo's MIT attribution.

Deterministic regression tests exercise invalid URL/integer/decimal Save prevention, exact numeric
precision and bounds, keyboard/disabled/accessibility state, per-field errors, navigation retention,
secret pending/apply/revert behavior, provider dirty/revert checks, uncertain commits, fresh CLI
recheck controls, callback attempt retention, session replacement, overlapping choice requests and
bounded-select rendering. Browser review uses generated component markup and the pinned Lumeo and
library scoped styles; this checks shared renderer layout, not a live CE/Agent-DS application or
interactive end-to-end host integration.

Final gates: zero-warning Debug/Release builds and passing tests for Connect (301), Tensor (260)
and UI (45, including 24 shared Common tests and 21 component tests) in each configuration.
All three format checks and transitive vulnerability audits pass. Browser fixtures were reviewed
at 1440×1000 and 390×844 using light inline and dark host-framed layouts; no horizontal overflow
was observed and numeric labels/error presentation and keyboard focus were checked. The package
verifier compiles. Archive creation/inspection was deferred in the implementation unit and is now
authorized for the coordinated 11.3.1-rc.1 release.

## 13. Correction release validation (2026-09-23)

All four 11.3.1-rc.1 packages are published privately from release source
`e2b4daedd8a96e60c986e7ea51a653de7cb8d546`, tagged locally as `v11.3.1-rc.1`. Authenticated restores,
zero-warning Debug/Release builds, the above test totals, formatting and transitive dependency audits
pass. Archive verification confirms exact `[11.3.1-rc.1]` Common dependencies, source identity,
owned DLL/XML boundaries, UI static assets and the required Lumeo notice. No sibling or third-party
DLLs/native libraries are bundled into these packages.

A fresh four-PackageReference consumer passes before publication and again against the public/private
feeds with an empty package cache and no HTTP cache. It loads the new Settings types and provider
surfaces, initializes Whisper through its transitive public runtime dependency, and verifies the
release source identity. All four downloaded archives match the local release files byte-for-byte;
GitHub readback confirms private visibility. No application was migrated and Lumeo remains unmodified.

## 14. Host-framed presentation corrections (2026-09-23)

Agent DS is the first application to place the editor in a live host, and its dialog exposed
presentation defects the component tests did not show. The user approved these corrections as
unpublished source, consumed by Agent DS through project references until a coordinated release.

- `ShowHeading` (default `true`) lets a host that already titles its frame omit the editor's
  provider name and description. Section 3 assigns the title to the host; the always-rendered
  heading duplicated it.
- `ShowActionEffects` (default `true`) lets a host whose click is consent omit the declared
  side-effect labels. The labels remain the default for hosts that ask separately.
- A multi-step session names the current step once: the step list marks it with `aria-current`
  and the scoped CSS sets it in bold, and the step title is shown only when there is no list.
- A device authorization offers **Copy code**. The clipboard write needs no provider effect or host
  consent, because the code is already on screen; a refused write reports that the code can be
  selected and copied instead, and the code selects in one click.
- A searchable choice field shows its separate provider-search box only while the loaded list is
  incomplete (a continuation exists) or a search is active. The combobox already filters loaded
  choices, so a complete list needs no second search control.

Connect's `LlmSetupSession` no longer exposes an endpoint field for Direct Codex (owner decision:
there is only the official endpoint). The draft always carries it, and an edit of a connection
holding a former value is dirty so the next save replaces it; a connection step with no fields
has no empty section. A real device sign-in through the setup session and through the Agent DS
host listed the provider's models and saved an authenticated connection.

Verification: zero-warning Connect and UI subset builds; Connect tests pass (LLM 166 with the new
Codex setup test) and UI tests pass (29 component tests, 24 Common tests), including new tests
for the heading and effect options, single-step titles, the current-step list, copy success and
refusal, and the search-box condition. No package was built or published.

