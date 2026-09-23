# Inference Settings UI: technical design

Date: 2026-09-22

Status: **Proposed technical design — awaiting explicit approval.** This document authorizes no
implementation, package publication, version change, or consuming-host migration.

## 1. Decision and scope

`Darkspyre.Inference.UI.Settings` is the fourth NuGet package in the Inference family. It lives in
this repository under `UI/`, follows the Connect and Tensor product-workspace layout, and uses the
same version and coordinated release as Common, Connect, and Tensor. Its only Inference package
dependency is `Darkspyre.Inference.Common`. It also uses Lumeo for controls. It does not reference
Connect, Tensor, DarkspyreCE, or Agent-DS assemblies.

The package renders provider-driven Settings for both DarkspyreCE and Agent-DS. It owns Text,
Image, and Audio groups; compact configured-provider cards in a wrapping collection; an Add picker;
the shared add/edit editor body and wizard; field rendering; validation presentation; action and
progress presentation; and accessible interaction within those surfaces. It consumes the
provider-neutral setup definitions and sessions from Common. The host supplies the definition
catalog, configured-instance summaries, session creation, dialog frame, theme, persistence,
protected credentials, consent, notifications, and operation lifetime.

The initial package does not introduce a Decision Settings group. Decision remains a distinct
`InferenceCapability` and must not appear in Text, Image, or Audio by accidental grouping.

The [provider setup and audio design](provider-setup-and-audio-technical-design.md) owns the
underlying Common/Connect/Tensor contract and provider behavior. This document owns only its
Blazor presentation and host integration. It does not expand provider protocols, model discovery,
model compatibility, persistence, or credential implementation.

## 2. Repository, assemblies, dependencies, and release

The intended source layout mirrors the existing product workspaces:

```text
UI/
  Darkspyre.Inference.UI.slnx
  README.md
  src/Settings/Darkspyre.Inference.UI.Settings/
  tests/Settings/Darkspyre.Inference.UI.Settings.Tests/
  examples/Darkspyre.Inference.UI.Settings.Example/
```

The Settings project is a Razor Class Library with the matching assembly, root namespace, and
`PackageId`. Its package contains its assembly, XML documentation, static web assets, README, and
the repository license. The example is a non-packable host-composition fixture, not another
product package. It uses controlled sessions and a second theme to demonstrate reuse; it never
stores real credentials or contacts a paid provider. Add the UI subset to repository build/release
gates when the design is approved. There is no umbrella assembly or source sharing with a host.

The repository's central package file owns Lumeo and test dependency versions. The root build
properties own the one Inference version. UI.Settings declares an exact synchronized dependency on
Common in its NuGet output; it does not embed Common. Common, Connect, and Tensor remain free of
Blazor and Lumeo dependencies. The release gate publishes all four at one immutable semantic
version only after each product's required verification passes. A runtime-contract change must be
settled and released with the corresponding UI update; the UI must not claim compatibility with
unreleased or incompatible setup contracts.

This is a new package-family decision. On approval, update `.roles/project.md`,
`.roles/design-rules.md`, package verification, and the current Settings gap assessment so they
describe four synchronized packages and the in-repository `UI/` root. The approved runtime design's
"three packages" wording describes the scope of its earlier implementation unit; do not silently
reinterpret that unit as authorizing UI implementation.

## 3. Contract boundary and host composition

The dependency and request path is:

```text
CE / Agent-DS composition root
  -> host Settings catalog and session factory
  -> UI.Settings components
  -> Common InferenceSetupDefinition / IInferenceSetupSession / typed fields and actions
  -> provider-owned Connect or Tensor session
  -> ISetupCommitTarget<TChanges> implemented by the host
  -> host settings and protected credential storage
```

The host composition root uses Connect and Tensor APIs to obtain definitions and create sessions.
It exposes only Common setup types and safe UI summaries to the shared component. The host resolves
existing configuration and its opaque version before opening an edit session, supplies the
capability-specific typed commit target, and owns the saved-instance list. The UI never creates a
provider by name, downcasts a session to a family type, reflects configuration properties, or calls
a host database or credential vault.

The UI-facing host contract has these semantics (final C# signatures belong to implementation
review against the completed Common API):

- `ListDefinitions`: return the host-registered `InferenceSetupDefinition` collection without
  activating providers, probing devices, resolving secrets, or loading models.
