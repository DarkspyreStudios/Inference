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
    /// <summary>The loaded checkpoint's GGUF architecture, which selects its chat protocol.</summary>
    string? Architecture { get; }
    Task LoadAsync(TensorModelPackDefinition definition, TensorModelPackLease lease, string backend, CancellationToken cancellationToken);
    void Unload();
    void ApplyGrammar(SamplingConfig sampling, StructuredOutputFormat format, bool enableThinking);
    /// <summary>Estimates a request's prompt tokens by rendering and counting it, or null when the protocol has no renderer.</summary>
    int? EstimatePromptTokens(List<ChatMessage> messages, List<ToolFunction>? tools, bool enableThinking);
    IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatSession session, List<ChatMessage> messages, int maxTokens,
        SamplingConfig sampling, List<ToolFunction>? tools, bool enableThinking, CancellationToken cancellationToken);
}

internal sealed class QwenModelRuntime : IQwenModelRuntime
{
    private readonly ModelService _service = new(NullLogger<ModelService>.Instance);
    private int? _usableContext;
    /// <summary>The usable context measured at load; see <see cref="QwenContextFit"/>.</summary>
    public int? ContextLength => _service.ContextTokens > 0 ? _usableContext : null;
    public string? LoadedBackend => _service.LoadedBackend;
    public string? Architecture => _service.Architecture;

    /// <summary>
    /// Validates and loads the pack's GGUF on a background thread. TensorSharp reads the file and
    /// uploads weights synchronously; sibling shards are found beside the primary file.
    /// </summary>
    public Task LoadAsync(TensorModelPackDefinition definition, TensorModelPackLease lease, string backend, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            string modelPath = lease.GetRequiredArtifact("model");
            long cacheBytesPerToken;
            using (var file = new GgufFile(modelPath))
            {
                QwenModelCompatibility.ValidateCheckpoint(file, definition);
                file.ThrowIfTruncated();
                cacheBytesPerToken = QwenContextFit.CacheBytesPerToken(file.Metadata, QwenModelCompatibility.ArchitectureOf(file.Metadata));
            }
            cancellationToken.ThrowIfCancellationRequested();
            // TensorSharp annotates the projector path as non-null but treats null as no projector.
            _service.LoadModel(modelPath, null!, backend);
            // Measured once the weights are resident: memory taken later does not lower it.
            _usableContext = QwenContextFit.Usable(_service.ContextTokens, QwenContextFit.SpareBytes(), cacheBytesPerToken);
        }, cancellationToken);

    public void ApplyGrammar(SamplingConfig sampling, StructuredOutputFormat format, bool enableThinking)
    {
        var cache = GrammarLibrary.ForJsonSchema(format.SchemaJson, _service.Model.Tokenizer);
        GrammarConstraint constraint = GrammarLibrary.NewConstraint(cache, _service.Model.Tokenizer);
        // A model that thinks first writes its thinking block before the answer the schema constrains.
        if (OutputParserFactory.GrammarActivationTrigger(_service.Architecture, enableThinking) is { } trigger)
        { constraint.ActivateAfter(trigger, skipLeadingWhitespace: true); }
        sampling.Grammar = constraint;
    }
    public IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatSession session, List<ChatMessage> messages, int maxTokens,
        SamplingConfig sampling, List<ToolFunction>? tools, bool enableThinking, CancellationToken cancellationToken) =>
        _service.ChatStreamWithMetricsAsync(session, messages, maxTokens, cancellationToken, sampling, tools, enableThinking);
    public int? EstimatePromptTokens(List<ChatMessage> messages, List<ToolFunction>? tools, bool enableThinking)
    {
        string architecture = _service.Architecture;
        if (ChatProtocolRegistry.For(architecture)?.Render is not { } render) { return null; }
        string prompt = render(new ChatRenderRequest(messages, AddGenerationPrompt: true, architecture, tools, enableThinking));
        return _service.Model.Tokenizer.Encode(prompt, addSpecial: false).Count;
    }
    public void Unload()
    {
        _service.UnloadModel();
        _usableContext = null;
    }
    public void Dispose() => _service.Dispose();
}

internal static class QwenModelCompatibility
{
    /// <summary>GGUF architectures this adapter loads: Qwen 3, and the Qwen 3.5 family that TensorSharp runs as one model class.</summary>
    internal static readonly IReadOnlySet<string> Architectures = new HashSet<string>(["qwen3", "qwen35", "qwen35moe", "qwen3next"], StringComparer.Ordinal);

