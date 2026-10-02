using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Runtime.ExceptionServices;
using System.Text;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed partial class GeminiApiLlmClient
{
    void ILlmContextCacheAdapter.ValidateCacheConnection(LlmConnection connection) { Validate(connection); _ = Key(connection); }
    void ILlmContextCacheAdapter.ValidateCacheReference(LlmConnection connection, LlmExplicitCacheReference reference)
    { ArgumentNullException.ThrowIfNull(reference); _ = GeminiCacheReads.ResourceId(reference.ResourceName); _ = ParseModel(reference.ModelId); }
    void ILlmContextCacheAdapter.ValidateCachedSuffix(LlmChatRequest request) => ValidateCachedSuffix(request);
    void ILlmContextCacheAdapter.ValidateCacheCreate(LlmConnection connection, LlmContextCacheCreate request)
    {
        Validate(connection); _ = ParseModel(request.ModelId); LlmResourcePreparation.ValidateArguments(new()
        { ModelId = request.ModelId, Messages = request.Messages, Tools = request.Tools, Resources = request.Resources });
    }
    Task<ILlmContextCacheOperation> ILlmContextCacheAdapter.PrepareCacheOperationAsync(LlmConnection connection,
        Action<CancellationToken> fence, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); fence(token); Validate(connection); _ = Key(connection);
        return Task.FromResult<ILlmContextCacheOperation>(new CacheOperation(this, connection));
    }

    private sealed class CacheOperation(GeminiApiLlmClient owner, LlmConnection connection) : ILlmContextCacheOperation
    {
        public LlmConnection Connection { get; } = connection;
        public Task<LlmContextCache> CreateAsync(LlmContextCacheCreate request, GeminiCacheBindings.Admission admission,
            Action<CancellationToken> fence, CancellationToken token) => owner.CreateCacheAsync(Connection, request, admission.Scope, fence, token);
        public Task<LlmContextCache> GetAsync(LlmExplicitCacheReference reference, GeminiCacheBindings.Admission admission,
            Action<CancellationToken> fence, CancellationToken token) => owner.GetCacheAsync(Connection, reference, admission.Scope, fence, token);
        public Task<LlmContextCachePage> ListAsync(LlmContextCacheList request, GeminiCacheBindings.Admission admission,
            Action<CancellationToken> fence, CancellationToken token) => owner.ListCachesAsync(Connection, request, admission.Scope, fence, token);
        public Task<LlmContextCache> UpdateExpiryAsync(LlmExplicitCacheReference reference, DateTimeOffset expiry,
            GeminiCacheBindings.Admission admission, Action<CancellationToken> fence, CancellationToken token) =>
            owner.UpdateCacheExpiryAsync(Connection, reference, expiry, admission.Scope, fence, token);
        public Task<LlmContextCacheDeleteResult> DeleteAsync(LlmExplicitCacheReference reference,
            Func<LlmExplicitCacheReference, bool> authorizeAlreadyAbsent, GeminiCacheBindings.Admission admission,
            Action<CancellationToken> fence, CancellationToken token) => owner.DeleteCacheAsync(Connection, reference, authorizeAlreadyAbsent, fence, token);
    }

    internal Task<LlmContextCache> GetCacheAsync(LlmConnection connection, LlmExplicitCacheReference reference,
        string scope, Action<CancellationToken> fence, CancellationToken token) =>
        GetCacheCoreAsync(connection, reference, scope, fence, token);

    private async Task<LlmContextCache> GetCacheCoreAsync(LlmConnection connection, LlmExplicitCacheReference reference,
        string scope, Action<CancellationToken> fence, CancellationToken token)
    {
        try
        {
            return await GeminiCacheReads.GetAsync(Http, connection, reference, scope, fence, token).ConfigureAwait(false);
        }
        catch (LlmProviderException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            throw new LlmContextCacheUnavailableException(LlmContextCacheUnavailableReason.Missing, reference,
                error.StatusCode, error.RequestId, error);
        }
    }

    internal Task<LlmContextCachePage> ListCachesAsync(LlmConnection connection, LlmContextCacheList request,
        string scope, Action<CancellationToken> fence, CancellationToken token) =>
        GeminiCacheReads.ListAsync(Http, connection, request, scope, fence, token);

    internal async Task<LlmContextCache> CreateCacheAsync(LlmConnection connection, LlmContextCacheCreate request,
        string scope, Action<CancellationToken> fence, CancellationToken token)
    {
        Validate(connection);
        _ = ParseModel(request.ModelId);
        if (request.Correlation.ReconciliationDisplayName.EnumerateRunes().Count() > 128)
            throw new ArgumentException("The cache reconciliation display name exceeds the documented Unicode-character bound.", nameof(request));
        connection = connection with { ContinuationCodec = Codec, ResolvedContinuationScope = KeyScope(connection) };
        var chat = new LlmChatRequest
        {
            ModelId = request.ModelId,
            Instructions = request.Instructions,
            Messages = request.Messages,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice,
            Resources = request.Resources
        };
        var capabilities = GetCapabilities(connection, request.ModelId);
        LlmResourcePreparation.ValidateArguments(chat);
        // Existing prompt/tool validation remains authoritative; logical content is checked before its read handle opens.
        var binary = chat with
        {
            Resources = null,
            Messages = chat.Messages.Select(message => message with
            { Attachments = message.Attachments.Where(attachment => attachment is not LlmResourceAttachment).ToArray() }).ToArray()
        };
        ValidateRequest(connection, binary, capabilities);
        ValidateGeminiRequest(binary, capabilities);
        var promptResources = chat.Messages.Where(message => message.ContinuationState is null)
            .SelectMany(message => message.Attachments).OfType<LlmResourceAttachment>().Select(attachment => attachment.Resource).ToHashSet();
        if (promptResources.Count > 0 && !capabilities.SupportsPromptImages && !capabilities.SupportsPromptDocuments)
            throw new NotSupportedException("The selected connection disables named prompt media.");
        if (!capabilities.RichContentSupported.Equals(RichContentRoutes.GeminiFunctionResponse)
            && chat.Messages.SelectMany(message => message.ToolResults).OfType<LlmToolResult>()
                .SelectMany(result => result.Output.Parts).Any(part => part is LlmToolOutputSessionResource))
            throw new NotSupportedException("This model cannot carry named resource content in function responses.");
        var files = new Dictionary<InferenceResourceReference, GeminiCacheFiles.File>();
        var parts = new Dictionary<string, GeminiCacheFiles.File>(StringComparer.Ordinal);
        JsonObject FilePart(GeminiCacheFiles.File file) => new()
        {
            ["fileData"] = new JsonObject
            { ["fileUri"] = file.Uri, ["mimeType"] = file.MimeType }
        };
        JsonObject Attachment(LlmMessageAttachment attachment)
        {
            if (attachment is LlmResourceAttachment logical) return FilePart(files[logical.Resource]);
            LlmAttachment media = LlmAttachments.RequireBinary(attachment);
            return new() { ["inlineData"] = new JsonObject { ["mimeType"] = LlmAttachments.ResolveMediaType(media), ["data"] = LlmAttachments.ToBase64(media) } };
        }
        ProjectedToolOutput Project(LlmToolOutput output) => ToolOutputProjector.Project(output,
            capabilities.RichContentSupported, capabilities.RichContentSupported,
            reference => new SentNativeFile(files[reference].Uri));
        // Validate the exact canonical prefix before any source callback or upload; placeholders never leave this frame.
        foreach (InferenceResourceReference reference in LlmResourcePreparation.NamedReferences(chat))
            files.Add(reference, new("", "", "text/plain", "ACTIVE"));
        _ = GeminiRequest.BuildCachePrefix(connection, request, Project, Reasoning(connection, request.ModelId), Attachment,
            _ => new() { ["fileData"] = new JsonObject() });
        files.Clear();
        var reading = LlmResourcePreparation.ForNamedReads(chat);
        InferenceResourceReference? currentReference = null;
        InferenceResourceOperationPhase phase = InferenceResourceOperationPhase.OpenRead;
        LlmResourcePreparation.CountingReadStream? content = null;
        long? transferredBytes = null;
        Exception? failure = null;
        JsonObject? prefix = null;
        try
        {
            foreach (InferenceResourceReference reference in LlmResourcePreparation.NamedReferences(chat))
            {
                currentReference = reference;
                transferredBytes = null;
                phase = InferenceResourceOperationPhase.OpenRead;
                var opened = await reading.OpenNamedAsync(request.Resources!, reference, descriptor =>
                {
                    GeminiCacheFiles.ValidateDescriptor(descriptor);
                    if (promptResources.Contains(reference)
                        && (descriptor.ContentType!.StartsWith("image/", StringComparison.Ordinal) ? !capabilities.SupportsPromptImages : !capabilities.SupportsPromptDocuments))
                        throw new NotSupportedException("The selected connection disables this named prompt media.");
                },
                    () => fence(token), token).ConfigureAwait(false);
                content = opened.Content;
                phase = InferenceResourceOperationPhase.Materialize;
                GeminiCacheFiles.File file = await GeminiCacheFiles.UploadAsync(Http, connection, opened.Descriptor, content, fence, token).ConfigureAwait(false);
                reading.VerifyRead(request.Resources!, opened.Descriptor, content, () => fence(token), token);
                if (file.Bytes is { } observedBytes && observedBytes != content.BytesRead)
                    throw new InvalidOperationException("The staged file contradicts its transmitted byte count.");
                transferredBytes = content.BytesRead;
                files.Add(reference, file); parts.Add(file.Uri, file);
                content = null;
            }
            prefix = GeminiRequest.BuildCachePrefix(connection, request, Project, Reasoning(connection, request.ModelId), Attachment,
                uri => FilePart(parts[uri]));
        }
        catch (Exception error)
        {
            var facts = new InferenceResourceOperationFailure(phase, currentReference,
                InferenceResourceCommitOutcome.NotAttempted, content?.BytesRead);
            failure = error is OperationCanceledException ? new InferenceResourceOperationCanceledException(facts, token, error)
                : new InferenceResourceOperationException(InferenceFailureCode.ExecutionFailed, "The cache prefix could not be materialized.", facts, error);
        }
        try { await reading.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { failure = failure is null ? cleanup : new AggregateException(failure, cleanup); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        try { token.ThrowIfCancellationRequested(); fence(token); }
        catch (OperationCanceledException error) when (currentReference is not null)
        {
            throw new InferenceResourceOperationCanceledException(new(phase, currentReference,
                InferenceResourceCommitOutcome.NotAttempted, transferredBytes), token, error);
        }
        prefix!["model"] = "models/" + request.ModelId;
        prefix["displayName"] = request.Correlation.ReconciliationDisplayName;
        GoogleCacheData.AddExpiration(prefix, request);
        var message = GeminiCacheReads.Request(string.Empty, Key(connection));
        message.Method = HttpMethod.Post;
        message.Content = JsonContent.Create(prefix);
        return await GoogleCacheHttp.CreateAsync(Http, message, Transport, request.Correlation,
            root => GeminiCacheReads.ParseReceipt(root, scope, null, request.ModelId), fence, token).ConfigureAwait(false);
    }

    internal async Task<LlmContextCache> UpdateCacheExpiryAsync(LlmConnection connection,
        LlmExplicitCacheReference reference, DateTimeOffset expiresAt, string scope,
        Action<CancellationToken> fence, CancellationToken token)
    {
        string id = GeminiCacheReads.ResourceId(reference.ResourceName);
        _ = ParseModel(reference.ModelId);
        Validate(connection);
        var message = GeminiCacheReads.Request("/" + Uri.EscapeDataString(id) + "?updateMask=expireTime", Key(connection));
        message.Method = HttpMethod.Patch;
        message.Content = JsonContent.Create(new JsonObject { ["expireTime"] = GoogleCacheData.Timestamp(expiresAt) });
        return await GoogleCacheHttp.UpdateExpiryAsync(Http, message, Transport, reference,
            root => GeminiCacheReads.ParseReceipt(root, scope, reference.ResourceName, reference.ModelId), fence, token).ConfigureAwait(false);
    }

    internal async Task<LlmContextCacheDeleteResult> DeleteCacheAsync(LlmConnection connection,
        LlmExplicitCacheReference reference, Func<LlmExplicitCacheReference, bool> authorizeAlreadyAbsent,
        Action<CancellationToken> fence, CancellationToken token)
    {
        string id = GeminiCacheReads.ResourceId(reference.ResourceName);
        _ = ParseModel(reference.ModelId);
        Validate(connection);
        var message = GeminiCacheReads.Request("/" + Uri.EscapeDataString(id), Key(connection));
        message.Method = HttpMethod.Delete;
        return await GoogleCacheHttp.DeleteAsync(Http, message, Transport, reference, authorizeAlreadyAbsent, fence, token).ConfigureAwait(false);
    }

    internal static void ValidateCachedSuffix(LlmChatRequest request)
    {
        if (request.ExplicitCache is null) return;
        if (!string.IsNullOrWhiteSpace(request.Instructions) || request.Messages.Any(message => message.Role == "system")
            || request.Tools.Count != 0 || request.ToolChoice is not null)
            throw new ArgumentException("Cached Gemini requests cannot replace instructions, tools or tool configuration.", nameof(request));
        if (request.ExplicitCache.ModelId != request.ModelId)
            throw new ArgumentException("The selected cache does not match the requested model.", nameof(request));
        _ = GeminiCacheReads.ResourceId(request.ExplicitCache.ResourceName);
    }
}