- `ListConfiguredAsync`: return safe card summaries with stable host configuration ID, definition
  ID, display label, optional supporting text, and the latest non-secret status observation. The
  host may expose unknown status; it must not fabricate readiness from registration.
- `OpenCreateAsync(definitionId)` and `OpenEditAsync(configurationId)`: create a fresh
  `IInferenceSetupSession` using the owning provider's factory and the host's typed commit target.
  They fail explicitly when a definition or saved instance has disappeared. Session construction
  cannot silently authenticate, test, install, or load.
- `RefreshConfiguredAsync`: reread the host-owned list after a successful commit, conflict
  readback, or explicit refresh. The UI does not cache an authoritative duplicate list.

These are presentation integration contracts in UI.Settings, not a second universal provider
configuration model in Common. The safe summary cannot contain a secret value, raw provider
response, provider-native payload, model weights, or persistence handle. Each configured instance
has a host ID distinct from the stable implementation ID. Multiple configured instances of one
implementation therefore render as separate cards.

The shared UI only requires Common setup types. If an accepted user flow cannot be expressed through
those types and actually requires a Connect- or Tensor-specific interface, stop this package design
and split the UI into family-specific libraries before implementing that flow. Do not quietly add
either dependency to UI.Settings or branch on provider identity. A missing presentation-neutral
operation belongs in the owning provider contract, subject to its own design review.

## 4. Settings collection and Add flow

The host Settings route remains a thin composition surface. The shared Settings root renders one
group each for Text, Image, and Audio using `InferenceTask.GetCapability()` from Common. Within a
group, configured entries render as compact, keyboard-operable cards in a dense horizontal grid
that wraps without stretching a lone card across the available width. The group has one Add action;
the Add picker shows only registered definitions that support creation, sorted by declared display
order and then display name. Definition cards indicate Connect or Tensor from
`InferenceImplementationKind` and use the provider's description and prerequisites. Discovery
does not open sessions or trigger external work.

Each configured card displays its saved label, capability, implementation family, and only status
facts supplied by the host/provider observation. Unknown and stale states are identified as such;
Configured, Authenticated, Installed, Verified, Loaded, and Ready are distinct. A card opens its
shared editor. No editable form appears on initial arrival, and there is no page-level Save All.
The host may add non-provider Settings groups around the shared root without the library owning
the whole route.

The chosen collection-first card grid is the explicit Settings requirement, even though CE's
general text-resource collection default is a dense list. It is a Settings-specific collection,
not a new rule for Worlds, Cast, or other resources. CE's page width, section hierarchy, and
outer spacing remain host layout responsibilities. The shared library owns only the inner card
grid, card density, and responsive wrapping.

## 5. Editor frame, host extensions, and styling

UI.Settings owns one provider-independent editor body and controller for add/edit state. It renders
the ordered `SetupStep` and `SetupSection` hierarchy, advanced disclosure, field controls, issues,
bound actions, progress, and interaction prompts. Hosts supply a **dialog-frame slot** that
receives the editor's title, description, body, dirty/busy/save state, and save/close callbacks.
This slot contains no provider-specific form logic. CE composes it with its existing `EditorLayer`
and `AppDialog`, preserving the one CE overlay owner, mobile fullscreen behavior, fixed footer,
dirty-close confirmation, focus trap, and focus restoration. Agent-DS supplies its own frame with
the same behavioral contract. The shared editor logic and body are identical in both hosts.

The frame must route every dismiss path (Escape, backdrop, close button, cancel, and navigation)
through one dirty-close decision. It restores focus to the invoking card or Add control. Closing a
view does not by itself cancel a provider action. A saved editor may close after the host confirms
the new list state; a conflict or uncertain commit leaves an actionable view open. The library
does not instantiate a second CE dialog, take over CE navigation, or require CE types at compile
time.

Extension points are presentation slots for group heading/action context, card supporting content,
editor frame, and optional host guidance alongside provider sections. They may add host context,
links, or chrome; they may not replace provider-owned fields, validation, action availability,
status, or commit behavior. The component and state contracts remain reusable and do not require
subclassing a host component. A host that subclasses an exposed component still uses the same
renderer and session controller.

