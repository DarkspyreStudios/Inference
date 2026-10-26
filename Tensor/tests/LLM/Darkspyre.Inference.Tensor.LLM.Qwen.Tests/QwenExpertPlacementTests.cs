using System.Reflection;
using System.Runtime.CompilerServices;
using Darkspyre.Inference.Tensor.Common;
using TensorSharp.Models;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class QwenExpertPlacementTests
{
    [Fact]
    public void PlacementCountsThePinnedRuntimePlanOnlyOnGpuBackends()
    {
        // ModelBase has no finalizer; this metadata-only fixture never allocates native weights.
        var model = (Qwen4ExpModel)RuntimeHelpers.GetUninitializedObject(typeof(Qwen4ExpModel));
        FieldInfo field = Assert.IsAssignableFrom<FieldInfo>(typeof(Qwen4ExpModel)
            .GetField("_expertOnHost", BindingFlags.Instance | BindingFlags.NonPublic));
        field.SetValue(model, Enumerable.Repeat(true, 37).Concat(Enumerable.Repeat(false, 11)).ToArray());
        Assert.Equal(new TensorExpertPlacement(11, 48), QwenExpertPlacement.From(model, "ggml_cuda"));
        Assert.Null(QwenExpertPlacement.From(model, "ggml_cpu"));
    }
}
