# Cross-family xAI binding

`XaiConnectionSelection` is data-only connection selection. `XaiFamilyBinding` borrows the existing
`LlmConnectionAccess`; its authorization delegates to that same owner. Its optional third constructor
argument borrows an existing `LlmProviderService` whose access must be the exact same instance. It
does not dispose either collaborator or create a credential
store or cache, or select an ambient connection. Construction is effect-free. Its public acquisition
resolves the selected connection once, checks xAI transport/typed region/OAuth before refresh, then
revalidates the returned snapshot. OAuth refresh and conditional credential save retain the existing
access owner's cancellation, retirement drainage and expected-version semantics. No API-key fallback
is supplied. Access retirement does not dispose its borrowed source or authenticators.

The validation-taking internal access path freezes caller headers, provider JSON and capability
overrides before refresh can await. The entered family's internal snapshot-bound view supplies the
same immutable snapshot and authorization to nested calls without another resolve/refresh. Such a
view is operation-local, not a public caller-authored credential snapshot or a second lifetime owner.
Ordinary standalone calls still acquire their own current snapshot. Family owners must retain their
operation leases and synchronous fences around their actual effects.

Snapshot-bound cross-family catalog operations borrow the service's selected HTTP and admission
owner, and Files operations delegate to its existing private Files codec. They never resolve or
refresh the supplied snapshot again. TTS owns its observed voices codec, with no voice cache or
second model-list owner. A missing facade supplies no hidden default and refuses catalog-dependent
operations before effects. These internal extensions do not themselves implement other media routes.
Shared snapshot-bound model discovery remains unwired pending the existing model-list owner's
credential scope and callback-drain qualification; current chat model discovery is unchanged.

`ConnectLlmJson.XaiConnectionSelection` is the exact transferable metadata root. A runtime binding is
not a transferable root. The owned context contains a fixed refusal converter: null is permitted,
non-null serialization/import refuses without acquiring credentials or invoking source callbacks.
A closed caller-owned generated envelope can compose the exact converter from
`ConnectLlmJson.Connection.Options.Converters` using `CanConvert(typeof(XaiFamilyBinding))` before
constructing its context. No converter attribute requires an inaccessible type in consumer-generated
source, and no arbitrary subtype registry or reflection fallback is installed.

These shared primitives do not register an xAI Image adapter, claim media endpoint availability,
qualify OAuth media access or establish native receipt account continuity. Existing Image transport
availability remains controlled by its real registered implementations.
