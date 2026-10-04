using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Embedding;

/// <summary>
/// How one embedding architecture is run. <see cref="QueryTemplate"/> holds <c>{text}</c> and, when the model takes
/// a task statement, <c>{instruction}</c>; documents are embedded as given. <see cref="SupportedDimensions"/> lists
/// the vector sizes the model was trained to be cut to, including its native size.
/// </summary>
public sealed record TensorEmbeddingArchitecture(string Name, int NativeDimensions, IReadOnlyList<int> SupportedDimensions,
    int MaximumInputTokens, string QueryTemplate, string? DefaultInstruction)
{
    public bool TakesInstruction => QueryTemplate.Contains("{instruction}", StringComparison.Ordinal);

    /// <summary>The query as the model expects it. A null instruction uses the default.</summary>
    public string FormatQuery(string text, string? instruction)
    {
        int slot = QueryTemplate.IndexOf("{text}", StringComparison.Ordinal);
        string prefix = QueryTemplate[..slot].Replace("{instruction}", instruction ?? DefaultInstruction, StringComparison.Ordinal);
        return prefix + text + QueryTemplate[(slot + "{text}".Length)..];
    }

    /// <summary>
    /// Identifies the vectors a pack produces: its weights, architecture and query formatting. Packs with different
    /// fingerprints produce vectors that must not be compared.
    /// </summary>
    public string Fingerprint(TensorModelPackDefinition pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        string identity = string.Join('\n', "darkspyre-embedding-1", Name,
            string.Join(',', pack.Artifacts.OrderBy(artifact => artifact.Role, StringComparer.Ordinal)
                .Select(artifact => artifact.Role + "=" + artifact.Sha256.ToLowerInvariant())),
            QueryTemplate, DefaultInstruction ?? string.Empty, MaximumInputTokens.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}

/// <summary>The embedding packs this package ships, pinned to published content.</summary>
public static class TensorEmbeddingModels
{
    /// <summary>GGUF BERT and XLM-RoBERTa sentence encoders with CLS pooling.</summary>
    public static TensorEmbeddingArchitecture BertGguf { get; } = new("bert-embedding-gguf", 1024, Array.AsReadOnly([256, 1024]), 8192,
        "query: {text}", null);

    public static IReadOnlyList<TensorEmbeddingArchitecture> Architectures { get; } = Array.AsReadOnly([BertGguf]);

    public const string ArcticRepository = "fisher046/snowflake-arctic-embed-l-v2.0-Q8_0-GGUF";
    public const string ArcticRevision = "2b05c46c74499a1a8e5075cabc6f58490aa6f2c1";
    private const string ArcticFile = "snowflake-arctic-embed-l-v2.0-q8_0.gguf";

    /// <summary>Snowflake Arctic Embed L v2.0, Q8_0. Managed CPU only: the published TensorSharp native path cannot load it.</summary>
    public static TensorModelPackDefinition ArcticEmbedL { get; } = new()
    {
        Key = new("snowflake/arctic-embed-l-v2.0-q8_0", ArcticRevision),
        Capability = TensorCapability.Embedding,
        RuntimeArchitecture = BertGguf.Name,
        License = "Apache-2.0",
        ProjectUri = new($"https://huggingface.co/{ArcticRepository}"),
        SupportedBackends = new[] { TensorBackendKind.Cpu }.ToFrozenSet(),
        Artifacts = Array.AsReadOnly<TensorModelArtifactDefinition>([new()
        {
            Role = "model", RelativePath = ArcticFile, Length = 634554784L,
            Sha256 = "30b0d43dd5825809d2999fa2f4013a1d788c1539ad9ec163d1d305fa3b586ed8",
            Provenance = new(new("https://huggingface.co/"), ArcticRepository, ArcticRevision, ArcticFile, "Apache-2.0"),
            Sources = Array.AsReadOnly<Uri>([new($"https://huggingface.co/{ArcticRepository}/resolve/{ArcticRevision}/{ArcticFile}")])
        }])
    };

    public static IReadOnlyList<TensorModelPackDefinition> BuiltIn { get; } = Array.AsReadOnly([ArcticEmbedL]);

    /// <summary>The architecture that runs <paramref name="pack"/>, or null when no supported architecture does.</summary>
    public static TensorEmbeddingArchitecture? ArchitectureOf(TensorModelPackDefinition pack) =>
        Architectures.FirstOrDefault(architecture => architecture.Name == pack.RuntimeArchitecture);

    internal static TensorModelPackDefinition? KnownContent(long? length, string? hash) => BuiltIn.FirstOrDefault(pack =>
        pack.Artifacts[0].Length == length && string.Equals(pack.Artifacts[0].Sha256, hash, StringComparison.OrdinalIgnoreCase));
}

internal static class EmbeddingCompatibility
{
    internal static TensorEmbeddingArchitecture Validate(TensorModelPackDefinition definition)
    {
        TensorModelDefinitionValidator.Validate(definition);
        TensorEmbeddingArchitecture? architecture = definition.Capability == TensorCapability.Embedding
            ? TensorEmbeddingModels.ArchitectureOf(definition) : null;
        if (architecture is null || definition.Artifacts.Count != 1 || definition.Artifacts[0].Role != "model"
            || definition.SupportedBackends.Count != 1 || !definition.SupportedBackends.Contains(TensorBackendKind.Cpu))
            throw new ArgumentException("An embedding pack requires one supported GGUF model and the CPU backend.", nameof(definition));
        return architecture;
    }
}