    public static void Validate(TensorModelPackDefinition definition)
    {
        TensorModelDefinitionValidator.Validate(definition);
        if (definition.Capability != TensorCapability.Llm || definition.RuntimeArchitecture != TensorLlmModels.QwenRuntimeArchitecture
            || definition.Artifacts.Count(artifact => artifact.Role == "model") != 1
            || definition.Artifacts.Any(artifact => !artifact.RelativePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                || (artifact.Role != "model" && !artifact.Role.StartsWith("model-shard-", StringComparison.Ordinal))))
        { throw new ArgumentException("A Qwen pack requires its GGUF model and any complete companion shards.", nameof(definition)); }
        if (definition.SupportedBackends.Count == 0 || definition.SupportedBackends.Any(backend =>
                !TensorLlmModels.Qwen3FourBillionQ4Km.SupportedBackends.Contains(backend)))
        { throw new ArgumentException("The Qwen pack declares an unsupported backend.", nameof(definition)); }
    }

    public static void ValidateCheckpoint(GgufFile file, TensorModelPackDefinition definition)
        => ValidateCheckpoint(new CheckpointMetadata(file.Version, file.Metadata, file.Tensors, file.FilePaths), definition);

    internal static void ValidateHeaders(IReadOnlyList<(TensorSourceArtifact Artifact, GgufMetadata Header)> files,
        TensorModelPackDefinition definition)
    {
        var tensors = new Dictionary<string, GgufTensorInfo>(StringComparer.Ordinal);
        for (int index = 0; index < files.Count; index++)
        {
            GgufMetadata header = files[index].Header;
            var view = new CheckpointMetadata(header.Version, header.Values, header.Tensors, [files[index].Artifact.Path]);
            if (header.Version != files[0].Header.Version || view.GetUint32("split.count", 1) != files.Count
                || view.GetUint32("split.no", (uint)index) != index
                || view.GetString("general.architecture") != files[0].Header.Values.GetValueOrDefault("general.architecture") as string)
            { throw new InvalidDataException("Inconsistent GGUF shard metadata."); }
            foreach (GgufTensorInfo tensor in header.Tensors.Values)
            {
                if (!tensors.TryAdd(tensor.Name, tensor)) { throw new InvalidDataException("Duplicate GGUF tensor across shards."); }
                long bytes = TensorBytes(tensor);
                if (checked(header.DataOffset + (long)tensor.Offset + bytes) > header.FileLength)
                { throw new InvalidDataException("GGUF tensor storage exceeds the artifact length."); }
            }
        }
        GgufMetadata primary = files[0].Header;
        ValidateCheckpoint(new CheckpointMetadata(primary.Version, primary.Values, tensors, files.Select(file => file.Artifact.Path).ToArray()), definition);
    }

    /// <summary>The architecture named by the checkpoint header, when this adapter loads it.</summary>
    internal static string ArchitectureOf(IReadOnlyDictionary<string, object> header) =>
        header.GetValueOrDefault("general.architecture") is string architecture && Architectures.Contains(architecture)
            ? architecture : throw new InvalidDataException("This adapter requires a Qwen 3 or Qwen 3.5 family GGUF.");

    private static void ValidateCheckpoint(CheckpointMetadata file, TensorModelPackDefinition definition)
    {
        if (file.Version is not (2 or 3)) { throw new InvalidDataException("This adapter requires GGUF version 2 or 3."); }
        string arch = ArchitectureOf(file.Values);
        int layers = Positive(file, $"{arch}.block_count"), hidden = Positive(file, $"{arch}.embedding_length");
        int heads = Positive(file, $"{arch}.attention.head_count"), kvHeads = Positive(file, $"{arch}.attention.head_count_kv");
        _ = Positive(file, $"{arch}.context_length");
        int headDimension = checked((int)file.GetUint32($"{arch}.attention.key_length", (uint)(hidden / heads)));
        if (headDimension <= 0 || kvHeads > heads || heads % kvHeads != 0)
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
        { throw new InvalidDataException("The Qwen pack does not contain the complete declared shard set."); }
        foreach (GgufTensorInfo tensor in file.Tensors.Values)
        {
            long bytes = TensorBytes(tensor);
            _ = checked((long)tensor.Offset + bytes); // Reject overflowing storage ranges before allocating the model.
        }
        Shape(file, "token_embd.weight", hidden, vocabulary.Length);
        Shape(file, "output_norm.weight", hidden);
        if (file.Tensors.ContainsKey("output.weight")) { Shape(file, "output.weight", hidden, vocabulary.Length); }
        if (arch == "qwen3") { ValidateQwen3Layers(file, layers, hidden, heads, kvHeads, headDimension); }
        else { ValidateHybridLayers(file, arch, layers); }
    }

