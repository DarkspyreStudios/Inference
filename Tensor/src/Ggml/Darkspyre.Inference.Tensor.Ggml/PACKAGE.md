# Darkspyre.Inference.Tensor.Ggml

`GgmlRuntimeOwner`, the one owner of the process's GGML and MLX native runtime. Every Tensor engine that runs on GGML
or MLX, such as Qwen and the GGUF embedding engine, borrows it, so all of them run on the backend the process
selected.

## Install

```bash
dotnet add package Darkspyre.Inference.Tensor.Ggml
```

The package ships no native GGML library. The application supplies verified GGML drivers and describes them in a
`TensorRuntimePlan`.

## Register

`AddTensorEngines(plan)` from `Darkspyre.Inference.Tensor.Registry` registers the owner for the given plan. Without the
registry, `GgmlRuntimeOwner.GetOrCreate(plan)` returns the process's owner, created on first use.

## Example

```csharp
GgmlRuntimeOwner runtime = GgmlRuntimeOwner.GetOrCreate(plan);

// Pass runtime to each engine constructor that takes a GgmlRuntimeOwner. State and Current report the backend the
// process selected once an engine has started it.
Console.WriteLine($"{runtime.State}: {runtime.Current}");
```

Reference this package directly whenever code passes a `GgmlRuntimeOwner` to an engine's constructor.

## Limits

- A process loads at most one native candidate, and its native state stays until the process exits.
- `GetOrCreate` with a plan different from the one the process already owns throws `ArgumentException`. Without any
  plan, the owner offers no native candidate.
- After a native library has loaded, a failed start leaves the owner unavailable; another candidate needs a fresh
  process.
- The owner verifies a candidate's files before it loads them by absolute path.
