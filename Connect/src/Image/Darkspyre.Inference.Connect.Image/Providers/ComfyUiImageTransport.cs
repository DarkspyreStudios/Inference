using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    public ImageTransportDescriptor Descriptor { get; } = new(
        ImageTransport.ComfyUi,
        "ComfyUI",
        "ComfyUI workflow server with reference-image upload and live progress support.",
        new ImageTransportCapabilities(
            SupportsProgress: true,
            SupportsReferenceImages: true,
            SupportsCancellation: true));

    public PreparedImageTransportRequest Prepare(ImageTransportPreparationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePreparation(request);
        ComfyWorkflowConfiguration configuration = ReadConfiguration(request.Workflow);
        ValidateConfiguration(request.Workflow, configuration);

        string referenceName = request.Reference is null
            ? string.Empty
            : BuildReferenceName(request.Reference);
        var values = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["workflowId"] = request.Workflow.Id,
            ["workflowVersion"] = request.Workflow.Version,
            ["positivePrompt"] = request.PositivePrompt,
            ["negativePrompt"] = request.NegativePrompt,
            ["width"] = request.Width,
            ["height"] = request.Height,
            ["seed"] = request.Seed,
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
        return new PreparedImageTransportRequest(renderedWorkflow.ToJsonString());
    }

    public async Task<ImageProviderHealth> TestAsync(
        ImageConnection connection,
        ImageWorkflow? workflow = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConnection(connection);
        try
        {
            using HttpClient client = CreateClient(connection);
            using HttpResponseMessage response = await client.GetAsync(
                BuildUri(connection, "system_stats"),
                cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
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
            string nodeBody = await nodeResponse.Content.ReadAsStringAsync(cancellationToken);
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

    public async Task<IImageTransportProgressConnection?> TryConnectProgressAsync(
        ImageConnection connection,
        CancellationToken cancellationToken = default)
    {
        ValidateConnection(connection);
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
    }

    public async Task UploadReferenceAsync(
        ImageConnection connection,
        ImageReference reference,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ValidateConnection(connection);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The reference image stream must be readable.", nameof(content));
        }

        string uploadName = BuildReferenceName(reference);
        using HttpClient client = CreateClient(connection);
        using var form = new MultipartFormDataContent();
        using var image = new StreamContent(content);
        image.Headers.ContentType = new MediaTypeHeaderValue(reference.ContentType);
        form.Add(image, "image", uploadName);
        form.Add(new StringContent("true"), "overwrite");
        using HttpResponseMessage response = await client.PostAsync(
            BuildUri(connection, "upload/image"),
            form,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"ComfyUI rejected the reference image with HTTP {(int)response.StatusCode}: {ReadError(body)}");
        }
    }

    public async Task<string> SubmitAsync(
        ImageConnection connection,
        PreparedImageTransportRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateConnection(connection);
        ArgumentNullException.ThrowIfNull(request);
        JsonObject workflow = JsonNode.Parse(request.ProviderRequest)?.AsObject()
            ?? throw new InvalidOperationException("The prepared ComfyUI workflow is empty.");
        using HttpClient client = CreateClient(connection);
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            BuildUri(connection, "prompt"),
            new ComfyPromptRequest(workflow, _clientId), ComfyProtocolJsonContext.Default.ComfyPromptRequest,
            cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
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
            return promptId.GetString()!;
        }

        throw new InvalidOperationException("ComfyUI did not return a prompt id.");
    }

    public async Task<ImageTransportJobSnapshot> GetJobAsync(
        ImageConnection connection,
        string providerJobId,
        ImageWorkflow workflow,
        CancellationToken cancellationToken = default)
    {
        ValidateConnection(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerJobId);
        ArgumentNullException.ThrowIfNull(workflow);
        ComfyWorkflowConfiguration configuration = ReadConfiguration(workflow);
        using HttpClient client = CreateClient(connection);
        using HttpResponseMessage historyResponse = await client.GetAsync(
            BuildUri(connection, $"history/{Uri.EscapeDataString(providerJobId)}"),
            cancellationToken);
        historyResponse.EnsureSuccessStatusCode();
        string historyBody = await historyResponse.Content.ReadAsStringAsync(cancellationToken);
        using JsonDocument history = JsonDocument.Parse(historyBody);
        if (history.RootElement.TryGetProperty(providerJobId, out JsonElement promptHistory))
        {
            string error = FindExecutionError(promptHistory);
            if (error.Length > 0)
            {
                return new ImageTransportJobSnapshot(
                    ImageTransportJobState.Failed,
                    Error: error,
                    Diagnostics: JsonSerializer.Serialize(new ComfyHistoryDiagnostics("history", "failed"), ComfyProtocolJsonContext.Default.ComfyHistoryDiagnostics));
            }

            ComfyOutputImage? output = FindOutput(promptHistory, configuration.OutputNodeId);
            if (output is not null)
            {
                return new ImageTransportJobSnapshot(
                    ImageTransportJobState.Completed,
                    new ImageTransportOutput(output.FileName, JsonSerializer.Serialize(output, ComfyOutputJsonContext.Default.ComfyOutputImage)),
                    Diagnostics: JsonSerializer.Serialize(new ComfyHistoryDiagnostics("history", "completed"), ComfyProtocolJsonContext.Default.ComfyHistoryDiagnostics));
            }

            return new ImageTransportJobSnapshot(
                ImageTransportJobState.Generating,
                Diagnostics: JsonSerializer.Serialize(new ComfyHistoryDiagnostics("history", "generating"), ComfyProtocolJsonContext.Default.ComfyHistoryDiagnostics));
        }

        using HttpResponseMessage queueResponse = await client.GetAsync(
            BuildUri(connection, "queue"),
            cancellationToken);
        queueResponse.EnsureSuccessStatusCode();
        string queueBody = await queueResponse.Content.ReadAsStringAsync(cancellationToken);
        using JsonDocument queue = JsonDocument.Parse(queueBody);
        bool running = QueueContains(queue.RootElement, "queue_running", providerJobId);
        bool pending = QueueContains(queue.RootElement, "queue_pending", providerJobId);
        return new ImageTransportJobSnapshot(
            running ? ImageTransportJobState.Generating : ImageTransportJobState.Queued,
            Error: pending || running ? string.Empty : "The prompt is not present in ComfyUI history or queue.",
            Diagnostics: JsonSerializer.Serialize(new ComfyQueueDiagnostics("queue", running, pending), ComfyProtocolJsonContext.Default.ComfyQueueDiagnostics));
    }

    public async Task<byte[]> DownloadAsync(
        ImageConnection connection,
        ImageTransportOutput output,
        CancellationToken cancellationToken = default)
    {
        ValidateConnection(connection);
        ArgumentNullException.ThrowIfNull(output);
        ComfyOutputImage comfyOutput = JsonSerializer.Deserialize(output.ProviderState, ComfyOutputJsonContext.Default.ComfyOutputImage)
            ?? throw new InvalidOperationException("The ComfyUI output state is invalid.");
        string query = $"view?filename={Uri.EscapeDataString(comfyOutput.FileName)}" +
            $"&subfolder={Uri.EscapeDataString(comfyOutput.Subfolder)}" +
            $"&type={Uri.EscapeDataString(comfyOutput.Type)}";
        using HttpClient client = CreateClient(connection);
        return await client.GetByteArrayAsync(BuildUri(connection, query), cancellationToken);
    }

    public async Task<bool> TryCancelAsync(
        ImageConnection connection,
        string providerJobId,
        bool interruptRunning,
        CancellationToken cancellationToken = default)
    {
        ValidateConnection(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerJobId);
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
        if (request.Width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Image width must be positive.");
        }

        if (request.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Image height must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.PositivePrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CacheKey);
        if (request.Workflow.RequiresReferenceImage && request.Reference is null)
        {
            throw new InvalidOperationException(
                $"Workflow '{request.Workflow.Label}' requires a reference image.");
        }
    }

    private static void ValidateConnection(ImageConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Transport != ImageTransport.ComfyUi)
        {
            throw new InvalidOperationException(
                $"Connection targets '{connection.Transport}', not ComfyUI.");
        }

        if (!Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out Uri? endpoint)
            || (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("ComfyUI requires an absolute HTTP or HTTPS endpoint.", nameof(connection));
        }

        if (connection.RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(connection), "Request timeout must be positive.");
        }
    }

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

        ArgumentException.ThrowIfNullOrWhiteSpace(reference.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.ContentType);
        string extension = Path.GetExtension(reference.FileName);
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

    private static ComfyOutputImage? FindOutput(JsonElement promptHistory, string outputNodeId)
    {
        if (!promptHistory.TryGetProperty("outputs", out JsonElement outputs)
            || outputs.ValueKind != JsonValueKind.Object
            || !outputs.TryGetProperty(outputNodeId, out JsonElement output)
            || !output.TryGetProperty("images", out JsonElement images)
            || images.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement image in images.EnumerateArray())
        {
            if (image.TryGetProperty("filename", out JsonElement filename)
                && filename.ValueKind == JsonValueKind.String)
            {
                return new ComfyOutputImage(
                    filename.GetString() ?? string.Empty,
                    image.TryGetProperty("subfolder", out JsonElement subfolder)
                        ? subfolder.GetString() ?? string.Empty
                        : string.Empty,
                    image.TryGetProperty("type", out JsonElement type)
                        ? type.GetString() ?? "output"
                        : "output");
            }
        }

        return null;
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
}
