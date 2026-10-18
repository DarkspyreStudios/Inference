using System.Reflection;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>The upstream TensorSharp release this library family was built against.</summary>
public static class TensorSharpRelease
{
    /// <summary>The TensorSharp package version, such as <c>2026.10.11</c>, from the assembly metadata
    /// <c>TensorSharpVersion</c> that the build writes from <c>Directory.Packages.props</c>.</summary>
    public static string Version { get; } = typeof(TensorSharpRelease).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "TensorSharpVersion")?.Value
        ?? throw new InvalidOperationException("The assembly carries no TensorSharpVersion metadata.");
}
