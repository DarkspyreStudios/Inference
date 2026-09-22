using System.Text;
using System.Text.Json;
using Microsoft.ML.Tokenizers;
using TensorSharp.Runtime;
using MlBpeTokenizer = Microsoft.ML.Tokenizers.BpeTokenizer;

namespace Darkspyre.Inference.Tensor.Decision;

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

    public static async Task<LayaTokenizer> CreateAsync(
        PersistenceFileReference source,
        CancellationToken cancellationToken)
    {
        await using Stream? content = await source.Store.OpenReadAsync(
            source.StoreName,
            source.AssetName,
            cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            throw new FileNotFoundException($"Tokenizer artifact '{source}' does not exist.", source.AssetName);
        }

        using JsonDocument document = await JsonDocument.ParseAsync(
            content,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement model = document.RootElement.GetProperty("model");
        Dictionary<string, int> vocabulary = model.GetProperty("vocab")
            .EnumerateObject()
            .ToDictionary(static item => item.Name, static item => item.Value.GetInt32(), StringComparer.Ordinal);
        var merges = new List<string>();
        foreach (JsonElement merge in model.GetProperty("merges").EnumerateArray())
        {
            merges.Add($"{merge[0].GetString()} {merge[1].GetString()}");
        }

        var specialTokens = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonElement item in document.RootElement.GetProperty("added_tokens").EnumerateArray())
        {
            if (item.GetProperty("special").GetBoolean())
            {
                specialTokens[item.GetProperty("content").GetString()!] = item.GetProperty("id").GetInt32();
            }
        }

        var options = new BpeOptions(vocabulary)
        {
            Merges = merges,
            SpecialTokens = specialTokens,
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
