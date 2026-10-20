using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Reads the subscription proxy's model list: GET models, whose data rows are the account's models.</summary>
internal static class XaiCatalog
{
    [SuppressMessage("Design", "CA1031", Justification = "Owned request/response cleanup preserves independent operation and cleanup failures.")]
    internal static async Task<IReadOnlyList<JsonElement>> ReadAsync(HttpClient http, LlmConnection connection,
        Action ownerFence, CancellationToken cancellationToken)
    {
        using var consumption = SdkHttpConsumption.Enter(http, cancellationToken);
        void Fence() { consumption.Token.ThrowIfCancellationRequested(); consumption.Fence(); ownerFence(); }
        HttpRequestMessage? request = null;
        HttpResponseMessage? response = null;
        IReadOnlyList<JsonElement>? result = null;
        var failures = new List<Exception>();
        try
        {
            Fence();
            request = new(HttpMethod.Get, ProviderHttp.BuildUri(connection, "models"));
            XaiRequestHeaders.Apply(request, connection, false);
            XaiRequestHeaders.ApplyAccount(request, connection);
            Fence();
            try
            {
                response = await http.SendAsync(request, consumption.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                string[] secrets = connection.AdditionalHeaders.Values.Concat(request.Headers.SelectMany(header => header.Value)).ToArray();
                throw XaiErrorClassifier.HttpSendFailure(error, LlmCredential.Parse(connection.Credential), secrets);
            }
            Fence();
            string[] appliedHeaderValues = connection.AdditionalHeaders.Values.Concat(request.Headers.SelectMany(header => header.Value)).ToArray();
            await XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, LlmCredential.Parse(connection.Credential), appliedHeaderValues, consumption.Token).ConfigureAwait(false);
            Fence();
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, consumption.Token).ConfigureAwait(false);
            Fence();
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("data", out JsonElement rows) && rows.ValueKind == JsonValueKind.Array
                    && rows.EnumerateArray().All(row => row.ValueKind == JsonValueKind.Object))
                    result = Array.AsReadOnly(rows.EnumerateArray().Select(row => row.Clone()).ToArray());
            }
            catch (JsonException) { }
            if (result is null) throw new LlmProviderException("Grok model discovery returned an invalid catalog.",
                LlmTransport.XaiResponses, response.StatusCode,
                requestId: ProviderHttp.ReadRequestId(response, LlmCredential.Parse(connection.Credential), appliedHeaderValues),
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

    /// <summary>The models of the list's rows, by display name. A row without an id is skipped.</summary>
    internal static IReadOnlyList<LlmModel> Project(IReadOnlyList<JsonElement> rows)
    {
        var results = new List<LlmModel>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement row in rows)
        {
            string? id = String(row, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (row.TryGetProperty("hidden", out JsonElement hidden) && hidden.ValueKind == JsonValueKind.True) continue;
            if (XaiFields.TryGet(row, "api_backend", "apiBackend", out JsonElement backend)
                && backend.ValueKind == JsonValueKind.String && backend.GetString() != "responses") continue;
            if (!ids.Add(id)) throw new LlmProviderException("Grok model discovery returned duplicate model identifiers.", LlmTransport.XaiResponses);
            results.Add(new(id, String(row, "name") ?? id, String(row, "owned_by"))
            {
                CatalogEntry = row,
                // The proxy publishes the default window as context_window.
                MaxContextTokens = row.TryGetProperty("context_window", out JsonElement length) && length.ValueKind == JsonValueKind.Number
                    && length.TryGetInt64(out long count) && count > 0 ? count : null
            });
        }
        return Array.AsReadOnly(results.OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal static LlmModel? FindModel(IReadOnlyList<LlmModel>? models, string? id) =>
        models is null || id is null ? null : models.FirstOrDefault(model => model.Id == id);

    private static string? String(JsonElement item, string name) => item.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