    private static void ValidateQwen3Layers(CheckpointMetadata file, int layers, int hidden, int heads, int kvHeads, int headDimension)
    {
        int feedForward = Positive(file, "qwen3.feed_forward_length");
        if (layers > file.Tensors.Count / 11) { throw new InvalidDataException("The Qwen layer tensor set is incomplete."); }
        if (file.GetUint32("qwen3.attention.value_length", (uint)headDimension) != headDimension)
        { throw new InvalidDataException("Incompatible Qwen attention dimensions."); }
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

    // The Qwen 3.5 family mixes full-attention layers with GatedDeltaNet recurrent layers. TensorSharp's
    // own load validates each layer's tensors; this checks the recurrent state and the layer layout.
    private static void ValidateHybridLayers(CheckpointMetadata file, string arch, int layers)
    {
        foreach (string key in new[] { "ssm.inner_size", "ssm.state_size", "ssm.group_count", "ssm.time_step_rank", "ssm.conv_kernel" })
        { _ = Positive(file, $"{arch}.{key}"); }
        if (FullAttentionLayers(file.Values, arch, layers) is <= 0)
        { throw new InvalidDataException("The Qwen layer layout has no full-attention layer."); }
        if (layers > file.Tensors.Count / 4) { throw new InvalidDataException("The Qwen layer tensor set is incomplete."); }
    }

    /// <summary>
    /// Counts the layers that keep full attention history. The Qwen 3.5 family names them in
    /// <c>layer_types</c> when its length matches the block count, or places one every
    /// <c>full_attention_interval</c> layers (default 4), as TensorSharp's loader does. Every Qwen 3
    /// layer is full attention.
    /// </summary>
    internal static int FullAttentionLayers(IReadOnlyDictionary<string, object> header, string arch, int layers)
    {
        if (arch == "qwen3") { return layers; }
        if (header.GetValueOrDefault($"{arch}.layer_types") is string[] types && types.Length == layers)
        { return types.Count(type => !string.Equals(type, "linear_attention", StringComparison.OrdinalIgnoreCase)); }
        uint interval = header.GetValueOrDefault($"{arch}.full_attention_interval") is { } value
            ? Convert.ToUInt32(value, System.Globalization.CultureInfo.InvariantCulture) : 4;
        return interval is > 0 and <= int.MaxValue ? layers / (int)interval : throw new InvalidDataException("Invalid Qwen full-attention interval.");
    }

    private static long TensorBytes(GgufTensorInfo tensor)
    {
        if (!Enum.IsDefined(tensor.Type) || tensor.Shape.Length is < 1 or > 4 || tensor.Shape.Any(dimension => dimension is 0 or > int.MaxValue)
            || (long)tensor.Shape[0] % GgufFile.GetBlockSize(tensor.Type) != 0)
        { throw new InvalidDataException("Unsupported Qwen tensor dimensions or quantization."); }
        long elements = 1;
        foreach (ulong dimension in tensor.Shape) { elements = checked(elements * (long)dimension); }
        return checked(elements / GgufFile.GetBlockSize(tensor.Type) * GgufFile.GetTypeSize(tensor.Type));
    }

    private sealed record CheckpointMetadata(uint Version, IReadOnlyDictionary<string, object> Values,
        IReadOnlyDictionary<string, GgufTensorInfo> Tensors, IReadOnlyList<string> FilePaths)
    {
        public string? GetString(string key) => Values.TryGetValue(key, out object? value) ? value as string : null;
        public string[]? GetStringArray(string key) => Values.TryGetValue(key, out object? value) ? value as string[] : null;
        public uint GetUint32(string key, uint defaultValue = 0) => Values.TryGetValue(key, out object? value)
            ? Convert.ToUInt32(value, System.Globalization.CultureInfo.InvariantCulture) : defaultValue;
    }

    private static int Positive(CheckpointMetadata file, string name)
    {
        uint value = file.GetUint32(name);
        return value is > 0 and <= int.MaxValue ? (int)value : throw new InvalidDataException("Missing or invalid Qwen dimensions.");
    }
    private static void Shape(CheckpointMetadata file, string name, params int[] dimensions)
    {
        if (!file.Tensors.TryGetValue(name, out GgufTensorInfo? tensor) || !tensor.Shape.SequenceEqual(dimensions.Select(value => (ulong)value)))
        { throw new InvalidDataException($"Qwen tensor '{name}' has a missing or incompatible shape."); }
    }
}
