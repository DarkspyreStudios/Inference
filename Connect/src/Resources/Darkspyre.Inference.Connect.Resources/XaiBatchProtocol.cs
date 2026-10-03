using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.Resources;

/// <summary>One admitted batch operation over the existing selected HTTP owner; no retry or polling.</summary>
internal sealed class XaiBatchProtocol(HttpClient http, LlmConnection connection, string connectionId,
    LlmResourcePageRequest? page, XaiBatchProtocol.Observation observation, Action fence)
{
    private readonly InferenceResourceScope _scope = new(ProviderHttp.BuildUri(connection, "batches").AbsoluteUri, connectionId);
    private readonly LlmCredential? _credential = LlmCredential.Parse(connection.Credential);
    private readonly string[] _secrets = connection.AdditionalHeaders.Values.ToArray();

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
            return new(items, OptionalString(root, "pagination_token"));
        }, token);

    internal Task<LlmBatchRequestPage> ListRequestsAsync(string id, CancellationToken token) =>
        ExchangeAsync(HttpMethod.Get, Path(id) + "/requests" + Query(), null, false, root =>
        {
            var items = new List<LlmBatchRequestMetadata>();
            foreach (JsonElement item in Array(root, "batch_request_metadata").EnumerateArray())
            {
                fence();
                string actualId = RequiredString(item, "batch_request_id");
                Identity(actualId);
                items.Add(new(actualId, OptionalString(item, "endpoint"), OptionalString(item, "model"),
                    OptionalString(item, "state"), Timestamp(item, "create_time"), Timestamp(item, "finish_time")));
            }
            return new(items, OptionalString(root, "pagination_token"));
        }, token);

    [SuppressMessage("Design", "CA1031", Justification = "Owned request and response cleanup preserves independent failures and actual refused owners.")]
    private async Task<T> ExchangeAsync<T>(HttpMethod method, string path, JsonObject? payload, bool mutation,
        Func<JsonElement, T> parse, CancellationToken token)
    {
        observation.BeginStage();
        HttpRequestMessage? request = null;
        HttpResponseMessage? response = null;
        T? result = default;
        var errors = new List<Exception>();
        try
        {
            fence();
            request = new(method, ProviderHttp.BuildUri(connection, path));
            XaiRequestHeaders.Apply(request, connection, false);
            if (payload is not null) request.Content = JsonContent.Create(payload);
            fence();
            if (mutation) observation.MutationDispatched = true;
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            observation.ResponseObserved = true;
            observation.StatusCode = (int)response.StatusCode;
            observation.RequestId = ProviderHttp.ReadRequestId(response) is { } id
                ? SecretRedactor.Sanitize(id, _credential, _secrets) : null;
            fence();
            await XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, _credential, _secrets, token,
                diagnosticBody: false).ConfigureAwait(false);
            fence();
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, token).ConfigureAwait(false);
            fence();
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                result = parse(document.RootElement);
            }
            catch (JsonException error) { throw new ProtocolFailure(error); }
            if (result is LlmBatch batch) observation.ObservedBatch = batch;
            fence();
        }
        catch (Exception error) { errors.Add(SafeFailure(error, observation)); }
        finally
        {
            if (response is not null)
                try { response.Dispose(); }
                catch (Exception error) { errors.Add(CleanupFailure(error, response)); }
            if (request is not null)
                try { request.Dispose(); }
                catch (Exception error) { errors.Add(CleanupFailure(error, request)); }
        }
        if (errors.Count == 1) ExceptionDispatchInfo.Throw(errors[0]);
        if (errors.Count > 1) throw new AggregateException("The batch operation and owned cleanup failed.", errors);
        fence();
        return result!;
    }

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

    internal static Exception OperationFailure(Exception error, Observation observation)
    {
        Exception safe = SafeFailure(error, observation);
        return safe is OperationCanceledException canceled
            ? new LlmResourceOperationCanceledException(observation.Snapshot(), canceled)
            : new LlmResourceOperationException(observation.Snapshot(), safe);
    }

    private static Exception SafeFailure(Exception error, Observation observation)
    {
        if (error is InferenceResourceOperationException or InferenceResourceOperationCanceledException
            or FailureEvidence) return error;
        if (error is AggregateException aggregate)
            return new AggregateException("The batch operation failed.", aggregate.InnerExceptions.Select(item => SafeFailure(item, observation)));
        if (error is OperationCanceledException canceled)
            return new OperationCanceledException("The batch operation was interrupted.", new FailureEvidence(canceled), canceled.CancellationToken);
        if (error is InferenceException inference && inference.InnerException is LlmProviderException provider)
            return new InferenceException(inference.Code, "Batch authorization or access failed.", SafeProvider(provider, inference));
        if (error is LlmProviderException failure) return SafeProvider(failure, failure);
        return new LlmProviderException(error is ProtocolFailure ? "xAI returned invalid batch protocol data." : "The batch transport failed.",
            LlmTransport.XaiResponses, observation.StatusCode is { } status ? (System.Net.HttpStatusCode)status : null,
            requestId: observation.RequestId, innerException: new FailureEvidence(error));
    }

    private static LlmProviderException SafeProvider(LlmProviderException provider, Exception original) => new(
        "The batch request failed.", LlmTransport.XaiResponses, provider.StatusCode,
        provider.ProviderCode is "xai_authentication_required" or "xai_entitlement_required" ? provider.ProviderCode : null,
        retryAfter: provider.RetryAfter, innerException: new FailureEvidence(original));

    private static Exception CleanupFailure(Exception original, object owner)
    {
        var evidence = new FailureEvidence(original);
        Exception safe = original is OperationCanceledException canceled
            ? new OperationCanceledException("Batch HTTP cleanup was interrupted.", evidence, canceled.CancellationToken) : evidence;
        return LlmResourcePreparation.AsCleanupFailure(safe, refusedOwner: owner);
    }

    private sealed class FailureEvidence(Exception original) : Exception("The batch HTTP boundary reported a failure.")
    {
        internal Exception Original { get; } = original;
    }

    private sealed class ProtocolFailure(Exception original) : Exception("Invalid batch protocol data.")
    {
        internal Exception Original { get; } = original;
    }

    internal sealed class Observation(LlmResourceOperation operation, LlmBatch? selected)
    {
        internal bool MutationDispatched { get; set; }
        internal bool ResponseObserved { get; set; }
        internal int? StatusCode { get; set; }
        internal string? RequestId { get; set; }
        internal LlmBatch? SelectedBatch { get; set; } = selected;
        internal LlmBatch? ObservedBatch { get; set; }

        internal void BeginStage()
        {
            ResponseObserved = false;
            StatusCode = null;
            RequestId = null;
            ObservedBatch = null;
        }

        internal LlmResourceOperationObservation Snapshot() => new(operation, MutationDispatched, false, false, ResponseObserved)
        {
            SelectedBatch = SelectedBatch,
            ObservedBatch = ObservedBatch,
            HttpStatusCode = StatusCode,
            RequestId = RequestId
        };
    }
}
