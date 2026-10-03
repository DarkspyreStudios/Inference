using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Unregistered text/float protocol adapter. The supplied connection and HTTP client remain borrowed.</summary>
internal sealed class XaiEmbeddingClient(HttpClient http) : ILlmEmbeddingClient
{
    public bool Supports(LlmTransport transport) => transport == LlmTransport.XaiResponses;

    public void ValidateRequest(LlmConnection connection, LlmEmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        XaiRequestHeaders.ValidateEndpoint(connection);
        if ((connection.Xai?.ServiceTier ?? XaiServiceTier.Default) != XaiServiceTier.Default)
            throw new NotSupportedException("This embedding adapter does not implement a service-tier control.");
        ProviderHttp.ValidateAdditionalHeaders(connection.AdditionalHeaders);
        _ = XaiAuthorization.Require(connection);
        if (string.IsNullOrWhiteSpace(request.ModelId))
            throw new ArgumentException("An embedding model is required.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.Inputs);
        if (request.Inputs.Count is < 1 or > 128 || request.Inputs.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Supply between one and 128 non-empty embedding inputs.", nameof(request));
        if (request.Dimensions is <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Embedding dimensions must be positive.");
        ValidateOptions(connection.ProviderOptions);
        ValidateOptions(request.ProviderOptions);
        ValidateModel(connection.Catalog, request.ModelId);
    }

    [SuppressMessage("Design", "CA1031", Justification = "The HTTP boundary preserves independent failures and actual refused cleanup owners without exporting untrusted diagnostics.")]
    public async Task<LlmEmbeddingResult> EmbedAsync(LlmConnection connection, LlmEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var consumption = SdkHttpConsumption.Enter(http, cancellationToken);
        void Fence() { consumption.Token.ThrowIfCancellationRequested(); consumption.Fence(); }
        Fence();
        connection = EmbeddingRequestData.Snapshot(connection);
        request = EmbeddingRequestData.Snapshot(request);
        Fence();
        ValidateRequest(connection, request);
        Fence();
        IReadOnlyList<LlmModel>? models = connection.Catalog;
        HttpRequestMessage? message = null;
        HttpResponseMessage? response = null;
        LlmEmbeddingResult? result = null;
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        string[] secrets = connection.AdditionalHeaders.Values.ToArray();
        var failures = new List<Exception>();
        try
        {
            Fence();
            if (models is null)
            {
                IReadOnlyList<JsonElement> generic = await XaiCatalog.ReadAsync(http, connection,
                    ModelCatalogPart.Generic, Fence, consumption.Token).ConfigureAwait(false);
                Fence();
                IReadOnlyList<JsonElement> embedding = await XaiCatalog.ReadAsync(http, connection,
                    ModelCatalogPart.Embedding, Fence, consumption.Token).ConfigureAwait(false);
                Fence();
                models = XaiCatalog.Project(generic, embedding);
                ValidateModel(models, request.ModelId);
            }
            JsonObject payload = BuildRequest(connection, request);
            Fence();
            message = new(HttpMethod.Post, ProviderHttp.BuildUri(connection, "embeddings"));
            XaiRequestHeaders.Apply(message, connection, false);
            message.Content = JsonContent.Create(payload);
            secrets = message.Headers.Where(header => header.Key == "Authorization" || connection.AdditionalHeaders.ContainsKey(header.Key))
                .SelectMany(header => header.Value).ToArray();
            Fence();
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, consumption.Token).ConfigureAwait(false);
            Fence();
            await XaiErrorClassifier.ThrowIfUnsuccessfulEmbeddingAsync(response, consumption.Token).ConfigureAwait(false);
            Fence();
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, consumption.Token).ConfigureAwait(false);
            Fence();
            result = Parse(body, request, models, response, credential, secrets, Fence);
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
        if (failures.Count > 1) throw new AggregateException("Grok embedding operation and cleanup failed.", failures);
        Fence();
        return consumption.Publish(result!);
    }

    private static JsonObject BuildRequest(LlmConnection connection, LlmEmbeddingRequest request)
    {
        var payload = new JsonObject
        {
            ["model"] = request.ModelId,
            ["input"] = new JsonArray(request.Inputs.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["encoding_format"] = "float"
        };
        if (request.Dimensions is { } dimensions) payload["dimensions"] = dimensions;
        foreach ((string key, JsonElement value) in connection.ProviderOptions)
            payload[key] = JsonNode.Parse(value.GetRawText());
        foreach ((string key, JsonElement value) in request.ProviderOptions)
            payload[key] = JsonNode.Parse(value.GetRawText());
        return payload;
    }

    private static void ValidateOptions(IReadOnlyDictionary<string, JsonElement> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach ((string key, JsonElement value) in options)
        {
            if (key is "model" or "input" or "dimensions" or "service_tier")
                throw new InvalidOperationException($"Embedding provider option '{key}' is reserved.");
            if (key == "encoding_format" && (value.ValueKind != JsonValueKind.String || value.GetString() != "float"))
                throw new NotSupportedException("This embedding contract returns float vectors only.");
            if (key == "preview" && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
                || key == "user" && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new ArgumentException("An embedding provider control has an invalid type.", nameof(options));
        }
    }

    private static void ValidateModel(IReadOnlyList<LlmModel>? models, string modelId)
    {
        LlmModel? model = XaiCatalog.FindModel(models, modelId);
        if (model?.CatalogEntry is { } entry && entry.TryGetProperty("input_modalities", out JsonElement modalities)
            && modalities.ValueKind == JsonValueKind.Array && modalities.GetArrayLength() > 0
            && modalities.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String)
            && !modalities.EnumerateArray().Any(value => value.GetString() == "text"))
            throw new NotSupportedException("The observed embedding model does not accept text input.");
    }

    private static LlmEmbeddingResult Parse(string body, LlmEmbeddingRequest request, IReadOnlyList<LlmModel>? models,
        HttpResponseMessage response, LlmCredential? credential, string[] secrets, Action fence)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || RequiredString(root, "object") != "list"
                || !root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                throw new JsonException();
            string actualModel = RequiredString(root, "model");
            if (SecretRedactor.ContainsProtectedValue(actualModel, credential, secrets))
                throw new ProtocolFailure(response.StatusCode, ProviderHttp.ReadRequestId(response), unpublishableMetadata: true);
            LlmModel? requested = XaiCatalog.FindModel(models, request.ModelId);
            LlmModel? actual = XaiCatalog.FindModel(models, actualModel);
            if (requested is not null && actual is not null && requested.Id != actual.Id) throw new JsonException();
            var vectors = new float[request.Inputs.Count][];
            var observed = new bool[request.Inputs.Count];
            int? dimensions = null;
            var ownedData = new JsonArray();
            foreach (JsonElement item in data.EnumerateArray())
            {
                fence();
                if (item.ValueKind != JsonValueKind.Object || RequiredString(item, "object") != "embedding"
                    || !item.TryGetProperty("index", out JsonElement indexValue) || !indexValue.TryGetInt32(out int index)
                    || index < 0 || index >= vectors.Length || observed[index]
                    || !item.TryGetProperty("embedding", out JsonElement embedding) || embedding.ValueKind != JsonValueKind.Array)
                    throw new JsonException();
                int length = embedding.GetArrayLength();
                if (length == 0 || dimensions is { } previous && length != previous
                    || request.Dimensions is { } expected && length != expected) throw new JsonException();
                dimensions = length;
                var vector = new float[length];
                int offset = 0;
                var ownedVector = new JsonArray();
                foreach (JsonElement value in embedding.EnumerateArray())
                {
                    fence();
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float number) || !float.IsFinite(number))
                        throw new JsonException();
                    vector[offset++] = number;
                    ownedVector.Add(number);
                }
                vectors[index] = vector;
                observed[index] = true;
                ownedData.Add(new JsonObject { ["object"] = "embedding", ["index"] = index, ["embedding"] = ownedVector });
            }
            if (observed.Any(value => !value)) throw new JsonException();
            LlmEmbeddingUsage? usage = null;
            JsonNode? ownedUsage = null;
            if (root.TryGetProperty("usage", out JsonElement usageValue) && usageValue.ValueKind != JsonValueKind.Null)
            {
                if (usageValue.ValueKind != JsonValueKind.Object) throw new JsonException();
                int input = RequiredCount(usageValue, "prompt_tokens");
                int total = RequiredCount(usageValue, "total_tokens");
                usage = new(input, total);
                ownedUsage = new JsonObject { ["prompt_tokens"] = input, ["total_tokens"] = total };
            }
            var owned = new JsonObject { ["object"] = "list", ["model"] = actualModel, ["data"] = ownedData };
            if (root.TryGetProperty("usage", out _)) owned["usage"] = ownedUsage;
            fence();
            return new LlmEmbeddingResult
            {
                Vectors = Array.AsReadOnly(vectors),
                Usage = usage,
                Diagnostics = new((int)response.StatusCode, ProviderHttp.ReadRequestId(response), owned.ToJsonString())
            };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new ProtocolFailure(response.StatusCode, ProviderHttp.ReadRequestId(response), unpublishableMetadata: false);
        }
    }

    private static string RequiredString(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
        ? value.GetString()! : throw new JsonException();

    private static int RequiredCount(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int count) && count >= 0
        ? count : throw new JsonException();

    private static Exception SafeFailure(Exception error, HttpResponseMessage? response,
        LlmCredential? credential, string[] secrets)
    {
        if (error is InferenceResourceOperationException or InferenceResourceOperationCanceledException) return error;
        if (error is ProtocolFailure protocol)
            return new LlmProviderException(protocol.Message, LlmTransport.XaiResponses, protocol.StatusCode,
                requestId: protocol.RequestId is null ? null : SecretRedactor.Sanitize(protocol.RequestId, credential, secrets),
                innerException: new FailureEvidence(protocol));
        if (error is AggregateException aggregate)
            return new AggregateException("Grok embedding operation failed.", aggregate.InnerExceptions.Select(item => SafeFailure(item, response, credential, secrets)));
        if (error is OperationCanceledException canceled)
            return new OperationCanceledException("Grok embedding operation was interrupted.", new FailureEvidence(canceled), canceled.CancellationToken);
        if (error is InferenceException inference && inference.InnerException is LlmProviderException provider)
            return new InferenceException(inference.Code, "Grok embedding authorization or access failed.", SafeProviderFailure(provider, inference, credential, secrets));
        if (error is LlmProviderException failure) return SafeProviderFailure(failure, failure, credential, secrets);
        if (error is NotSupportedException)
            return new NotSupportedException("The observed embedding model does not support this request.", new FailureEvidence(error));
        return new LlmProviderException("Grok embedding transport failed.", LlmTransport.XaiResponses,
            response?.StatusCode, requestId: response is null ? null : ProviderHttp.ReadRequestId(response),
            innerException: new FailureEvidence(error));
    }

    private static LlmProviderException SafeProviderFailure(LlmProviderException error, Exception original,
        LlmCredential? credential, string[] secrets) => new(
        "Grok embedding request failed.", LlmTransport.XaiResponses, error.StatusCode,
        error.ProviderCode is "xai_authentication_required" or "xai_entitlement_required" ? error.ProviderCode : null,
        error.RequestId is null ? null : SecretRedactor.Sanitize(error.RequestId, credential, secrets), error.RetryAfter,
        innerException: new FailureEvidence(original));

    private static Exception CleanupFailure(Exception original, object owner)
    {
        var evidence = new FailureEvidence(original);
        Exception safe = original is OperationCanceledException canceled
            ? new OperationCanceledException("Grok embedding HTTP cleanup was interrupted.", evidence, canceled.CancellationToken) : evidence;
        return LlmResourcePreparation.AsCleanupFailure(safe, refusedOwner: owner);
    }

    // Actual evidence stays private; its untrusted message and inner graph are not diagnostic output.
    private sealed class FailureEvidence(Exception original) : Exception("Grok embedding HTTP boundary reported a failure.")
    {
        internal Exception Original { get; } = original;
    }

    private sealed class ProtocolFailure(System.Net.HttpStatusCode statusCode, string? requestId, bool unpublishableMetadata)
        : Exception(unpublishableMetadata ? "Grok embedding response metadata cannot be published safely."
            : "Grok returned invalid embedding protocol data.")
    {
        internal System.Net.HttpStatusCode StatusCode { get; } = statusCode;
        internal string? RequestId { get; } = requestId;
    }
}
