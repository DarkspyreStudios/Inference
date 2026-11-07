using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Decision.Laya.Tests;

public sealed class LayaServingParityTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static IEnumerable<object[]> GoldenCases() => File.ReadLines(Path.Combine(Fixtures, "laya-tokenizer-golden.jsonl"))
        .Select(line =>
        {
            using JsonDocument row = JsonDocument.Parse(line);
            return new object[] { row.RootElement.GetProperty("text").GetString()!,
                row.RootElement.GetProperty("ids").EnumerateArray().Select(id => id.GetInt32()).ToArray() };
        });

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public async Task TokenIdsMatchHuggingFaceReference(string text, int[] expected)
    {
        LayaTokenizer tokenizer = await LayaTokenizer.CreateAsync(Path.Combine(Fixtures, "tokenizer.json"), CancellationToken.None);
        Assert.Equal(expected, tokenizer.Encode(text));
    }
}
