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
/// <param name="Backends">The backends a pack of this architecture runs on.</param>
public sealed record TensorEmbeddingArchitecture(string Name, string GgufArchitecture, int NativeDimensions,
    IReadOnlyList<int> SupportedDimensions, int MaximumInputTokens, string QueryTemplate, string? DefaultInstruction,
    IReadOnlySet<TensorBackendKind> Backends)
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

/// <summary>The embedding architectures and backends this package runs. Embedding packs arrive by import.</summary>
public static class TensorEmbeddingModels
{
    /// <summary>
    /// The backends embedding packs may declare: managed CPU and the GGML CPU and Metal backends, each verified on
    /// macOS. A pack declares only the backends its model was verified on.
    /// </summary>
    public static IReadOnlySet<TensorBackendKind> Backends { get; } = new[]
    {
        TensorBackendKind.Cpu, TensorBackendKind.GgmlCpu, TensorBackendKind.GgmlMetal,
    }.ToFrozenSet();

    /// <summary>
    /// GGUF BERT and XLM-RoBERTa sentence encoders with CLS pooling. They run on managed CPU and GGML CPU only, because
    /// TensorSharp's Metal encoder cannot run them.
    /// </summary>
    public static TensorEmbeddingArchitecture BertGguf { get; } = new("bert-embedding-gguf", "bert", 1024, Array.AsReadOnly([256, 1024]), 8192,
        "query: {text}", null, new[] { TensorBackendKind.Cpu, TensorBackendKind.GgmlCpu }.ToFrozenSet());

    /// <summary>The architectures this package runs: <see cref="BertGguf"/>.</summary>
    public static IReadOnlyList<TensorEmbeddingArchitecture> Architectures { get; } = Array.AsReadOnly([BertGguf]);

    /// <summary>The reason an installed Qwen3-Embedding pack is not supported.</summary>
    public const string Qwen3EmbeddingRetiredReason = "Qwen3-Embedding models are no longer supported.";

    /// <summary>The architecture that runs <paramref name="pack"/>, or null when no supported architecture does.</summary>
    public static TensorEmbeddingArchitecture? ArchitectureOf(TensorModelPackDefinition pack) =>
        Architectures.FirstOrDefault(architecture => architecture.Name == pack.RuntimeArchitecture);
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

    /// <summary>
    /// The family architecture a GGUF header describes, or an InvalidDataException naming the first rule it breaks.
    /// </summary>
    internal static TensorEmbeddingArchitecture MatchHeader(IReadOnlyDictionary<string, object> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string? name = values.GetValueOrDefault("general.architecture") as string;
        TensorEmbeddingArchitecture architecture = TensorEmbeddingModels.Architectures.FirstOrDefault(item => item.GgufArchitecture == name)
            ?? throw new InvalidDataException($"GGUF architecture '{name}' is not an embedding architecture this engine runs.");
        if (values.GetValueOrDefault($"{name}.pooling_type") is not uint pooling || pooling is < 1 or > 3)
            throw new InvalidDataException("The GGUF names no embedding pooling; it is not an embedding model.");
        object? dimensions = values.GetValueOrDefault($"{name}.embedding_length");
        if (dimensions is not uint length || length != architecture.NativeDimensions)
            throw new InvalidDataException($"The GGUF has {dimensions ?? "no"} dimensions; this engine's {name} embeddings have {architecture.NativeDimensions}.");
        object? context = values.GetValueOrDefault($"{name}.context_length");
        if (context is not uint tokens || tokens < architecture.MaximumInputTokens)
            throw new InvalidDataException($"The GGUF's context of {context ?? "no"} tokens is below this engine's {architecture.MaximumInputTokens}-token limit.");
        string? tokenizer = values.GetValueOrDefault("tokenizer.ggml.model") as string;
        if (tokenizer is not ("bert" or "t5"))
            throw new InvalidDataException($"Tokenizer '{tokenizer}' is not one this engine reads for {name}.");
        return architecture;
    }
}
