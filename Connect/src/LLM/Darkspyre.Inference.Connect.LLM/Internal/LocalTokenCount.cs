using Microsoft.ML.Tokenizers;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>The bundled o200k_base count behind every local estimate. It is an estimate, not a provider count.</summary>
internal static class LocalTokenCount
{
    /// <summary>The accounting diagnostic for a CLI route counted with this tokenizer.</summary>
    public const string CliDiagnostic =
        "Estimated with a tokenizer that is not this model's; the CLI's own framing is not counted.";

    private static readonly Lazy<TiktokenTokenizer> Tokenizer = new(() => TiktokenTokenizer.CreateForEncoding("o200k_base"));

    public static int Count(string text) => Tokenizer.Value.CountTokens(text);
}
