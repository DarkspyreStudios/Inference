using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.Resources.Internal;

/// <summary>Deferred submission uses the canonical Chat builder and existing Files preparation.</summary>
internal sealed class XaiDeferredProtocol(HttpClient http, LlmConnection connection, string connectionId,
    XaiResourceHttp.Observation observation, Action fence)
{
    internal static void ValidateCompletion(LlmDeferredCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        XaiFileRequestData.Identity(completion.RequestId);
        BatchData.Scope(completion.Scope);
    }

    internal static void ValidateConnection(LlmConnection selected, LlmDeferredCompletion completion)
    {
        XaiFileRequestData.Connection(selected);
        if (completion.Scope.Owner != ProviderHttp.BuildUri(selected, "chat/deferred-completion").AbsoluteUri)
            throw new ArgumentException("The deferred receipt contradicts the selected fixed route.", nameof(completion));
        // Scope.Identity is descriptive. The selected token authorizes this single consume-once GET.
    }

    internal async Task<LlmDeferredResult> GetAsync(LlmDeferredCompletion completion, XaiFileClient files,
        IInferenceResourceAccess? resources, CancellationToken token)
    {
        var codec = new XaiDeferredChatCodec(http);
        var exchange = new XaiResourceHttp(http, connection, observation, fence);
        ChatCompletionsLlmClientBase.ChatCompletionResponse? decoded = await exchange.ExchangeResponseAsync(
            HttpMethod.Get, "chat/deferred-completion/" + Uri.EscapeDataString(completion.RequestId), null, false,
            async (response, current) =>
            {
                if (response.StatusCode == HttpStatusCode.Accepted)
                {
                    observation.ObservedDeferredResult = new LlmDeferredPending();
                    fence();
                    return null;
                }
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new InferenceException(InferenceFailureCode.ProtocolFailure, "xAI returned an unexpected deferred response status.");
                string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, current).ConfigureAwait(false);
                fence();
                var actual = codec.DecodeCompleteResponse(body, response, connection);
                observation.ObservedDeferredResult = new LlmDeferredCompleted(actual.Result);
                fence();
                return actual;
            }, token).ConfigureAwait(false);
        fence();
        if (decoded is null) return new LlmDeferredPending();
        if (decoded.OutputFiles.Count == 0) return new LlmDeferredCompleted(decoded.Result);
        if (resources is null)
            throw new ArgumentException("Generated deferred artifacts require explicit resource access.", nameof(resources));
        // Validate every known ID before the first resource callback or file transfer.
        foreach (var file in decoded.OutputFiles) XaiFileRequestData.Identity(file.Id);
        fence();
        InferenceResourceSelectionVersion scope = await SpeechResourceWriter.ReadScopeAsync(resources, fence, token).ConfigureAwait(false);
        fence();
        var receipts = new List<InferenceResourceCommitResult>(decoded.OutputFiles.Count);
        LlmChatResult result = decoded.Result;
        foreach (var file in decoded.OutputFiles)
        {
            fence();
            var writer = new XaiGeneratedFileWriter(resources, scope, file.Name, fence, observation);
            InferenceResourceCommitResult receipt = await writer.MaterializeAsync(files, connection, file.Id, token).ConfigureAwait(false);
            receipts.Add(receipt);
            result = decoded.Result with { GeneratedResources = receipts };
            observation.ObservedDeferredResult = new LlmDeferredCompleted(result);
            fence();
        }
        fence();
        return new LlmDeferredCompleted(result);
    }

    [SuppressMessage("Design", "CA1031", Justification = "Admitted preparation attempts cleanup after operation failure and retains actual partial upload effects.")]
    internal async Task<LlmDeferredCompletion> SubmitAsync(LlmChatRequest request, XaiFileClient? files, CancellationToken token)
    {
        var codec = new XaiDeferredChatCodec(http);
        var errors = new List<Exception>();
        IInferenceResourceAccess? admittedResources = request.Resources;
        LlmResourcePreparation? preparation = null;
        LlmDeferredCompletion? result = null;
        try
        {
            fence();
            LlmProviderCapabilities capabilities = codec.GetCapabilities(connection, request.ModelId);
            codec.ValidateSubmissionRequest(connection, request, capabilities, admittedResources);
            fence();
            LlmConnection preparedConnection = await codec.PrepareAsync(connection, token).ConfigureAwait(false);
            fence();
            (request, _) = ContinuationStateProjection.Project(request, preparedConnection);
            fence();
            if (request.Resources is not null)
            {
                if (files is null) throw new NotSupportedException("The selected facade has no Files preparation route.");
                var fileScope = new InferenceResourceScope(ProviderHttp.BuildUri(connection, "files").AbsoluteUri, connectionId);
                preparation = await LlmResourcePreparation.PrepareAsync(preparedConnection, request, files, fence, token,
                    source =>
                    {
                        observation.ReserveUploadCapacity();
                        int attempt = -1;
                        return (() => attempt = observation.BeginUpload(fileScope, null, source, false),
                            observed => observation.ObserveUpload(attempt, observed));
                    }).ConfigureAwait(false);
                fence();
                request = preparation.Request;
            }
            codec.ValidateSubmissionRequest(preparedConnection, request, capabilities, admittedResources);
            fence();
            LlmStructuredOutputEnforcement? enforcement = XaiSchemaLimits.ResolveEnforcement(request.StructuredOutput, capabilities);
            var body = codec.BuildPreparedPayload(preparedConnection, request, enforcement, capabilities, preparation);
            fence();
            var exchange = new XaiResourceHttp(http, preparedConnection, observation, fence);
            result = await exchange.ExchangeAsync(HttpMethod.Post, "chat/completions", body, true,
                (text, _) => ReadSubmission(text), token).ConfigureAwait(false);
            fence();
        }
        catch (Exception error) { errors.Add(XaiResourceHttp.SafeFailure(error, observation)); }
        finally
        {
            if (preparation is not null)
            {
                try { await preparation.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { errors.Add(XaiResourceHttp.SafeFailure(error, observation)); }
            }
        }
        if (errors.Count == 1) ExceptionDispatchInfo.Throw(errors[0]);
        if (errors.Count > 1) throw XaiResourceHttp.CombineFailures(errors);
        fence();
        return result!;
    }

    private LlmDeferredCompletion ReadSubmission(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        string? id = null;
        foreach (JsonProperty field in document.RootElement.EnumerateObject())
        {
            if (field.Name != "request_id") continue;
            if (id is not null || field.Value.ValueKind != JsonValueKind.String) throw new JsonException();
            id = field.Value.GetString();
        }
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.Any(char.IsControl)) throw new JsonException();
        string safe = SecretRedactor.Sanitize(id, LlmCredential.Parse(connection.Credential), connection.AdditionalHeaders.Values);
        if (!string.Equals(id, safe, StringComparison.Ordinal)) throw new JsonException();
        // The submission response supplies no timestamp; the documented retention period is not an observed expiry.
        return new(id, new(ProviderHttp.BuildUri(connection, "chat/deferred-completion").AbsoluteUri, connectionId));
    }
}
