using Darkspyre.Inference.Tensor.Common;
using Microsoft.Extensions.Logging.Abstractions;
using TensorSharp.Runtime;
using TensorSharp.Runtime.Grammar;
using TensorSharp.Server;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

internal interface IQwenModelRuntime : IDisposable
{
    int? ContextLength { get; }
    string? LoadedBackend { get; }
    Task LoadAsync(TensorModelPackDefinition definition, TensorModelPackLease lease, string backend, CancellationToken cancellationToken);
    void Unload();
    void ApplyGrammar(SamplingConfig sampling, StructuredOutputFormat format);
    IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatSession session, List<ChatMessage> messages, int maxTokens,
        SamplingConfig sampling, CancellationToken cancellationToken);
}

internal sealed class QwenModelRuntime : IQwenModelRuntime
{
    private readonly ModelService _service = new(NullLogger<ModelService>.Instance);
    public int? ContextLength => _service.ContextTokens > 0 ? _service.ContextTokens : null;
    public string? LoadedBackend => _service.LoadedBackend;

    public async Task LoadAsync(TensorModelPackDefinition definition, TensorModelPackLease lease, string backend, CancellationToken cancellationToken)
    {
        using (GgufFile file = await GgufFile.OpenAsync(lease.GetRequiredArtifact("model"), cancellationToken).ConfigureAwait(false))
        {
            QwenModelCompatibility.ValidateCheckpoint(file, definition);
            file.ThrowIfTruncated();
        }
        await _service.LoadModelAsync(lease.GetRequiredArtifact("model"), null, backend, cancellationToken).ConfigureAwait(false);
    }

    public void ApplyGrammar(SamplingConfig sampling, StructuredOutputFormat format)
    {
        var cache = GrammarLibrary.ForJsonSchema(format.SchemaJson, _service.Model.Tokenizer);
        sampling.Grammar = GrammarLibrary.NewConstraint(cache, _service.Model.Tokenizer);
    }
    public IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatSession session, List<ChatMessage> messages, int maxTokens,
        SamplingConfig sampling, CancellationToken cancellationToken) =>
        _service.ChatStreamWithMetricsAsync(session, messages, maxTokens, cancellationToken, sampling, tools: null, enableThinking: false);
    public void Unload() => _service.UnloadModel();
    public void Dispose() => _service.Dispose();
}

internal static class QwenModelCompatibility
{
    public static void Validate(TensorModelPackDefinition definition)
    {
        TensorModelDefinitionValidator.Validate(definition);
        if (definition.Capability != TensorCapability.Llm || definition.RuntimeArchitecture != TensorLlmModels.QwenRuntimeArchitecture
            || definition.Artifacts.Count(artifact => artifact.Role == "model") != 1
            || definition.Artifacts.Any(artifact => !artifact.RelativePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                || (artifact.Role != "model" && !artifact.Role.StartsWith("model-shard-", StringComparison.Ordinal))))
        { throw new ArgumentException("A Qwen3 pack requires its GGUF model and any complete companion shards.", nameof(definition)); }
        if (definition.SupportedBackends.Count == 0 || definition.SupportedBackends.Any(backend =>
                !TensorLlmModels.Qwen3FourBillionQ4Km.SupportedBackends.Contains(backend)))
        { throw new ArgumentException("The Qwen pack declares an unsupported backend.", nameof(definition)); }
    }

