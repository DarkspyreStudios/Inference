using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

internal sealed partial class VertexLlmClient
{
    internal const long InlineCacheMediaBytes = 10L * 1024 * 1024;

    private void ValidateCacheCreate(LlmConnection connection, LlmContextCacheCreate request)
    {
        ValidateCacheConnection(connection);
        if (VertexEndpoints.ParseModel(request.ModelId).Publisher != "google")
            throw new NotSupportedException("Vertex explicit caches serve Gemini models only.");
        if (request.Correlation.ReconciliationDisplayName.EnumerateRunes().Count() > 128)
            throw new ArgumentException("The cache reconciliation display name exceeds the documented Unicode-character bound.", nameof(request));
        var chat = CacheChat(request);
        LlmResourcePreparation.ValidateArguments(chat);
        var capabilities = GetCapabilities(connection, request.ModelId);
        var binary = chat with
        {
            Resources = null,
            Messages = chat.Messages.Select(message => message with
            { Attachments = message.Attachments.Where(attachment => attachment is not LlmResourceAttachment).ToArray() }).ToArray()
        };
        ValidateRequest(connection, binary, capabilities);
        if (!capabilities.RichContentSupported.Equals(RichContentRoutes.GeminiFunctionResponse)
            && chat.Messages.SelectMany(message => message.ToolResults).OfType<LlmToolResult>()
                .SelectMany(result => result.Output.Parts).Any(part => part is LlmToolOutputSessionResource))
            throw new NotSupportedException("This model cannot carry named resource content in function responses.");
        foreach (LlmAttachment attachment in binary.Messages.Where(message => message.ContinuationState is null)
            .SelectMany(message => message.Attachments).Select(LlmAttachments.RequireBinary))
            VertexCacheFiles.ValidateMedia(LlmAttachments.ResolveMediaType(attachment), attachment.Data.Length);
        if (connection.Vertex!.CacheStaging is null)
        {
            long total = 0;
            foreach (LlmAttachment attachment in binary.Messages.Where(message => message.ContinuationState is null)
                .SelectMany(message => message.Attachments).Select(LlmAttachments.RequireBinary)) total = checked(total + attachment.Data.Length);
            foreach (LlmToolResult result in chat.Messages.SelectMany(message => message.ToolResults).OfType<LlmToolResult>())
                foreach (SentToolPart part in ToolOutputProjector.Project(result.Output, capabilities.RichContentSupported,
                    capabilities.RichContentSupported, _ => new SentNativeFile("pending")).Parts)
                    total = checked(total + Media(part).Data.Length);
            if (total > InlineCacheMediaBytes) throw new NotSupportedException("The inline cache media requires explicitly configured Cloud Storage staging.");
        }
    }

    private static LlmChatRequest CacheChat(LlmContextCacheCreate request) => new()
    {
        ModelId = request.ModelId,
        Instructions = request.Instructions,
        Messages = request.Messages,
        Tools = request.Tools,
        ToolChoice = request.ToolChoice,
        Resources = request.Resources
    };

    private static (string? Mime, ReadOnlyMemory<byte> Data) Media(SentToolPart part) => part switch
    {
        SentImage image => (image.MediaType, image.Data),
        SentFile { IsPdf: true } file => (file.MediaType, file.Data),
        _ => (null, default)
    };

    private sealed partial class CacheOperation
    {
        private sealed class MediaComparer : IEqualityComparer<(string Mime, ReadOnlyMemory<byte> Data)>
        {
            public bool Equals((string Mime, ReadOnlyMemory<byte> Data) x, (string Mime, ReadOnlyMemory<byte> Data) y)
                => StringComparer.Ordinal.Equals(x.Mime, y.Mime) && x.Data.Span.SequenceEqual(y.Data.Span);
            public int GetHashCode((string Mime, ReadOnlyMemory<byte> Data) value)
            {
                var hash = new HashCode(); hash.Add(value.Mime, StringComparer.Ordinal); hash.AddBytes(value.Data.Span);
                return hash.ToHashCode();
            }
        }

        private sealed record PreparedMedia(string Mime, ReadOnlyMemory<byte> Data, VertexCacheFiles.File? File)
        {
            internal JsonObject Part() => File is { } staged
                ? new() { ["fileData"] = new JsonObject { ["mimeType"] = Mime, ["fileUri"] = staged.Uri } }
                : new() { ["inlineData"] = new JsonObject { ["mimeType"] = Mime, ["data"] = Convert.ToBase64String(Data.Span) } };
        }

