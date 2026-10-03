using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.Resources.Internal;

/// <summary>Deferred submission uses the canonical Chat builder and existing Files preparation.</summary>
internal sealed class XaiDeferredProtocol(HttpClient http, LlmConnection connection, string connectionId,
    XaiResourceHttp.Observation observation, Action fence)
{
    [SuppressMessage("Design", "CA1031", Justification = "Admitted preparation attempts cleanup after operation failure and retains actual partial upload effects.")]
    internal async Task<LlmDeferredCompletion> SubmitAsync(LlmChatRequest request, XaiFileClient? files, CancellationToken token)
    {
        var codec = new XaiDeferredChatCodec(http);
        var errors = new List<Exception>();
        LlmResourcePreparation? preparation = null;
        LlmDeferredCompletion? result = null;
        try
        {
            fence();
            LlmProviderCapabilities capabilities = codec.GetCapabilities(connection, request.ModelId);
            codec.ValidatePreparedRequest(connection, request, capabilities);
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
                    (source, file) => observation.ObserveUpload(fileScope, source, file)).ConfigureAwait(false);
                fence();
                request = preparation.Request;
            }
            codec.ValidatePreparedRequest(preparedConnection, request, capabilities);
            fence();
            LlmStructuredOutputEnforcement? enforcement = XaiSchemaLimits.ResolveEnforcement(request.StructuredOutput, capabilities);
            var body = codec.BuildPreparedPayload(preparedConnection, request, enforcement, capabilities);
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
