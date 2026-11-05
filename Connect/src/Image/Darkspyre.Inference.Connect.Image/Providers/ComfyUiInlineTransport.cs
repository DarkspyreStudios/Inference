using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.Image.Providers;

internal sealed partial class ComfyUiImageTransport
{
    private readonly Dictionary<(InferenceResourceScope Scope, string Id), ComfyInlineJob> _inlineJobs = [];
    private bool _disposed;

    private static bool IsInline(ComfyWorkflowConfiguration configuration) => IsInline(configuration.Workflow, configuration.OutputNodeId);
    private static bool IsInline(JsonElement graph, string outputNodeId) =>
        graph.TryGetProperty(outputNodeId, out JsonElement output) && output.TryGetProperty("class_type", out JsonElement type) &&
        type.ValueKind == JsonValueKind.String && type.GetString() == "SaveImageWebsocket";

    private static ImagePreparedReference InlineReference(ImageReference metadata, InferenceResourceScope scope, byte[] data)
    {
        if (data.Length > ComfyFrameReader.MaximumBytes) throw InlinePayloadTooLarge();
        return new(metadata, scope, JsonSerializer.SerializeToElement(new ComfyInlineReference("inline", Convert.ToBase64String(data)),
            ComfyInlineJsonContext.Default.ComfyInlineReference));
    }

    private static string ReadInlineReference(JsonElement state)
    {
        ComfyInlineReference reference = JsonSerializer.Deserialize(state, ComfyInlineJsonContext.Default.ComfyInlineReference)
            ?? throw new InferenceException(InferenceFailureCode.ProtocolFailure, "The inline reference receipt is invalid.");
        if (reference.Kind != "inline" || reference.Data is null)
            throw new InferenceException(InferenceFailureCode.ProtocolFailure, "The reference receipt does not match inline transfer.");
        _ = InlineReferenceLength(reference.Data);
        return reference.Data;
    }

    private static int InlineReferenceLength(string data)
    {
        if (data.Length > ((ComfyFrameReader.MaximumBytes + 2L) / 3) * 4) throw InlinePayloadTooLarge();
        try
        {
            int length = Convert.FromBase64String(data).Length;
            if (length > ComfyFrameReader.MaximumBytes) throw InlinePayloadTooLarge();
            return length;
        }
        catch (FormatException)
        {
            throw new InferenceException(InferenceFailureCode.ProtocolFailure, "The inline reference receipt is invalid.");
        }
    }

