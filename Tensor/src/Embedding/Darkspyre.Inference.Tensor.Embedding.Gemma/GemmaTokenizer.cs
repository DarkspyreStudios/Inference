using System.Text.Json;
using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.Embedding.Gemma;

/// <summary>Maps the pinned Hugging Face vocabulary into the shared Gemma BPE implementation.</summary>
internal sealed class GemmaTokenizer(BpeTokenizer tokenizer)
{
    internal static GemmaTokenizer Load(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;
        JsonElement model = root.GetProperty("model");
        if (model.GetProperty("type").GetString() != "BPE" || !model.GetProperty("byte_fallback").GetBoolean())
            throw new InvalidDataException("EmbeddingGemma requires its byte-fallback BPE tokenizer.");
        JsonElement vocab = model.GetProperty("vocab");
        var tokens = new string[vocab.EnumerateObject().Max(item => item.Value.GetInt32()) + 1];
        foreach (JsonProperty item in vocab.EnumerateObject()) tokens[item.Value.GetInt32()] = item.Name;
        if (tokens.Any(string.IsNullOrEmpty)) throw new InvalidDataException("The EmbeddingGemma vocabulary has missing entries.");
        var types = new int[tokens.Length];
        foreach (JsonElement item in root.GetProperty("added_tokens").EnumerateArray()) types[item.GetProperty("id").GetInt32()] = 3;
        string[] merges = model.GetProperty("merges").EnumerateArray()
            .Select(item => item[0].GetString() + " " + item[1].GetString()).ToArray();
        return new(new BpeTokenizer(tokens, types, merges, 2, [1], addBos: true, addEos: true, preTokenizerType: "gemma4"));
    }

    internal int[] Encode(string text) => tokenizer.Encode(text).ToArray();
}
