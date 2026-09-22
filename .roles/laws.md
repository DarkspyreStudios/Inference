# DarkspyreCE engineering laws

These cross-cutting rules govern every design, implementation, review, and cleanup in this
repository. A change that violates a law is wrong unless the exception is declared here and
justified at the affected site.

## L1 — No legacy shims

When an internal type, signature, schema, message, or contract changes, update every caller in the
same unit and delete the old shape. Do not retain adapters, deprecated parallel methods, dual-read
bridges, or loopback compatibility paths for internal compatibility.

The only compatibility exception is a genuinely public, versioned, externally consumed contract
listed in `project.md`. Several wanted provider implementations behind one provider-neutral seam are
not compatibility shims.

## L2 — No dead code

Delete orphaned code, assets, configuration, navigation, tests, and commented-out blocks in the same
unit that makes them obsolete. Version control is the archive.

A forward seam is allowed only when it is inert, accurately documented as not yet wired, and tracked
in the authoritative tracker. Untracked or falsely documented forward seams are dead code.

## L3 — Zero warnings

The standing build state is zero warnings, with warnings treated as errors. Fix real problems. A
deliberately correct warning may be suppressed only narrowly at the exact site with a written
justification. Security and dependency findings are never suppressed merely to obtain a green build.

## L4 — Whole-file commits only

Stage and commit with explicit pathspecs. Never split hunks from one file into separate commits. If
multiple actors edited a file, one commit carries the entire file and says so. Commit only files
whose complete current contents belong to the finished unit.

## L5 — Track or ignore every new file immediately

Every new file is added to Git or covered by an intentional ignore rule as soon as it is created.
Source, tests, documentation, tooling, and hand-authored input are tracked. Generated output, caches,
logs, local databases, and secrets are ignored.

## L6 — Finishing a unit includes committing it

A unit is complete only when its implementation, affected canonical documentation, and verification
are complete and committed together using whole-file pathspecs.

## L7 — Maintain a deterministic, cost-bounded test suite

Paid or metered model calls are not development or commit gates. Normal tests use controlled doubles
around remote models, filesystems, credential vaults, clocks, and network boundaries. A conditional
live experiment reports that it was skipped or unavailable when its external service or credential
is absent; it never silently returns and appears to pass.

## L8 — Consult and maintain canonical documents

Before changing an area, read its canonical design. When a change alters a pattern, seam, interface,
data shape, or decision, update the authoritative document in the same unit and record the decision
and reason.

When a concept is renamed or removed, search the entire repository and update every current source
pointer, test name, comment, and document reference. Historical evidence may retain the old term only
when it is clearly historical.

## L9 — One way only

There is one implementation for each concern, shared by every consumer. Migrating to a new way means
moving all consumers and deleting the old way in the same unit. A half-migration is blocking rather
than temporary. Several real backends exist only as adapters behind one capability seam.

## L10 — Shared runtime state has one owner

Shared live state has exactly one owner. Consumers read its published view and mutate through its
methods. Filtering, ordering, derived-state reconciliation, and the single change notification
belong to that owner. Consumers do not rebuild or cache parallel authoritative views.

## L11 — A request is authoritative only over the records it names

If a parent owns five children and an operation names three, only those three may be inserted or
updated. The other two remain bit-identical. Omission never means delete, deactivate, re-parent, or
touch timestamps.

Incoming items update existing records in place by stable identity and insert only genuinely new
records. A contract that needs deletion carries an explicit mutation.

## L12 — Deletion is explicit, never inferred

A record is deleted only when the user or owning operation explicitly directs deletion of that
record. Application code validates dependent records and deletion authority before committing.
Storage cascades are an implementation detail, not the source of intent.

## L13 — Schema changes are generated, never hand-written

Schema changes come from the selected migration tooling. Validate a migration against a fresh or
isolated local store. Do not hand-author replacement migration statements.

## L14 — Untrusted input is classified and handled once at the boundary

