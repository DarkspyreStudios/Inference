# Apple Foundation Models prototype

Research for [DSA-59](https://darkspyre.atlassian.net/browse/DSA-59). This is an
isolated, non-shipping tool. It does not register a Connect provider or change the
product's supported capabilities. The connector remains in technical design.

## Native dependency

The prototype evaluates Apple's C bridge at commit
`e868e60811aa0706feb2ccb33cfe7e27626287b7` of
[`apple/python-apple-fm-sdk`](https://github.com/apple/python-apple-fm-sdk).

Upstream extraction classification: retain the unmodified `foundation-models-c`
package and its license solely as an ignored build dependency in `.artifacts/`;
exclude the Python SDK and other upstream application code. No upstream source or
binary becomes part of the tracked prototype or a product package. Production
dependency distribution remains a design decision.

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

The native source stays unmodified so observations are attributable to the pinned
bridge. A build prerequisite failure stops the experiment; changing the bridge or
the integration design requires discussion.

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

Case names are in `Experiments.Names`. Reports contain only the built-in synthetic
prompts and native observations. Machine-generated logs and downloaded/build
artifacts stay in `.artifacts/`. The default report is `.artifacts/results.json`;
pass a third suite argument to preserve another run under a different name.

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

## Local verification

```bash
dotnet build AppleFoundationModelsPrototype.csproj --no-restore
dotnet format AppleFoundationModelsPrototype.csproj --no-restore --verify-no-changes
dotnet list AppleFoundationModelsPrototype.csproj package --vulnerable --include-transitive
```

No product pack, publication, Connect registration, schema converter or production
adapter is part of this prototype.
