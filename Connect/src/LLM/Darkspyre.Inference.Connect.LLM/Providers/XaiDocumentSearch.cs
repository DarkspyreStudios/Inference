using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Stateless search protocol consuming facade-admitted snapshots. The fence and HTTP client remain borrowed.</summary>
internal static class XaiDocumentSearch
{
    [SuppressMessage("Design", "CA1031", Justification = "Owned HTTP cleanup preserves independent failures and refused owners without exporting untrusted diagnostics.")]
    internal static async Task<DocumentSearchResult> SearchAsync(HttpClient http, LlmConnection connection,
        DocumentSearchRequest request, Action ownerFence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(ownerFence);
        cancellationToken.ThrowIfCancellationRequested();
        using var consumption = SdkHttpConsumption.Enter(http, cancellationToken);
        void Fence() { consumption.Token.ThrowIfCancellationRequested(); consumption.Fence(); ownerFence(); }
        Fence();
        ValidateConnection(connection);
        Fence();

        HttpRequestMessage? message = null;
        HttpResponseMessage? response = null;
        DocumentSearchResult? result = null;
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        string[] secrets = connection.AdditionalHeaders.Values.ToArray();
        var failures = new List<Exception>();
        try
        {
            JsonObject payload = BuildRequest(request, Fence);
            Fence();
            message = new(HttpMethod.Post, ProviderHttp.BuildUri(connection, "documents/search"));
            XaiRequestHeaders.Apply(message, connection, false);
            message.Content = RequestJson.Content(payload);
            Fence();
            try
            {
                response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, consumption.Token).ConfigureAwait(false);
            }
            finally
            {
                secrets = connection.AdditionalHeaders.Values.Concat(message.Headers.SelectMany(header => header.Value)).ToArray();
            }
            Fence();
            await XaiErrorClassifier.ThrowIfUnsuccessfulDocumentSearchAsync(response, consumption.Token).ConfigureAwait(false);
            Fence();
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, consumption.Token).ConfigureAwait(false);
            Fence();
            result = Parse(body, response, credential, secrets, Fence);
            Fence();
        }
        catch (Exception error) { failures.Add(SafeFailure(error, response, credential, secrets)); }
        finally
        {
            if (response is not null)
                try { response.Dispose(); }
                catch (Exception error) { failures.Add(CleanupFailure(error, response)); }
            if (message is not null)
                try { message.Dispose(); }
                catch (Exception error) { failures.Add(CleanupFailure(error, message)); }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Count > 1) throw new AggregateException("Grok document-search operation and cleanup failed.", failures);
        Fence();
        return consumption.Publish(result!);
    }

    internal static void ValidateConnection(LlmConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        XaiRequestHeaders.ValidateEndpoint(connection);
        if ((connection.Xai?.ServiceTier ?? XaiServiceTier.Default) != XaiServiceTier.Default)
            throw new NotSupportedException("This document-search adapter does not implement a service-tier control.");
        ProviderHttp.ValidateAdditionalHeaders(connection.AdditionalHeaders);
        _ = XaiAuthorization.Require(connection);
    }

    internal static DocumentSearchRequest Snapshot(DocumentSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DocumentSearchGroupBy? group = request.GroupBy is { } grouping
            ? new(grouping.Keys, grouping.Aggregate switch
            {
                null => null,
                DocumentMinK min => new DocumentMinK(min.K, min.Keys),
                DocumentMaxK max => new DocumentMaxK(max.K, max.Keys),
                _ => throw new NotSupportedException("Unknown document grouping aggregate.")
            }) : null;
        DocumentRetrievalMode? retrieval = request.RetrievalMode switch
        {
            null => null,
            DocumentHybridRetrieval hybrid => new DocumentHybridRetrieval(hybrid.SearchMultiplier, hybrid.Reranker switch
            {
                null => null,
                DocumentRerankerModel model => new DocumentRerankerModel(model.Model, model.Instructions),
                DocumentRrfReranker rrf => new DocumentRrfReranker(rrf.EmbeddingWeight, rrf.TextWeight, rrf.K),
                _ => throw new NotSupportedException("Unknown hybrid document reranker.")
            }),
            DocumentSemanticRetrieval semantic => new DocumentSemanticRetrieval(CopyModel(semantic.Reranker)),
            DocumentKeywordRetrieval keyword => new DocumentKeywordRetrieval(CopyModel(keyword.Reranker)),
            _ => throw new NotSupportedException("Unknown document retrieval mode.")
        };
        return new(request.Query, request.CollectionIds, request.Limit, request.Filter, group, request.Instructions,
            retrieval, request.Xai is { } options ? new(options.RankingMetric, options.RagPipeline) : null);
    }

    private static DocumentRerankerModel? CopyModel(DocumentRerankerModel? model) =>
        model is null ? null : new(model.Model, model.Instructions);

    private static JsonObject BuildRequest(DocumentSearchRequest request, Action fence)
    {
        var source = new JsonObject { ["collection_ids"] = Strings(request.CollectionIds, fence) };
        if (request.Xai?.RagPipeline is { } pipeline) source["rag_pipeline"] = pipeline switch
        {
            XaiDocumentRagPipeline.ChromaDb => "chroma_db",
            XaiDocumentRagPipeline.Elasticsearch => "es",
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
        var body = new JsonObject { ["query"] = request.Query, ["source"] = source };
        if (request.Limit is { } limit) body["limit"] = limit;
        if (request.Filter is { } filter) body["filter"] = filter;
        if (request.Instructions is { } instructions) body["instructions"] = instructions;
        if (request.GroupBy is { } group)
        {
            var native = new JsonObject { ["keys"] = Strings(group.Keys, fence) };
            native["aggregate"] = group.Aggregate switch
            {
                null => null,
                DocumentMinK min => new JsonObject { ["min_k"] = Aggregate(min.K, min.Keys, fence) },
                DocumentMaxK max => new JsonObject { ["max_k"] = Aggregate(max.K, max.Keys, fence) },
                _ => throw new NotSupportedException("Unknown document grouping aggregate.")
            };
            if (group.Aggregate is null) native.Remove("aggregate");
            body["group_by"] = native;
        }
        if (request.RetrievalMode is { } retrieval) body["retrieval_mode"] = Retrieval(retrieval);
        if (request.Xai?.RankingMetric is { } ranking) body["ranking_metric"] = ranking switch
        {
            XaiDocumentRankingMetric.Unknown => "RANKING_METRIC_UNKNOWN",
            XaiDocumentRankingMetric.L2Distance => "RANKING_METRIC_L2_DISTANCE",
            XaiDocumentRankingMetric.CosineSimilarity => "RANKING_METRIC_COSINE_SIMILARITY",
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
        return body;
    }

    private static JsonArray Strings(IReadOnlyList<string> values, Action fence)
    {
        var result = new JsonArray();
        // Generic Add<T> needs default serializer metadata; primitive JsonNode conversion does not.
        foreach (string value in values) { fence(); result.Add((JsonNode?)value); }
        return result;
    }

    private static JsonObject Aggregate(int count, IReadOnlyList<string> keys, Action fence) =>
        new() { ["k"] = count, ["keys"] = Strings(keys, fence) };

    private static JsonObject Retrieval(DocumentRetrievalMode mode)
    {
        var native = new JsonObject();
        switch (mode)
        {
            case DocumentHybridRetrieval hybrid:
                native["type"] = "hybrid";
                if (hybrid.SearchMultiplier is { } multiplier) native["search_multiplier"] = multiplier;
                if (hybrid.Reranker is DocumentRerankerModel model) native["reranker"] = ModelReranker(model, tagged: true);
                else if (hybrid.Reranker is DocumentRrfReranker rrf)
                {
                    var reranker = new JsonObject { ["type"] = "rrf" };
                    if (rrf.EmbeddingWeight is { } embedding) reranker["embedding_weight"] = embedding;
                    if (rrf.TextWeight is { } text) reranker["text_weight"] = text;
                    if (rrf.K is { } count) reranker["k"] = count;
                    native["reranker"] = reranker;
                }
                else if (hybrid.Reranker is not null) throw new NotSupportedException("Unknown hybrid document reranker.");
                break;
            case DocumentSemanticRetrieval semantic:
                native["type"] = "semantic";
                if (semantic.Reranker is { } semanticModel) native["reranker"] = ModelReranker(semanticModel, tagged: false);
                break;
            case DocumentKeywordRetrieval keyword:
                native["type"] = "keyword";
                if (keyword.Reranker is { } keywordModel) native["reranker"] = ModelReranker(keywordModel, tagged: false);
                break;
            default: throw new NotSupportedException("Unknown document retrieval mode.");
        }
        return native;
    }

    private static JsonObject ModelReranker(DocumentRerankerModel model, bool tagged)
    {
        var result = new JsonObject();
        if (tagged) result["type"] = "reranker_model";
        if (model.Model is { } id) result["model"] = id;
        if (model.Instructions is { } instructions) result["instructions"] = instructions;
        return result;
    }

    private static DocumentSearchResult Parse(string body, HttpResponseMessage response,
        LlmCredential? credential, string[] secrets, Action fence)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("matches", out JsonElement rows)
                || rows.ValueKind != JsonValueKind.Array) throw new JsonException();
            var matches = new List<DocumentSearchMatch>();
            foreach (JsonElement row in rows.EnumerateArray())
            {
                fence();
                if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("score", out JsonElement scoreValue)
                    || scoreValue.ValueKind != JsonValueKind.Number || !scoreValue.TryGetDouble(out double score)
                    || !double.IsFinite(score)) throw new JsonException();
                string fileId = RequiredString(row, "file_id");
                string chunkId = RequiredString(row, "chunk_id");
                // Provider snippets and metadata are opaque data, not paths, markup or diagnostic output.
                string content = RequiredString(row, "chunk_content");
                if (!row.TryGetProperty("collection_ids", out JsonElement ids) || ids.ValueKind != JsonValueKind.Array)
                    throw new JsonException();
                var collectionIds = new List<string>();
                foreach (JsonElement id in ids.EnumerateArray())
                {
                    fence();
                    if (id.ValueKind != JsonValueKind.String) throw new JsonException();
                    collectionIds.Add(id.GetString()!);
                }
                Dictionary<string, string>? fields = null;
                if (row.TryGetProperty("fields", out JsonElement metadata))
                {
                    if (metadata.ValueKind != JsonValueKind.Object) throw new JsonException();
                    fields = new(StringComparer.Ordinal);
                    foreach (JsonProperty field in metadata.EnumerateObject())
                    {
                        fence();
                        if (field.Value.ValueKind != JsonValueKind.String || !fields.TryAdd(field.Name, field.Value.GetString()!))
                            throw new JsonException();
                    }
                }
                int? page = null;
                if (row.TryGetProperty("page_number", out JsonElement pageValue))
                {
                    if (pageValue.ValueKind != JsonValueKind.Number || !pageValue.TryGetInt32(out int actual)) throw new JsonException();
                    page = actual;
                }
                matches.Add(new(fileId, chunkId, content, score, collectionIds, fields, page));
            }
            fence();
            return new(matches, new((int)response.StatusCode, SafeRequestId(response, credential, secrets), null));
        }
        catch (JsonException error) { throw new ProtocolFailure(response.StatusCode, ProviderHttp.ReadRequestId(response), error); }
    }

    private static string RequiredString(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new JsonException();

    private static Exception SafeFailure(Exception error, HttpResponseMessage? response,
        LlmCredential? credential, string[] secrets)
    {
        if (error is InferenceResourceOperationException or InferenceResourceOperationCanceledException) return error;
        if (error is ProtocolFailure protocol)
            return new LlmProviderException(protocol.Message, LlmTransport.XaiResponses, protocol.StatusCode,
                requestId: protocol.RequestId is null ? null : SecretRedactor.Sanitize(protocol.RequestId, credential, secrets),
                innerException: new FailureEvidence(protocol));
        if (error is AggregateException aggregate)
            return new AggregateException("Grok document-search operation failed.",
                aggregate.InnerExceptions.Select(item => SafeFailure(item, response, credential, secrets)));
        if (error is OperationCanceledException canceled)
            return new OperationCanceledException("Grok document-search operation was interrupted.",
                new FailureEvidence(canceled), canceled.CancellationToken);
        if (error is InferenceException inference && inference.InnerException is LlmProviderException provider)
            return new InferenceException(inference.Code, "Grok document-search authorization or access failed.",
                SafeProviderFailure(provider, inference, credential, secrets));
        if (error is LlmProviderException failure) return SafeProviderFailure(failure, failure, credential, secrets);
        return new LlmProviderException("Grok document-search transport failed.", LlmTransport.XaiResponses,
            response?.StatusCode, requestId: response is null ? null : SafeRequestId(response, credential, secrets),
            innerException: new FailureEvidence(error));
    }

    private static string? SafeRequestId(HttpResponseMessage response, LlmCredential? credential, string[] secrets) =>
        ProviderHttp.ReadRequestId(response) is { } id ? SecretRedactor.Sanitize(id, credential, secrets) : null;

    private static LlmProviderException SafeProviderFailure(LlmProviderException error, Exception original,
        LlmCredential? credential, string[] secrets) => new(
        "Grok document-search request failed.", LlmTransport.XaiResponses, error.StatusCode,
        error.ProviderCode is { } code ? SecretRedactor.Sanitize(code, credential, secrets) : null,
        error.RequestId is null ? null : SecretRedactor.Sanitize(error.RequestId, credential, secrets), error.RetryAfter,
        innerException: new FailureEvidence(original), isContextOverflow: error.IsContextOverflow);

    private static Exception CleanupFailure(Exception original, object owner)
    {
        var evidence = new FailureEvidence(original);
        Exception safe = original is OperationCanceledException canceled
            ? new OperationCanceledException("Grok document-search HTTP cleanup was interrupted.", evidence, canceled.CancellationToken) : evidence;
        return LlmResourcePreparation.AsCleanupFailure(safe, refusedOwner: owner);
    }

    // Original evidence remains private; untrusted messages and inner graphs are not diagnostics.
    private sealed class FailureEvidence(Exception original) : Exception("Grok document-search HTTP boundary reported a failure.")
    {
        internal Exception Original { get; } = original;
    }

    private sealed class ProtocolFailure(HttpStatusCode statusCode, string? requestId, Exception original)
        : Exception("Grok returned invalid document-search protocol data.")
    {
        internal HttpStatusCode StatusCode { get; } = statusCode;
        internal string? RequestId { get; } = requestId;
        internal Exception Original { get; } = original;
    }
}
