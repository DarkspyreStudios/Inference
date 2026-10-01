using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class DocumentSearchContractTests
{
    private const string Match = """{"FileId":"file","ChunkId":"chunk","Content":"snippet","Score":-7.25,"CollectionIds":["second","first"],"Fields":{"payload":"{\"text\":1}"},"PageNumber":0}""";
    private const string Request = """{"Query":"query","CollectionIds":["second","first"],"Limit":0,"Filter":"opaque >>> expression","GroupBy":{"Keys":["section"],"Aggregate":{"type":"max_k","K":2,"Keys":[]}},"Instructions":"exact","RetrievalMode":{"type":"hybrid","SearchMultiplier":100,"Reranker":{"type":"rrf","K":-1,"EmbeddingWeight":1,"TextWeight":1}},"Xai":{"RankingMetric":0,"RagPipeline":1}}""";

    public static TheoryData<string, string> Roots => new()
    {
        { "DocumentGroupAggregate", """{"type":"min_k","K":1,"Keys":[]}""" },
        { "DocumentGroupAggregate", """{"type":"max_k","K":2,"Keys":["#score","custom"]}""" },
        { "DocumentMinK", """{"K":1,"Keys":[]}""" },
        { "DocumentMaxK", """{"K":2,"Keys":["custom","#score"]}""" },
        { "DocumentSearchGroupBy", """{"Keys":["section"]}""" },
        { "DocumentHybridReranker", """{"type":"reranker_model"}""" },
        { "DocumentHybridReranker", """{"type":"rrf","K":-1,"EmbeddingWeight":1,"TextWeight":1}""" },
        { "DocumentRerankerModel", """{"Model":"manual-alias","Instructions":"unchanged"}""" },
        { "DocumentRrfReranker", "{}" },
        { "DocumentRetrievalMode", """{"type":"hybrid"}""" },
        { "DocumentRetrievalMode", """{"type":"semantic","Reranker":{"Model":"manual-alias"}}""" },
        { "DocumentRetrievalMode", """{"type":"keyword","Reranker":{"Instructions":"exact"}}""" },
        { "DocumentHybridRetrieval", """{"SearchMultiplier":1,"Reranker":{"type":"rrf","TextWeight":0}}""" },
        { "DocumentSemanticRetrieval", "{}" },
        { "DocumentKeywordRetrieval", "{}" },
        { "XaiDocumentRankingMetric", "0" },
        { "XaiDocumentRankingMetric", "1" },
        { "XaiDocumentRankingMetric", "2" },
        { "XaiDocumentRagPipeline", "0" },
        { "XaiDocumentRagPipeline", "1" },
        { "XaiDocumentSearchOptions", "{}" },
        { "XaiDocumentSearchOptions", """{"RankingMetric":0,"RagPipeline":0}""" },
        { "DocumentSearchRequest", """{"Query":"query","CollectionIds":[]}""" },
        { "DocumentSearchRequest", Request },
        { "DocumentSearchMatch", Match },
        { "DocumentSearchMatch", """{"FileId":"file","ChunkId":"chunk","Content":"","Score":2.5,"CollectionIds":[]}""" },
        { "DocumentSearchMatch", """{"FileId":"file","ChunkId":"chunk","Content":"","Score":0,"CollectionIds":[],"Fields":{}}""" },
        { "DocumentSearchResult", """{"Matches":[]}""" },
        { "DocumentSearchResult", "{\"Matches\":[" + Match + "],\"Diagnostics\":{\"HttpStatusCode\":200,\"RequestId\":\"request\",\"RawFinalResponseJson\":null,\"ServiceTier\":null}}" }
    };

    [Theory]
    [MemberData(nameof(Roots))]
    public void EveryOwnedRootPreservesExactNestedFacts(string name, string json)
    {
        JsonTypeInfo metadata = Metadata(typeof(ConnectLlmJson), name);
        Assert.Equal("Darkspyre.Inference.Connect.LLM." + name, metadata.Type.FullName);
        Assert.Same(typeof(ConnectLlmJson).Assembly, metadata.Type.Assembly);
        RoundTrip(json, metadata);
    }

    [Fact]
    public void NullOptionsOmitDefaultsWithoutInventingObservedFieldsOrPageNumbers()
    {
        var request = new DocumentSearchRequest("query", ["collection"]);
        Assert.Equal("{\"Query\":\"query\",\"CollectionIds\":[\"collection\"]}", JsonSerializer.Serialize(request, ConnectLlmJson.DocumentSearchRequest));
        Assert.Equal("{}", JsonSerializer.Serialize(new XaiDocumentSearchOptions(), ConnectLlmJson.XaiDocumentSearchOptions));
        Assert.Equal("{}", JsonSerializer.Serialize(new DocumentRrfReranker(), ConnectLlmJson.DocumentRrfReranker));
        var absent = new DocumentSearchMatch("file", "chunk", "", 0, []);
        DocumentSearchMatch empty = absent with { Fields = new Dictionary<string, string>(), PageNumber = 0 };
        Assert.Null(JsonSerializer.Deserialize(JsonSerializer.Serialize(absent, ConnectLlmJson.DocumentSearchMatch), ConnectLlmJson.DocumentSearchMatch)!.Fields);
        DocumentSearchMatch actual = JsonSerializer.Deserialize(JsonSerializer.Serialize(empty, ConnectLlmJson.DocumentSearchMatch), ConnectLlmJson.DocumentSearchMatch)!;
        Assert.Empty(actual.Fields!);
        Assert.Equal(0, actual.PageNumber);
    }

    [Fact]
    public void CollectionsAndMetadataSnapshotAtConstructionAndWithAssignment()
    {
        var ids = new List<string> { "second", "first", "second" };
        var keys = new List<string> { "section", "#score" };
        var fields = new Dictionary<string, string> { ["Case"] = "{\"raw\":true}", ["case"] = "other" };
        var group = new DocumentSearchGroupBy(keys, new DocumentMinK(1, keys));
        var request = new DocumentSearchRequest("query", ids, GroupBy: group);
        var match = new DocumentSearchMatch("file", "chunk", "content", 42, ids, fields, 0);
        var matches = new List<DocumentSearchMatch> { match, match with { ChunkId = "next" } };
        var result = new DocumentSearchResult(matches);
        var changed = request with { CollectionIds = keys };
        ids.Clear(); keys.Clear(); fields.Clear(); matches.Clear();
        Assert.Equal(["second", "first", "second"], request.CollectionIds);
        Assert.Equal(["section", "#score"], changed.CollectionIds);
        Assert.Equal(["section", "#score"], group.Keys);
        Assert.Equal(["section", "#score"], Assert.IsType<DocumentMinK>(group.Aggregate).Keys);
        Assert.Equal(2, result.Matches.Count);
        Assert.Equal("{\"raw\":true}", match.Fields!["Case"]);
        Assert.Equal("other", match.Fields["case"]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)request.CollectionIds).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)match.Fields).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<DocumentSearchMatch>)result.Matches).Clear());
    }

    [Fact]
    public void PositionalNamesDeconstructionAndWithCopiesRetainTheApprovedApi()
    {
        var request = new DocumentSearchRequest(Query: "q", CollectionIds: [], Filter: "invalid locally but opaque");
        var (query, ids, limit, filter, group, instructions, retrieval, xai) = request;
        Assert.Equal("q", query);
        Assert.Empty(ids);
        Assert.Null(limit); Assert.Null(group); Assert.Null(instructions); Assert.Null(retrieval); Assert.Null(xai);
        Assert.Equal(request.Filter, filter);
        Assert.Equal(int.MinValue, (new DocumentRrfReranker() with { K = int.MinValue }).K);
        Assert.Equal(int.MinValue, (request with { Limit = int.MinValue }).Limit);
        Assert.Empty(new DocumentMinK(int.MaxValue, []).Keys);
        Assert.Equal(1, new DocumentRrfReranker(1, 1).EmbeddingWeight);
        Assert.Equal(-40, new DocumentSearchMatch("f", "c", "", -40, []).Score);
    }

    [Fact]
    public void PublishedBoundsAndRequiredDataCannotBeBypassedWithCopies()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentMinK(0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentMaxK(1, []) with { K = -1 });
        Assert.Throws<ArgumentException>(() => new DocumentSearchGroupBy([]));
        Assert.Throws<ArgumentException>(() => new DocumentSearchGroupBy(["key"]) with { Keys = [] });
        Assert.Throws<ArgumentException>(() => new DocumentSearchRequest("q", [null!]));
        Assert.Throws<ArgumentNullException>(() => new DocumentSearchRequest(null!, []));
        Assert.Throws<ArgumentNullException>(() => new DocumentSearchMatch("f", "c", "", 0, []) with { Content = null! });
        Assert.Throws<ArgumentException>(() => new DocumentSearchResult([null!]));
        Assert.Throws<ArgumentNullException>(() => new DocumentSearchMatch("f", "c", "", 0, [], new Dictionary<string, string> { ["k"] = null! }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XaiDocumentSearchOptions((XaiDocumentRankingMetric)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XaiDocumentSearchOptions() with { RagPipeline = (XaiDocumentRagPipeline)99 });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void OnlyThePublishedMultiplierRangeIsAdmitted(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentHybridRetrieval(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentHybridRetrieval() with { SearchMultiplier = value });
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonfiniteAndOutOfRangeWeightsRefuse(float value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentRrfReranker(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentRrfReranker() with { TextWeight = value });
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteScoresRefuse(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentSearchMatch("f", "c", "", value, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentSearchMatch("f", "c", "", 0, []) with { Score = value });
    }

    [Theory]
    [InlineData("DocumentSearchRequest", "{\"Query\":\"q\",\"CollectionIds\":[],\"RawOptions\":{}}")]
    [InlineData("DocumentGroupAggregate", "{\"type\":\"sum\",\"K\":1,\"Keys\":[]}")]
    [InlineData("DocumentRetrievalMode", "{\"type\":\"unknown\"}")]
    [InlineData("DocumentHybridReranker", "{\"type\":\"unknown\"}")]
    [InlineData("DocumentRetrievalMode", "{\"type\":\"semantic\",\"SearchMultiplier\":1}")]
    [InlineData("DocumentRetrievalMode", "{\"type\":\"keyword\",\"Reranker\":{\"type\":\"rrf\",\"K\":1}}")]
    [InlineData("DocumentSearchMatch", "{\"FileId\":\"f\",\"ChunkId\":\"c\",\"Content\":\"\",\"Score\":0,\"CollectionIds\":[],\"Fields\":{\"bad\":{}}}")]
    public void OwnedCodecsRefuseUnknownVariantsMembersAndMalformedMetadata(string name, string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, Metadata(typeof(ConnectLlmJson), name)));

    [Theory]
    [InlineData("DocumentMinK", "{\"K\":1,\"Keys\":[]}", "K")]
    [InlineData("DocumentMinK", "{\"K\":1,\"Keys\":[]}", "Keys")]
    [InlineData("DocumentSearchRequest", "{\"Query\":\"q\",\"CollectionIds\":[]}", "Query")]
    [InlineData("DocumentSearchRequest", "{\"Query\":\"q\",\"CollectionIds\":[]}", "CollectionIds")]
    [InlineData("DocumentSearchMatch", Match, "Score")]
    [InlineData("DocumentSearchResult", "{\"Matches\":[]}", "Matches")]
    public void MissingRequiredFactsNeverBecomeInventedDefaults(string name, string json, string member)
    {
        JsonObject missing = JsonNode.Parse(json)!.AsObject();
        Assert.True(missing.Remove(member));
        Exception? failure = Record.Exception(() => JsonSerializer.Deserialize(missing.ToJsonString(), Metadata(typeof(ConnectLlmJson), name)));
        Assert.True(failure is JsonException or ArgumentException, failure?.ToString() ?? "Missing fact was accepted.");
    }

    [Theory]
    [InlineData("XaiDocumentRankingMetric")]
    [InlineData("XaiDocumentRagPipeline")]
    public void StandaloneEnumRootsRefuseUndefinedValuesOnReadAndWrite(string name)
    {
        JsonTypeInfo metadata = Metadata(typeof(ConnectLlmJson), name);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("99", metadata));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(Enum.ToObject(metadata.Type, 99), metadata));
    }

    [Theory]
    [MemberData(nameof(Roots))]
    public void ActualForeignCommonAndConnectMetadataRetires(string name, string json)
    {
        WeakReference weak = ExerciseForeign(name, json);
        for (int attempt = 0; weak.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10);
        }
        Assert.False(weak.IsAlive, "Actual document-search metadata retained its foreign generation.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign(string name, string json)
    {
        var generation = new DocumentGeneration(Path.GetDirectoryName(typeof(ConnectLlmJson).Assembly.Location)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly connect = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            foreach ((Assembly original, Assembly actual) in new[] { (typeof(ConnectLlmJson).Assembly, connect), (typeof(InferenceJson).Assembly, common) })
            {
                Assert.NotSame(original, actual);
                Assert.Equal(original.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
            }
            JsonTypeInfo metadata = Metadata(connect.GetType(typeof(ConnectLlmJson).FullName!, true)!, name);
            Assert.Same(connect, metadata.Type.Assembly);
            Assert.Same(connect, metadata.Options.TypeInfoResolver!.GetType().Assembly);
            Type access = common.GetType("Darkspyre.Inference.Common.Resources.IInferenceResourceAccess", true)!;
            Assert.Same(common, Assert.Single(metadata.Options.Converters, converter => converter.CanConvert(access)).GetType().Assembly);
            object value = RoundTrip(json, metadata);
            Assert.Same(connect, value.GetType().Assembly);
            foreach (string member in new[] { "GroupBy", "RetrievalMode", "Xai", "Reranker", "Aggregate" })
            {
                object? nested = value.GetType().GetProperty(member)?.GetValue(value);
                if (nested is not null) Assert.Same(connect, nested.GetType().Assembly);
            }
            Assert.DoesNotContain(metadata.Options.Converters, converter => converter.CanConvert(typeof(Stream)));
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static object RoundTrip(string json, JsonTypeInfo metadata)
    {
        object value = JsonSerializer.Deserialize(json, metadata)!;
        using JsonDocument expected = JsonDocument.Parse(json);
        using JsonDocument actual = JsonDocument.Parse(JsonSerializer.Serialize(value, metadata));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement), actual.RootElement.GetRawText());
        return value;
    }

    private static JsonTypeInfo Metadata(Type owner, string name)
        => Assert.IsAssignableFrom<JsonTypeInfo>(owner.GetProperty(name)!.GetValue(null));

    private sealed class DocumentGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