    private static async Task CopyInlineBoundedAsync(Stream source, Stream destination, CancellationToken token)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            total += count;
            if (total > ComfyFrameReader.MaximumBytes) throw InlinePayloadTooLarge();
            await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
    }

    private static InferenceException InlinePayloadTooLarge() => new(InferenceFailureCode.ProtocolFailure,
        "The inline image payload exceeds the supported size.");

    private async Task<string> SubmitInlineAsync(ImageConnection connection, InferenceResourceScope scope,
        PreparedImageTransportRequest request, CancellationToken token)
    {
        ComfyInlinePreparedEnvelope envelope = JsonSerializer.Deserialize(request.ProviderRequest,
            ComfyInlineJsonContext.Default.ComfyInlinePreparedEnvelope)
            ?? throw new InferenceException(InferenceFailureCode.ProtocolFailure, "The inline request is invalid.");
        if (!envelope.Inline || string.IsNullOrWhiteSpace(envelope.RequestId) ||
            !IsInline(envelope.Workflow, envelope.OutputNodeId))
            throw new InferenceException(InferenceFailureCode.ProtocolFailure, "The prepared image transfer mode does not match its graph.");
        foreach (InferenceResourceScope reference in envelope.ReferenceScopes) ValidateScope(reference, scope);
        int referenceBytes = 0;
        foreach (JsonProperty node in envelope.Workflow.EnumerateObject())
        {
            if (node.Value.TryGetProperty("class_type", out JsonElement type) && type.GetString() == "easy loadImageBase64" &&
                node.Value.TryGetProperty("inputs", out JsonElement inputs) && inputs.TryGetProperty("base64_data", out JsonElement data))
            {
                if (!inputs.TryGetProperty("image_output", out JsonElement mode) || mode.GetString() != "Hide" || data.ValueKind != JsonValueKind.String)
                    throw new InferenceException(InferenceFailureCode.ProtocolFailure, "The inline workflow requires a hidden base64 reference loader.");
                int length = InlineReferenceLength(data.GetString()!);
                if (length > ComfyFrameReader.MaximumBytes - referenceBytes) throw InlinePayloadTooLarge();
                referenceBytes += length;
            }
        }

        using var submission = SdkHttpConsumption.Enter(httpClientFactory, token);
        var socket = new ClientWebSocket();
        ApplyHeaders(socket.Options, connection);
        SdkHttpConsumption.Operation admission = SdkHttpConsumption.Enter(httpClientFactory, submission.Token);
        var job = new ComfyInlineJob(envelope.RequestId, envelope.OutputNodeId, scope, socket, referenceBytes, admission);
        string clientId = Guid.NewGuid().ToString("N");
        bool reserved;
        lock (_scopeGate)
        {
            reserved = !_disposed && _inlineJobs.TryAdd((scope, envelope.RequestId), job);
        }
        if (!reserved)
        {
            await job.ReleaseLocalAsync().ConfigureAwait(false);
            throw new InferenceException(InferenceFailureCode.ProtocolFailure, "The inline image request identity is already used or its owner is retiring.");
        }
        try
        {
            var endpoint = new UriBuilder(BuildWebSocketUri(connection)) { Query = $"clientId={Uri.EscapeDataString(clientId)}" };
            await job.StartAsync(endpoint.Uri).ConfigureAwait(false);
            job.BeginDispatch();
            using HttpClient client = CreateClient(connection);
            using HttpResponseMessage response = await client.PostAsJsonAsync(BuildUri(connection, "prompt"),
                new ComfyInlinePromptRequest(envelope.Workflow, clientId, envelope.RequestId),
                ComfyInlineJsonContext.Default.ComfyInlinePromptRequest, job.Token).ConfigureAwait(false);
            using JsonDocument body = await ReadInlineJsonAsync(response, job.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.BadRequest && IsValidationRejection(body.RootElement)) job.ConfirmValidationRejection();
                throw new InferenceException(InferenceFailureCode.ExecutionFailed, "The image provider rejected the inline workflow.");
            }
            if (!body.RootElement.TryGetProperty("prompt_id", out JsonElement id) || id.ValueKind != JsonValueKind.String || id.GetString() != job.Id)
                throw new InferenceException(InferenceFailureCode.ProtocolFailure, "The image provider returned a different job identity.");
            return submission.Publish(job.Id);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A lost ACK still owns this reserved id. Local capture always drains, even when cleanup cannot be proven.
            try
            {
                if (!await ReleaseInlineAsync(connection, scope, job.Id, CancellationToken.None).ConfigureAwait(false))
                    throw new ImageJobCleanupException(job.Id, connection.Endpoint, mayStillBeRunning: true);
            }
            catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
            {
                Exception safeCleanup = cleanup is ImageJobCleanupException ? cleanup
                    : new ImageJobCleanupException(job.Id, connection.Endpoint, !job.NoDispatch && !job.TerminalProof);
                throw new AggregateException(SafeInlineSubmissionFailure(error, token), safeCleanup);
            }
            throw SafeInlineSubmissionFailure(error, token);
        }
    }

    private static Exception SafeInlineSubmissionFailure(Exception error, CancellationToken token) => error switch
    {
        OperationCanceledException => new OperationCanceledException("The inline image submission was cancelled.", token),
        InferenceException failure => new InferenceException(failure.Code, "The inline image submission failed."),
        _ => new InferenceException(InferenceFailureCode.ExecutionFailed, "The inline image submission failed.")
    };

    private static bool IsValidationRejection(JsonElement root) =>
        root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object &&
        error.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String &&
        type.GetString() is "prompt_no_outputs" or "prompt_outputs_failed_validation";

    public async Task<bool> ReleaseJobAsync(ImageConnection connection, ImageWorkflow workflow,
        string providerJobId, CancellationToken cancellationToken = default)
    {
        (InferenceResourceScope scope, connection) = Admit(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerJobId);
        ArgumentNullException.ThrowIfNull(workflow);
        if (!IsInline(ReadConfiguration(workflow))) return true;
        return await ReleaseInlineAsync(connection, scope, providerJobId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ReleaseInlineAsync(ImageConnection connection, InferenceResourceScope scope, string id, CancellationToken token)
    {
        ComfyInlineJob? job;
        lock (_scopeGate)
        {
            _inlineJobs.TryGetValue((scope, id), out job);
            if (job is null && _inlineJobs.Any(entry => entry.Key.Id == id && entry.Key.Scope.Owner == scope.Owner))
                throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The inline job belongs to a different transport owner.");
        }
        if (job is not null)
        {
            await job.ReleaseLocalAsync().ConfigureAwait(false);
            await job.CleanupGate.WaitAsync(token).ConfigureAwait(false);
        }
        try
        {
            using var consumption = SdkHttpConsumption.Enter(httpClientFactory, token);
            token = consumption.Token;
            using HttpClient client = CreateClient(connection);
            using JsonDocument history = await GetInlineJsonAsync(client, connection, $"history/{Uri.EscapeDataString(id)}", token).ConfigureAwait(false);
            using JsonDocument queue = await GetInlineJsonAsync(client, connection, "queue", token).ConfigureAwait(false);
            bool active = InlineQueueContains(queue.RootElement, "queue_running", id) || InlineQueueContains(queue.RootElement, "queue_pending", id);
            if (active) return consumption.Publish(false);
            bool exists = history.RootElement.TryGetProperty(id, out JsonElement record);
            bool terminal = exists && record.TryGetProperty("status", out JsonElement status) &&
                status.TryGetProperty("status_str", out JsonElement state) && state.ValueKind == JsonValueKind.String &&
                state.GetString() is "success" or "error";
            if (terminal) job?.ConfirmTerminalProof();
            bool proven = terminal || job?.TerminalProof == true;
            if (!proven)
                return consumption.Publish(job?.NoDispatch == true && !exists);
            using HttpResponseMessage deletion = await client.PostAsJsonAsync(BuildUri(connection, "history"),
                new ComfyDeleteRequest([id]), ComfyProtocolJsonContext.Default.ComfyDeleteRequest, token).ConfigureAwait(false);
            if (!deletion.IsSuccessStatusCode) throw new InferenceException(InferenceFailureCode.ExecutionFailed, "The image job metadata could not be released.");
            using JsonDocument remaining = await GetInlineJsonAsync(client, connection, $"history/{Uri.EscapeDataString(id)}", token).ConfigureAwait(false);
            using JsonDocument remainingQueue = await GetInlineJsonAsync(client, connection, "queue", token).ConfigureAwait(false);
            return consumption.Publish(!remaining.RootElement.TryGetProperty(id, out _) &&
                !InlineQueueContains(remainingQueue.RootElement, "queue_running", id) && !InlineQueueContains(remainingQueue.RootElement, "queue_pending", id));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new InferenceException(InferenceFailureCode.ExecutionFailed, "The image job metadata could not be released.");
        }
        finally { job?.CleanupGate.Release(); }
    }

    private static bool InlineQueueContains(JsonElement queue, string propertyName, string id)
    {
        if (queue.ValueKind != JsonValueKind.Object || !queue.TryGetProperty(propertyName, out JsonElement items) ||
            items.ValueKind != JsonValueKind.Array || items.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Array ||
                item.GetArrayLength() < 2 || item[1].ValueKind != JsonValueKind.String))
            throw new InferenceException(InferenceFailureCode.ProtocolFailure, "The image queue response is invalid.");
        return QueueContains(queue, propertyName, id);
    }

    private static async Task<JsonDocument> GetInlineJsonAsync(HttpClient client, ImageConnection connection, string path, CancellationToken token)
    {
        using HttpResponseMessage response = await client.GetAsync(BuildUri(connection, path), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InferenceException(InferenceFailureCode.ExecutionFailed, "The image job metadata could not be read.");
        return await ReadInlineJsonAsync(response, token).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadInlineJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        using Stream source = await SdkHttpConsumption.ReadAsStreamAsync(response.Content, token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        await CopyInlineBoundedAsync(source, bytes, token).ConfigureAwait(false);
        SdkHttpConsumption.Fence(response.Content, token);
        return JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)));
    }

    public async ValueTask DisposeAsync()
    {
        ComfyInlineJob[] jobs;
        lock (_scopeGate)
        {
            _disposed = true;
            jobs = _inlineJobs.Values.ToArray();
        }
        await Task.WhenAll(jobs.Select(job => job.ReleaseLocalAsync())).ConfigureAwait(false);
        lock (_scopeGate) _inlineJobs.Clear();
    }
}
