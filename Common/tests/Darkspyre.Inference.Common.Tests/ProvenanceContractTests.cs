using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.STT;

namespace Darkspyre.Inference.Common.Tests;

public sealed class ProvenanceContractTests
{
    private const string Unknown = "{\"ImplementationId\":\"actual-speech\",\"ModelId\":null}";

    [Fact]
    public void UnknownModelIsNullableButRemainsRequired()
    {
        PropertyInfo property = typeof(InferenceProvenance).GetProperty(nameof(InferenceProvenance.ModelId))!;
        Assert.Equal(NullabilityState.Nullable, new NullabilityInfoContext().Create(property).WriteState);
        Assert.True(InferenceJson.Provenance.Properties.Single(item => item.Name == "ModelId").IsRequired);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"ImplementationId\":\"actual-speech\"}", InferenceJson.Provenance));
        InferenceProvenance value = JsonSerializer.Deserialize(Unknown, InferenceJson.Provenance)!;
        Assert.Null(value.ModelId);
        using JsonDocument encoded = JsonDocument.Parse(JsonSerializer.Serialize(value, InferenceJson.Provenance));
        Assert.Equal(JsonValueKind.Null, encoded.RootElement.GetProperty("ModelId").ValueKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("actual-model")]
    public void NestedResultPreservesActualModelObservation(string? model)
    {
        var result = new LlmInferenceResult
        {
            Text = "actual",
            Status = LlmCompletionStatus.Completed,
            Provenance = new() { ImplementationId = "actual-speech", ModelId = model }
        };
        LlmInferenceResult restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(result, InferenceJson.LlmResult), InferenceJson.LlmResult)!;
        Assert.Equal(model, restored.Provenance.ModelId);
        var speech = new SpeechTranscriptionResult("actual", result.Provenance);
        var restoredSpeech = JsonSerializer.Deserialize(JsonSerializer.Serialize(speech, InferenceJson.SpeechTranscriptionResult), InferenceJson.SpeechTranscriptionResult)!;
        Assert.Equal(model, restoredSpeech.Provenance.ModelId);
    }

    [Fact]
    public void ForeignOwnedMetadataExecutesAndRetiresUnknownModelGraph()
    {
        WeakReference[] roots = ForeignRoundtrip();
        for (int attempt = 0; attempt < 12 && roots.Any(root => root.IsAlive); attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Assert.All(roots, root => Assert.False(root.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ForeignRoundtrip()
    {
        var context = new ForeignContext();
        Assembly assembly = context.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
        Type owner = assembly.GetType(typeof(InferenceJson).FullName!, throwOnError: true)!;
        var metadata = (JsonTypeInfo)owner.GetProperty(nameof(InferenceJson.Provenance))!.GetValue(null)!;
        Assert.Same(assembly, metadata.Type.Assembly);
        Assert.Equal(typeof(InferenceJson).Assembly.ManifestModule.ModuleVersionId, assembly.ManifestModule.ModuleVersionId);
        Assert.Same(context, AssemblyLoadContext.GetLoadContext(metadata.Type.Assembly));
        object value = JsonSerializer.Deserialize(Unknown, metadata)!;
        Assert.Null(metadata.Type.GetProperty("ModelId")!.GetValue(value));
        using JsonDocument encoded = JsonDocument.Parse(JsonSerializer.Serialize(value, metadata));
        Assert.Equal(JsonValueKind.Null, encoded.RootElement.GetProperty("ModelId").ValueKind);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"ImplementationId\":\"actual-speech\"}", metadata));
        WeakReference[] roots = [new(context), new(assembly), new(owner), new(metadata), new(value)];
        context.Unload();
        return roots;
    }

    private sealed class ForeignContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }
}