    public static void ValidateCheckpoint(GgufFile file, TensorModelPackDefinition definition)
    {
        if (file.Version is not (2 or 3) || file.GetString("general.architecture") != "qwen3")
        { throw new InvalidDataException("This adapter requires Qwen3 GGUF version 2 or 3."); }
        int layers = Positive(file, "qwen3.block_count"), hidden = Positive(file, "qwen3.embedding_length");
        int heads = Positive(file, "qwen3.attention.head_count"), kvHeads = Positive(file, "qwen3.attention.head_count_kv");
        int feedForward = Positive(file, "qwen3.feed_forward_length");
        if (layers > file.Tensors.Count / 11) { throw new InvalidDataException("The Qwen layer tensor set is incomplete."); }
        _ = Positive(file, "qwen3.context_length");
        int headDimension = checked((int)file.GetUint32("qwen3.attention.key_length", (uint)(hidden / heads)));
        if (headDimension <= 0 || kvHeads > heads || heads % kvHeads != 0
            || file.GetUint32("qwen3.attention.value_length", (uint)headDimension) != headDimension)
        { throw new InvalidDataException("Incompatible Qwen attention dimensions."); }
        string[] vocabulary = file.GetStringArray("tokenizer.ggml.tokens") ?? throw new InvalidDataException("Missing tokenizer vocabulary.");
        if (vocabulary.Length == 0 || file.GetString("tokenizer.ggml.model") != "gpt2"
            || file.GetStringArray("tokenizer.ggml.merges") is not { Length: > 0 }
            || string.IsNullOrWhiteSpace(file.GetString("tokenizer.chat_template")))
        { throw new InvalidDataException("Qwen requires its BPE tokenizer and chat template metadata."); }
        int shards = checked((int)file.GetUint32("split.count", 1));
        if (shards != definition.Artifacts.Count || file.FilePaths.Count != shards
            || !file.FilePaths.Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(
                definition.Artifacts.Select(artifact => Path.GetFileName(artifact.RelativePath)).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            // Non-file stores may project a single artifact to a generated filename. Sharded packs must retain names.
            if (shards != 1 || definition.Artifacts.Count != 1 || file.FilePaths.Count != 1)
            { throw new InvalidDataException("The Qwen pack does not contain the complete declared shard set."); }
        }
        foreach (GgufTensorInfo tensor in file.Tensors.Values)
        {
            if (!Enum.IsDefined(tensor.Type) || tensor.Shape.Length is < 1 or > 4 || tensor.Shape.Any(dimension => dimension is 0 or > int.MaxValue)
                || (long)tensor.Shape[0] % GgufFile.GetBlockSize(tensor.Type) != 0)
            { throw new InvalidDataException("Unsupported Qwen tensor dimensions or quantization."); }
            long elements = 1;
            foreach (ulong dimension in tensor.Shape) { elements = checked(elements * (long)dimension); }
            long bytes = checked(elements / GgufFile.GetBlockSize(tensor.Type) * GgufFile.GetTypeSize(tensor.Type));
            _ = checked((long)tensor.Offset + bytes); // Reject overflowing storage ranges before allocating the model.
        }
        Shape(file, "token_embd.weight", hidden, vocabulary.Length);
        Shape(file, "output_norm.weight", hidden);
        if (file.Tensors.ContainsKey("output.weight")) { Shape(file, "output.weight", hidden, vocabulary.Length); }
        for (int layer = 0; layer < layers; layer++)
        {
            string prefix = $"blk.{layer}.";
            Shape(file, prefix + "attn_norm.weight", hidden);
            Shape(file, prefix + "attn_q.weight", hidden, checked(heads * headDimension));
            Shape(file, prefix + "attn_k.weight", hidden, checked(kvHeads * headDimension));
            Shape(file, prefix + "attn_v.weight", hidden, checked(kvHeads * headDimension));
            Shape(file, prefix + "attn_q_norm.weight", headDimension);
            Shape(file, prefix + "attn_k_norm.weight", headDimension);
            Shape(file, prefix + "attn_output.weight", checked(heads * headDimension), hidden);
            Shape(file, prefix + "ffn_norm.weight", hidden);
            Shape(file, prefix + "ffn_gate.weight", hidden, feedForward);
            Shape(file, prefix + "ffn_up.weight", hidden, feedForward);
            Shape(file, prefix + "ffn_down.weight", feedForward, hidden);
        }
    }

    private static int Positive(GgufFile file, string name)
    {
        uint value = file.GetUint32(name);
        return value is > 0 and <= int.MaxValue ? (int)value : throw new InvalidDataException("Missing or invalid Qwen dimensions.");
    }
    private static void Shape(GgufFile file, string name, params int[] dimensions)
    {
        if (!file.Tensors.TryGetValue(name, out GgufTensorInfo? tensor) || !tensor.Shape.SequenceEqual(dimensions.Select(value => (ulong)value)))
        { throw new InvalidDataException($"Qwen tensor '{name}' has a missing or incompatible shape."); }
    }
}
