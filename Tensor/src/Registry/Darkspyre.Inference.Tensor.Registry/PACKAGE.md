# Darkspyre.Inference.Tensor.Registry

Registration and discovery of the shipped Tensor engines: Qwen, Supra, Laya, Whisper, the GGUF embedding engine and
EmbeddingGemma. `TensorEngineRegistry` lists their definitions and creates their instances and settings managers, so
an application offers every engine without keeping its own engine list.

## Install

```bash
dotnet add package Darkspyre.Inference.Tensor.Registry
```

The package references every engine package. An application that uses one engine can reference that engine alone.

## Register

```csharp
services.AddTensorEngines(ggmlRuntimePlan);
```

`AddTensorEngines()` registers each shipped engine's `ITensorEngineProvider`, the shared `GgmlRuntimeOwner` and the
`TensorEngineRegistry` as singletons. A repeated call adds nothing. A provider registered separately joins the same
registry.

## Example

```csharp
TensorEngineRegistry registry = serviceProvider.GetRequiredService<TensorEngineRegistry>();
foreach (TensorEngineDefinition definition in registry.Definitions)
{
    // Show definition.Setup in the application's provider list.
}

if (registry.TryGetProvider(implementationId, out ITensorEngineProvider? provider))
{
    var packs = new TensorModelPackManager(new TensorModelCatalog(provider.BuiltInModels),
        Path.Combine(storageRoot, provider.Definition.ModelStoreKey));
    ITensorModelInstance instance = provider.CreateInstance(packs, TensorBackendKind.Auto, scratchDirectory);
}
```

## Limits

- Two providers with the same implementation id, or store keys equal ignoring case, fail when the registry is
  resolved.
- `TryGetProvider` matches the implementation id exactly and case-sensitively.
- Reading a definition or the built-in models constructs no engine, loads no native library and opens no store.
  `CreateInstance` does not start, load or download anything.
- The registry scans no assemblies and loads no plugins.
