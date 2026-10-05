# Darkspyre.Inference engineering laws

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

## L7 — Test the behaviour that changed with the smallest test that would catch a likely defect

- A change needs a new test only when it alters observable behaviour and no existing test would fail
  on a plausible defect in it.
- Write one test per behaviour. Add an edge or empty case only when the code really receives that
  input, or when a failure was observed there.
- Use a real collaborator when it is cheap, such as a task-owned scratch directory or the real SQLite
  store. A cheap stand-in at an expensive seam (native code, another process, the network) is allowed.
  A mock is allowed when the test proves the logic under it works. A test that only asserts a mock
  was called is not coverage.
- A new fixture project or probe needs the owner's approval.
- A test for the changed area must fit the focused run, under one minute. A test that cannot is a load
  test and follows the load-test exception.
- A review finding of "missing test" must name the defect the test would catch.

Paid or metered model calls are not development or commit gates. A conditional live experiment
reports that it was skipped or unavailable when its external service or credential is absent; it
never silently returns and appears to pass.

**Check.** Tests added per change, and the time of a focused run.

**Why it is a law.** A test exists to catch a likely defect cheaply. Every test is code someone
maintains and runs, so a test that catches nothing likely is a cost with no return.

**What it cost.** The source project's previous L7 required edge-case matrices, integration tests for
every multi-component change and real collaborators without stand-ins. It grew about 2,190 tests,
104k lines of test code, 15 fixture and probe projects and a 962-line Testing section. That rule
contradicted the one-minute focused-test limit.

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

## L17 — Product-family boundaries are explicit

`Connect` owns connections to external inference providers. `Tensor` owns self-hosted inference.
`Console` is a separate product surface. A capability belongs to exactly one product family, and
shared repository placement does not permit one family to absorb another family's runtime,
persistence, user interface, or deployment concerns.

Cross-family integration occurs through explicit package contracts. Do not use shared source,
linked files, or an umbrella assembly to blur ownership.

## L18 — Donor extraction is explicit

No donor project or directory is copied wholesale merely because it compiles. Every extracted area
is classified by the active technical design as retain, convert, rewrite, or exclude. Donor comments,
tests, and documents that encode application-specific authority are not current requirements.

## L19 — Complexity carries the burden of proof

Added complexity must name a concrete, likely harm it prevents. Complexity means a new guard, option,
layer, piece of state, abstraction or fallback. Without a named harm, the simpler option wins. "This
could happen" is not a likely harm.

This law never trims the L3 security findings, L11 to L15, credential handling or the sandbox.

**Why it is a law.** Agents add just-in-case code because they can. Every piece is code someone reads,
tests and keeps.

**What it cost.** Hardening nobody asked for, and the test suite the source project's old L7 grew.

**Check.** Diff size and new options or guards per change, compared with what was asked.

## L20 — Only real requirements bind

A requirement is an explicit ask for this piece of work, a law or recorded rule, or an observed
failure. Everything else must be offered as a proposal and not built. That includes a preference, an
earlier general remark such as "keep it safe", and nice-to-have hardening.

**Why it is a law.** Old remarks harden into permanent requirements, and higher effort levels spiral
into security work nobody asked for.

**What it cost.** The same hardening and test growth as L19, justified by remarks that were never
requirements.

**Check.** Diff size and new options or guards per change, compared with what was asked.

## L21 — Review findings name the harm and the smallest fix

Every review finding must name the concrete harm it prevents and the smallest change that fixes it. A
finding that names neither is taste, and it is dropped. A review asks for nothing beyond the change
under review, except a real defect the change exposes.

**Why it is a law.** Reviewers that try to please create work from taste.

**What it cost.** Review rounds spent on preference findings.

**Check.** The share of review findings that name a harm.

## L22 — Count the ongoing cost

A design or change must weigh maintenance, operational load and the reading cost for future
developers, not only the cost of building it. When two options both meet the requirements, the one
that is cheaper to keep wins.

**Why it is a law.** Build cost is paid once. Keep cost is paid on every later change.

**What it cost.** The source project's 104k lines of tests and 962-line testing section required
maintenance on every later change.

**Check.** Lines added against lines removed per change.

## L23 — A law must change behaviour

A new or changed law must state how its effect is checked. After it lands, it is checked against real
agent work: a rerun task, a review, or git history. A law that changes nothing is deleted.

**Why it is a law.** A sentence in a long rule file does nothing by being written down.

**What it cost.** Testing limits with no check lost to the source project's stronger L7 for weeks.

**Check.** Whether each law states a check.

## M-a — Provider model

Capabilities supplied by selectable or externally configured services use one provider-neutral
seam. Core and application consumers depend on that seam, while concrete implementations live in
outer adapters. Persisted selection, configuration, a manager, and data-driven discovery are added
only when the provider is genuinely selectable.

Before introducing a provider, find and reuse the existing seam for that capability. Never build a
second seam for the same job. Provider names and capabilities are self-describing so live
implementations, rather than duplicated configuration, define the catalog.

## M-b — Request path and dependency direction

The provider path is one-way:

`Host -> provider-neutral capability -> selected adapter -> external provider`

Hosts supply connection information, protected credentials, and policy through capability contracts.
Adapters own provider protocols. A capability assembly does not reach into a host's persistence,
filesystem, media library, authorization system, or user interface.

## M-c — Concrete implementations stay inside their owning adapters

Implementation-specific APIs are legal only inside the adapter that owns that implementation and the
host composition root that selects it. Provider SDK usage stays in provider-specific assemblies; the
owning product NuGet declares those SDK package dependencies. Vault APIs
stay in host tools or credential adapters, and persistence APIs stay outside shipping capability
libraries unless the capability contract explicitly owns persistence.

Consumers depend on semantic contracts and never receive provider-native payloads, concrete SDK
clients, secret-store handles, or persistence handles as a way to bypass them. Do not create universal
wrappers merely to hide implementations; contracts are named for the capability the consumer needs.
