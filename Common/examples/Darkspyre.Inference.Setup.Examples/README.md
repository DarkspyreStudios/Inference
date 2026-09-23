# Generic setup editor example

This non-packable, framework-neutral example compiles against the real Connect and Tensor setup
sessions. `SetupCompositionExample` demonstrates typed host registration. `SetupEditorExample`
renders every session through the same semantic field/action contract, with no provider-name
switch. It is a reference for the separately scoped UI library, not a competing UI framework.

The host implements `ISetupEditorControls` using its chosen controls. Choice retrieval is explicit:
inspect `ChoiceSideEffects`, obtain consent where needed, pass the current snapshot revision, and
ignore stale pages. Re-read `Snapshot` after any edit/action or `Changed` notification; bound
controls from an older revision return Stale. Never log secret inputs or typed commit payloads.

The host retains sessions, tasks and cancellation sources outside dialog components when work
must survive closing a view. Dispose a session when cancelling the edit, not merely hiding its UI.
Normal Cancel does not revoke already-issued external authorization or undo a downloaded model.
An already-cancelled token passed to an Unload/Stop action requests urgent, safe teardown.

Implement each typed `ISetupCommitTarget<TChanges>` with create-if-absent/edit-if-version-matches
semantics over both configuration and protected secret changes. A conflict never overwrites newer
state. An uncertain outcome closes the draft: read the host store before beginning a new edit.
Image header removals are explicit; omitted headers are untouched. LLM changes carry a separate
credential mutation; never put it into ordinary connection settings. Save does not run provider
tests, authenticate, install or load weights.

Tensor examples currently list the built-in supported packs; remote source discovery and wider
compatible selection are a later unit of the approved implementation, not yet claimed here.
No package/version change or paid/native inference run is needed to build this example.
