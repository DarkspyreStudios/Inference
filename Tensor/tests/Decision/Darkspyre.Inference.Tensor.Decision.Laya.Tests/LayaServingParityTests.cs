using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Tensor.Common;
using TensorSharp.Runtime;

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
        var source = new PersistenceFileReference(new TensorDirectFilePersistStore(AppContext.BaseDirectory), "Fixtures", "tokenizer.json");
        LayaTokenizer tokenizer = await LayaTokenizer.CreateAsync(source, CancellationToken.None);
        Assert.Equal(expected, tokenizer.Encode(text));
    }

    [Fact]
    public void GoldenFixtureIsThePinnedCheckpointTokenizer()
    {
        Assert.Equal(24, GoldenCases().Count());
        Assert.Equal("6c8aaa9a542084f2457eab775d4eeb51f92a70c0fd9de28d5edb0ddec3c08d30",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Fixtures, "tokenizer.json")))));
    }

    [Theory]
    [InlineData("laya-agent-config.json", 512, 192, 1.6369030475616455)]
    [InlineData("typed-decisions-agent-config.json", 1024, 256, 1.0148024559020996)]
    public async Task PublishedCheckpointConfigurationsRetainTheirOwnLimitsAndFallbacks(string file, int maximum, int head, double fallback)
    {
        LayaAgentConfiguration configuration = await LayaAgentConfiguration.LoadAsync(
            new(new TensorDirectFilePersistStore(AppContext.BaseDirectory), "Fixtures", file), CancellationToken.None);
        Assert.Equal(maximum, configuration.MaximumLength);
        Assert.Equal(head, configuration.MaximumHeadLength);
        Assert.Equal(fallback, configuration.Temperature(0, 1));
        Assert.Equal(0.10058280825614929, configuration.Temperature(0, 11));
    }

    [Theory]
    [InlineData("type", "Whitespace")]
    [InlineData("add_prefix_space", "true")]
    [InlineData("trim_offsets", "false")]
    [InlineData("use_regex", "false")]
    [InlineData("missing", "")]
    public async Task UnsupportedPreTokenizerFailsExplicitly(string field, string value)
    {
        JsonNode root = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Fixtures, "tokenizer.json")))!;
        if (field == "missing") { root.AsObject().Remove("pre_tokenizer"); }
        else { root["pre_tokenizer"]![field] = field == "type" ? JsonValue.Create(value) : JsonValue.Create(bool.Parse(value)); }
        string directory = Path.Combine(Path.GetTempPath(), $"laya-tokenizer-{Guid.NewGuid():N}");
        var store = new TensorDirectFilePersistStore(directory);
        try
        {
            using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()));
            await store.SaveAsync("fixture", "tokenizer.json", content);
            await Assert.ThrowsAsync<InvalidDataException>(() => LayaTokenizer.CreateAsync(new(store, "fixture", "tokenizer.json"), CancellationToken.None));
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(0, 2, 0.2)]
    [InlineData(0, 3, 0.3)]
    [InlineData(0, 5, 0.3)]
    [InlineData(0, 6, 0.4)]
    [InlineData(0, 10, 0.4)]
    [InlineData(0, 11, 0.1)]
    [InlineData(1, 2, 3)]
    [InlineData(1, 3, 0.6)]
    [InlineData(1, 5, 0.6)]
    [InlineData(1, 6, 3)]
    [InlineData(2, 2, 1.006)]
    public void CalibrationUsesPackBucketsWithoutClampingAndFallsBackByQuestionType(int type, int count, double expected)
    {
        LayaAgentConfiguration configuration = LayaAgentConfiguration.Parse(JsonElement.Parse("""
            {"max_len":512,"head_max_len":192,"temperature":[2,3,4],"temperature_by_options":{
              "choice:2":0.2,"choice:3-5":0.3,"choice:6-10":0.4,"choice:11+":0.1,"score:3-5":0.6,"noul:2":1.006}}
            """));
        Assert.Equal(expected, configuration.Temperature(type, count));
        Assert.Equal(512, configuration.MaximumLength);
        Assert.Equal(192, configuration.MaximumHeadLength);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"max_len\":512,\"head_max_len\":192,\"temperature\":[1,2,0],\"temperature_by_options\":{}}")]
    [InlineData("{\"max_len\":512,\"head_max_len\":192,\"temperature\":[1,2],\"temperature_by_options\":{}}")]
    [InlineData("{\"max_len\":128,\"head_max_len\":192,\"temperature\":[1,2,3],\"temperature_by_options\":{}}")]
    [InlineData("{\"max_len\":512,\"head_max_len\":192,\"temperature\":[1,2,3],\"temperature_by_options\":{\"noul:2\":-1}}")]
    public void InvalidAgentConfigurationNeverUsesUpstreamDefaults(string json) =>
        Assert.Throws<InvalidDataException>(() => LayaAgentConfiguration.Parse(JsonElement.Parse(json)));

    [Fact]
    public void AbsentOptionBucketUsesThePackFallback() => Assert.Equal(4,
        LayaAgentConfiguration.Parse(JsonElement.Parse("""
            {"max_len":512,"head_max_len":192,"temperature":[2,3,4],"temperature_by_options":{}}
            """)).Temperature(2, 2));
}
