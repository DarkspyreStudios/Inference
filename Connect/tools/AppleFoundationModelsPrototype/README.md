# Apple Foundation Models prototype

Research for [DSA-59](https://darkspyre.atlassian.net/browse/DSA-59). This is an
isolated, non-shipping tool. It does not register a Connect provider or change the
product's supported capabilities. The connector remains in technical design.

## Native dependency

The prototype evaluates Apple's C bridge at commit
`e868e60811aa0706feb2ccb33cfe7e27626287b7` of
[`apple/python-apple-fm-sdk`](https://github.com/apple/python-apple-fm-sdk).

Upstream extraction classification: retain the unmodified `foundation-models-c`
package and its license as the baseline ignored build dependency in `.artifacts/`;
exclude the Python SDK and other upstream application code. The authorized lifecycle
experiment applies a narrow patch to a separate copy. Only the reviewable patch,
owned continuation helper and research harness are tracked, not the full upstream
source or binaries. Production dependency distribution remains a design decision.

Prerequisites: macOS on Apple Silicon, Xcode with Foundation Models, the repository's
.NET 11 SDK, Python 3 for fetching the pinned source, and Apple Intelligence enabled
with the system model available. The tool does not install software, accept license
agreements, change system settings, or download model weights. Prompts are synthetic
and use only Apple's on-device system model.

Run from this directory:

```bash
python3 prepare-native.py
xcrun swift build --package-path .artifacts/upstream/foundation-models-c --configuration release --product FoundationModels
dotnet build AppleFoundationModelsPrototype.csproj
dotnet run --no-build --project AppleFoundationModelsPrototype.csproj -- suite .artifacts/upstream/foundation-models-c/.build/release/libFoundationModels.dylib
```

This baseline stays unmodified so its observations remain attributable to the pinned
bridge. A build prerequisite failure stops the experiment.

## Patched lifecycle experiment

The user authorized prototype fixes for immediate tool results, pending-tool
cancellation and cancelled streams. `native/bridge-lifecycle.patch` registers the
pending call before invoking host code, uses `PendingToolCall.swift` to settle
completion/cancellation once, and checks cancellation when the stream iterator ends.
No C ABI or production adapter is changed.

Build and run from this directory after preparing the baseline:

```bash
python3 prepare-patched.py --build --test
dotnet build AppleFoundationModelsPrototype.csproj
python3 - <<'PY'
import json
import subprocess
from pathlib import Path
metadata = json.loads(Path('.artifacts/patched.json').read_text())
subprocess.run(['dotnet', 'bin/Debug/net11.0/AppleFoundationModelsPrototype.dll',
                'lifecycle', metadata['libraryPath'],
                '.artifacts/patched-lifecycle-results.json'], check=True)
PY
```

Each patch/helper/test revision gets a separate directory identified by its SHA-256.
`.artifacts/patched.json` records the active library, original upstream revision and
patch fingerprint. The four deterministic Swift tests use the identical owned helper
in a small test-only package, including 1,000 races between registration, completion
and cancellation. They do not invoke a model.

## Experiments and output

The .NET project targets portable `net11.0`, is non-packable, has no package/project
dependencies and is not part of the shipping solution. Each experiment runs in a
fresh worker process with a 45-second parent timeout. The parent kills only the
worker tree it started. Native request waits are bounded as well.

The suite records availability/context size, UTF-8 completion, text streaming,
transcript restoration, schema shapes, ordinary JSON Schema acceptance, output
limits, cancellation and native tool callbacks. A single case can be repeated:

```bash
dotnet run --no-build --project AppleFoundationModelsPrototype.csproj -- worker .artifacts/upstream/foundation-models-c/.build/release/libFoundationModels.dylib cancel-stream
```

Case names are in `Experiments.Names`, `Experiments.LifecycleNames` and
`Experiments.BatchNames`, with additional worker-only cases below. The lifecycle
suite also checks duplicate immediate results and late results after cancellation.
Reports contain only the built-in synthetic prompts and native observations.
Machine-generated logs and downloaded/build
artifacts stay in `.artifacts/`. Default reports are `.artifacts/suite-results.json`
and `.artifacts/lifecycle-results.json` (`batches` writes `.artifacts/batches-results.json`);
pass a third suite argument to preserve
another run under a different name.

Outcomes:

- `observed`: the stated behavior was observed on this machine and input.
- `limitation`: the experiment exposed a bridge or contract-mapping limitation.
- `unexpected` or `inconclusive`: the experiment needs investigation; exit 2.
- `unavailable`: the prerequisite is missing; exit 3, never a passing test.
- `error` or a parent timeout: the experiment could not finish; exit 2.

Exit 0 means evidence was collected without an unresolved harness error. It does
**not** mean the bridge is production-ready: inspect every `limitation` result.

The immediate-tool experiment completes inside the callback, waits five seconds,
then re-sends the same result if the operation is still waiting. The cancellation
experiment withholds a tool result, cancels the native task, waits two seconds,
then supplies that result to let the worker settle. These diagnostic recovery
steps demonstrate the stalled operation; they are not proposed production fixes.
The delayed-tool control deliberately waits 100 ms before completing the tool.

Managed replies record `CancellationRequested` separately from raw native status.
Cancellation intent is ordered against terminal completion under one lock. This
preserves host intent even when the baseline bridge returns success for a cancelled
stream or Foundation Models wraps tool cancellation in a generic tool error.
The raw status remains available as evidence; this is not a production error mapper.

Native strings and owned references are released. Static unmanaged callbacks use
numeric IDs backed by an operation registry rather than dereferencing managed
object pointers. Finished contexts are removed; callbacks after removal are counted.
An operation that never finishes retains its context until its worker exits.
The dylib stays loaded for the worker lifetime. The suite observes terminal counts
and a short post-completion interval; it is not a leak detector or a complete proof
of native teardown under arbitrary concurrent calls.

These live probes are optional research, not normal deterministic test gates. See
[recorded findings](RESULTS.md) for the tested environment, evidence, design
consequences and remaining questions.

## Direct Swift output-limit probe

`native/TruncationProbe.swift` bypasses the bridge to inspect `rawContent.isComplete`
at output caps of 8 and 128 tokens. Run with a bounded parent process:

```bash
python3 - <<'PY'
import subprocess
from pathlib import Path
result = subprocess.run(['xcrun', 'swift', 'native/TruncationProbe.swift'],
                        capture_output=True, text=True, timeout=45)
Path('.artifacts/truncation-swift.json').write_text(result.stdout)
print(result.stdout)
print(result.stderr)
raise SystemExit(result.returncode)
PY
```

This is another optional live experiment. Unavailable models exit 3 and errors exit 2.

## Chat tool round-trip probe

The worker-only `tool-roundtrip` case evaluates Connect's return-call / later-result
chat pattern using the patched bridge. It captures the native transcript while a
single tool call is pending, cancels and disposes that original session, appends a
synthetic host result with the captured native call ID, and imports it into a new
session with the same tool registered. It then requests a response with an empty
composed prompt. Unexpected tool re-execution returns a sentinel so it cannot be
mistaken for consuming the supplied history result.

```bash
python3 - <<'PY'
import json
import subprocess
from pathlib import Path
metadata = json.loads(Path('.artifacts/patched.json').read_text())
result = subprocess.run(['dotnet', 'bin/Debug/net11.0/AppleFoundationModelsPrototype.dll',
                         'worker', metadata['libraryPath'], 'tool-roundtrip'],
                        capture_output=True, text=True, timeout=45)
Path('.artifacts/tool-roundtrip.json').write_text(result.stdout)
print(result.stdout)
print(result.stderr)
raise SystemExit(result.returncode)
PY
```

The probe compares callback arguments, restored transcript and preserved entry
prefix, counts terminal/tool callbacks, and records any added user entries. The
working design allows the adapter's empty internal turn while preserving host
history. The probe accepts at most one newly added user entry with an empty content
array. Its research projection excludes only the exact IDs observed for those
adapter-generated entries; it never removes arbitrary empty host messages.

Use `tool-roundtrip-repeat` in the command above, with report path
`.artifacts/tool-roundtrip-repeat.json`, to request the same key again in a second
turn. The host supplies `ORCHID-729` first and `LANTERN-842` second. The probe selects
unsettled native call IDs rather than matching a previous call by name/arguments,
verifies IDs are unique, disposes the generating session before each replay, and
records both native and projected histories. An answer without the requested fresh
tool call is `inconclusive`.

The original exact-turn-preservation result remains historical evidence. Current
`observed` results mean this narrow research mapping passed; they do not enable
chat tools or provide a production Common-to-Apple converter.

## Parallel tools and request boundaries

The `batches` suite covers two distinct calls in one request, two identical calls,
cancellation of two pending calls, two concurrent sessions sharing one tool, and
chat replay of an identical-call batch with results supplied in reverse order.
It requires two callbacks to be pending together; failing to reach that state is
`inconclusive`, never proof of parallel support. It waits up to 20 seconds for the
first callback and three more for the second, then cancels incomplete experiments.

```bash
python3 - <<'PY'
import json
import subprocess
from pathlib import Path
metadata = json.loads(Path('.artifacts/patched.json').read_text())
subprocess.run(['dotnet', 'bin/Debug/net11.0/AppleFoundationModelsPrototype.dll',
                'batches', metadata['libraryPath'],
                '.artifacts/batches-latest-results.json'], check=True)
PY
```

`callbackDeliveryOrder` is the order observed by the reader. Results are completed
in reverse order; the callback recorder's concurrent queue order is not used as a
native transcript identity. Matching identical arguments yields two candidates and
is recorded as a limitation. Batch chat replay uses transcript call UUIDs directly,
so it does not require that ambiguous callback-to-transcript join.

The separate direct Swift `native/ToolIdentityProbe.swift` holds two identical calls
and compares each callback argument's `GenerationID` with transcript argument IDs.
It reports missing/ambiguous identity without guessing. Run with a bounded parent:

```bash
python3 - <<'PY'
import subprocess
from pathlib import Path
result = subprocess.run(['xcrun', 'swift', '-swift-version', '6', '-warnings-as-errors',
                         'native/ToolIdentityProbe.swift'],
                        capture_output=True, text=True, timeout=45)
Path('.artifacts/tool-identity-swift.json').write_text(result.stdout)
print(result.stdout)
print(result.stderr)
raise SystemExit(result.returncode)
PY
```

Two additional worker-only cases use the same bounded command pattern as the chat
probe: `context-overflow` sends a synthetic prompt larger than the reported context
window and checks rollback; `tool-session-overlap` issues a second request while
the first awaits a tool, observes rejection and checks the first still completes.
The latter records native `isResponding` separately from actual request completion.

## Local verification

```bash
dotnet build AppleFoundationModelsPrototype.csproj --no-restore
dotnet format AppleFoundationModelsPrototype.csproj --no-restore --verify-no-changes
dotnet list AppleFoundationModelsPrototype.csproj package --vulnerable --include-transitive
```

No product pack, publication, Connect registration, schema converter or production
adapter is part of this prototype.
