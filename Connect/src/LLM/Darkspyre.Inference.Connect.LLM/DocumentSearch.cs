using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.LLM;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DocumentMinK), "min_k")]
[JsonDerivedType(typeof(DocumentMaxK), "max_k")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record DocumentGroupAggregate
{
    private protected DocumentGroupAggregate() { }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentMinK(int K, IReadOnlyList<string> Keys) : DocumentGroupAggregate
{
    [JsonRequired]
    public int K { get; init => field = DocumentSearchData.AggregateCount(value); }
        = DocumentSearchData.AggregateCount(K);
    [JsonRequired]
    public IReadOnlyList<string> Keys { get; init => field = DocumentSearchData.List(value); }
        = DocumentSearchData.List(Keys);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentMaxK(int K, IReadOnlyList<string> Keys) : DocumentGroupAggregate
{
    [JsonRequired]
    public int K { get; init => field = DocumentSearchData.AggregateCount(value); }
        = DocumentSearchData.AggregateCount(K);
    [JsonRequired]
    public IReadOnlyList<string> Keys { get; init => field = DocumentSearchData.List(value); }
        = DocumentSearchData.List(Keys);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentSearchGroupBy(IReadOnlyList<string> Keys,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentGroupAggregate? Aggregate = null)
{
    [JsonRequired]
    public IReadOnlyList<string> Keys { get; init => field = DocumentSearchData.GroupKeys(value); }
        = DocumentSearchData.GroupKeys(Keys);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DocumentRerankerModel), "reranker_model")]
[JsonDerivedType(typeof(DocumentRrfReranker), "rrf")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record DocumentHybridReranker
{
    private protected DocumentHybridReranker() { }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentRerankerModel(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Instructions = null)
    : DocumentHybridReranker;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentRrfReranker(float? EmbeddingWeight = null, float? TextWeight = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? K = null) : DocumentHybridReranker
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? EmbeddingWeight { get; init => field = DocumentSearchData.Weight(value); }
        = DocumentSearchData.Weight(EmbeddingWeight);
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? TextWeight { get; init => field = DocumentSearchData.Weight(value); }
        = DocumentSearchData.Weight(TextWeight);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DocumentHybridRetrieval), "hybrid")]
[JsonDerivedType(typeof(DocumentSemanticRetrieval), "semantic")]
[JsonDerivedType(typeof(DocumentKeywordRetrieval), "keyword")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record DocumentRetrievalMode
{
    private protected DocumentRetrievalMode() { }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentHybridRetrieval(int? SearchMultiplier = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentHybridReranker? Reranker = null)
    : DocumentRetrievalMode
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SearchMultiplier { get; init => field = DocumentSearchData.Multiplier(value); }
        = DocumentSearchData.Multiplier(SearchMultiplier);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentSemanticRetrieval(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentRerankerModel? Reranker = null)
    : DocumentRetrievalMode;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentKeywordRetrieval(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentRerankerModel? Reranker = null)
    : DocumentRetrievalMode;

public enum XaiDocumentRankingMetric { Unknown = 0, L2Distance = 1, CosineSimilarity = 2 }
public enum XaiDocumentRagPipeline { ChromaDb = 0, Elasticsearch = 1 }

/// <summary>Explicit xAI controls. Null values leave the provider's defaults unspecified.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record XaiDocumentSearchOptions(
    XaiDocumentRankingMetric? RankingMetric = null, XaiDocumentRagPipeline? RagPipeline = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XaiDocumentRankingMetric? RankingMetric { get; init => field = DocumentSearchData.Defined(value); }
        = DocumentSearchData.Defined(RankingMetric);
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XaiDocumentRagPipeline? RagPipeline { get; init => field = DocumentSearchData.Defined(value); }
        = DocumentSearchData.Defined(RagPipeline);
}

/// <summary>A search against existing provider collections. Filter is an opaque provider expression.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentSearchRequest(string Query, IReadOnlyList<string> CollectionIds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Limit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Filter = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentSearchGroupBy? GroupBy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Instructions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentRetrievalMode? RetrievalMode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] XaiDocumentSearchOptions? Xai = null)
{
    [JsonRequired]
    public string Query { get; init => field = value ?? throw new ArgumentNullException(nameof(Query)); }
        = Query ?? throw new ArgumentNullException(nameof(Query));
    [JsonRequired]
    public IReadOnlyList<string> CollectionIds { get; init => field = DocumentSearchData.List(value); }
        = DocumentSearchData.List(CollectionIds);
}

/// <summary>An observed provider snippet. Native file IDs are not portable workspace resource IDs.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentSearchMatch(string FileId, string ChunkId, string Content,
    double Score, IReadOnlyList<string> CollectionIds, IReadOnlyDictionary<string, string>? Fields = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PageNumber = null)
{
    [JsonRequired]
    public string FileId { get; init => field = value ?? throw new ArgumentNullException(nameof(FileId)); }
        = FileId ?? throw new ArgumentNullException(nameof(FileId));
    [JsonRequired]
    public string ChunkId { get; init => field = value ?? throw new ArgumentNullException(nameof(ChunkId)); }
        = ChunkId ?? throw new ArgumentNullException(nameof(ChunkId));
    [JsonRequired]
    public string Content { get; init => field = value ?? throw new ArgumentNullException(nameof(Content)); }
        = Content ?? throw new ArgumentNullException(nameof(Content));
    [JsonRequired]
    public double Score { get; init => field = DocumentSearchData.Score(value); }
        = DocumentSearchData.Score(Score);
    [JsonRequired]
    public IReadOnlyList<string> CollectionIds { get; init => field = DocumentSearchData.List(value); }
        = DocumentSearchData.List(CollectionIds);
    /// <summary>Null means unreported; an empty dictionary is an observed empty field set.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Fields { get; init => field = DocumentSearchData.Fields(value); }
        = DocumentSearchData.Fields(Fields);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentSearchResult(IReadOnlyList<DocumentSearchMatch> Matches,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LlmProviderDiagnostics? Diagnostics = null)
{
    [JsonRequired]
    public IReadOnlyList<DocumentSearchMatch> Matches { get; init => field = DocumentSearchData.List(value); }
        = DocumentSearchData.List(Matches);
}

/// <summary>Searches existing provider collections through an explicitly selected connection.</summary>
public interface IDocumentSearchClient
{
    Task<DocumentSearchResult> SearchAsync(string connectionId, DocumentSearchRequest request,
        CancellationToken cancellationToken = default);
}

internal static class DocumentSearchData
{
    internal static IReadOnlyList<T> List<T>(IReadOnlyList<T> values) where T : class
    {
        ArgumentNullException.ThrowIfNull(values);
        T[] copy = values.ToArray();
        if (copy.Any(value => value is null))
            throw new ArgumentException("Document search lists cannot contain null entries.", nameof(values));
        return Array.AsReadOnly(copy);
    }

    internal static IReadOnlyList<string> GroupKeys(IReadOnlyList<string> keys)
    {
        IReadOnlyList<string> copy = List(keys);
        if (copy.Count == 0) throw new ArgumentException("At least one grouping key is required.", nameof(keys));
        return copy;
    }

    internal static int AggregateCount(int count) => count >= 1 ? count
        : throw new ArgumentOutOfRangeException(nameof(count), "Aggregate K must be at least one.");

    internal static int? Multiplier(int? value) => value is null or (>= 1 and <= 100) ? value
        : throw new ArgumentOutOfRangeException(nameof(value), "Search multiplier must be between one and one hundred.");

    internal static float? Weight(float? value) => value is null || (float.IsFinite(value.Value) && value is >= 0 and <= 1) ? value
        : throw new ArgumentOutOfRangeException(nameof(value), "A reranker weight must be finite and between zero and one.");

    internal static double Score(double value) => double.IsFinite(value) ? value
        : throw new ArgumentOutOfRangeException(nameof(value), "A reported score must be finite.");

    internal static T? Defined<T>(T? value) where T : struct, Enum => value is null || Enum.IsDefined(value.Value) ? value
        : throw new ArgumentOutOfRangeException(nameof(value), "Unknown document search option.");

    internal static IReadOnlyDictionary<string, string>? Fields(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null) return null;
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            copy.Add(key, value);
        }
        return new ReadOnlyDictionary<string, string>(copy);
    }
}
