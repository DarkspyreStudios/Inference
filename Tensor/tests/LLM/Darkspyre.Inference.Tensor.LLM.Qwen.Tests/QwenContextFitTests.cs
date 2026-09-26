namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class QwenContextFitTests
{
    // Qwen3.8-27B: 64 layers, a full-attention layer every fourth, 4 KV heads of 256.
    private static readonly Dictionary<string, object> Qwen38 = new()
    {
        ["qwen35.block_count"] = 64u,
        ["qwen35.embedding_length"] = 5120u,
        ["qwen35.attention.head_count"] = 24u,
        ["qwen35.attention.head_count_kv"] = 4u,
        ["qwen35.attention.key_length"] = 256u,
        ["qwen35.attention.value_length"] = 256u
    };

    [Fact]
    public void CacheBytesCountOnlyFullAttentionLayers() =>
        Assert.Equal(64 * 1024, QwenContextFit.CacheBytesPerToken(Qwen38, "qwen35"));

    [Fact]
    public void Qwen3CountsEveryLayerAndDefaultsTheHeadSize()
    {
        var header = new Dictionary<string, object>
        {
            ["qwen3.block_count"] = 36u,
            ["qwen3.embedding_length"] = 2560u,
            ["qwen3.attention.head_count"] = 32u,
            ["qwen3.attention.head_count_kv"] = 8u
        };
        Assert.Equal(36L * 8 * (80 + 80) * 2, QwenContextFit.CacheBytesPerToken(header, "qwen3"));
    }

    [Theory]
    [InlineData(262144, 32L * 1024 * 1024 * 1024, 262144)] // Room for the whole declared context.
    [InlineData(262144, 8L * 1024 * 1024 * 1024, 131072)]  // 8 GiB spare fits half of it.
    [InlineData(262144, 0L, 0)]
    [InlineData(0, 8L * 1024 * 1024 * 1024, 0)]
    public void UsableContextIsTheDeclaredContextOrWhatFits(int declared, long spare, int expected) =>
        Assert.Equal(expected, QwenContextFit.Usable(declared, spare, 64 * 1024));

    [Fact]
    public void AnUnknownCacheSizeLeavesTheDeclaredContext() =>
        Assert.Equal(4096, QwenContextFit.Usable(4096, 0, 0));
}
