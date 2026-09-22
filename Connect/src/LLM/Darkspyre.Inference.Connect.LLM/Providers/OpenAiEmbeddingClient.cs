using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed class OpenAiEmbeddingClient(HttpClient http) : ILlmEmbeddingClient
{
    private static readonly string[] ReservedFields = ["model", "input"];

    public bool Supports(LlmTransport transport) => transport is
        LlmTransport.OpenAiResponses or LlmTransport.OpenAiCompatibleChat;

    public async Task<LlmEmbeddingResult> EmbedAsync(
        LlmConnection connection,
        LlmEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["model"] = request.ModelId,
            ["input"] = JsonSerializer.SerializeToNode(request.Inputs)
        };
        ApplyOptions(payload, connection.ProviderOptions);
        ApplyOptions(payload, request.ProviderOptions);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            ProviderHttp.BuildUri(connection, "embeddings"))
        {
            Content = JsonContent.Create(payload)
        };
        ProviderHttp.ApplyHeaders(httpRequest, connection);
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        using HttpResponseMessage response = await http.SendAsync(httpRequest, cancellationToken);
        await ProviderHttp.ThrowIfUnsuccessfulAsync(
            response,
            connection.Transport,
            credential,
            "Embedding request",
            cancellationToken);
        string raw = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Embedding response did not contain a data array.");
            }

            List<(int Index, float[] Vector)> indexed = [];
            foreach (JsonElement item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("index", out JsonElement indexElement)
                    || !indexElement.TryGetInt32(out int index)
                    || !item.TryGetProperty("embedding", out JsonElement embedding)
                    || embedding.ValueKind != JsonValueKind.Array)
                {
                    throw new JsonException("Embedding response contained an invalid item.");
                }

                float[] vector = embedding.EnumerateArray().Select(value => value.GetSingle()).ToArray();
                if (vector.Length == 0 || vector.Any(value => !float.IsFinite(value)))
                {
                    throw new JsonException("Embedding vectors must contain finite numeric values.");
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

            return new LlmEmbeddingResult
            {
                Vectors = vectors,
                Usage = new LlmEmbeddingUsage(inputTokens, totalTokens),
                Diagnostics = new LlmProviderDiagnostics(
                    (int)response.StatusCode,
                    ProviderHttp.ReadRequestId(response),
                    SecretRedactor.Sanitize(raw, credential))
            };
        }
        catch (JsonException exception)
        {
            throw new LlmProviderException(
                $"Embedding response was invalid: {exception.Message}",
                connection.Transport,
                response.StatusCode,
                "invalid_embedding_response",
                ProviderHttp.ReadRequestId(response),
                null,
                SecretRedactor.Sanitize(raw, credential),
                exception);
        }
    }

    private static void ApplyOptions(JsonObject payload, IReadOnlyDictionary<string, JsonElement> options)
    {
        foreach ((string key, JsonElement value) in options)
        {
            if (ReservedFields.Contains(key, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Embedding provider option '{key}' is reserved.");
            }

            payload[key] = JsonNode.Parse(value.GetRawText());
        }
    }

    private static int? ReadInt(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) && property.TryGetInt32(out int result)
            ? result
            : null;
}
