using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Embedding;

/// <summary>
/// How one embedding architecture is run. <see cref="GgufArchitecture"/> is the <c>general.architecture</c> the loaded
/// GGUF must report. <see cref="QueryTemplate"/> holds <c>{text}</c> and, when the model takes a task statement,
/// <c>{instruction}</c>; documents are embedded as given. <see cref="SupportedDimensions"/> lists the vector sizes the
/// model was trained to be cut to, including its native size.
/// </summary>
/// <param name="Name">The architecture name.</param>
/// <param name="GgufArchitecture">The <c>general.architecture</c> value the loaded GGUF must report.</param>
/// <param name="NativeDimensions">The size of the vectors the model produces.</param>
/// <param name="SupportedDimensions">The vector sizes the model was trained to be cut to, including the native size.</param>
/// <param name="MaximumInputTokens">The most tokens one input may hold.</param>
/// <param name="QueryTemplate">The template a query is formatted with.</param>
/// <param name="DefaultInstruction">The task statement used when a query gives none, or null when the model takes none.</param>
public sealed record TensorEmbeddingArchitecture(string Name, string GgufArchitecture, int NativeDimensions,
    IReadOnlyList<int> SupportedDimensions, int MaximumInputTokens, string QueryTemplate, string? DefaultInstruction)
{
    /// <summary>True when <see cref="QueryTemplate"/> has an <c>{instruction}</c> slot for a task statement.</summary>
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
        string identity = string.Join('\n', "darkspyre-embedding-1", Name, GgufArchitecture,
            string.Join(',', pack.Artifacts.OrderBy(artifact => artifact.Role, StringComparer.Ordinal)
                .Select(artifact => artifact.Role + "=" + artifact.Sha256.ToLowerInvariant())),
            QueryTemplate, DefaultInstruction ?? string.Empty, MaximumInputTokens.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}

/// <summary>The embedding packs this package ships, pinned to published content.</summary>
public static class TensorEmbeddingModels
{
    /// <summary>
    /// Qwen3-Embedding decoders: the final-normalised hidden state of the last token. A query states its task; the
    /// default instruction is the codebase retrieval task the model was evaluated with.
    /// </summary>
    public static TensorEmbeddingArchitecture Qwen3Gguf { get; } = new("qwen3-embedding-gguf", "qwen3", 1024,
        Array.AsReadOnly([32, 64, 128, 256, 512, 768, 1024]), 8192, "Instruct: {instruction}\nQuery:{text}",
        "Given a question about a codebase, retrieve the code or documentation that answers it");

    /// <summary>GGUF BERT and XLM-RoBERTa sentence encoders with CLS pooling.</summary>
    public static TensorEmbeddingArchitecture BertGguf { get; } = new("bert-embedding-gguf", "bert", 1024, Array.AsReadOnly([256, 1024]), 8192,
        "query: {text}", null);

    /// <summary>The architectures this package can run: <see cref="Qwen3Gguf"/> and <see cref="BertGguf"/>.</summary>
    public static IReadOnlyList<TensorEmbeddingArchitecture> Architectures { get; } = Array.AsReadOnly([Qwen3Gguf, BertGguf]);

    /// <summary>
    /// The backends embedding packs may declare: managed CPU and the GGML CPU and Metal backends, each verified on
    /// macOS. A pack declares only the backends its model was verified on.
    /// </summary>
    public static IReadOnlySet<TensorBackendKind> Backends { get; } = new[]
    {
        TensorBackendKind.Cpu, TensorBackendKind.GgmlCpu, TensorBackendKind.GgmlMetal,
    }.ToFrozenSet();

    /// <summary>The Hugging Face repository the Qwen3 embedding pack downloads from.</summary>
    public const string Qwen3Repository = "Qwen/Qwen3-Embedding-0.6B-GGUF";
    /// <summary>The pinned commit of <see cref="Qwen3Repository"/> that the Qwen3 embedding pack uses.</summary>
    public const string Qwen3Revision = "370f27d7550e0def9b39c1f16d3fbaa13aa67728";
    private const string Qwen3File = "Qwen3-Embedding-0.6B-Q8_0.gguf";

    /// <summary>Qwen3-Embedding-0.6B, Q8_0: the default embedding model.</summary>
    public static TensorModelPackDefinition Qwen3Embedding06B { get; } = new()
    {
        Key = new("qwen/qwen3-embedding-0.6b-q8_0", Qwen3Revision),
        Capability = TensorCapability.Embedding,
        RuntimeArchitecture = Qwen3Gguf.Name,
        License = "Apache-2.0",
        ProjectUri = new($"https://huggingface.co/{Qwen3Repository}"),
        SupportedBackends = Backends,
        Artifacts = Array.AsReadOnly<TensorModelArtifactDefinition>([new()
        {
            Role = "model", RelativePath = Qwen3File, Length = 639150592L,
            Sha256 = "06507c7b42688469c4e7298b0a1e16deff06caf291cf0a5b278c308249c3e439",
            Provenance = new(new("https://huggingface.co/"), Qwen3Repository, Qwen3Revision, Qwen3File, "Apache-2.0"),
            Sources = Array.AsReadOnly<Uri>([new($"https://huggingface.co/{Qwen3Repository}/resolve/{Qwen3Revision}/{Qwen3File}")])
        }])
    };

    /// <summary>The Hugging Face repository the Arctic embedding pack downloads from.</summary>
    public const string ArcticRepository = "fisher046/snowflake-arctic-embed-l-v2.0-Q8_0-GGUF";
    /// <summary>The pinned commit of <see cref="ArcticRepository"/> that the Arctic embedding pack uses.</summary>
    public const string ArcticRevision = "2b05c46c74499a1a8e5075cabc6f58490aa6f2c1";
    private const string ArcticFile = "snowflake-arctic-embed-l-v2.0-q8_0.gguf";

    /// <summary>Snowflake Arctic Embed L v2.0, Q8_0. TensorSharp's Metal encoder cannot run it, so it runs on CPU only.</summary>
    public static TensorModelPackDefinition ArcticEmbedL { get; } = new()
    {
        Key = new("snowflake/arctic-embed-l-v2.0-q8_0", ArcticRevision),
        Capability = TensorCapability.Embedding,
        RuntimeArchitecture = BertGguf.Name,
        License = "Apache-2.0",
        ProjectUri = new($"https://huggingface.co/{ArcticRepository}"),
        SupportedBackends = new[] { TensorBackendKind.Cpu, TensorBackendKind.GgmlCpu }.ToFrozenSet(),
        Artifacts = Array.AsReadOnly<TensorModelArtifactDefinition>([new()
        {
            Role = "model", RelativePath = ArcticFile, Length = 634554784L,
            Sha256 = "30b0d43dd5825809d2999fa2f4013a1d788c1539ad9ec163d1d305fa3b586ed8",
            Provenance = new(new("https://huggingface.co/"), ArcticRepository, ArcticRevision, ArcticFile, "Apache-2.0"),
            Sources = Array.AsReadOnly<Uri>([new($"https://huggingface.co/{ArcticRepository}/resolve/{ArcticRevision}/{ArcticFile}")])
        }])
    };

    /// <summary>The embedding packs this package ships: <see cref="Qwen3Embedding06B"/> and <see cref="ArcticEmbedL"/>.</summary>
    public static IReadOnlyList<TensorModelPackDefinition> BuiltIn { get; } = Array.AsReadOnly([Qwen3Embedding06B, ArcticEmbedL]);

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
            || definition.SupportedBackends.Count == 0 || !definition.SupportedBackends.IsSubsetOf(TensorEmbeddingModels.Backends))
            throw new ArgumentException("An embedding pack requires one supported GGUF model and backends TensorSharp's encoders run on.", nameof(definition));
        return architecture;
    }
}
