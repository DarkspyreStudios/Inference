using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.Resources.Internal;

/// <summary>The single admitted batch/deferred HTTP exchange; borrows the selected HTTP owner.</summary>
internal sealed class XaiResourceHttp(HttpClient http, LlmConnection connection, XaiResourceHttp.Observation observation,
    Action fence, IReadOnlyList<string>? secretContext = null)
{
    internal Task<T> ExchangeAsync<T>(HttpMethod method, string path, JsonObject? payload, bool mutation,
        Func<string, HttpResponseMessage, T> parse, CancellationToken token)
        => ExchangeResponseAsync(method, path, payload, mutation, async (response, current) =>
        {
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, current).ConfigureAwait(false);
            fence();
            try { return parse(body, response); }
            catch (JsonException error) { throw new ProtocolFailure(error); }
        }, token);

    [SuppressMessage("Design", "CA1031", Justification = "Owned request and response cleanup preserves independent failures and actual refused owners.")]
    internal async Task<T> ExchangeResponseAsync<T>(HttpMethod method, string path, JsonObject? payload, bool mutation,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read, CancellationToken token)
    {
        observation.BeginStage(method.Method + " " + path);
        HttpRequestMessage? request = null;
        HttpResponseMessage? response = null;
        T? result = default;
        var errors = new List<Exception>();
        try
        {
            fence();
            request = new(method, ProviderHttp.BuildUri(connection, path));
            XaiRequestHeaders.Apply(request, connection, false);
            if (payload is not null) request.Content = RequestJson.Content(payload);
            fence();
            if (mutation)
            {
                if (observation.Operation == LlmResourceOperation.SubmitDeferred) observation.SubmissionDispatched = true;
                else observation.MutationDispatched = true;
            }
            observation.RequestDispatched = true;
            if (observation.Operation == LlmResourceOperation.GetDeferred) observation.RetrievalDispatched = true;
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            observation.SecretContext = observation.SecretContext.Concat(connection.AdditionalHeaders.Values).Concat(secretContext ?? [])
                .Concat(request.Headers.SelectMany(header => header.Value)).Distinct(StringComparer.Ordinal).ToArray();
            observation.ObserveResponse(response, connection);
            fence();
            await XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, LlmCredential.Parse(connection.Credential),
                observation.SecretContext, token,
                diagnosticBody: false).ConfigureAwait(false);
            fence();
            result = await read(response, token).ConfigureAwait(false);
            if (result is LlmBatch batch) observation.ObservedBatch = batch;
            if (result is LlmDeferredCompletion deferred) observation.ObservedDeferred = deferred;
            fence();
        }
        catch (Exception error) { errors.Add(SafeFailure(error, observation, connection: connection)); }
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
        if (errors.Count > 1) throw new ExchangeFailure(errors);
        fence();
        return result!;
    }

    internal static Exception OperationFailure(Exception error, Observation observation)
    {
        if (error is ExchangeFailure exchange)
            return new OperationCleanupFailure(
                new[] { OperationFailure(exchange.InnerExceptions[0], observation) }
                    .Concat(exchange.InnerExceptions.Skip(1).Select(cleanup => SafeFailure(cleanup, observation, forceSafe: true))), exchange);
        if (!observation.HasEffects) return error;
        Exception safe = SafeFailure(error, observation);
        return safe is OperationCanceledException canceled
            ? new LlmResourceOperationCanceledException(observation.Snapshot(), canceled)
            : new LlmResourceOperationException(observation.Snapshot(), safe);
    }

    internal static Exception CombineFailures(IEnumerable<Exception> errors) => new ExchangeFailure(errors);

    internal static Exception SafeFailure(Exception error, Observation observation, bool forceSafe = false,
        LlmConnection? connection = null)
    {
        if (error is FailureEvidence or ExchangeFailure) return error;
        if (error is XaiFileClient.ExchangeFailure files)
            return new ExchangeFailure(files.InnerExceptions.Select(item => SafeFailure(item, observation, forceSafe: true, connection)), files);
        if (error is LlmResourcePreparation.PreparationFailure preparation)
            return new ExchangeFailure(preparation.InnerExceptions.Select(item => SafeFailure(item, observation, forceSafe: true, connection)), preparation);
        if (error is InferenceResourceOperationException resource)
        {
            observation.ResourceFailure = resource.Failure;
            if (IsOwnedSafe(resource.InnerException)) return resource;
            Exception safe = new InferenceResourceOperationException(resource.Code,
                "The resource operation failed.", resource.Failure,
                new FailureEvidence(resource, resource.InnerException is { } cause ? SafeFailure(cause, observation, true, connection) : null));
            return resource.Failure.Phase == InferenceResourceOperationPhase.Cleanup
                ? LlmResourcePreparation.AsCleanupFailure(safe, refusedOwner: resource) : safe;
        }
        if (error is InferenceResourceOperationCanceledException canceledResource)
        {
            observation.ResourceFailure = canceledResource.Failure;
            if (IsOwnedSafe(canceledResource.InnerException)) return canceledResource;
            Exception safe = new InferenceResourceOperationCanceledException(canceledResource.Failure,
                canceledResource.CancellationToken,
                new FailureEvidence(canceledResource, canceledResource.InnerException is { } cause ? SafeFailure(cause, observation, true, connection) : null));
            return canceledResource.Failure.Phase == InferenceResourceOperationPhase.Cleanup
                ? LlmResourcePreparation.AsCleanupFailure(safe, refusedOwner: canceledResource) : safe;
        }
        if (!forceSafe && !observation.HasEffects && observation.SecretContext.Count == 0) return error;
        if (error is AggregateException aggregate)
            return new AggregateException("The resource operation failed.", aggregate.InnerExceptions.Select(item => SafeFailure(item, observation, forceSafe, connection)));
        if (error is OperationCanceledException canceled)
            return new OperationCanceledException("The resource operation was interrupted.", new FailureEvidence(canceled, observation: observation), canceled.CancellationToken);
        if (error is InferenceException inference && inference.InnerException is LlmProviderException provider)
            return new InferenceException(inference.Code, "Resource authorization or access failed.", SafeProvider(provider, inference, observation, connection));
        if (error is LlmProviderException failure) return SafeProvider(failure, failure, observation, connection);
        return new LlmProviderException(error is ProtocolFailure ? "xAI returned invalid resource protocol data." : "The resource transport failed.",
            LlmTransport.XaiResponses, observation.StatusCode is { } status ? (System.Net.HttpStatusCode)status : null,
            requestId: observation.RequestId, innerException: new FailureEvidence(error, observation: observation));
    }

    private static LlmProviderException SafeProvider(LlmProviderException provider, Exception original, Observation observation,
        LlmConnection? connection) => new(
        "The resource request failed.", LlmTransport.XaiResponses, provider.StatusCode,
        provider.ProviderCode is { } code && (connection is not null || provider.InnerException is FailureEvidence
            || code is "xai_authentication_required" or "xai_entitlement_required")
            ? SecretRedactor.Sanitize(code, LlmCredential.Parse(connection?.Credential),
                (connection?.AdditionalHeaders.Values ?? []).Concat(observation.SecretContext)) : null,
        requestId: observation.RequestId, retryAfter: provider.RetryAfter,
        innerException: new FailureEvidence(original, observation: observation), isContextOverflow: provider.IsContextOverflow);

    private static bool IsOwnedSafe(Exception? error) => error is FailureEvidence
        || error is OperationCanceledException { InnerException: FailureEvidence };

    private static Exception CleanupFailure(Exception original, object owner)
    {
        var evidence = new FailureEvidence(original);
        Exception safe = original is OperationCanceledException canceled
            ? new OperationCanceledException("Resource HTTP cleanup was interrupted.", evidence, canceled.CancellationToken) : evidence;
        return LlmResourcePreparation.AsCleanupFailure(safe, refusedOwner: owner);
    }

    private sealed class FailureEvidence(Exception original, Exception? safeCause = null, Observation? observation = null)
        : Exception("The resource HTTP boundary reported a failure.", safeCause)
    {
        internal Exception Original { get; } = original;
        internal string? Stage { get; } = observation?.Stage;
        internal bool RequestDispatched { get; } = observation?.RequestDispatched ?? false;
    }

    private sealed class ProtocolFailure(Exception original) : Exception("Invalid resource protocol data.")
    {
        internal Exception Original { get; } = original;
    }

    // Only this owner constructs the operation-first, response-cleanup, request-cleanup sequence.
    private sealed class ExchangeFailure(IEnumerable<Exception> errors, Exception? original = null)
        : AggregateException("The resource operation and owned cleanup failed.", errors)
    {
        internal Exception? Original { get; } = original;
    }

    private sealed class OperationCleanupFailure(IEnumerable<Exception> errors, Exception original)
        : AggregateException("The resource operation and owned cleanup failed.", errors)
    {
        internal Exception Original { get; } = original;
    }

    internal sealed class Observation(LlmResourceOperation operation, LlmBatch? selected = null)
    {
        private readonly List<UploadAttempt> _uploads = [];
        private UploadAttempt? _reservedUpload;
        private readonly List<InferenceResourceCommitResult> _commits = [];
        private readonly List<LlmBatchResult> _results = [];
        private bool _hadRequest;
        internal LlmResourceOperation Operation { get; } = operation;
        internal bool MutationDispatched { get; set; }
        internal bool SubmissionDispatched { get; set; }
        internal bool RetrievalDispatched { get; set; }
        internal bool RequestDispatched
        {
            get;
            set { field = value; _hadRequest |= value; }
        }
        internal bool ResponseObserved { get; set; }
        internal int? StatusCode { get; set; }
        internal string? RequestId { get; set; }
        internal string? Stage { get; private set; }
        internal IReadOnlyList<string> SecretContext { get; set; } = [];
        internal LlmBatch? SelectedBatch { get; set; } = selected;
        internal LlmBatch? ObservedBatch { get; set; }
        internal LlmDeferredCompletion? ObservedDeferred { get; set; }
        internal LlmDeferredCompletion? SelectedDeferred { get; set; }
        internal LlmDeferredResult? ObservedDeferredResult { get; set; }
        internal InferenceResourceOperationFailure? ResourceFailure { get; set; }
        internal bool HasEffects => _hadRequest || MutationDispatched || SubmissionDispatched || RetrievalDispatched || _uploads.Count != 0 || _commits.Count != 0 || _results.Count != 0;

        internal void ObserveResponse(HttpResponseMessage response, LlmConnection selected)
        {
            ResponseObserved = true;
            StatusCode = (int)response.StatusCode;
            RequestId = ProviderHttp.ReadRequestId(response) is { } id
                ? SecretRedactor.Sanitize(id, LlmCredential.Parse(selected.Credential),
                    selected.AdditionalHeaders.Values.Concat(SecretContext)) : null;
        }

        internal void ObserveCommit(InferenceResourceCommitResult receipt)
            => _commits.Add(ResourceObservationData.CopyCommits([receipt])[0]);

        internal void ReserveResultCapacity(int count) => _results.EnsureCapacity(checked(_results.Count + count));
        internal int ObserveResult(LlmBatchResult result)
        {
            int index = _results.Count;
            _results.Add(result);
            return index;
        }
        internal void UpdateResult(int index, LlmBatchResult result) => _results[index] = result;

        internal void ReserveUploadCapacity()
        {
            _uploads.EnsureCapacity(checked(_uploads.Count + 1));
            _reservedUpload = new();
        }

        internal int BeginUpload(InferenceResourceScope scope, string? customId, InferenceResourceReference? source, bool isJsonl)
        {
            int attempt = _uploads.Count;
            UploadAttempt reserved = _reservedUpload ?? throw new InvalidOperationException("Upload capacity was not reserved.");
            _reservedUpload = null;
            reserved.Scope = scope;
            reserved.CustomId = customId;
            reserved.Source = source;
            reserved.IsJsonl = isJsonl;
            _uploads.Add(reserved);
            BeginStage("POST files");
            RequestDispatched = true;
            return attempt;
        }

        internal void ObserveUpload(int attempt, LlmProviderFile file)
        {
            if ((uint)attempt >= (uint)_uploads.Count || _uploads[attempt].File is not null)
                throw new InvalidOperationException("No dispatched upload exists for the observed receipt.");
            _uploads[attempt].File = file;
        }

        internal void BeginStage(string? stage = null)
        {
            Stage = stage;
            RequestDispatched = false;
            ResponseObserved = false;
            StatusCode = null;
            RequestId = null;
            if (Operation != LlmResourceOperation.CreateBatchFromRequests) ObservedBatch = null;
        }

        internal LlmResourceOperationObservation Snapshot() => new(Operation, MutationDispatched, SubmissionDispatched, RetrievalDispatched, ResponseObserved)
        {
            SelectedBatch = SelectedBatch,
            ObservedBatch = ObservedBatch,
            ObservedDeferred = ObservedDeferred,
            SelectedDeferred = SelectedDeferred,
            ObservedDeferredResult = ObservedDeferredResult,
            ObservedResults = _results,
            ObservedCommits = _commits,
            InputUploads = _uploads.Select(attempt => new LlmResourceInputUploadObservation(attempt.Scope, true, attempt.IsJsonl)
            { CustomId = attempt.CustomId, Source = attempt.Source, ObservedFile = attempt.File }).ToArray(),
            ResourceFailure = ResourceFailure,
            HttpStatusCode = StatusCode,
            RequestId = RequestId
        };

        private sealed class UploadAttempt
        {
            internal InferenceResourceScope Scope { get; set; } = null!;
            internal string? CustomId { get; set; }
            internal InferenceResourceReference? Source { get; set; }
            internal bool IsJsonl { get; set; }
            internal LlmProviderFile? File { get; set; }
        }
    }
}
