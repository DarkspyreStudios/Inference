using System.Globalization;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.Image.Internal;
using Microsoft.Extensions.Logging;

namespace Darkspyre.Inference.Connect.Image.Providers;

internal enum ComfySignalKind
{
    None,
    Executing,
    Progress,
    Finished,
    Failed
}

internal readonly record struct ComfySignal(
    ComfySignalKind Kind,
    string? PromptId,
    string Error = "",
    int? Value = null,
    int? Maximum = null);

internal sealed record ComfyWorkflowConfiguration(
    string OutputNodeId,
    string FilenamePrefixTemplate,
    JsonElement DefaultParameters,
    JsonElement Workflow);

internal sealed class ComfyProgressConnection(ClientWebSocket socket) : IImageTransportProgressConnection
{
    public async Task<ImageTransportProgress?> ReceiveAsync(
        string providerJobId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerJobId);
        byte[] buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text || message.Length == 0)
            {
                continue;
            }

            ComfySignal signal = ComfyUiImageTransport.InterpretFrame(
                Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length)));
            if (!string.Equals(signal.PromptId, providerJobId, StringComparison.Ordinal)
                || signal.Kind == ComfySignalKind.None)
            {
                continue;
            }

            return signal.Kind switch
            {
                ComfySignalKind.Executing => new ImageTransportProgress(ImageTransportProgressKind.Executing),
                ComfySignalKind.Progress => new ImageTransportProgress(
                    ImageTransportProgressKind.Progress,
                    Value: signal.Value,
                    Maximum: signal.Maximum),
                ComfySignalKind.Finished => new ImageTransportProgress(ImageTransportProgressKind.Finished),
                ComfySignalKind.Failed => new ImageTransportProgress(
                    ImageTransportProgressKind.Failed,
                    signal.Error),
                _ => null
            };
        }

        return null;
    }

    public ValueTask DisposeAsync()
    {
        socket.Abort();
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class ComfyUiImageTransport(
    IHttpClientFactory httpClientFactory,
    ILogger<ComfyUiImageTransport> logger) : IImageTransport
{
    private static readonly Action<ILogger, string, Exception?> LogCancellationFailure = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(1, "ImageCancellationFailed"),
        "Could not cancel ComfyUI prompt {PromptId}.");
    private static readonly string[] RequiredPlaceholders =
    [
        "${positivePrompt}",
        "${width}",
        "${height}",
        "${seed}"
    ];

    private readonly string _clientId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly object _scopeGate = new();
    private readonly ConditionalWeakTable<ImageConnection, ScopeEntry> _scopes = new();

    public ImageTransportDescriptor Descriptor { get; } = new(
        ImageTransport.ComfyUi,
        "ComfyUI",
        "ComfyUI workflow server with reference-image upload and live progress support.",
        new ImageTransportCapabilities(
            SupportsProgress: InferenceSupport.Supported,            SupportsReferenceImages: InferenceSupport.Supported,            SupportsCancellation: InferenceSupport.Supported));

    public Task<IReadOnlyList<ImageWorkflow>> GetWorkflowsAsync(
        ImageConnection connection, XaiFamilyBinding? xai = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConnection(connection, xai);
        throw new InferenceException(InferenceFailureCode.UnsupportedFeature,
            "ComfyUI workflows are supplied by the host; built-in workflow discovery is unavailable.");
    }

    public Task<ImageCapabilities> GetCapabilitiesAsync(
        ImageConnection connection, ImageWorkflow workflow, string? modelId = null,
        XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConnection(connection, xai);
        ArgumentNullException.ThrowIfNull(workflow);
        if (modelId is not null && !string.Equals(modelId, workflow.Id, StringComparison.Ordinal))
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The requested workflow is not bound.");
        ValidateConfiguration(workflow, ReadConfiguration(workflow));
        return Task.FromResult(new ImageCapabilities
        {
            SupportedSizes = [new(workflow.DefaultWidth, workflow.DefaultHeight)],
            SupportsNegativePrompt = InferenceSupport.Supported,
            SupportsSeed = InferenceSupport.Supported,
            MaximumImageCount = 1,
            SupportedReferenceKinds = new[] { InferenceMediaSourceKind.Binary, InferenceMediaSourceKind.Resource }.ToFrozenSet(),
            MaximumReferences = 1
        });
    }

    public PreparedImageTransportRequest Prepare(ImageTransportPreparationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePreparation(request);
        ComfyWorkflowConfiguration configuration = ReadConfiguration(request.Workflow);
        ValidateConfiguration(request.Workflow, configuration);

        ImagePreparedReference[] references = request.References.ToArray();
        string referenceName = references.Length == 0 ? string.Empty : ReadUploadName(references[0].ProviderState);
        var values = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["workflowId"] = request.Workflow.Id,
            ["workflowVersion"] = request.Workflow.Version,
            ["positivePrompt"] = request.PositivePrompt,
            ["negativePrompt"] = request.NegativePrompt ?? request.Workflow.DefaultNegativePrompt,
            ["width"] = request.Width ?? request.Workflow.DefaultWidth,
            ["height"] = request.Height ?? request.Workflow.DefaultHeight,
            ["seed"] = request.Seed ?? Random.Shared.NextInt64(),
            ["count"] = request.Count,
            ["cacheKey"] = request.CacheKey,
            ["referenceImageName"] = referenceName,
            ["label"] = request.Label
        };
        foreach (JsonProperty parameter in configuration.DefaultParameters.EnumerateObject())
        {
            if (!values.TryAdd(parameter.Name, JsonElementValue(parameter.Value)))
            {
                throw new InvalidOperationException(
                    $"Workflow '{request.Workflow.Label}' defines reserved parameter '{parameter.Name}'.");
            }
        }

        values["filenamePrefix"] = ComfyWorkflowRenderer.RenderText(
            configuration.FilenamePrefixTemplate,
            values);
        values["fileName"] = $"{request.CacheKey}.{request.Workflow.OutputExtension.TrimStart('.')}";
        JsonObject renderedWorkflow = ComfyWorkflowRenderer.Render(configuration.Workflow, values);
        return new PreparedImageTransportRequest(JsonSerializer.Serialize(
            new ComfyPreparedEnvelope(JsonElement.Parse(renderedWorkflow.ToJsonString()),
                Array.AsReadOnly(references.Select(reference => reference.Scope).ToArray())),
            ComfyOutputJsonContext.Default.ComfyPreparedEnvelope));
    }

    public async Task<ImageProviderHealth> TestAsync(
        ImageConnection connection,
        ImageWorkflow? workflow = null,
        XaiFamilyBinding? xai = null,
        CancellationToken cancellationToken = default)
    {
        connection = CaptureConnection(connection, xai);
        using var consumption = SdkHttpConsumption.Enter(httpClientFactory, cancellationToken); cancellationToken = consumption.Token;
        return consumption.Publish(await ReadHealthAsync().ConfigureAwait(false));

        async Task<ImageProviderHealth> ReadHealthAsync()
        {
            try
            {
                using HttpClient client = CreateClient(connection);
                using HttpResponseMessage response = await client.GetAsync(
                    BuildUri(connection, "system_stats"),
                    cancellationToken);
                string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return new ImageProviderHealth(
                        false,
                        $"ComfyUI returned HTTP {(int)response.StatusCode}.",
                        SerializeDiagnostics(response.StatusCode.ToString(), body));
                }

                if (workflow is null)
                {
                    return new ImageProviderHealth(true, "ComfyUI is reachable.");
                }

                ComfyWorkflowConfiguration configuration = ReadConfiguration(workflow);
                ValidateConfiguration(workflow, configuration);
                using HttpResponseMessage nodeResponse = await client.GetAsync(
                    BuildUri(connection, "object_info"),
                    cancellationToken);
                string nodeBody = await SdkHttpConsumption.ReadAsStringAsync(nodeResponse.Content, cancellationToken);
                if (!nodeResponse.IsSuccessStatusCode)
                {
                    return new ImageProviderHealth(
                        false,
                        $"ComfyUI is reachable, but workflow discovery returned HTTP {(int)nodeResponse.StatusCode}.",
                        SerializeDiagnostics(nodeResponse.StatusCode.ToString(), nodeBody));
                }

                using JsonDocument objectInfo = JsonDocument.Parse(nodeBody);
                string[] requiredNodeTypes = configuration.Workflow.EnumerateObject()
                    .Select(node => node.Value.TryGetProperty("class_type", out JsonElement classType)
                        ? classType.GetString()
                        : null)
                    .Where(classType => !string.IsNullOrWhiteSpace(classType))
                    .Select(classType => classType!)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                string[] missingNodeTypes = requiredNodeTypes
                    .Where(classType => !objectInfo.RootElement.TryGetProperty(classType, out _))
                    .ToArray();
                string diagnostics = JsonSerializer.Serialize(new ComfyWorkflowDiagnostics(workflow.Id, workflow.Version,
                    requiredNodeTypes, missingNodeTypes), ComfyProtocolJsonContext.Default.ComfyWorkflowDiagnostics);
                return missingNodeTypes.Length == 0
                    ? new ImageProviderHealth(
                        true,
                        $"ComfyUI is reachable and supports every node in '{workflow.Label}'.",
                        diagnostics)
                    : new ImageProviderHealth(
                        false,
                        $"ComfyUI is missing workflow nodes: {string.Join(", ", missingNodeTypes)}.",
                        diagnostics);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                return new ImageProviderHealth(false, exception.Message);
            }
        }
    }

    public async Task<IImageTransportProgressConnection?> TryConnectProgressAsync(
        ImageConnection connection,
        XaiFamilyBinding? xai = null,
        CancellationToken cancellationToken = default)
    {
        connection = CaptureConnection(connection, xai);
        var socket = new ClientWebSocket();
        ApplyHeaders(socket.Options, connection);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await socket.ConnectAsync(BuildWebSocketUri(connection), timeout.Token);
            return new ComfyProgressConnection(socket);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            return null;
        }
        catch (WebSocketException)
        {
            socket.Dispose();
            return null;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task<ImagePreparedReference> UploadReferenceAsync(
        ImageConnection connection,
        ImageReference reference,
        Stream content,
        XaiFamilyBinding? xai = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (InferenceResourceScope scope, connection) = Admit(connection, xai);
        using var consumption = SdkHttpConsumption.Enter(httpClientFactory, cancellationToken); cancellationToken = consumption.Token;
        return consumption.Publish<ImagePreparedReference>(await UploadAdmittedAsync(connection, reference, content, scope, cancellationToken).ConfigureAwait(false));
    }

    private async Task<ImagePreparedReference> UploadAdmittedAsync(
        ImageConnection connection, ImageReference reference, Stream content,
        InferenceResourceScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The reference image stream must be readable.", nameof(content));
        }

        string uploadName = BuildReferenceName(reference);
        using var consumption = SdkHttpConsumption.Enter(httpClientFactory, cancellationToken); cancellationToken = consumption.Token;
        ImagePreparedReference result;
        {
            using HttpClient client = CreateClient(connection);
            using var form = new MultipartFormDataContent();
            using var image = new BorrowedStreamContent(content);
            if (reference.ContentType is not null)
                image.Headers.ContentType = new MediaTypeHeaderValue(reference.ContentType);
            form.Add(image, "image", uploadName);
            form.Add(new StringContent("true"), "overwrite");
            using HttpResponseMessage response = await client.PostAsync(
                BuildUri(connection, "upload/image"),
                form,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken);
                throw new InvalidOperationException(
                    $"ComfyUI rejected the reference image with HTTP {(int)response.StatusCode}: {ReadError(body)}");
            }
            string responseBody = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ComfyUploadImage uploaded = JsonSerializer.Deserialize(responseBody, ComfyOutputJsonContext.Default.ComfyUploadImage)
                ?? throw new InferenceException(InferenceFailureCode.ExecutionFailed, "ComfyUI returned no uploaded image identity.");
            _ = ReadUploadName(JsonSerializer.SerializeToElement(uploaded, ComfyOutputJsonContext.Default.ComfyUploadImage));
            result = new(reference with { FileName = uploaded.Name }, scope,
                JsonSerializer.SerializeToElement(uploaded, ComfyOutputJsonContext.Default.ComfyUploadImage));
        }
        return consumption.Publish(result);
    }

    public async Task<ImagePreparedReference> ResolveReferenceAsync(
        ImageConnection connection, InferenceMediaSource source,
        XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConnection(connection, xai);
        ArgumentNullException.ThrowIfNull(source);
        if (source is not (BinaryMediaSource or ResourceMediaSource))
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "ComfyUI accepts binary or logical resource reference images only.");
        if (source is ResourceMediaSource && resources is null)
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The resource source requires caller-owned access.");
        (InferenceResourceScope scope, connection) = Admit(connection, xai);
        using var consumption = SdkHttpConsumption.Enter(httpClientFactory, cancellationToken); cancellationToken = consumption.Token;
        if (source is BinaryMediaSource binary)
        {
            if (resources is null)
            {
                ImagePreparedReference result;
                {
                    using var stream = new MemoryStream(binary.Data.ToArray(), writable: false);
                    result = await UploadAdmittedAsync(connection, new ImageReference
                    { Id = Guid.NewGuid(), FileName = binary.FileName, ContentType = binary.ContentType },
                        stream, scope, cancellationToken).ConfigureAwait(false);
                }
                return consumption.Publish(result);
            }
            InferenceResourceCommitResult committed = await ImageResourceOperations.CommitAsync(resources,
                binary.FileName, binary.ContentType, binary.Data, _ => consumption.Fence(), token: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            consumption.Fence();
            source = new ResourceMediaSource(committed.Resource.Reference);
        }
        if (source is ResourceMediaSource resource)
        {
            if (resources is null)
                throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The resource source requires caller-owned access.");
            IInferenceResourceReadHandle? reader = null;
            ImagePreparedReference? result = null;
            Exception? failure = null;
            InferenceResourceOperationPhase phase = InferenceResourceOperationPhase.OpenRead;
            long? bytesTransferred = null;
            string? workspaceId = null;
            string? sessionId = null;
            try
            {
                workspaceId = resources.WorkspaceId;
                cancellationToken.ThrowIfCancellationRequested(); consumption.Fence();
                sessionId = resources.SessionId;
                CheckReadScope();
                if (resource.Resource.WorkspaceId != workspaceId) throw InvalidReadScope();
                reader = await resources!.OpenReadAsync(resource.Resource, cancellationToken).ConfigureAwait(false);
                CheckReadScope();
                if (reader is null) throw InvalidReadScope();
                InferenceResourceDescriptor descriptor = reader.Resource;
                CheckReadScope();
                if (descriptor is null || descriptor.Reference != resource.Resource
                    || descriptor.SessionId is { } producerSession && producerSession != sessionId
                    || descriptor.Bytes is < 0)
                    throw new InferenceException(InferenceFailureCode.ProtocolFailure, "Resource access returned a different content identity.");
                Stream sourceContent = reader.Content;
                CheckReadScope();
                if (sourceContent is null || !sourceContent.CanRead) throw InvalidReadScope();
                CheckReadScope();
                using var content = new MemoryStream();
                phase = InferenceResourceOperationPhase.Read;
                try { await sourceContent.CopyToAsync(content, cancellationToken).ConfigureAwait(false); }
                catch (Exception) { bytesTransferred = content.Length; throw; }
                bytesTransferred = content.Length;
                CheckReadScope();
                if (descriptor.Bytes is { } expectedBytes && expectedBytes != content.Length) throw InvalidReadScope();
                content.Position = 0;
                phase = InferenceResourceOperationPhase.Materialize;
                bytesTransferred = null;
                ImagePreparedReference receipt = await UploadAdmittedAsync(connection, new ImageReference
                { Id = Guid.NewGuid(), FileName = descriptor.FileName, ContentType = descriptor.ContentType },
                    content, scope, cancellationToken).ConfigureAwait(false);
                CheckReadScope();
                result = receipt with { Resource = resource.Resource };
            }
            catch (Exception error)
            {
                var facts = new InferenceResourceOperationFailure(phase, resource.Resource, InferenceResourceCommitOutcome.NotAttempted, bytesTransferred);
                failure = error switch
                {
                    InferenceResourceOperationException or InferenceResourceOperationCanceledException => error,
                    OperationCanceledException canceled => new InferenceResourceOperationCanceledException(facts, canceled.CancellationToken, error),
                    _ => new InferenceResourceOperationException(error is InferenceException provider ? provider.Code : InferenceFailureCode.ExecutionFailed,
                        "The image reference resource operation failed.", facts, error)
                };
            }
            if (reader is not null)
            {
                try { await reader.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup)
                {
                    var facts = new InferenceResourceOperationFailure(InferenceResourceOperationPhase.Cleanup, resource.Resource,
                        InferenceResourceCommitOutcome.NotAttempted, bytesTransferred);
                    Exception refused = LlmResourcePreparation.AsCleanupFailure(LlmResourcePreparation.IsCleanupFailure(cleanup) ? cleanup
                        : new InferenceResourceOperationException(InferenceFailureCode.ExecutionFailed, "The image reference resource cleanup failed.", facts, cleanup),
                        resource.Resource, reader);
                    if (failure is not null) throw new AggregateException(failure, refused);
                    throw refused;
                }
            }
            if (failure is not null) ExceptionDispatchInfo.Throw(failure);
            try { CheckReadScope(); }
            catch (Exception error)
            {
                var facts = new InferenceResourceOperationFailure(phase, resource.Resource, InferenceResourceCommitOutcome.NotAttempted, bytesTransferred);
                throw error is OperationCanceledException canceled ? new InferenceResourceOperationCanceledException(facts, canceled.CancellationToken, error)
                    : new InferenceResourceOperationException(error is InferenceException provider ? provider.Code : InferenceFailureCode.ExecutionFailed,
                        "The image reference scope changed before publication.", facts, error);
            }
            return consumption.Publish<ImagePreparedReference>(result!);

            void CheckReadScope()
            {
                cancellationToken.ThrowIfCancellationRequested(); consumption.Fence();
                if (string.IsNullOrEmpty(workspaceId) || string.IsNullOrEmpty(sessionId)) throw InvalidReadScope();
                string currentWorkspace = resources.WorkspaceId;
                cancellationToken.ThrowIfCancellationRequested(); consumption.Fence();
                if (currentWorkspace != workspaceId) throw InvalidReadScope();
                string currentSession = resources.SessionId;
                cancellationToken.ThrowIfCancellationRequested(); consumption.Fence();
                if (currentSession != sessionId) throw InvalidReadScope();
            }
            static InferenceException InvalidReadScope() => new(InferenceFailureCode.ProtocolFailure, "The image reference scope or content does not match the admitted resource.");
        }
        throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "ComfyUI accepts binary or logical resource reference images only.");
    }

    public async Task<string> SubmitAsync(
        ImageConnection connection,
        PreparedImageTransportRequest request,
        XaiFamilyBinding? xai = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (InferenceResourceScope scope, connection) = Admit(connection, xai);
        ArgumentNullException.ThrowIfNull(request);
        ComfyPreparedEnvelope envelope = JsonSerializer.Deserialize(request.ProviderRequest, ComfyOutputJsonContext.Default.ComfyPreparedEnvelope)
            ?? throw new InvalidOperationException("The prepared ComfyUI request is empty.");
        ArgumentNullException.ThrowIfNull(envelope.ReferenceScopes);
        foreach (InferenceResourceScope referenceScope in envelope.ReferenceScopes)
            ValidateScope(referenceScope, scope);
        JsonObject workflow = JsonNode.Parse(envelope.Workflow.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException("The prepared ComfyUI workflow is empty.");
        using var consumption = SdkHttpConsumption.Enter(httpClientFactory, cancellationToken); cancellationToken = consumption.Token;
        string result;
        {
            using HttpClient client = CreateClient(connection);
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                BuildUri(connection, "prompt"),
                new ComfyPromptRequest(workflow, _clientId), ComfyProtocolJsonContext.Default.ComfyPromptRequest,
                cancellationToken);
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"ComfyUI rejected the workflow with HTTP {(int)response.StatusCode}: {ReadError(body)}");
            }

            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("prompt_id", out JsonElement promptId)
                && promptId.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(promptId.GetString()))
            {
                result = promptId.GetString()!;
            }
            else
                throw new InvalidOperationException("ComfyUI did not return a prompt id.");
        }
        return consumption.Publish(result);
    }

    public async Task<ImageTransportJobSnapshot> GetJobAsync(
        ImageConnection connection,
        string providerJobId,
        ImageWorkflow workflow,
        XaiFamilyBinding? xai = null,
        CancellationToken cancellationToken = default)
    {
        (InferenceResourceScope scope, connection) = Admit(connection, xai);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerJobId);
        ArgumentNullException.ThrowIfNull(workflow);
        ComfyWorkflowConfiguration configuration = ReadConfiguration(workflow);
        using var consumption = SdkHttpConsumption.Enter(httpClientFactory, cancellationToken); cancellationToken = consumption.Token;
        return consumption.Publish(await ReadJobAsync().ConfigureAwait(false));

        async Task<ImageTransportJobSnapshot> ReadJobAsync()
        {
            using HttpClient client = CreateClient(connection);
            using HttpResponseMessage historyResponse = await client.GetAsync(
                BuildUri(connection, $"history/{Uri.EscapeDataString(providerJobId)}"),
                cancellationToken);
            historyResponse.EnsureSuccessStatusCode();
            string historyBody = await SdkHttpConsumption.ReadAsStringAsync(historyResponse.Content, cancellationToken);
            using JsonDocument history = JsonDocument.Parse(historyBody);
            if (history.RootElement.TryGetProperty(providerJobId, out JsonElement promptHistory))
            {
                string error = FindExecutionError(promptHistory);
                if (error.Length > 0)
                {
                    return new ImageTransportJobSnapshot(
                        ImageTransportJobState.Failed,
                        Error: new InferenceResourceFailure(InferenceFailureCode.ExecutionFailed, null, error),
                        Diagnostics: JsonSerializer.SerializeToElement(new ComfyHistoryDiagnostics("history", "failed"), ComfyProtocolJsonContext.Default.ComfyHistoryDiagnostics));
                }

                ReadOnlyCollection<ComfyOutputImage> outputs = FindOutputs(promptHistory, configuration.OutputNodeId);
                if (outputs.Count > 0)
                {
                    return new ImageTransportJobSnapshot(
                        ImageTransportJobState.Completed,
                        Array.AsReadOnly(outputs.Select(output => new ImageTransportOutput(output.FileName, scope,
                            JsonSerializer.SerializeToElement(output, ComfyOutputJsonContext.Default.ComfyOutputImage))).ToArray()),
                        Diagnostics: JsonSerializer.SerializeToElement(new ComfyHistoryDiagnostics("history", "completed"), ComfyProtocolJsonContext.Default.ComfyHistoryDiagnostics));
                }

                if (promptHistory.TryGetProperty("status", out JsonElement status)
                    && status.TryGetProperty("completed", out JsonElement completed) && completed.ValueKind == JsonValueKind.True)
                    return new ImageTransportJobSnapshot(ImageTransportJobState.Completed, Array.Empty<ImageTransportOutput>());

                return new ImageTransportJobSnapshot(
                    ImageTransportJobState.Generating,
                    Diagnostics: JsonSerializer.SerializeToElement(new ComfyHistoryDiagnostics("history", "generating"), ComfyProtocolJsonContext.Default.ComfyHistoryDiagnostics));
            }

            using HttpResponseMessage queueResponse = await client.GetAsync(
                BuildUri(connection, "queue"),
                cancellationToken);
            queueResponse.EnsureSuccessStatusCode();
            string queueBody = await SdkHttpConsumption.ReadAsStringAsync(queueResponse.Content, cancellationToken);
            using JsonDocument queue = JsonDocument.Parse(queueBody);
            bool running = QueueContains(queue.RootElement, "queue_running", providerJobId);
            bool pending = QueueContains(queue.RootElement, "queue_pending", providerJobId);
            return new ImageTransportJobSnapshot(
                running ? ImageTransportJobState.Generating : ImageTransportJobState.Queued,
                Error: pending || running ? null : new InferenceResourceFailure(InferenceFailureCode.ExecutionFailed, null,
                    "The prompt is not present in ComfyUI history or queue."),
                Diagnostics: JsonSerializer.SerializeToElement(new ComfyQueueDiagnostics("queue", running, pending), ComfyProtocolJsonContext.Default.ComfyQueueDiagnostics));
        }
    }

    public async Task<ImageTransportDownload> DownloadAsync(
        ImageConnection connection,
        ImageTransportOutput output,
        XaiFamilyBinding? xai = null,
        CancellationToken cancellationToken = default)
    {
        (InferenceResourceScope scope, connection) = Admit(connection, xai);
        ArgumentNullException.ThrowIfNull(output);
        ValidateScope(output.Scope, scope);
        ComfyOutputImage comfyOutput = JsonSerializer.Deserialize(output.ProviderState, ComfyOutputJsonContext.Default.ComfyOutputImage)
            ?? throw new InvalidOperationException("The ComfyUI output state is invalid.");
        string query = $"view?filename={Uri.EscapeDataString(comfyOutput.FileName)}" +
            $"&subfolder={Uri.EscapeDataString(comfyOutput.Subfolder)}" +
            $"&type={Uri.EscapeDataString(comfyOutput.Type)}";
        using var consumption = SdkHttpConsumption.Enter(httpClientFactory, cancellationToken); cancellationToken = consumption.Token;
        ImageTransportDownload result;
        {
            using HttpClient client = CreateClient(connection);
            using HttpResponseMessage response = await client.GetAsync(BuildUri(connection, query), cancellationToken);
            response.EnsureSuccessStatusCode();
            result = new ImageTransportDownload(response.Content.Headers.ContentType?.MediaType,
                await SdkHttpConsumption.ReadAsByteArrayAsync(response.Content, cancellationToken));
        }
        return consumption.Publish(result);
    }

    public async Task<bool> TryCancelAsync(
        ImageConnection connection,
        string providerJobId,
        bool interruptRunning,
        XaiFamilyBinding? xai = null,
        CancellationToken cancellationToken = default)
    {
        connection = CaptureConnection(connection, xai);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerJobId);
        using var consumption = SdkHttpConsumption.Enter(httpClientFactory, cancellationToken); cancellationToken = consumption.Token;
        return consumption.Publish(await CancelAsync().ConfigureAwait(false));

        async Task<bool> CancelAsync()
        {
            try
            {
                using HttpClient client = CreateClient(connection);
                using HttpResponseMessage queueResponse = await client.PostAsJsonAsync(
                    BuildUri(connection, "queue"),
                    new ComfyDeleteRequest([providerJobId]), ComfyProtocolJsonContext.Default.ComfyDeleteRequest,
                    cancellationToken);
                queueResponse.EnsureSuccessStatusCode();
                if (interruptRunning)
                {
                    using HttpResponseMessage interruptResponse = await client.PostAsync(
                        BuildUri(connection, "interrupt"),
                        content: null,
                        cancellationToken);
                    interruptResponse.EnsureSuccessStatusCode();
                }

                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogCancellationFailure(logger, providerJobId, exception);
                return false;
            }
        }
    }

    internal static ComfySignal InterpretFrame(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out JsonElement typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Object)
            {
                return new ComfySignal(ComfySignalKind.None, null);
            }

            string? promptId = data.TryGetProperty("prompt_id", out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(promptId))
            {
                return new ComfySignal(ComfySignalKind.None, null);
            }

            return typeElement.GetString() switch
            {
                "executing" when data.TryGetProperty("node", out JsonElement node)
                    && node.ValueKind == JsonValueKind.Null =>
                    new ComfySignal(ComfySignalKind.Finished, promptId),
                "executing" => new ComfySignal(ComfySignalKind.Executing, promptId),
                "progress" => new ComfySignal(
                    ComfySignalKind.Progress,
                    promptId,
                    Value: ReadInteger(data, "value"),
                    Maximum: ReadInteger(data, "max")),
                "execution_success" => new ComfySignal(ComfySignalKind.Finished, promptId),
                "execution_error" => new ComfySignal(
                    ComfySignalKind.Failed,
                    promptId,
                    data.TryGetProperty("exception_message", out JsonElement error)
                        && error.ValueKind == JsonValueKind.String
                            ? error.GetString() ?? "ComfyUI execution error."
                            : "ComfyUI execution error."),
                _ => new ComfySignal(ComfySignalKind.None, promptId)
            };
        }
        catch (JsonException)
        {
            return new ComfySignal(ComfySignalKind.None, null);
        }
    }

    private static ComfyWorkflowConfiguration ReadConfiguration(ImageWorkflow workflow)
    {
        if (workflow.Transport != ImageTransport.ComfyUi)
        {
            throw new InvalidOperationException(
                $"Workflow '{workflow.Label}' targets '{workflow.Transport}', not ComfyUI.");
        }

        JsonElement root = workflow.TransportConfiguration;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Workflow '{workflow.Label}' has no ComfyUI configuration.");
        }

        return new ComfyWorkflowConfiguration(
            ReadRequiredString(root, "outputNodeId", workflow),
            ReadRequiredString(root, "filenamePrefixTemplate", workflow),
            ReadRequiredObject(root, "defaultParameters", workflow),
            ReadRequiredObject(root, "workflow", workflow));
    }

    private static void ValidateConfiguration(
        ImageWorkflow workflow,
        ComfyWorkflowConfiguration configuration)
    {
        string graph = configuration.Workflow.GetRawText();
        foreach (string placeholder in RequiredPlaceholders)
        {
            if (!graph.Contains(placeholder, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Workflow '{workflow.Label}' is missing required placeholder '{placeholder}'.");
            }
        }

        if (workflow.RequiresReferenceImage
            && !graph.Contains("${referenceImageName}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Workflow '{workflow.Label}' requires a reference image but has no reference-image placeholder.");
        }
    }

    private static void ValidatePreparation(ImageTransportPreparationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Workflow);
        ArgumentNullException.ThrowIfNull(request.References);
        if (request.Count != 1 || request.Steps is not null || request.GuidanceScale is not null
            || request.References.Count > 1)
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature,
                "ComfyUI supports one image and at most one reference, without step or guidance overrides.");
        if ((request.Width ?? request.Workflow.DefaultWidth) <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Image width must be positive.");
        }

        if ((request.Height ?? request.Workflow.DefaultHeight) <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Image height must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.PositivePrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CacheKey);
        if (request.Workflow.RequiresReferenceImage && request.References.Count == 0)
        {
            throw new InvalidOperationException(
                $"Workflow '{request.Workflow.Label}' requires a reference image.");
        }
    }

    private static void ValidateConnection(ImageConnection connection, XaiFamilyBinding? xai = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Authorization is not null || connection.Xai is not null || xai is not null)
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature,
                "ComfyUI uses static protected request headers; dynamic authorization and xAI bindings are not qualified for this transport.");
        if (connection.Transport != ImageTransport.ComfyUi)
        {
            throw new InvalidOperationException(
                $"Connection targets '{connection.Transport}', not ComfyUI.");
        }

        if (!Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out Uri? endpoint)
            || (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
        {
            throw new ArgumentException("ComfyUI requires an absolute HTTP or HTTPS endpoint.", nameof(connection));
        }

        if (connection.RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(connection), "Request timeout must be positive.");
        }
    }

    private static ImageConnection CaptureConnection(ImageConnection connection, XaiFamilyBinding? xai)
    {
        ValidateConnection(connection, xai);
        ArgumentNullException.ThrowIfNull(connection.AdditionalHeaders);
        return connection with
        {
            Endpoint = new Uri(connection.Endpoint.TrimEnd('/') + "/").AbsoluteUri,
            AdditionalHeaders = new ReadOnlyDictionary<string, string>(
                connection.AdditionalHeaders.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase))
        };
    }

    private (InferenceResourceScope Scope, ImageConnection Snapshot) Admit(ImageConnection connection, XaiFamilyBinding? xai)
    {
        ImageConnection snapshot = CaptureConnection(connection, xai);
        lock (_scopeGate)
        {
            if (_scopes.TryGetValue(connection, out ScopeEntry? entry)
                && entry.Endpoint == snapshot.Endpoint
                && entry.Headers.Count == snapshot.AdditionalHeaders.Count
                && entry.Headers.All(header => snapshot.AdditionalHeaders.TryGetValue(header.Key, out string? value) && value == header.Value))
                return (entry.Scope, snapshot);
            _scopes.Remove(connection);
            var scope = new InferenceResourceScope(snapshot.Endpoint, Guid.NewGuid().ToString("N"));
            _scopes.Add(connection, new ScopeEntry(snapshot.Endpoint, snapshot.AdditionalHeaders, scope));
            return (scope, snapshot);
        }
    }

    private static void ValidateScope(InferenceResourceScope observed, InferenceResourceScope expected)
    {
        if (observed != expected)
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature,
                "The native image receipt belongs to a different or changed transport owner. Reupload logical content under the current connection.");
    }

    private static string ReadUploadName(JsonElement state)
    {
        ComfyUploadImage uploaded = JsonSerializer.Deserialize(state, ComfyOutputJsonContext.Default.ComfyUploadImage)
            ?? throw new InvalidOperationException("The uploaded ComfyUI image state is invalid.");
        if (string.IsNullOrWhiteSpace(uploaded.Name) || uploaded.Subfolder is null || uploaded.Type is not "input")
            throw new InvalidOperationException("The uploaded ComfyUI image identity is invalid.");
        return uploaded.Subfolder.Length == 0 ? uploaded.Name : uploaded.Subfolder.TrimEnd('/') + "/" + uploaded.Name;
    }

    private sealed record ScopeEntry(string Endpoint, IReadOnlyDictionary<string, string> Headers, InferenceResourceScope Scope);

    private static string ReadRequiredString(
        JsonElement root,
        string propertyName,
        ImageWorkflow workflow) =>
        root.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidOperationException(
                $"Workflow '{workflow.Label}' has no valid ComfyUI '{propertyName}' value.");

    private static JsonElement ReadRequiredObject(
        JsonElement root,
        string propertyName,
        ImageWorkflow workflow) =>
        root.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidOperationException(
                $"Workflow '{workflow.Label}' has no valid ComfyUI '{propertyName}' object.");

    private static object JsonElementValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number when value.TryGetInt64(out long integer) => integer,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidOperationException(
                "ComfyUI workflow default parameters must be strings, numbers, or booleans.")
        };

    private HttpClient CreateClient(ImageConnection connection)
    {
        HttpClient client = httpClientFactory.CreateClient();
        client.Timeout = connection.RequestTimeout;
        foreach ((string name, string value) in connection.AdditionalHeaders)
        {
            client.DefaultRequestHeaders.Add(name, value);
        }

        return client;
    }

    private static Uri BuildUri(ImageConnection connection, string relativePath) =>
        new(new Uri(connection.Endpoint.TrimEnd('/') + "/"), relativePath);

    private Uri BuildWebSocketUri(ImageConnection connection)
    {
        var endpoint = new Uri(connection.Endpoint.TrimEnd('/'));
        string scheme = string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? "wss"
            : "ws";
        return new UriBuilder(endpoint)
        {
            Scheme = scheme,
            Path = endpoint.AbsolutePath.TrimEnd('/') + "/ws",
            Query = $"clientId={Uri.EscapeDataString(_clientId)}"
        }.Uri;
    }

    private static void ApplyHeaders(ClientWebSocketOptions options, ImageConnection connection)
    {
        foreach ((string name, string value) in connection.AdditionalHeaders)
        {
            options.SetRequestHeader(name, value);
        }
    }

    private static string BuildReferenceName(ImageReference reference)
    {
        if (reference.Id == Guid.Empty)
        {
            throw new ArgumentException("A reference image id is required.", nameof(reference));
        }

        string extension = Path.GetExtension(reference.FileName) ?? string.Empty;
        if (extension.Length > 16 || extension.Skip(1).Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("The reference image has an invalid file extension.", nameof(reference));
        }

        return $"darkspyre-reference-{reference.Id:N}{extension.ToLowerInvariant()}";
    }

    private static int? ReadInteger(JsonElement data, string propertyName) =>
        data.TryGetProperty(propertyName, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : null;

    private static bool QueueContains(JsonElement root, string propertyName, string promptId)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement queue) || queue.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in queue.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array
                && item.GetArrayLength() > 1
                && item[1].ValueKind == JsonValueKind.String
                && string.Equals(item[1].GetString(), promptId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static ReadOnlyCollection<ComfyOutputImage> FindOutputs(JsonElement promptHistory, string outputNodeId)
    {
        if (!promptHistory.TryGetProperty("outputs", out JsonElement outputs)
            || outputs.ValueKind != JsonValueKind.Object
            || !outputs.TryGetProperty(outputNodeId, out JsonElement output)
            || !output.TryGetProperty("images", out JsonElement images)
            || images.ValueKind != JsonValueKind.Array)
        {
            return Array.AsReadOnly(Array.Empty<ComfyOutputImage>());
        }

        var result = new List<ComfyOutputImage>();
        foreach (JsonElement image in images.EnumerateArray())
        {
            if (image.TryGetProperty("filename", out JsonElement filename)
                && filename.ValueKind == JsonValueKind.String)
            {
                result.Add(new ComfyOutputImage(
                    filename.GetString() ?? string.Empty,
                    image.TryGetProperty("subfolder", out JsonElement subfolder)
                        ? subfolder.GetString() ?? string.Empty
                        : string.Empty,
                    image.TryGetProperty("type", out JsonElement type)
                        ? type.GetString() ?? "output"
                        : "output"));
            }
        }

        return result.AsReadOnly();
    }

    private static string FindExecutionError(JsonElement promptHistory)
    {
        if (promptHistory.TryGetProperty("status", out JsonElement status)
            && status.TryGetProperty("status_str", out JsonElement statusText)
            && string.Equals(statusText.GetString(), "error", StringComparison.OrdinalIgnoreCase))
        {
            return "ComfyUI reported an execution error.";
        }

        return string.Empty;
    }

    private static string ReadError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                return error.ValueKind == JsonValueKind.String
                    ? error.GetString() ?? "Unknown error"
                    : error.ToString();
            }
        }
        catch (JsonException)
        {
            // Fall through to the bounded plain-text response.
        }

        return body.Length <= 1_000 ? body : body[..1_000];
    }

    private static string SerializeDiagnostics(string status, string body) =>
        JsonSerializer.Serialize(new ComfyResponseDiagnostics(status, ReadError(body)), ComfyProtocolJsonContext.Default.ComfyResponseDiagnostics);

    private sealed class BorrowedStreamContent(Stream source) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            source.CopyToAsync(stream);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) =>
            source.CopyToAsync(stream, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
