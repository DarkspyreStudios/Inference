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
internal sealed class XaiResourceHttp(HttpClient http, LlmConnection connection, XaiResourceHttp.Observation observation, Action fence)
{
    private readonly LlmCredential? _credential = LlmCredential.Parse(connection.Credential);
    private readonly string[] _secrets = connection.AdditionalHeaders.Values.ToArray();

    [SuppressMessage("Design", "CA1031", Justification = "Owned request and response cleanup preserves independent failures and actual refused owners.")]
    internal async Task<T> ExchangeAsync<T>(HttpMethod method, string path, JsonObject? payload, bool mutation,
        Func<string, HttpResponseMessage, T> parse, CancellationToken token)
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
            if (mutation)
            {
                if (observation.Operation == LlmResourceOperation.SubmitDeferred) observation.SubmissionDispatched = true;
                else observation.MutationDispatched = true;
            }
            observation.RequestDispatched = true;
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
            try { result = parse(body, response); }
            catch (JsonException error) { throw new ProtocolFailure(error); }
            if (result is LlmBatch batch) observation.ObservedBatch = batch;
            if (result is LlmDeferredCompletion deferred) observation.ObservedDeferred = deferred;
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
        if (errors.Count > 1) throw new ExchangeFailure(errors);
        fence();
        return result!;
    }

    internal static Exception OperationFailure(Exception error, Observation observation)
    {
        if (error is ExchangeFailure exchange)
            return new AggregateException("The resource operation and owned cleanup failed.",
                new[] { OperationFailure(exchange.InnerExceptions[0], observation) }.Concat(exchange.InnerExceptions.Skip(1)));
        if (!observation.HasEffects) return error;
        Exception safe = SafeFailure(error, observation);
        return safe is OperationCanceledException canceled
            ? new LlmResourceOperationCanceledException(observation.Snapshot(), canceled)
            : new LlmResourceOperationException(observation.Snapshot(), safe);
    }

    internal static Exception CombineFailures(IEnumerable<Exception> errors) => new ExchangeFailure(errors);

    internal static Exception SafeFailure(Exception error, Observation observation, bool forceSafe = false)
    {
        if (error is FailureEvidence or ExchangeFailure) return error;
        if (error is InferenceResourceOperationException resource)
        {
            observation.ResourceFailure = resource.Failure;
            if (IsOwnedSafe(resource.InnerException)) return resource;
            Exception safe = new InferenceResourceOperationException(resource.Code,
                "The resource operation failed.", resource.Failure,
                new FailureEvidence(resource, resource.InnerException is { } cause ? SafeFailure(cause, observation, true) : null));
            return resource.Failure.Phase == InferenceResourceOperationPhase.Cleanup
                ? LlmResourcePreparation.AsCleanupFailure(safe, refusedOwner: resource) : safe;
        }
        if (error is InferenceResourceOperationCanceledException canceledResource)
        {
            observation.ResourceFailure = canceledResource.Failure;
            if (IsOwnedSafe(canceledResource.InnerException)) return canceledResource;
            Exception safe = new InferenceResourceOperationCanceledException(canceledResource.Failure,
                canceledResource.CancellationToken,
                new FailureEvidence(canceledResource, canceledResource.InnerException is { } cause ? SafeFailure(cause, observation, true) : null));
            return canceledResource.Failure.Phase == InferenceResourceOperationPhase.Cleanup
                ? LlmResourcePreparation.AsCleanupFailure(safe, refusedOwner: canceledResource) : safe;
        }
        if (!forceSafe && !observation.HasEffects) return error;
        if (error is AggregateException aggregate)
            return new AggregateException("The resource operation failed.", aggregate.InnerExceptions.Select(item => SafeFailure(item, observation, forceSafe)));
        if (error is OperationCanceledException canceled)
            return new OperationCanceledException("The resource operation was interrupted.", new FailureEvidence(canceled), canceled.CancellationToken);
        if (error is InferenceException inference && inference.InnerException is LlmProviderException provider)
            return new InferenceException(inference.Code, "Resource authorization or access failed.", SafeProvider(provider, inference));
        if (error is LlmProviderException failure) return SafeProvider(failure, failure);
        return new LlmProviderException(error is ProtocolFailure ? "xAI returned invalid resource protocol data." : "The resource transport failed.",
            LlmTransport.XaiResponses, observation.StatusCode is { } status ? (System.Net.HttpStatusCode)status : null,
            requestId: observation.RequestId, innerException: new FailureEvidence(error));
    }

    private static LlmProviderException SafeProvider(LlmProviderException provider, Exception original) => new(
        "The resource request failed.", LlmTransport.XaiResponses, provider.StatusCode,
        provider.ProviderCode is "xai_authentication_required" or "xai_entitlement_required" ? provider.ProviderCode : null,
        retryAfter: provider.RetryAfter, innerException: new FailureEvidence(original));

    private static bool IsOwnedSafe(Exception? error) => error is FailureEvidence
        || error is OperationCanceledException { InnerException: FailureEvidence };

    private static Exception CleanupFailure(Exception original, object owner)
    {
        var evidence = new FailureEvidence(original);
        Exception safe = original is OperationCanceledException canceled
            ? new OperationCanceledException("Resource HTTP cleanup was interrupted.", evidence, canceled.CancellationToken) : evidence;
        return LlmResourcePreparation.AsCleanupFailure(safe, refusedOwner: owner);
    }

    private sealed class FailureEvidence(Exception original, Exception? safeCause = null)
        : Exception("The resource HTTP boundary reported a failure.", safeCause)
    {
        internal Exception Original { get; } = original;
    }

    private sealed class ProtocolFailure(Exception original) : Exception("Invalid resource protocol data.")
    {
        internal Exception Original { get; } = original;
    }

    // Only this owner constructs the operation-first, response-cleanup, request-cleanup sequence.
    private sealed class ExchangeFailure(IEnumerable<Exception> errors)
        : AggregateException("The resource operation and owned cleanup failed.", errors);

    internal sealed class Observation(LlmResourceOperation operation, LlmBatch? selected = null)
    {
        private readonly List<LlmResourceInputUploadObservation> _uploads = [];
        internal LlmResourceOperation Operation { get; } = operation;
        internal bool MutationDispatched { get; set; }
        internal bool SubmissionDispatched { get; set; }
        internal bool RequestDispatched { get; set; }
        internal bool ResponseObserved { get; set; }
        internal int? StatusCode { get; set; }
        internal string? RequestId { get; set; }
        internal LlmBatch? SelectedBatch { get; set; } = selected;
        internal LlmBatch? ObservedBatch { get; set; }
        internal LlmDeferredCompletion? ObservedDeferred { get; set; }
        internal InferenceResourceOperationFailure? ResourceFailure { get; set; }
        internal bool HasEffects => RequestDispatched || MutationDispatched || SubmissionDispatched || _uploads.Count != 0;

        internal void ObserveUpload(InferenceResourceScope scope, InferenceResourceReference source, LlmProviderFile? file)
        {
            if (file is null)
            {
                _uploads.Add(new(scope, true, false) { Source = source });
                return;
            }
            int index = _uploads.FindLastIndex(item => item.Source == source && item.ObservedFile is null);
            if (index < 0) throw new InvalidOperationException("No dispatched upload exists for the observed receipt.");
            _uploads[index] = _uploads[index] with { ObservedFile = file };
        }

        internal void BeginStage()
        {
            RequestDispatched = false;
            ResponseObserved = false;
            StatusCode = null;
            RequestId = null;
            ObservedBatch = null;
        }

        internal LlmResourceOperationObservation Snapshot() => new(Operation, MutationDispatched, SubmissionDispatched, false, ResponseObserved)
        {
            SelectedBatch = SelectedBatch,
            ObservedBatch = ObservedBatch,
            ObservedDeferred = ObservedDeferred,
            InputUploads = _uploads,
            ResourceFailure = ResourceFailure,
            HttpStatusCode = StatusCode,
            RequestId = RequestId
        };
    }
}
