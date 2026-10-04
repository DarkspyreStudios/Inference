using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Stateless REST reader and observed-fact merger. LlmModelListCache owns shared production.</summary>
internal static class XaiCatalog
{
    [SuppressMessage("Design", "CA1031", Justification = "Owned request/response cleanup preserves independent operation and cleanup failures.")]
    internal static async Task<IReadOnlyList<JsonElement>> ReadAsync(HttpClient http, LlmConnection connection,
        ModelCatalogPart part, Action ownerFence, CancellationToken cancellationToken)
    {
        (string path, string array) = part switch
        {
            ModelCatalogPart.Generic => ("models", "data"),
            ModelCatalogPart.Chat => ("language-models", "models"),
            ModelCatalogPart.Image => ("image-generation-models", "models"),
            ModelCatalogPart.Video => ("video-generation-models", "models"),
            ModelCatalogPart.Embedding => ("embedding-models", "models"),
            _ => throw new ArgumentOutOfRangeException(nameof(part))
        };
        using var consumption = SdkHttpConsumption.Enter(http, cancellationToken);
        void Fence() { consumption.Token.ThrowIfCancellationRequested(); consumption.Fence(); ownerFence(); }
        HttpRequestMessage? request = null;
        HttpResponseMessage? response = null;
        IReadOnlyList<JsonElement>? result = null;
        var failures = new List<Exception>();
        try
        {
            Fence();
            request = new(HttpMethod.Get, ProviderHttp.BuildUri(connection, path));
            XaiRequestHeaders.Apply(request, connection, false);
            string[] appliedHeaderValues = request.Headers.SelectMany(header => header.Value).ToArray();
            Fence();
            response = await http.SendAsync(request, consumption.Token).ConfigureAwait(false);
            Fence();
            await XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, LlmCredential.Parse(connection.Credential), appliedHeaderValues, consumption.Token).ConfigureAwait(false);
            Fence();
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, consumption.Token).ConfigureAwait(false);
            Fence();
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty(array, out JsonElement rows) && rows.ValueKind == JsonValueKind.Array
                    && rows.EnumerateArray().All(row => row.ValueKind == JsonValueKind.Object))
                    result = Array.AsReadOnly(rows.EnumerateArray().Select(row => row.Clone()).ToArray());
            }
            catch (JsonException) { }
            if (result is null) throw new LlmProviderException("Grok model discovery returned an invalid catalog.",
                LlmTransport.XaiResponses, response.StatusCode, requestId: ProviderHttp.ReadRequestId(response),
                sanitizedResponseBody: SecretRedactor.Sanitize(body, LlmCredential.Parse(connection.Credential), appliedHeaderValues));
            Fence();
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            if (response is not null)
                try { response.Dispose(); }
                catch (Exception error) { failures.Add(CleanupFailure(error, response)); }
            if (request is not null)
                try { request.Dispose(); }
                catch (Exception error) { failures.Add(CleanupFailure(error, request)); }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
        Fence();
        return consumption.Publish(result!);
    }

    private static Exception CleanupFailure(Exception original, object refusedOwner)
    {
        var evidence = new CleanupEvidence(original);
        Exception safe = original is OperationCanceledException canceled
            ? new OperationCanceledException("Grok catalog cleanup was interrupted.", evidence, canceled.CancellationToken)
            : evidence;
        return LlmResourcePreparation.AsCleanupFailure(safe, refusedOwner: refusedOwner);
    }

    // Preserve the actual failure without exporting untrusted diagnostics through InnerException.
    private sealed class CleanupEvidence(Exception original) : Exception("Grok catalog owned HTTP cleanup failed.")
    {
        internal Exception Original { get; } = original;
    }

    internal static IReadOnlyList<LlmModel> Project(IReadOnlyList<JsonElement> generic, IReadOnlyList<JsonElement> specific)
    {
        var results = new List<LlmModel>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement row in specific)
        {
            string? id = String(row, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!ids.Add(id)) throw new LlmProviderException("Grok model discovery returned duplicate model identifiers.", LlmTransport.XaiResponses);
            JsonObject metadata = (JsonObject)JsonNode.Parse(row.GetRawText())!;
            JsonElement? match = generic.Where(candidate => String(candidate, "id") == id).Cast<JsonElement?>().FirstOrDefault();
            if (match is null)
            {
                JsonElement[] candidates = [.. generic.Where(candidate => Aliases(candidate).Contains(id, StringComparer.Ordinal)
                    || String(candidate, "id") is { } genericId && Aliases(row).Contains(genericId, StringComparer.Ordinal))];
                if (candidates.Length == 1) match = candidates[0];
            }
            if (match is { } observed)
            {
                JsonObject other = (JsonObject)JsonNode.Parse(observed.GetRawText())!;
                foreach ((string key, JsonNode? value) in other)
                    if (!metadata.ContainsKey(key)) metadata[key] = value?.DeepClone();
                if (metadata["capabilities"] is JsonObject capabilities && other["capabilities"] is JsonObject genericCapabilities)
                    foreach ((string key, JsonNode? value) in genericCapabilities)
                        if (!capabilities.ContainsKey(key)) capabilities[key] = value?.DeepClone();
                string[] aliases = [.. Aliases(row).Concat(Aliases(observed)).Distinct(StringComparer.Ordinal)];
                if (aliases.Length > 0) metadata["aliases"] = new JsonArray([.. aliases.Select(alias => JsonValue.Create(alias))]);
            }
            JsonElement entry = JsonElement.Parse(metadata.ToJsonString());
            results.Add(new(id, String(row, "name") ?? id, String(row, "owned_by"))
            {
                CatalogEntry = entry,
                MaxContextTokens = entry.TryGetProperty("context_length", out JsonElement length) && length.ValueKind == JsonValueKind.Number
                    && length.TryGetInt64(out long count) && count > 0 ? count : null
            });
        }
        return Array.AsReadOnly(results.OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal static LlmModel? FindModel(IReadOnlyList<LlmModel>? models, string? id)
    {
        if (models is null || id is null) return null;
        LlmModel? exact = models.FirstOrDefault(model => model.Id == id);
        if (exact is not null) return exact;
        LlmModel? found = null;
        foreach (LlmModel model in models)
        {
            if (model.CatalogEntry is not { } entry || !Aliases(entry).Contains(id, StringComparer.Ordinal)) continue;
            if (found is not null && found.Id != model.Id) return null;
            found = model;
        }
        return found;
    }

    private static string? String(JsonElement item, string name) => item.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IEnumerable<string> Aliases(JsonElement item) => item.TryGetProperty("aliases", out JsonElement aliases)
        && aliases.ValueKind == JsonValueKind.Array
        ? aliases.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!) : [];
}
