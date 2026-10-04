using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.Image.Internal;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.Image.Providers;

/// <summary>Synchronous image generation through the selected xAI connection and its Files owner.</summary>
public sealed class XaiImagineImageTransport : IImageTransport, IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly string _issuer = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, Completed> _completed = new(StringComparer.Ordinal);
    private readonly List<(Exception Original, Exception Safe)> _cleanupFailures = [];

    public ImageTransportDescriptor Descriptor { get; } = new(ImageTransport.XaiImagine, "Grok Imagine",
        "xAI image generation and editing", new(false, true, false) { SupportsBuiltInWorkflows = true });

    public Task<IReadOnlyList<ImageWorkflow>> GetWorkflowsAsync(ImageConnection connection,
        XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<ImageWorkflow>>(connection, xai, async (captured, bound, snapshot, fence, token) =>
        {
            IReadOnlyList<LlmModel> models = await bound.GetModelsAsync(snapshot, ModelCatalogFamily.Image, fence, token).ConfigureAwait(false);
            fence();
            foreach (LlmModel model in models)
                if (Protected(model.Id, captured, snapshot) || Protected(model.DisplayName, captured, snapshot)) throw XaiMediaData.Malformed();
            return Array.AsReadOnly(models.Select(model => new ImageWorkflow
            {
                Id = model.Id,
                Label = model.DisplayName,
                Transport = ImageTransport.XaiImagine,
                TransportConfiguration = XaiMediaData.Element(new JsonObject { ["model"] = model.Id })
            }).ToArray());
        }, cancellationToken);

    public Task<ImageCapabilities> GetCapabilitiesAsync(ImageConnection connection, ImageWorkflow workflow,
        string? modelId = null, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        ValidateConnection(connection, xai);
        ValidateWorkflow(workflow);
        if (modelId is not null && modelId != workflow.Id) throw new ArgumentException("The image workflow is not bound to this model.", nameof(modelId));
        Fence(operation.Token, xai);
        return Task.FromResult(new ImageCapabilities
        {
            SupportedSizes = [],
            MaximumImageCount = 10,
            MaximumReferences = 5,
            SupportedReferenceKinds = new[] { InferenceMediaSourceKind.Binary, InferenceMediaSourceKind.Url,
                InferenceMediaSourceKind.ProviderFile, InferenceMediaSourceKind.Resource }.ToFrozenSet()
        });
    }

    public PreparedImageTransportRequest Prepare(ImageTransportPreparationRequest request)
    {
        using var operation = _lifetime.Enter(default);
        ArgumentNullException.ThrowIfNull(request);
        ValidateWorkflow(request.Workflow);
        JsonObject[] references = request.References.Select(reference =>
        {
            ArgumentNullException.ThrowIfNull(reference);
            if (reference.Scope.Identity != _issuer) throw new ArgumentException("The image reference belongs to another transport.", nameof(request));
            JsonObject state = JsonNode.Parse(reference.ProviderState.GetRawText()) as JsonObject ?? throw InvalidPreparation();
            state.Remove("generated_resources");
            return state;
        }).ToArray();
        (string route, JsonObject body) = XaiImageProtocol.Build(request, references);
        var scopes = new JsonArray(request.References.Select(reference => (JsonNode)new JsonObject
        { ["owner"] = reference.Scope.Owner, ["identity"] = reference.Scope.Identity }).ToArray());
        var envelope = new JsonObject
        {
            ["workflow"] = new JsonObject
            {
                ["id"] = request.Workflow.Id,
                ["version"] = request.Workflow.Version,
                ["requires_reference"] = request.Workflow.RequiresReferenceImage,
                ["configuration"] = JsonNode.Parse(request.Workflow.TransportConfiguration.GetRawText())
            },
            ["route"] = route,
            ["body"] = body,
            ["scopes"] = scopes
        };
        Fence(operation.Token);
        return new(envelope.ToJsonString());
    }

    public async Task<ImageProviderHealth> TestAsync(ImageConnection connection, ImageWorkflow? workflow = null,
        XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        if (workflow is not null) ValidateWorkflow(workflow);
        await GetWorkflowsAsync(connection, xai, operation.Token).ConfigureAwait(false);
        Fence(operation.Token, xai);
        return new(true, "The selected connection can read the image catalog.");
    }

    public Task<IImageTransportProgressConnection?> TryConnectProgressAsync(ImageConnection connection,
        XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        ValidateConnection(connection, xai);
        Fence(operation.Token, xai);
        return Task.FromResult<IImageTransportProgressConnection?>(null);
    }

    public async Task<ImagePreparedReference> UploadReferenceAsync(ImageConnection connection, ImageReference reference,
        Stream content, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        ArgumentNullException.ThrowIfNull(reference);
        ValidateMetadata(reference.FileName, reference.ContentType);
        XaiFileRequestData.Upload(content, UploadLabel(reference.FileName), null);
        return await RunAsync(connection, xai, async (captured, bound, snapshot, fence, token) =>
        {
            LlmProviderFile file = await UploadAsync(captured, bound, snapshot, reference, content, fence, token).ConfigureAwait(false);
            return Receipt(captured, reference, new JsonObject { ["file_id"] = file.Id });
        }, operation.Token).ConfigureAwait(false);
    }

    public Task<ImagePreparedReference> ResolveReferenceAsync(ImageConnection connection, InferenceMediaSource source,
        XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default)
    {
        ValidateSource(source, resources);
        if (source is BinaryMediaSource binary) source = binary with { Data = binary.Data.ToArray() };
        return RunAsync(connection, xai, (captured, bound, snapshot, fence, token) =>
            ResolveAsync(captured, bound, snapshot, source, resources, fence, token), cancellationToken);
    }

    [SuppressMessage("Design", "CA1031", Justification = "Named readers retain the original operation and each owned handle cleanup failure.")]
    private async Task<ImagePreparedReference> ResolveAsync(ImageConnection connection, XaiFamilyBinding bound,
        LlmConnection snapshot, InferenceMediaSource source, IInferenceResourceAccess? resources, Action fence, CancellationToken token)
    {
        if (source is UrlMediaSource url)
            return Receipt(connection, new() { Id = Guid.NewGuid() }, new JsonObject { ["url"] = url.Url.OriginalString });
        if (source is FileMediaSource native)
        {
            LlmProviderFile observed = await AuthorizeFileAsync(connection, bound, snapshot, native.File.FileId, fence, token).ConfigureAwait(false);
            return Receipt(connection, new() { Id = Guid.NewGuid(), FileName = observed.FileName }, new JsonObject { ["file_id"] = observed.Id });
        }
        if (source is BinaryMediaSource binary)
        {
            InferenceResourceCommitResult? committed = null;
            if (resources is not null)
                committed = await ImageResourceOperations.CommitAsync(resources, binary.FileName, binary.ContentType,
                    binary.Data, _ => fence(), token: token).ConfigureAwait(false);
            var metadata = new ImageReference { Id = Guid.NewGuid(), FileName = binary.FileName, ContentType = binary.ContentType };
            using var content = new MemoryStream(binary.Data.ToArray(), writable: false);
            try
            {
                LlmProviderFile uploaded = await UploadAsync(connection, bound, snapshot, metadata, content, fence, token).ConfigureAwait(false);
                return Receipt(connection, metadata, new JsonObject { ["file_id"] = uploaded.Id }, committed?.Resource.Reference, committed);
            }
            catch (Exception error) when (committed is not null)
            {
                throw ResourceFailure(error, committed.Resource.Reference, binary.Data.Length, committed);
            }
        }
        var logical = (ResourceMediaSource)source;
        LlmResourcePreparation reads = LlmResourcePreparation.ForNamedReads();
        ImagePreparedReference? result = null;
        var errors = new List<Exception>();
        LlmResourcePreparation.CountingReadStream? contentRead = null;
        try
        {
            var (descriptor, content) = await reads.OpenNamedAsync(resources!, logical.Resource,
                observed => ValidateMetadata(observed.FileName, observed.ContentType), fence, token).ConfigureAwait(false);
            contentRead = content;
            var metadata = new ImageReference { Id = Guid.NewGuid(), FileName = descriptor.FileName, ContentType = descriptor.ContentType };
            LlmProviderFile uploaded = await UploadAsync(connection, bound, snapshot, metadata, content, fence, token).ConfigureAwait(false);
            reads.VerifyRead(resources!, descriptor, content, fence, token);
            result = Receipt(connection, metadata, new JsonObject { ["file_id"] = uploaded.Id }, logical.Resource);
        }
        catch (Exception error) { errors.Add(ResourceFailure(error, logical.Resource, contentRead?.BytesRead)); }
        finally
        {
            try { await reads.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { errors.Add(cleanup); }
        }
        ThrowFailures(errors);
        fence();
        return result!;
    }

    public Task<string> SubmitAsync(ImageConnection connection, PreparedImageTransportRequest request,
        XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Preparation preparation = ReadPreparation(request);
        foreach (InferenceResourceScope scope in preparation.Scopes) RequireScope(connection, scope);
        return RunAsync(connection, xai, async (captured, bound, snapshot, fence, token) =>
        {
            foreach (JsonObject reference in preparation.References)
                if (reference["file_id"] is { } file)
                    await AuthorizeFileAsync(captured, bound, snapshot, file.GetValue<string>(), fence, token).ConfigureAwait(false);
            XaiImageResult result = await bound.RunCatalogAsync(snapshot, fence, (http, selected, ownerFence, current) =>
                SendAsync(http, captured, selected, preparation.Route, preparation.Body, ownerFence, current), token).ConfigureAwait(false);
            fence();
            string handle = Guid.NewGuid().ToString("N");
            var completed = new Completed(captured.Endpoint.TrimEnd('/'), bound.Selection.ConnectionId, preparation.Workflow, result);
            lock (_completed) _completed.Add(handle, completed);
            // A late refusal does not expose a successful handle, but actual output remains owned until retirement.
            fence();
            return handle;
        }, cancellationToken);
    }

    public Task<ImageTransportJobSnapshot> GetJobAsync(ImageConnection connection, string providerJobId,
        ImageWorkflow workflow, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        ValidateConnection(connection, xai);
        Completed completed = Find(connection, xai!, providerJobId);
        if (!SameWorkflow(workflow, completed.Workflow)) throw new ArgumentException("The image result belongs to another workflow.", nameof(workflow));
        Fence(operation.Token, xai);
        ImageTransportOutput[] outputs = completed.Result.Images.Select((image, index) =>
        {
            JsonObject state = JsonNode.Parse(image.ProviderState.GetRawText()) as JsonObject ?? throw XaiMediaData.Malformed();
            state["handle"] = providerJobId; state["index"] = index; state["width"] = image.Width; state["height"] = image.Height;
            return new ImageTransportOutput(image.FileName, Scope(connection), XaiMediaData.Element(state), image.ContentType,
                image.FileId is { } file ? new(file) : null);
        }).ToArray();
        var snapshot = new ImageTransportJobSnapshot(ImageTransportJobState.Completed, outputs,
            Diagnostics: completed.Result.Usage)
        { ModelId = completed.Result.ModelId };
        Fence(operation.Token, xai);
        return Task.FromResult(snapshot);
    }

    public Task<ImageTransportDownload> DownloadAsync(ImageConnection connection, ImageTransportOutput output,
        XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
    {
        using var admission = _lifetime.Enter(cancellationToken);
        ArgumentNullException.ThrowIfNull(output);
        ValidateConnection(connection, xai);
        RequireScope(connection, output.Scope);
        string handle = XaiMediaData.RequiredString(output.ProviderState, "handle", static _ => false);
        Completed completed = Find(connection, xai!, handle);
        if (!output.ProviderState.TryGetProperty("index", out JsonElement value) || !value.TryGetInt32(out int index)
            || (uint)index >= (uint)completed.Result.Images.Count) throw InvalidPreparation();
        XaiImageSource image = completed.Result.Images[index];
        Fence(admission.Token, xai);
        if (image.Base64 is { } encoded)
        {
            byte[] bytes = XaiImageProtocol.DecodeBase64(encoded, XaiImageProtocol.MeasureBase64(encoded));
            Fence(admission.Token, xai);
            return Task.FromResult(new ImageTransportDownload(image.ContentType, bytes));
        }
        return RunAsync(connection, xai, async (captured, bound, snapshot, fence, token) =>
        {
            using var buffer = new MemoryStream();
            string? receivedType = null;
            Task<Stream> Open(string? type, long? length, CancellationToken current)
            {
                current.ThrowIfCancellationRequested(); fence();
                if (XaiMediaData.ContentTypesConflict(type, image.ContentType)) throw XaiMediaData.Malformed();
                receivedType = type;
                return Task.FromResult<Stream>(buffer);
            }
            if (image.FileId is { } file)
                await bound.RunFilesAsync(snapshot, fence, (files, selected, ownerFence, current) =>
                    files.DownloadToAsync(selected, file, Open, ownerFence, () => { }, _ => { }, current,
                        captured.RequestTimeout), token).ConfigureAwait(false);
            else if (image.Url is { } url)
                await bound.RunMediaAsync(snapshot, fence, (media, ownerFence, current) =>
                    media.DownloadToAsync(url, captured.RequestTimeout, Open, ownerFence, () => { }, _ => { }, current), token).ConfigureAwait(false);
            else throw XaiMediaData.Malformed();
            fence();
            if (buffer.Length == 0) throw XaiMediaData.Malformed();
            return new ImageTransportDownload(receivedType ?? image.ContentType, buffer.ToArray());
        }, cancellationToken);
    }

    public Task<bool> TryCancelAsync(ImageConnection connection, string providerJobId, bool interruptRunning,
        XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        ValidateConnection(connection, xai);
        _ = Find(connection, xai!, providerJobId);
        // Completed local output has no remote cancel operation. Ignore the originating operation's fence.
        Fence(operation.Token);
        return Task.FromResult(false);
    }

    private static async Task<LlmProviderFile> UploadAsync(ImageConnection connection, XaiFamilyBinding bound, LlmConnection snapshot,
        ImageReference metadata, Stream content, Action fence, CancellationToken token)
    {
        LlmProviderFile uploaded = await bound.RunFilesAsync(snapshot, fence, (files, selected, ownerFence, current) =>
            files.UploadAsync(selected, content, UploadLabel(metadata.FileName), null, ownerFence, current,
                secretContext: connection.AdditionalHeaders.Values.ToArray(), requestTimeout: connection.RequestTimeout), token).ConfigureAwait(false);
        fence();
        if (Protected(uploaded.Id, connection, snapshot) || Protected(uploaded.FileName, connection, snapshot)) throw XaiMediaData.Malformed();
        return uploaded;
    }

    private static async Task<LlmProviderFile> AuthorizeFileAsync(ImageConnection connection, XaiFamilyBinding bound,
        LlmConnection snapshot, string id, Action fence, CancellationToken token)
    {
        XaiFileRequestData.Identity(id);
        LlmProviderFile file = await bound.RunFilesAsync(snapshot, fence, (files, selected, ownerFence, current) =>
            files.GetAsync(selected, id, ownerFence, current, connection.RequestTimeout), token).ConfigureAwait(false);
        fence();
        if (file.Id != id) throw XaiMediaData.Malformed();
        if (Protected(file.Id, connection, snapshot) || Protected(file.FileName, connection, snapshot)) throw XaiMediaData.Malformed();
        return file;
    }

    private static bool Protected(string value, ImageConnection connection, LlmConnection snapshot) =>
        value != SecretRedactor.Sanitize(value, LlmCredential.Parse(snapshot.Credential),
            snapshot.AdditionalHeaders.Values.Concat(connection.AdditionalHeaders.Values));

    [SuppressMessage("Design", "CA1031", Justification = "The image boundary retains actual failures privately and records only real cleanup refusal.")]
    private async Task<T> RunAsync<T>(ImageConnection connection, XaiFamilyBinding? xai,
        Func<ImageConnection, XaiFamilyBinding, LlmConnection, Action, CancellationToken, Task<T>> action, CancellationToken token)
    {
        using var operation = _lifetime.Enter(token);
        ValidateConnection(connection, xai);
        xai!.RequireCatalogService();
        ImageConnection captured = connection with
        { AdditionalHeaders = new ReadOnlyDictionary<string, string>(connection.AdditionalHeaders.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)) };
        XaiFamilyBinding? bound = null;
        T? result = default;
        try
        {
            bound = await xai!.AcquireBoundAsync(selected => ValidateSelected(captured, selected),
                () => Fence(operation.Token), operation.Token).ConfigureAwait(false);
            void Check() => Fence(operation.Token, bound);
            LlmConnection snapshot = await bound.AcquireAsync(operation.Token).ConfigureAwait(false);
            Check();
            result = await action(captured, bound, snapshot, Check, operation.Token).ConfigureAwait(false);
            Check();
            return result;
        }
        catch (Exception error)
        {
            if (result is ImagePreparedReference reference)
                error = LlmResourcePreparation.WithGeneratedResources(error, PreparedResources(reference).ToArray());
            RecordCleanup(error);
            Exception safe = SafeFailure(error);
            ExceptionDispatchInfo.Throw(safe);
            throw;
        }
        finally { bound?.DetachOperationFence(); }
    }

    [SuppressMessage("Design", "CA1031", Justification = "The exchange attempts each actual request/response cleanup and preserves independent failures.")]
    private static async Task<XaiImageResult> SendAsync(HttpClient http, ImageConnection connection, LlmConnection snapshot,
        string route, JsonObject body, Action fence, CancellationToken token)
    {
        HttpRequestMessage? request = null;
        HttpResponseMessage? response = null;
        XaiImageResult? result = null;
        var errors = new List<Exception>();
        try
        {
            fence();
            request = new(HttpMethod.Post, ProviderHttp.BuildUri(snapshot, route));
            ApplyHeaders(request, connection, snapshot);
            request.Content = RequestJson.Content(body);
            fence();
            using (var headers = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                headers.CancelAfter(connection.RequestTimeout);
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
            }
            fence();
            string[] secrets = snapshot.AdditionalHeaders.Values.Concat(connection.AdditionalHeaders.Values).ToArray();
            LlmCredential? credential = LlmCredential.Parse(snapshot.Credential);
            await XaiErrorClassifier.ThrowIfUnsuccessfulImageAsync(response, credential, secrets, token).ConfigureAwait(false);
            fence();
            string text = await SdkHttpConsumption.ReadAsStringAsync(response.Content, token).ConfigureAwait(false);
            fence();
            result = XaiImageProtocol.Parse(text, value => value != SecretRedactor.Sanitize(value, credential, secrets), fence);
        }
        catch (Exception error) { errors.Add(SafeFailure(error)); }
        finally
        {
            if (response is not null)
                try { response.Dispose(); }
                catch (Exception cleanup) { errors.Add(LlmResourcePreparation.AsCleanupFailure(SafeFailure(cleanup), refusedOwner: response)); }
            if (request is not null)
                try { request.Dispose(); }
                catch (Exception cleanup) { errors.Add(LlmResourcePreparation.AsCleanupFailure(SafeFailure(cleanup), refusedOwner: request)); }
        }
        ThrowFailures(errors);
        fence();
        return result!;
    }

    internal static void ValidateSource(InferenceMediaSource source, IInferenceResourceAccess? resources)
    {
        switch (source)
        {
            case BinaryMediaSource binary: ValidateMetadata(binary.FileName, binary.ContentType); break;
            case UrlMediaSource url: XaiMediaData.RequireHttpUrl(url.Url.OriginalString); break;
            case FileMediaSource native: XaiFileRequestData.Identity(native.File.FileId); break;
            case ResourceMediaSource logical: LlmResourcePreparation.ValidateNamedReference(logical.Resource, resources); break;
            default: throw new ArgumentException("The image reference source is unsupported.", nameof(source));
        }
    }

    internal static void ValidateConnection(ImageConnection connection, XaiFamilyBinding? xai)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ConnectImageInference.ValidateBinding(connection, xai);
        if (connection.Transport != ImageTransport.XaiImagine) throw new ArgumentException("Select the xAI image transport.", nameof(connection));
        ArgumentException.ThrowIfNullOrWhiteSpace(connection.Endpoint);
        XaiMediaData.RequireHttpUrl(connection.Endpoint);
        if (connection.RequestTimeout != Timeout.InfiniteTimeSpan
            && (connection.RequestTimeout <= TimeSpan.Zero || connection.RequestTimeout.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(connection), "The image request timeout is invalid.");
        ProviderHttp.ValidateAdditionalHeaders(connection.AdditionalHeaders);
    }

    internal static void ValidateSelected(ImageConnection connection, LlmConnection snapshot)
    {
        if (connection.Endpoint.TrimEnd('/') != snapshot.Endpoint.TrimEnd('/'))
            throw new ArgumentException("The image endpoint does not match the selected xAI region.");
        using var request = new HttpRequestMessage();
        ApplyHeaders(request, connection, snapshot);
    }

    private static void ApplyHeaders(HttpRequestMessage request, ImageConnection connection, LlmConnection snapshot)
    {
        XaiRequestHeaders.Apply(request, snapshot, false);
        LlmCredential? credential = LlmCredential.Parse(snapshot.Credential);
        foreach ((string name, string value) in connection.AdditionalHeaders)
        {
            if (credential?.SecretHeaders.Keys.Contains(name, StringComparer.OrdinalIgnoreCase) is true)
                throw new ArgumentException("An image additional header conflicts with a selected secret header.");
            if (!request.Headers.TryAddWithoutValidation(name, value)) throw new ArgumentException("An image header cannot be applied.");
        }
    }

    private static void ValidateWorkflow(ImageWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (workflow.Transport != ImageTransport.XaiImagine) throw new ArgumentException("The workflow is not an xAI image workflow.", nameof(workflow));
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow.Version);
        _ = XaiMediaData.RequiredString(workflow.TransportConfiguration, "model", static _ => false);
    }

    private static void ValidateMetadata(string? fileName, string? contentType)
    {
        if (fileName is not null) XaiFileRequestData.FileName(fileName);
        if (contentType is not null && !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out _))
            throw new ArgumentException("The supplied image MIME type is invalid.");
    }

    private static string UploadLabel(string? name) => name ?? "image-upload-" + Guid.NewGuid().ToString("N");
    private InferenceResourceScope Scope(ImageConnection connection) => new(connection.Endpoint.TrimEnd('/') + "/images", _issuer);
    private void RequireScope(ImageConnection connection, InferenceResourceScope scope)
    {
        if (scope != Scope(connection)) throw new ArgumentException("The image receipt belongs to another transport or endpoint.");
    }
    private ImagePreparedReference Receipt(ImageConnection connection, ImageReference metadata, JsonObject state,
        InferenceResourceReference? resource = null, InferenceResourceCommitResult? committed = null)
    {
        if (committed is not null)
            state["generated_resources"] = new JsonArray(JsonSerializer.SerializeToNode(committed, InferenceJson.InferenceResourceCommitResult));
        return new(metadata, Scope(connection), XaiMediaData.Element(state), resource);
    }

    internal static IEnumerable<InferenceResourceCommitResult> PreparedResources(ImagePreparedReference reference)
    {
        if (!reference.ProviderState.TryGetProperty("generated_resources", out JsonElement receipts)) yield break;
        if (receipts.ValueKind != JsonValueKind.Array) throw InvalidPreparation();
        foreach (JsonElement receipt in receipts.EnumerateArray())
            yield return JsonSerializer.Deserialize(receipt, InferenceJson.InferenceResourceCommitResult) ?? throw InvalidPreparation();
    }

    private Completed Find(ImageConnection connection, XaiFamilyBinding xai, string handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        lock (_completed)
        {
            if (!_completed.TryGetValue(handle, out Completed? result) || result.Endpoint != connection.Endpoint.TrimEnd('/')
                || result.ConnectionId != xai.Selection.ConnectionId) throw new ArgumentException("The image result is not owned by this transport and connection.", nameof(handle));
            return result;
        }
    }

    private Preparation ReadPreparation(PreparedImageTransportRequest request)
    {
        try
        {
            JsonObject root = JsonNode.Parse(request.ProviderRequest) as JsonObject ?? throw InvalidPreparation();
            JsonObject metadata = root["workflow"] as JsonObject ?? throw InvalidPreparation();
            JsonObject body = root["body"] as JsonObject ?? throw InvalidPreparation();
            var workflow = new ImageWorkflow
            {
                Id = metadata["id"]!.GetValue<string>(),
                Label = string.Empty,
                Version = metadata["version"]!.GetValue<string>(),
                Transport = ImageTransport.XaiImagine,
                RequiresReferenceImage = metadata["requires_reference"]!.GetValue<bool>(),
                TransportConfiguration = XaiMediaData.Element(metadata["configuration"]!)
            };
            ValidateWorkflow(workflow);
            JsonObject[] references = body["image"] is JsonObject single ? [single]
                : body["images"] is JsonArray multiple ? multiple.Select(node => node as JsonObject ?? throw InvalidPreparation()).ToArray() : [];
            JsonArray scopes = root["scopes"] as JsonArray ?? throw InvalidPreparation();
            if (scopes.Count != references.Length || body.ContainsKey("image") && body.ContainsKey("images")) throw InvalidPreparation();
            var receiptScopes = scopes.Select(node => new InferenceResourceScope(node!["owner"]!.GetValue<string>(), node["identity"]!.GetValue<string>())).ToArray();
            if (receiptScopes.Any(scope => scope.Identity != _issuer)) throw InvalidPreparation();
            var ingress = new ImageTransportPreparationRequest
            {
                Workflow = workflow,
                PositivePrompt = body["prompt"]!.GetValue<string>(),
                Count = body["n"]!.GetValue<int>(),
                CacheKey = string.Empty,
                References = references.Select((reference, index) => new ImagePreparedReference(new() { Id = Guid.Empty },
                    receiptScopes[index], XaiMediaData.Element(reference))).ToArray()
            };
            (string route, JsonObject expected) = XaiImageProtocol.Build(ingress, references);
            if (root["route"]!.GetValue<string>() != route || !JsonNode.DeepEquals(body, expected)) throw InvalidPreparation();
            return new(workflow, route, expected, receiptScopes, references);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NullReferenceException or FormatException or ArgumentException)
        { throw InvalidPreparation(); }
    }

    private static bool SameWorkflow(ImageWorkflow left, ImageWorkflow right) =>
        left.Transport == right.Transport && left.Id == right.Id && left.Version == right.Version
        && left.RequiresReferenceImage == right.RequiresReferenceImage && JsonElement.DeepEquals(left.TransportConfiguration, right.TransportConfiguration);

    private void Fence(CancellationToken token, XaiFamilyBinding? xai = null)
    {
        token.ThrowIfCancellationRequested();
        try { _lifetime.ThrowIfStopping(); }
        catch (ObjectDisposedException error) { throw new OperationCanceledException("The image transport is retiring.", error, token); }
        xai?.FenceOperation();
        token.ThrowIfCancellationRequested();
    }

    private static Exception ResourceFailure(Exception error, InferenceResourceReference reference, long? bytes,
        InferenceResourceCommitResult? committed = null)
    {
        if (committed is null && LlmResourcePreparation.IsResourceFailure(error)) return error;
        var facts = new InferenceResourceOperationFailure(InferenceResourceOperationPhase.Materialize, reference,
            committed is null ? InferenceResourceCommitOutcome.NotAttempted : InferenceResourceCommitOutcome.Committed, bytes)
        {
            SelectionOutcome = committed?.Selection is null ? InferenceResourceCommitOutcome.NotAttempted : InferenceResourceCommitOutcome.Committed,
            SelectionVersion = committed?.Selection is { } selection ? new(selection.WorkspaceId, selection.SessionId, selection.Revision) : null,
            GeneratedResources = committed is null ? [] : [committed]
        };
        return error is OperationCanceledException canceled
                ? new InferenceResourceOperationCanceledException(facts, canceled.CancellationToken, error)
            : new InferenceResourceOperationException(error is InferenceException inference ? inference.Code : InferenceFailureCode.ExecutionFailed,
                "The image resource materialization failed.", facts, error);
    }

    internal static Exception SafeFailure(Exception error) => error switch
    {
        ImageEvidence => error,
        AggregateException aggregate => new AggregateException("The image operation failed.", aggregate.InnerExceptions.Select(SafeFailure)),
        InferenceResourceOperationException resource => RetainResource(resource, new InferenceResourceOperationException(resource.Code,
            "The image resource operation failed.", resource.Failure,
            new ImageEvidence(resource, resource.InnerException is { } cause ? SafeFailure(cause) : null))),
        InferenceResourceOperationCanceledException resource => RetainResource(resource, new InferenceResourceOperationCanceledException(resource.Failure,
            resource.CancellationToken, new ImageEvidence(resource, resource.InnerException is { } cause ? SafeFailure(cause) : null))),
        OperationCanceledException canceled => new OperationCanceledException("The image operation was interrupted.", new ImageEvidence(canceled), canceled.CancellationToken),
        InferenceException { InnerException: LlmProviderException provider } inference => new InferenceException(inference.Code,
            "Image authorization or access failed.", SafeFailure(provider)),
        LlmProviderException provider => new LlmProviderException("The image provider request failed.", LlmTransport.XaiResponses,
            provider.StatusCode, provider.ProviderCode is "xai_authentication_required" or "xai_entitlement_required" ? provider.ProviderCode : null,
            retryAfter: provider.RetryAfter, innerException: new ImageEvidence(provider), isContextOverflow: provider.IsContextOverflow),
        InferenceException inference => new InferenceException(inference.Code, "The image operation failed.", new ImageEvidence(inference)),
        _ => new InferenceException(error is JsonException ? InferenceFailureCode.ProtocolFailure : InferenceFailureCode.ExecutionFailed,
            "The image operation failed.", new ImageEvidence(error))
    };

    private static Exception RetainResource(Exception original, Exception safe) => LlmResourcePreparation.IsCleanupFailure(original)
        ? LlmResourcePreparation.AsCleanupFailure(safe, refusedOwner: original) : safe;

    private static void ThrowFailures(List<Exception> errors)
    {
        if (errors.Count == 1) ExceptionDispatchInfo.Throw(errors[0]);
        if (errors.Count > 1) throw new AggregateException("The image operation and cleanup failed.", errors);
    }

    private void RecordCleanup(Exception error)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(error);
        lock (_cleanupFailures)
        {
            while (pending.TryPop(out Exception? current))
            {
                if (!visited.Add(current)) continue;
                if (current is InferenceResourceOperationException resource && resource.Failure.Phase == InferenceResourceOperationPhase.Cleanup
                    || current is InferenceResourceOperationCanceledException canceled && canceled.Failure.Phase == InferenceResourceOperationPhase.Cleanup)
                {
                    Exception actual = ImageResourceOperations.ActualCleanup(current);
                    if (!_cleanupFailures.Any(item => ReferenceEquals(item.Original, actual))) _cleanupFailures.Add((actual, SafeFailure(current)));
                    continue;
                }
                if (current is AggregateException aggregate)
                    foreach (Exception cause in aggregate.InnerExceptions) pending.Push(cause);
                else if (current is ImageEvidence evidence) pending.Push(evidence.Original);
                else if (current is ImageResourceOperations.FailureEvidence resourceEvidence) pending.Push(resourceEvidence.Original);
                else if (current.InnerException is { } inner) pending.Push(inner);
            }
        }
    }

    public void Dispose() => RetireAsync().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(RetireAsync());
    private Task RetireAsync() => _lifetime.RetireAsync(() =>
    {
        lock (_completed) _completed.Clear();
        lock (_cleanupFailures)
        {
            if (_cleanupFailures.Count == 1) return Task.FromException(_cleanupFailures[0].Safe);
            if (_cleanupFailures.Count > 1) return Task.FromException(new AggregateException(_cleanupFailures.Select(item => item.Safe)));
        }
        return Task.CompletedTask;
    });

    private static ArgumentException InvalidPreparation() => new("The prepared image request or output is invalid.");
    private sealed record Preparation(ImageWorkflow Workflow, string Route, JsonObject Body,
        IReadOnlyList<InferenceResourceScope> Scopes, IReadOnlyList<JsonObject> References);
    private sealed record Completed(string Endpoint, string ConnectionId, ImageWorkflow Workflow, XaiImageResult Result);
    internal sealed class ImageEvidence(Exception original, Exception? safeCause = null)
        : Exception("The image boundary reported a failure.", safeCause)
    { internal Exception Original { get; } = original; }
}