Every untrusted value receives one declared boundary policy. Sanitizable display text uses the owned
sanitizer at its entry boundary. Paths, URLs, files, imports, and structured values use their
authoritative parsers and validators there. Downstream handlers do not repeat ad hoc stripping.

Opaque narrative text, source material, provider payloads, and credentials may be exempt from text
sanitization only through an annotated policy explaining why mutation would corrupt the value and
how it is prevented from becoming executable markup, a path, a command, or a log secret.

## L15 — Dependency vulnerabilities are fixed, never hidden

Upgrade or replace vulnerable dependencies. Do not suppress an advisory merely to reduce the count.
A narrow temporary exception requires a recorded advisory, reachability rationale, dated approval,
removal task, and scope limited to that advisory.

## L16 — Early-alpha state is disposable

Until `project.md` records the first release baseline, no local database, migration chain, package,
or internal interface is a backward-compatibility obligation. Implement only the authoritative
shape and update every caller, test, fixture, and document together. Prefer resetting disposable
development data over adding upgrade code or dual readers.

Tracked source, hand-authored fixtures, documentation, and assets are not disposable. Secrets remain
protected even when their local store is reset.

## L17 — The product is local

DarkspyreCE owns one local user's private workspace. Story data, memory, canonical state, media,
provider profiles, and operation state remain on that installation. Network access is limited to a
model provider explicitly configured by the user and a separately approved update mechanism.

Do not introduce a hosted application API, account system, multi-user ownership model, public
content catalog, synchronization service, content telemetry, or server moderation workflow without
returning to general design.

Technical validation remains mandatory. Imports, files, paths, structured values, provider results,
and canonical writes enforce their structural, integrity, authority, and resource-boundary rules.
Any non-overridable content policy is a separate explicit product decision.

## L18 — Donor extraction is explicit

No donor project or directory is copied wholesale merely because it compiles. Every extracted area
is classified by `docs/source-extraction-manifest.md` as retain, convert, rewrite, or exclude. Donor
comments, tests, and documents that encode hosted authority are not current requirements.

## M-a — Provider model

Capabilities supplied by selectable or externally configured services use one provider-neutral
seam. Core and application consumers depend on that seam, while concrete implementations live in
outer adapters. Persisted selection, configuration, a manager, and data-driven discovery are added
only when the provider is genuinely selectable.

Before introducing a provider, find and reuse the existing seam for that capability. Never build a
second seam for the same job. Provider names and capabilities are self-describing so live
implementations, rather than duplicated configuration, define the catalog.

## M-b — Request path and dependency direction

The local interactive path is one-way:

`Page -> Component -> application contract -> application Service -> deterministic Logic`

An application Service may orchestrate provider-neutral capability contracts. Concrete persistence,
filesystem, credential-vault, model, media, and operating-system adapters live outside the
application layer. A layer does not skip its next owned boundary to reach those implementations.

Routed Pages are composition roots for route parameters and layout only. Components own feature
state and invoke application contracts. The Desktop project is the application composition root: it
selects adapters and starts owned background facilities, but it does not contain use-case logic.

## M-c — Concrete implementations stay inside their owning adapters

Implementation-specific APIs are legal only inside the adapter that owns that implementation and in
the Desktop composition root that selects it. EF contexts stay inside persistence adapters; `File`
and `Directory` stay inside file-backed adapters; provider SDKs stay inside provider adapters; vault
APIs stay inside the credential adapter.

Consumers depend on semantic contracts and never receive database handles, absolute paths,
provider-native payloads, concrete clients, or vault instances as a way to bypass them.

EF persistence adapters use a factory rather than retaining a scoped context. When a use case needs
one atomic multi-record operation, one semantic transaction contract owns the context and
transaction. Independently created repositories are not composed in a way that weakens atomicity.

Do not create universal wrappers merely to hide implementations. Contracts are named for the
capability the consumer needs.