        private async Task<LlmContextCache> CreateCoreAsync(LlmContextCacheCreate request, GeminiCacheBindings.Admission admission,
            Action<CancellationToken> fence, CancellationToken token)
        {
            Owner.ValidateCacheCreate(Connection, request);
            var chat = CacheChat(request);
            var capabilities = Owner.GetCapabilities(Connection, request.ModelId);
            var reasoning = GeminiModelProfiles.Get(VertexEndpoints.ParseModel(request.ModelId).Model, null);
            var logical = new Dictionary<InferenceResourceReference, PreparedMedia>();
            var media = new Dictionary<(string Mime, ReadOnlyMemory<byte> Data), PreparedMedia>(new MediaComparer());
            var native = new Dictionary<string, PreparedMedia>(StringComparer.Ordinal);
            var occurrences = new Dictionary<InferenceResourceReference, int>();
            var promptReferences = chat.Messages.Where(message => message.ContinuationState is null)
                .SelectMany(message => message.Attachments).OfType<LlmResourceAttachment>().Select(attachment => attachment.Resource).ToHashSet();
            long usedInline = 0;
            foreach (LlmChatMessage message in chat.Messages)
            {
                if (message.ContinuationState is null)
                    foreach (LlmMessageAttachment attachment in message.Attachments)
                    {
                        if (attachment is LlmResourceAttachment reference) AddOccurrence(reference.Resource);
                        else
                        {
                            LlmAttachment binary = LlmAttachments.RequireBinary(attachment);
                            AddRaw(LlmAttachments.ResolveMediaType(binary), binary.Data);
                        }
                    }
                foreach (LlmToolResult result in ClientTools.Functions(message.ToolResults))
                {
                    foreach (LlmToolOutputSessionResource reference in result.Output.Parts.OfType<LlmToolOutputSessionResource>()) AddOccurrence(reference.Resource);
                    foreach (SentToolPart part in ProjectRaw(result.Output).Parts)
                        if (Media(part) is { Mime: { } mime } value) AddRaw(mime, value.Data);
                }
            }
            foreach (InferenceResourceReference reference in occurrences.Keys) logical.Add(reference, new("text/plain", default, null));
            // This canonical dry projection checks call/replay/schema ordering before source callbacks or uploads.
            _ = GeminiRequest.BuildCachePrefix(Connection, request, Project, reasoning, Attachment, Native);
            logical.Clear();
            var reading = LlmResourcePreparation.ForNamedReads();
            InferenceResourceReference? current = null;
            InferenceResourceOperationPhase phase = InferenceResourceOperationPhase.OpenRead;
            long? transferred = null;
            LlmResourcePreparation.CountingReadStream? content = null;
            Exception? failure = null;
            JsonObject? prefix = null;
            try
            {
                foreach (InferenceResourceReference reference in LlmResourcePreparation.NamedReferences(chat))
                {
                    current = reference;
                    phase = InferenceResourceOperationPhase.OpenRead;
                    transferred = null;
                    int count = occurrences[reference];
                    long available = (InlineCacheMediaBytes - usedInline) / count;
                    var opened = await reading.OpenNamedAsync(request.Resources!, reference, descriptor =>
                    {
                        if (descriptor.ContentType is not null) VertexCacheFiles.ValidateDescriptor(descriptor);
                        if (promptReferences.Contains(reference) && descriptor.ContentType is not null
                            && (descriptor.ContentType!.StartsWith("image/", StringComparison.Ordinal) ? !capabilities.SupportsPromptImages : !capabilities.SupportsPromptDocuments))
                            throw new NotSupportedException("The selected connection disables this named prompt media.");
                        if (Connection.Vertex!.CacheStaging is null && descriptor.Bytes > available)
                            throw new NotSupportedException("The named cache media requires explicitly configured Cloud Storage staging.");
                    }, () => fence(token), token).ConfigureAwait(false);
                    content = opened.Content;
                    phase = InferenceResourceOperationPhase.Materialize;
                    VertexCacheFiles.ValidateDescriptor(opened.Descriptor);
                    if (promptReferences.Contains(reference)
                        && (opened.Descriptor.ContentType!.StartsWith("image/", StringComparison.Ordinal) ? !capabilities.SupportsPromptImages : !capabilities.SupportsPromptDocuments))
                        throw new NotSupportedException("The selected connection disables this named prompt media.");
                    PreparedMedia prepared;
                    if (Connection.Vertex!.CacheStaging is { } staging)
                    {
                        VertexCacheFiles.File file = await VertexCacheFiles.UploadAsync(Owner.Http, Access, staging,
                            opened.Descriptor.ContentType!, opened.Descriptor.Bytes, reference, content, fence, token).ConfigureAwait(false);
                        prepared = new(opened.Descriptor.ContentType!, default, file); native.Add(file.Uri, prepared);
                    }
                    else
                    {
                        byte[] bytes = await ReadInlineAsync(content, available, fence, token).ConfigureAwait(false);
                        usedInline = checked(usedInline + bytes.LongLength * count);
                        prepared = new(opened.Descriptor.ContentType!, bytes, null);
                    }
                    reading.VerifyRead(request.Resources!, opened.Descriptor, content, () => fence(token), token);
                    transferred = content.BytesRead;
                    logical.Add(reference, prepared); content = null;
                }
                if (Connection.Vertex!.CacheStaging is { } configured)
                    foreach (var (key, prepared) in media.ToArray())
                    {
                        current = null;
                        phase = InferenceResourceOperationPhase.Materialize;
                        transferred = null;
                        if (!MemoryMarshal.TryGetArray(prepared.Data, out ArraySegment<byte> bytes))
                            throw new InvalidOperationException("The admitted media snapshot has no immutable array backing.");
                        using var source = new MemoryStream(bytes.Array!, bytes.Offset, bytes.Count, writable: false);
                        VertexCacheFiles.File file = await VertexCacheFiles.UploadAsync(Owner.Http, Access, configured,
                            prepared.Mime, prepared.Data.Length, null, source, fence, token).ConfigureAwait(false);
                        var replacement = prepared with { Data = default, File = file };
                        transferred = file.Bytes;
                        media[key] = replacement; native.Add(file.Uri, replacement);
                    }
                prefix = GeminiRequest.BuildCachePrefix(Connection, request, Project, reasoning, Attachment, Native);
            }
            catch (Exception error)
            {
                failure = LlmResourcePreparation.AsInputFailure(error, phase, current,
                    content?.BytesRead ?? transferred, token);
            }
            try { await reading.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { failure = failure is null ? cleanup : new AggregateException(failure, cleanup); }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            try { token.ThrowIfCancellationRequested(); fence(token); }
            catch (OperationCanceledException error)
            {
                ExceptionDispatchInfo.Throw(LlmResourcePreparation.AsInputFailure(error, InferenceResourceOperationPhase.Materialize,
                    current, content?.BytesRead ?? transferred, token));
            }
            prefix!["model"] = $"projects/{Connection.Vertex!.ProjectId}/locations/{Connection.Vertex.Location}/publishers/google/models/{VertexEndpoints.ParseModel(request.ModelId).Model}";
            prefix["displayName"] = request.Correlation.ReconciliationDisplayName;
            GoogleCacheData.AddExpiration(prefix, request);
            var post = Message(HttpMethod.Post); post.Content = JsonContent.Create(prefix);
            return await GoogleCacheHttp.CreateAsync(Owner.Http, post, Owner.Transport, request.Correlation,
                root => Parse(root, admission, null, request.ModelId, fence, token), fence, token).ConfigureAwait(false);

            void AddOccurrence(InferenceResourceReference reference) => occurrences[reference] = checked(occurrences.GetValueOrDefault(reference) + 1);
            void AddRaw(string mime, ReadOnlyMemory<byte> data)
            {
                VertexCacheFiles.ValidateMedia(mime, data.Length);
                media.TryAdd((mime, data), new(mime, data, null));
                if (Connection.Vertex!.CacheStaging is null) usedInline = checked(usedInline + data.Length);
            }
            ProjectedToolOutput ProjectRaw(LlmToolOutput output) => ToolOutputProjector.Project(output,
                capabilities.RichContentSupported, capabilities.RichContentSupported, _ => new SentNativeFile("pending"));
            ProjectedToolOutput Project(LlmToolOutput output)
            {
                ProjectedToolOutput projected = ToolOutputProjector.Project(output, capabilities.RichContentSupported,
                    capabilities.RichContentSupported, reference =>
                    {
                        PreparedMedia prepared = logical[reference];
                        if (prepared.File is { } file) return new SentNativeFile(file.Uri);
                        if (prepared.Mime == "text/plain") return new SentText(new UTF8Encoding(false, true).GetString(prepared.Data.Span));
                        return prepared.Mime.StartsWith("image/", StringComparison.Ordinal)
                            ? new SentImage(prepared.Mime, prepared.Data, null) : new SentFile(prepared.Mime, prepared.Data, "resource.pdf");
                    });
                return projected with
                {
                    Parts = projected.Parts.Select(part =>
                {
                    var raw = Media(part);
                    return raw.Mime is { } mime && media.TryGetValue((mime, raw.Data), out PreparedMedia? prepared)
                        && prepared.File is { } file ? new SentNativeFile(file.Uri) : part;
                }).ToArray()
                };
            }
            JsonObject Attachment(LlmMessageAttachment attachment)
            {
                if (attachment is LlmResourceAttachment reference) return logical[reference.Resource].Part();
                LlmAttachment binary = LlmAttachments.RequireBinary(attachment);
                return media[(LlmAttachments.ResolveMediaType(binary), binary.Data)].Part();
            }
            JsonObject Native(string uri) => native[uri].Part();
        }

        private static async Task<byte[]> ReadInlineAsync(Stream source, long available,
            Action<CancellationToken> fence, CancellationToken token)
        {
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[32 * 1024];
            while (true)
            {
                token.ThrowIfCancellationRequested(); fence(token);
                int requested = (int)Math.Min(chunk.Length, available - buffer.Length + 1);
                int count = await source.ReadAsync(chunk.AsMemory(0, requested), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); fence(token);
                if (count == 0) return buffer.ToArray();
                if (buffer.Length + count > available)
                    throw new NotSupportedException("The named cache media requires explicitly configured Cloud Storage staging.");
                buffer.Write(chunk, 0, count);
            }
        }
    }
}
