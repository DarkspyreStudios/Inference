using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Embedding;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed class OpenAiEmbeddingClient(HttpClient http) : ILlmEmbeddingClient
{
    private static readonly string[] ReservedFields = ["model", "input", "dimensions"];

    public bool Supports(LlmTransport transport) => transport is
        LlmTransport.OpenAiResponses or LlmTransport.OpenAiCompatibleChat;

    public void ValidateRequest(LlmConnection connection, LlmEmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(connection);
        RuntimeInputAdmission.Validate(connection);
        ArgumentNullException.ThrowIfNull(request);
        if (!Supports(connection.Transport))
            throw new NotSupportedException("The configured transport does not support this embedding client.");
        if (string.IsNullOrWhiteSpace(request.ModelId))
            throw new ArgumentException("An embedding model is required.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.Inputs);
        if (request.Inputs.Count == 0 || request.Inputs.OfType<TextEmbeddingInput>().Any(input => string.IsNullOrWhiteSpace(input.Text)))
            throw new ArgumentException("At least one non-empty embedding input is required.", nameof(request));
        bool images = connection.Embedding?.AcceptsImages == true;
        if (request.Inputs.Any(input => input is not TextEmbeddingInput
            && !(images && input is MediaEmbeddingInput { Media.ContentType: { } type } && type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))))
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, images
                ? "This embedding connection accepts text and images with an image media type only."
                : "This embedding provider accepts text inputs only.");
        if (request.Dimensions is <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Embedding dimensions must be positive.");
        ValidateOptions(connection.ProviderOptions);
        ValidateOptions(request.ProviderOptions);
    }

    public async Task<LlmEmbeddingResult> EmbedAsync(
        LlmConnection connection,
        LlmEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        request = EmbeddingRequestData.Snapshot(request);
        connection = EmbeddingRequestData.Snapshot(connection);
        ValidateRequest(connection, request);
        cancellationToken.ThrowIfCancellationRequested();
        bool inputType = connection.Embedding?.SendsInputType == true;
        var payload = new JsonObject
        {
            ["model"] = request.ModelId,
            ["input"] = new JsonArray(request.Inputs.Select(value => InputItem(value, inputType && request.Kind == EmbeddingInputKind.Document)).ToArray())
        };
        if (inputType) payload["input_type"] = request.Kind == EmbeddingInputKind.Query ? "query" : "document";
        if (request.Dimensions is { } dimensions) payload["dimensions"] = dimensions;
        ApplyOptions(payload, connection.ProviderOptions);
        ApplyOptions(payload, request.ProviderOptions);
        using var consumption = SdkHttpConsumption.Enter(http, cancellationToken); cancellationToken = consumption.Token;
        LlmEmbeddingResult result;
        {
            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Post,
                ProviderHttp.BuildUri(connection, "embeddings"))
            {
                Content = RequestJson.Content(payload)
            };
            ProviderHttp.ApplyHeaders(httpRequest, connection);
            LlmCredential? credential = LlmCredential.Parse(connection.Credential);
            using HttpResponseMessage response = await http.SendAsync(httpRequest, cancellationToken);
            string[] secrets = httpRequest.Headers.SelectMany(header => header.Value).ToArray();
            await ProviderHttp.ThrowIfUnsuccessfulAsync(
                response,
                connection.Transport,
                credential,
                "Embedding request",
                secrets,
                OpenAiApiErrors.Reading,
                cancellationToken);
            string raw = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken);
            try
            {
                using JsonDocument document = JsonDocument.Parse(raw);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                {
                    throw new JsonException("Embedding response did not contain a data array.");
                }

                string? modelId = root.TryGetProperty("model", out JsonElement model) && model.ValueKind == JsonValueKind.String
                    ? model.GetString() : null;
                if (modelId is not null && SecretRedactor.ContainsProtectedValue(modelId, credential, secrets))
                    throw new JsonException("Embedding model metadata cannot be published safely.");
                // An endpoint that serves a known model pack names the identity of its vectors.
                string? fingerprint = root.TryGetProperty("fingerprint", out JsonElement reported) && reported.ValueKind == JsonValueKind.String
                    ? reported.GetString() : null;
                if (fingerprint is not null && SecretRedactor.ContainsProtectedValue(fingerprint, credential, secrets))
                    throw new JsonException("Embedding model metadata cannot be published safely.");

                List<(int Index, float[] Vector)> indexed = [];
                var indexes = new HashSet<int>();
                foreach (JsonElement item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("index", out JsonElement indexElement)
                        || indexElement.ValueKind != JsonValueKind.Number
                        || !indexElement.TryGetInt32(out int index)
                        || index < 0 || index >= request.Inputs.Count || !indexes.Add(index)
                        || !item.TryGetProperty("embedding", out JsonElement embedding)
                        || embedding.ValueKind != JsonValueKind.Array)
                    {
                        throw new JsonException("Embedding response contained an invalid item.");
                    }

                    var vector = new float[embedding.GetArrayLength()];
                    int offset = 0;
                    foreach (JsonElement value in embedding.EnumerateArray())
                    {
                        if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float number) || !float.IsFinite(number))
                            throw new JsonException("Embedding vectors must contain finite numeric values.");
                        vector[offset++] = number;
                    }
                    if (vector.Length == 0 || (request.Dimensions is { } expected && vector.Length != expected))
                    {
                        throw new JsonException("Embedding vector dimensions did not match the request.");
                    }

                    indexed.Add((index, vector));
                }

                List<float[]> vectors = indexed.OrderBy(value => value.Index).Select(value => value.Vector).ToList();
                if (vectors.Count != request.Inputs.Count || vectors.Select(value => value.Length).Distinct().Count() != 1)
                {
                    throw new JsonException("Embedding response count or dimensions did not match the request.");
                }

                int? inputTokens = null;
                int? totalTokens = null;
                if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    inputTokens = ReadInt(usage, "prompt_tokens") ?? ReadInt(usage, "input_tokens");
                    totalTokens = ReadInt(usage, "total_tokens");
                }

                result = new LlmEmbeddingResult
                {
                    ModelId = modelId,
                    ModelFingerprint = fingerprint,
                    Vectors = vectors,
                    Usage = new LlmEmbeddingUsage(inputTokens, totalTokens),
                    Diagnostics = new LlmProviderDiagnostics(
                        (int)response.StatusCode,
                        ProviderHttp.ReadRequestId(response, credential, secrets),
                        SecretRedactor.Sanitize(raw, credential, secrets))
                };
            }
            catch (JsonException exception)
            {
                throw new LlmProviderException(
                    $"Embedding response was invalid: {SecretRedactor.Sanitize(exception.Message, credential, secrets)}",
                    connection.Transport,
                    response.StatusCode,
                    "invalid_embedding_response",
                    ProviderHttp.ReadRequestId(response, credential, secrets),
                    null,
                    SecretRedactor.Sanitize(raw, credential, secrets),
                    SecretRedactor.Sanitize(exception));
            }
        }
        return consumption.Publish(result);
    }

    // A plain string, the standard form; a document's title, when sent, and an image travel as a content item.
    private static JsonNode InputItem(EmbeddingInput input, bool sendsTitle) => input switch
    {
        TextEmbeddingInput { Title: { } title } text when sendsTitle && !string.IsNullOrWhiteSpace(title) => new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text.Text }),
            ["title"] = title
        },
        TextEmbeddingInput text => JsonValue.Create(text.Text),
        MediaEmbeddingInput { Media: var media } => new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:{media.ContentType};base64,{Convert.ToBase64String(media.Data.Span)}" }
            })
        },
        _ => throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "This embedding input is not supported.")
    };

    private static void ApplyOptions(JsonObject payload, IReadOnlyDictionary<string, JsonElement> options)
    {
        foreach ((string key, JsonElement value) in options)
            payload[key] = JsonNode.Parse(value.GetRawText());
    }

    private static void ValidateOptions(IReadOnlyDictionary<string, JsonElement> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (string key in options.Keys)
        {
            if (ReservedFields.Contains(key, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Embedding provider option '{key}' is reserved.");
            }
        }
    }

    private static int? ReadInt(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out int result) && result >= 0
            ? result
            : null;
}