Structural CSS in the Razor Class Library may define grid flow, card sizing, field alignment,
visibility, progress layout, and responsive behavior. It uses scoped class names and documented
CSS custom properties for host-adjustable dimensions. It defines no palette, literal foreground
or background colors, typography family, control border treatment, or host shell layout. Lumeo and
the host theme supply control appearance. CE maps these classes into its layout tokens and World
Atlas theme; Agent-DS supplies its own theme. A host need not copy or fork the shared stylesheet.

## 6. Field, choice, and wizard rendering

The renderer maps Common field variants to one Lumeo control per job: text/URI/executable
reference to single-line input, integer/decimal to numeric input, Boolean to submitted-form
checkbox, bounded static choices to select, searchable/paged or manually enterable choices to
combobox, secrets to a masked input with explicit Keep/Replace/Clear, and repeatable groups to
ordered rows with Add/Remove controls. The actual Lumeo component API is verified against the
centrally pinned version during implementation. Field labels, help, constraints, required state,
visibility, enablement, and read-only state come from the current Common snapshot. The renderer
never invents provider fields or evaluates provider-authored scripts.

An edit invokes the bound field method with the displayed snapshot revision. On Applied, the UI
requests the new snapshot and updates dependent fields, choices, validation, actions, and status.
On Stale, it reloads the snapshot and tells the user the choice changed; on Busy or Invalid, it
retains the draft and shows the returned issue. Bound fields from an older snapshot are discarded.
Field and step issues appear beside their targets with an accessible summary; warnings remain
warnings. Advanced sections remain discoverable and open automatically when they contain a blocking
issue.

Choices are queried with `SetupChoiceQuery` and the current revision. The UI supports paging,
search, continuation, loading, empty, failed, and stale outcomes. It invalidates outstanding pages
when the draft revision changes and never selects an option merely because it appeared in a page;
the provider validates selection. Natural-prerequisite choice loading may start automatically only
when its declared side effects and host consent policy allow it. Manual entry appears only when
`AllowsManualEntry` is true. Failure or an empty choice source leaves manual entry available when
the provider permits it.

Wizard navigation follows the provider's ordered steps. Next validates the current draft and
shows issues associated with the current step before advancing; Save/Finish validates the whole
draft and calls `CommitAsync`. Back does not undo edits. A step's descriptive completion text is
not proof of completion. The UI never treats a successful action result as an implicit save.
If the final Common contract cannot identify current-step blocking issues or draft dirtiness,
those contract gaps must be resolved by the SetupSession owner before the wizard is implemented;
the UI must not infer them from revision counts, field labels, or descriptions.

## 7. Actions, consent, progress, and lifetime

The UI invokes the bound `ISetupAction` supplied by the current snapshot. Its descriptor governs
availability, semantic purpose, and declared side effects. Before an action that opens a browser,
runs an external CLI, contacts a provider, may bill, downloads weights, loads a model, unloads a
model, or stops an instance, the host consent policy authorizes the specific effect and the UI
shows the relevant consequence. Merely listing cards, opening an editor, saving a draft, or
visiting a step must not execute those actions. No paid test or install is an automatic retry.

The editor displays `SetupProgress` by stage and bounded amount, with cancellation when the bound
operation permits it. Completed, NeedsInteraction, Failed, Cancelled, Busy, Stale, and Unavailable
produce distinct UI states. Typed browser/device authorization, callback, and external-prerequisite
interactions render from Common without exposing OAuth verifier/state or asking the UI to interpret
provider protocol. Opening a browser or running a host interaction occurs only through the host's
consented capability. Errors display the provider's structured safe issues; exceptions and raw
provider responses are never written into UI diagnostics or visible detail by default.

The host retains any setup session, task, progress sink, and cancellation source that should
survive a dialog close. The shared component subscribes to `IInferenceSetupSession.Changed` while
mounted, reads `Snapshot`, and unsubscribes on disposal; it never becomes a second authoritative
session-state owner. The host explicitly disposes a session when its operation should end. UI
component disposal alone is not a stop command for a Tensor instance or cancellation of an
accepted download or paid request. A new editor opening is keyed to a new session identity, so
late callbacks from an old editor cannot update the new one.

## 8. Commit, conflicts, and secrets

Save calls `IInferenceSetupSession.CommitAsync` once for the current draft. The provider validates
and sends a capability-specific typed change set to the supplied `ISetupCommitTarget<TChanges>`;
the host compares the saved opaque version and publishes configuration plus protected secret
changes. UI.Settings never sees that change set or performs a separate settings/credential write.
Create requires absence; edit requires the expected version. The host handles vault staging and
cleanup where settings and secrets cannot share a transaction.

