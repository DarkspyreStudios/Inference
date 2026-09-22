# DarkspyreCE interface and repository design rules

These standards apply to every screen and repository tool unless `project.md` records a specific
exception. They are approval criteria, not suggestions.

## Pages are containers

A routed Razor Page provides layout, composition, route parameters, and thin wiring. It does not own
feature state, loading, cancellation, forms, grids, dialogs, editors, provider calls, or application
orchestration. Each functional feature is a reusable component under
`src/DarkspyreCE.Ui/Components/<Feature>/`; cross-feature interface primitives live under
`src/DarkspyreCE.Ui/Components/Common/`.

Audit every routed Page touched by a change. Extract accumulated functional logic rather than
growing the Page.

## Lumeo is the default component library

Use Lumeo whenever it has a suitable component. Confirm APIs through the authoritative Lumeo MCP
interface when available or the official version-matched documentation at `https://lumeo.nativ.sh/`.
Never infer an API by inspecting, grepping, or decompiling package source.

## One chosen control per job

- Bounded application-owned choices use Lumeo `Select`.
- Searchable or provider-derived choices that allow manual fallback use Lumeo `Combobox`.
- Single-line text uses `Input`; numeric values use `NumberInput`; multiline prose or JSON uses
  `Textarea`; booleans use `Switch` or `Checkbox` according to whether the change is immediate or
  submitted with a form.
- Dates use `DatePicker`; date-and-time values use `DateTimePicker`.

Do not mix these with native controls for the same job.

## Styling lives in classes

Presentation belongs in tracked stylesheets and reusable classes. Avoid inline styles and inline
scripts. Project JavaScript lives in tracked JavaScript files and is invoked through one owned
interop surface.

The canonical visual language is `docs/ui-design-language.md`. Theme tokens own colors, typography,
radii, shadows, control boundaries, and layout measures. Feature CSS consumes those tokens; it does
not introduce a local palette or patch a Lumeo component to make it match the product.

## Shared interface shapes have one component

Dialogs, editor layers, section headers, dense lists, resource rows, art-led tiles, empty states,
status chips, and repeated panel frames each have one shared component under `Components/Common/`.
Features provide semantic content and actions rather than copying markup or restyling the frame.

Feature components do not instantiate Lumeo `Dialog` directly after the shared wrapper exists.
`AppDialog` owns dialog chrome and sizing; `EditorLayer` composes it with layered editor behavior.
Confirmation semantics remain with Lumeo `AlertDialog` through the owned confirmation path.

Create a shared primitive with the first approved consumer, not as unused infrastructure. Once the
primitive exists, a feature-specific clone is a defect.

## Resource sections are collection-first

Worlds, Rulesets, Characters, Stories, Playthroughs, and other substantial sections open to a
purpose-built collection surface. They do not render an editor by default or present a bare sequence
of fields on arrival.

Read-only resource detail and immersive workspaces use dedicated routed pages. Create and edit use
the shared `EditorLayer` when the approved workflow is contextual to a collection or detail page.
The layer composes the shared wide-screen `AppDialog` and mobile-fullscreen behavior recorded in
`docs/ui-design-language.md`; a feature cannot substitute an ad hoc drawer or inline editor.

Confirmations and short self-contained actions use Lumeo `AlertDialog` through `OverlayService`.
Every dirty editor intercepts all close and navigation paths and requires confirmation before
discarding changes. Closing restores focus to the invoking control. Returning to a collection
preserves applicable filters, sorting, loaded pages, and scroll.

## Collections are lists by default

Use the shared dense, scannable list for resources recognized primarily by text and metadata. A tile
grid requires a documented imagery or uneven-content reason. Worlds and Playthroughs are approved
art-led tile-grid exceptions; their imagery is a primary recognition affordance.

## Data-driven inputs populate themselves and degrade

Load local or provider-derived options when their natural prerequisite becomes available. A failed
or empty provider source degrades to manual entry when the field supports it. A manual reload action
may supplement automatic loading but is not the only path.

## Independent reads start together

For initial load or refresh, first resolve only genuine prerequisites. Start every remaining
independent application-contract read before awaiting any of them, then await the group. Requests
with a real data dependency remain ordered after their prerequisite.

## Settings compose self-persisting cards

A settings route is a thin wrapper that composes groups. Each group renders self-contained cards,
and each card owns its load, validation, test action, status, and persistence. Shared card chrome
comes from one project component. There is no page-level Save All.

## Persistent surfaces live outside routed content

Anything that must survive navigation is a sibling of the routed outlet. One scoped session service
owns its live state and emits one change event. The component is a thin view and remains below modal
layers.

## Passing feedback is transient

Successful saves, deletes, imports, copies, tests, and refreshes use Lumeo `ToastService`.
Persistent inline alerts are reserved for information or failure states that remain actionable.

## Width uses named tiers

Pages and feature roots select one layout-owned width tier:

- `Reading` — prose and lone forms, maximum 48rem.
- `Default` — ordinary libraries and workspaces, maximum 100rem.
- `Full` — dense tools such as gameplay and inspectors, full available width.

Pages do not introduce their own maximum widths.

## Icons come from one owned library

All application icons route through the future owned `AppIcon` and `AppIconName` library. Add SVG
paths to that library; do not introduce another icon package. Do not create the library until the
first interface requires an icon.

## Repository tooling

- Tracked scripts, task documents, playbooks, and hand-authored input live in the repository.
- Each generator or ad hoc tool gets its own directory.
- Generated artifacts become tracked only when intentionally copied to their canonical destination.
- Fixed application ports, if ever introduced for development tooling, remain fixed rather than
  silently moving around stale processes.

## Project choices

| Principle | DarkspyreCE choice |
|---|---|
| Component library | Lumeo 5.9.1, centrally pinned |
| Routed UI | Thin `Pages/` containers over `Components/<Feature>/` behavior |
| Visual authority | Direction C (“World Atlas”) as recorded in `docs/ui-design-language.md` |
| Shared primitives | One centrally styled component per repeated interface shape |
| Resource editing | Collection-first routes; shared layered editor with dirty-close interception |
| Confirmation | Lumeo `AlertDialog` through `OverlayService` |
| Transient feedback | Lumeo `ToastService` and root `ToastProvider` |
| Width tiers | `Reading`, `Default`, and `Full` as defined above |
| Application boundary | UI Components call semantic application contracts; no persistence, provider SDK, vault, filesystem, or OS access |

Worlds and Playthroughs are the approved card-grid exceptions. Add another only when imagery or
uneven content is the primary recognition affordance and record it here with the design authority.
