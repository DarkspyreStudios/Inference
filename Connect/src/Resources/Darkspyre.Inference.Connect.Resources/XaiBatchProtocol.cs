using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.Inference.Connect.Resources.Internal;

namespace Darkspyre.Inference.Connect.Resources;

/// <summary>One admitted batch operation over the existing selected HTTP owner; no retry or polling.</summary>
internal sealed class XaiBatchProtocol(HttpClient http, LlmConnection connection, string connectionId,
    LlmResourcePageRequest? page, XaiResourceHttp.Observation observation, Action fence)
{
    private readonly InferenceResourceScope _scope = new(ProviderHttp.BuildUri(connection, "batches").AbsoluteUri, connectionId);
    private readonly XaiResourceHttp _exchange = new(http, connection, observation, fence);

    internal static void ValidateBatch(LlmBatch batch)
    {
        Identity(batch.Id);
        BatchData.Required(batch.Name);
        BatchData.Scope(batch.Scope);
        BatchData.Required(batch.Counts);
    }

    internal static void ValidateConnection(LlmConnection connection, LlmBatch? batch)
    {
        XaiFileRequestData.Connection(connection);
        if (batch is not null && !string.Equals(batch.Scope.Owner,
                ProviderHttp.BuildUri(connection, "batches").AbsoluteUri, StringComparison.Ordinal))
            throw new ArgumentException("The batch receipt contradicts the selected batches route.", nameof(batch));
        // Identity describes the original selection, not a principal. The selected token's GET authorizes imports.
    }

    internal Task<LlmBatch> CreateAsync(string name, CancellationToken token) =>
        ExchangeAsync(HttpMethod.Post, "batches", new JsonObject { ["name"] = name }, true,
            root => ReadBatch(root, null), token);

    internal Task<LlmBatch> GetAsync(string id, CancellationToken token) =>
        ExchangeAsync(HttpMethod.Get, Path(id), null, false, root => ReadBatch(root, id), token);

    internal async Task AuthorizeAsync(string id, CancellationToken token)
    {
        LlmBatch actual = await GetAsync(id, token).ConfigureAwait(false);
        observation.SelectedBatch = actual;
        fence();
    }

    internal Task<LlmBatch> CancelAsync(string id, CancellationToken token) =>
        ExchangeAsync(HttpMethod.Post, Path(id) + ":cancel", null, true, root => ReadBatch(root, id), token);

    internal Task<LlmBatchPage> ListAsync(CancellationToken token) =>
        ExchangeAsync(HttpMethod.Get, "batches" + Query(), null, false, root =>
        {
            var items = new List<LlmBatch>();
            foreach (JsonElement item in Array(root, "batches").EnumerateArray())
            {
                fence();
                items.Add(ReadBatch(item, null));
            }
            return new LlmBatchPage(items, OptionalString(root, "pagination_token"));
        }, token);

    internal Task<LlmBatchRequestPage> ListRequestsAsync(string id, CancellationToken token) =>
        ExchangeAsync(HttpMethod.Get, Path(id) + "/requests" + Query(), null, false, root =>
        {
            var items = new List<LlmBatchRequestMetadata>();
            foreach (JsonElement item in Array(root, "batch_request_metadata").EnumerateArray())
            {
                fence();
                string actualId = RequiredString(item, "batch_request_id");
                if (string.IsNullOrWhiteSpace(actualId)) throw new JsonException();
                items.Add(new(actualId, OptionalString(item, "endpoint"), OptionalString(item, "model"),
                    OptionalString(item, "state"), Timestamp(item, "create_time"), Timestamp(item, "finish_time")));
            }
            return new LlmBatchRequestPage(items, OptionalString(root, "pagination_token"));
        }, token);

    private Task<T> ExchangeAsync<T>(HttpMethod method, string path, JsonObject? payload, bool mutation,
        Func<JsonElement, T> parse, CancellationToken token) =>
        _exchange.ExchangeAsync(method, path, payload, mutation, (body, _) =>
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return parse(document.RootElement);
        }, token);

    private LlmBatch ReadBatch(JsonElement root, string? expectedId)
    {
        string id = RequiredString(root, "batch_id");
        if (string.IsNullOrWhiteSpace(id) || expectedId is not null && id != expectedId) throw new JsonException();
        JsonElement state = Property(root, "state");
        if (state.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object)) throw new JsonException();
        var counts = state.ValueKind == JsonValueKind.Object
            ? new LlmBatchCounts(Count(state, "num_requests"), Count(state, "num_pending"), Count(state, "num_success"),
                Count(state, "num_error"), Count(state, "num_cancelled"))
            : new(null, null, null, null, null);
        return new(id, RequiredString(root, "name"), _scope, counts,
            Timestamp(root, "create_time"), Timestamp(root, "expire_time"), Timestamp(root, "cancel_time"),
            OptionalString(root, "cancel_by_xai_message"), OptionalString(root, "create_api_key_id"));
    }

    private string Query()
    {
        var parameters = new List<string>();
        if (page?.Limit is { } limit) parameters.Add("limit=" + limit.ToString(CultureInfo.InvariantCulture));
        if (page?.PaginationToken is { } cursor) parameters.Add("pagination_token=" + Uri.EscapeDataString(cursor));
        return parameters.Count == 0 ? "" : "?" + string.Join('&', parameters);
    }

    private static string Path(string id) => "batches/" + Uri.EscapeDataString(id);

    private static void Identity(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id is "." or ".." || id.Any(char.IsControl))
            throw new ArgumentException("Invalid literal batch identifier.", nameof(id));
    }

    private static JsonElement Property(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        JsonElement result = default;
        bool found = false;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name != name) continue;
            if (found) throw new JsonException();
            found = true;
            result = property.Value;
        }
        return result;
    }

    private static JsonElement Array(JsonElement root, string name) => Property(root, name) is { ValueKind: JsonValueKind.Array } value
        ? value : throw new JsonException();

    private static string RequiredString(JsonElement root, string name) => Property(root, name) is { ValueKind: JsonValueKind.String } value
        ? value.GetString()! : throw new JsonException();

    private static string? OptionalString(JsonElement root, string name) => Property(root, name) switch
    {
        { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null } => null,
        { ValueKind: JsonValueKind.String } value => value.GetString(),
        _ => throw new JsonException()
    };

    private static long? Count(JsonElement root, string name) => Property(root, name) switch
    {
        { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null } => null,
        { ValueKind: JsonValueKind.Number } value when value.TryGetInt64(out long count) && count >= 0 => count,
        _ => throw new JsonException()
    };

    private static DateTimeOffset? Timestamp(JsonElement root, string name)
    {
        string? text = OptionalString(root, name);
        if (text is null) return null;
        // A date alone has no observed time/zone; do not manufacture midnight from guide examples.
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
        return DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out DateTimeOffset time) ? time : throw new JsonException();
    }

}
