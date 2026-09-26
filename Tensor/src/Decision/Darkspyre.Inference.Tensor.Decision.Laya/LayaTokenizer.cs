using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;
using MlBpeTokenizer = Microsoft.ML.Tokenizers.BpeTokenizer;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

internal interface ILayaTokenizer
{
    IReadOnlyList<int> Encode(string text);
}

internal sealed class LayaTokenizer : ILayaTokenizer
{
    public const int ClassTokenId = 50_281;
    public const int SeparatorTokenId = 50_282;
    public const int PaddingTokenId = 50_283;
    public const int MaskTokenId = 50_284;
    public const string MaskToken = "[MASK]";

    private readonly MlBpeTokenizer _tokenizer;

    private LayaTokenizer(MlBpeTokenizer tokenizer)
    {
        _tokenizer = tokenizer;
    }

    /// <summary>
    /// Reads tokenizer.json at <paramref name="path"/> asynchronously, then builds the BPE tokenizer on
    /// a background thread: ML.Tokenizers builds it synchronously.
    /// </summary>
    public static async Task<LayaTokenizer> CreateAsync(string path, CancellationToken cancellationToken)
    {
        await using var content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using JsonDocument document = await JsonDocument.ParseAsync(
            content,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Parse(document.RootElement), cancellationToken).ConfigureAwait(false);
    }

    internal static LayaTokenizer Parse(JsonElement root)
    {
        if (!root.TryGetProperty("pre_tokenizer", out JsonElement preTokenizer)
            || preTokenizer.ValueKind != JsonValueKind.Object
            || preTokenizer.EnumerateObject().Count() != 4
            || !preTokenizer.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String || type.GetString() != "ByteLevel"
            || !preTokenizer.TryGetProperty("add_prefix_space", out JsonElement prefix) || prefix.ValueKind != JsonValueKind.False
            || !preTokenizer.TryGetProperty("trim_offsets", out JsonElement trim) || trim.ValueKind != JsonValueKind.True
            || !preTokenizer.TryGetProperty("use_regex", out JsonElement regex) || regex.ValueKind != JsonValueKind.True)
        {
            throw new InvalidDataException("Laya requires the ByteLevel GPT-2 pre-tokenizer without an added prefix space.");
        }
        JsonElement model = root.GetProperty("model");
        Dictionary<string, int> vocabulary = model.GetProperty("vocab")
            .EnumerateObject()
            .ToDictionary(static item => item.Name, static item => item.Value.GetInt32(), StringComparer.Ordinal);
        var merges = new List<string>();
        foreach (JsonElement merge in model.GetProperty("merges").EnumerateArray())
        {
            merges.Add($"{merge[0].GetString()} {merge[1].GetString()}");
        }

        var specialTokens = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonElement item in root.GetProperty("added_tokens").EnumerateArray())
        {
            // HF matches all added tokens before BPE, including ordinary whitespace runs.
            specialTokens.Add(item.GetProperty("content").GetString()!, item.GetProperty("id").GetInt32());
        }

        var options = new BpeOptions(vocabulary)
        {
            Merges = merges,
            SpecialTokens = specialTokens,
            PreTokenizer = new RegexPreTokenizer(
                new Regex(@"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+", RegexOptions.Compiled),
                specialTokens),
            ByteLevel = true,
            FuseUnknownTokens = false
        };
        return new LayaTokenizer(MlBpeTokenizer.Create(options));
    }

    public IReadOnlyList<int> Encode(string text) =>
        _tokenizer.EncodeToIds(
            text.Normalize(NormalizationForm.FormC),
            considerPreTokenization: true,
            considerNormalization: false);
}