Secret fields expose only `HasValue` and Keep/Replace/Clear. The UI never prepopulates a saved
secret, stores replacement input in a durable component state service, logs it, or echoes it in an
error. It clears its transient replacement input on a successful field update, session disposal,
or explicit discard. Cancel/discard does not clear a saved credential. External actions already
performed during a draft are reported honestly and are not rolled back by closing the editor.

A commit conflict keeps the editor open, explains that saved settings changed, and offers a fresh
host read and new edit session; it never overwrites or automatically replays a credential or
authorization step. Invalid stays on the relevant fields. Failed remains retryable only when the
host reports a definite non-commit. Uncertain requires host readback before a new save attempt;
the UI does not label it Cancelled or retry automatically. On Committed, the host refreshes the
configured list and the UI shows transient success through the host's notification service.

## 9. Contract readiness and implementation gates

The 2026-09-22 working tree already has the Common setup records, bound fields/actions, session
snapshot, status, and typed commit target. It also has representative Connect Image and Tensor
Qwen/Supra sessions. This is enough to design the provider-independent UI, but it is in-progress,
uncommitted code and is not a release contract. Connect LLM currently exposes setup definitions
without a completed setup session; Audio sessions and broader Tensor model discovery are pending.

Before implementation begins, reconcile this design with the finished Common contract and verify:

1. Every supported definition can open a provider-owned create and edit session through host
   composition; no supported card needs a provider-name branch in the shared UI.
2. Common exposes reliable draft-dirty state for dirty-close behavior, or an equivalent provider-
   owned signal; action-driven revision changes alone cannot mean unsaved edits.
3. Step-associated validation is sufficient for Next gating and fields can report the state and
   constraints needed by Lumeo controls.
4. Configured-card summaries and refresh can be supplied without secret readback, provider
   activation, or a second readiness authority.
5. Action side effects, continuation, cancellation, and host retention are implementable for
   Connect and Tensor without leaking family interfaces into UI.Settings.
6. Text, Image, and Audio have real Connect/Tensor definitions and sessions, or their absent
   combinations are explicitly unavailable with accurate copy rather than placeholder success.

If a gate exposes a missing Common or provider contract, return to that owning technical design.
Do not add a UI-local substitute or narrow the required consumer experience silently.

## 10. Verification and delivery

The UI package's deterministic component tests use fake Common sessions and controlled host
contracts. They cover Text/Image/Audio grouping, Connect/Tensor identity, multiple configured
instances, card wrapping and empty states, create/edit opening, conditional and advanced fields,
dependent paged choices, manual fallback, stale snapshots, warning/error placement, wizard
navigation, secret Keep/Replace/Clear, cancelled drafts, conflict and uncertain commit handling,
action side-effect consent, progress and cancellation, view close during retained work, and
focus/keyboard behavior. A host-composition fixture exercises at least one real Connect session
and one real Tensor session without network, paid inference, model weights, or native execution.
It verifies that the UI assembly and package graph contain no Connect or Tensor dependency.

Render the same components under CE-like and Agent-DS-like themes at wide desktop and below the
mobile breakpoint. Verify dense wrapping, full-screen mobile frame through each host adapter,
scrolling body and fixed footer, focus trap/restoration, accessible labels and issues, visible
focus, contrast under each theme, and reduced-motion behavior. Compare CE composition with
`DarkspyreCE/docs/ui-design-language.md` and its named Settings and editor references. Layout
checks must use rendered Lumeo markup; unit tests alone do not establish visual fit.

After approval and implementation, run authenticated restore, zero-warning Debug/Release builds,
deterministic tests, format verification, and the transitive dependency vulnerability audit for
the new UI subset and the synchronized family. Verify NuGet contents and exact Common dependency
before a separately authorized four-package publication. Commit complete files with explicit
pathspecs and update the canonical documents in the same implementation unit. No normal test gate
may require paid calls, full model downloads, or stored production credentials.

## 11. Approval boundary

Approval of this technical design authorizes building the UI package and its host-neutral example
against the completed setup contract. It does not authorize publishing a NuGet version, migrating
CE or Agent-DS, changing a user's configured providers, downloading weights, or running a paid
provider action. The final release and each host integration are separate reviewed units.
