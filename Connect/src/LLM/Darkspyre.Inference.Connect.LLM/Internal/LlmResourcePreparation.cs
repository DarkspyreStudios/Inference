using System.Runtime.ExceptionServices;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>One admitted resource graph. Caller authority is borrowed; opened handles are owned.</summary>
internal sealed class LlmResourcePreparation : IAsyncDisposable
{
    private readonly List<(IInferenceResourceReadHandle Handle, InferenceResourceReference Reference)> _handles = [];
    private readonly Dictionary<InferenceResourceReference, LlmProviderFile> _files = [];
    private readonly object _gate = new();
    private Task? _retirement;
    private Scope? _readScope;
    internal InferenceResourceSelectionVersion? SelectionVersion { get; private set; }

    private LlmChatRequest? _request;
    private LlmResourcePreparation(LlmChatRequest? request = null) => _request = request;

    internal LlmChatRequest Request
    {
        get => _request ?? throw new InvalidOperationException("A named resource reader has no chat request.");
        private set => _request = value;
    }

    internal static LlmResourcePreparation ForNamedReads() => new();

    internal static IEnumerable<InferenceResourceReference> NamedReferences(LlmChatRequest request) => References(request).Distinct();

    internal async Task<(InferenceResourceDescriptor Descriptor, CountingReadStream Content)> OpenNamedAsync(
        IInferenceResourceAccess access, InferenceResourceReference reference, Action<InferenceResourceDescriptor> preflight,
        Action fence, CancellationToken token)
    {
        Scope scope = _readScope ??= ReadScope(access, fence, token);
        InferenceResourceDescriptor descriptor = await RequireDescriptorAsync(access, reference, scope, fence, token).ConfigureAwait(false);
        preflight(descriptor);
        Check(fence, token);
        IInferenceResourceReadHandle handle = await access.OpenReadAsync(reference, token).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(handle);
        _handles.Add((handle, reference));
        Check(fence, token);
        InferenceResourceDescriptor observed = handle.Resource;
        Check(fence, token);
        CheckScope(access, scope, fence, token);
        observed = ValidateDescriptor(observed, reference, scope);
        ValidateImmutableFacts(descriptor, observed);
        Stream content = handle.Content;
        Check(fence, token);
        CheckScope(access, scope, fence, token);
        if (content is null || !content.CanRead) throw new InvalidOperationException("The resource handle has no readable content.");
        Check(fence, token);
        CheckScope(access, scope, fence, token);
        return (descriptor with
        {
            FileName = descriptor.FileName ?? observed.FileName,
            ContentType = descriptor.ContentType ?? observed.ContentType,
            Bytes = descriptor.Bytes ?? observed.Bytes
        }, new(content, fence, token));
    }

    internal void VerifyRead(IInferenceResourceAccess access, InferenceResourceDescriptor descriptor,
        CountingReadStream content, Action fence, CancellationToken token)
    {
        Check(fence, token);
        CheckScope(access, _readScope ?? throw InvalidScope(), fence, token);
        if (descriptor.Bytes is { } expected && content.BytesRead != expected)
            throw new InvalidOperationException("The resource content does not match its observed byte count.");
    }

    internal static void ValidateArguments(LlmChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (InferenceResourceReference reference in References(request)) ValidateReference(reference);
        if (References(request).Any() && request.Resources is null)
            throw new ArgumentException("Logical resources require explicit caller-owned access.", nameof(request));
    }

    internal static async Task<InferenceResourceSelection> ReadSelectionAsync(
        IInferenceResourceAccess access, Action fence, CancellationToken token)
    {
        Scope scope = ReadScope(access, fence, token);
        InferenceResourceSelection selected = await access.GetSelectionAsync(token).ConfigureAwait(false);
        Check(fence, token);
        CheckScope(access, scope, fence, token);
        ValidateSelection(selected, scope);
        return new(selected.WorkspaceId, selected.SessionId, selected.Revision,
            selected.SelectedResources.Select(LlmAttachments.CopyResource).ToArray());
    }

    internal static void ValidateScope(IInferenceResourceAccess access, InferenceResourceSelectionVersion expected,
        Action fence, CancellationToken token)
    {
        CheckScope(access, new(expected.WorkspaceId, expected.SessionId), fence, token);
    }

    internal static async Task<InferenceResourceDescriptor> RequireDescriptorAsync(
        IInferenceResourceAccess access, InferenceResourceReference reference, Action fence, CancellationToken token)
    {
        Scope scope = ReadScope(access, fence, token);
        return await RequireDescriptorAsync(access, reference, scope, fence, token).ConfigureAwait(false);
    }

    private static async Task<InferenceResourceDescriptor> RequireDescriptorAsync(
        IInferenceResourceAccess access, InferenceResourceReference reference, Scope scope, Action fence, CancellationToken token)
    {
        CheckScope(access, scope, fence, token);
        ValidateReference(reference);
        if (reference.WorkspaceId != scope.WorkspaceId) throw InvalidScope();
        InferenceResourceDescriptor? descriptor = await access.GetAsync(reference, token).ConfigureAwait(false);
        Check(fence, token);
        CheckScope(access, scope, fence, token);
        return ValidateDescriptor(descriptor, reference, scope);
    }

    internal static async Task ValidateContextAsync(LlmAgentContext context, InferenceResourceSelection selection,
        IInferenceResourceAccess access, Action fence, CancellationToken token)
    {
        Scope scope = ReadScope(access, fence, token);
        ValidateSelection(selection, scope);
        InferenceResourceSelectionVersion? version = context.ResourceSelectionVersion;
        if (version is null || version.WorkspaceId != scope.WorkspaceId || version.SessionId != scope.SessionId
            || version.Revision != selection.Revision || !context.SelectedResources.SequenceEqual(selection.SelectedResources))
            throw new ArgumentException("The imported resource selection does not match the current caller-owned snapshot.", nameof(context));
        foreach (InferenceResourceReference reference in context.SelectedResources.Concat(
            References(new() { ModelId = "context", Messages = context.Entries.Select(entry => entry.Message).ToArray() }, actualOnly: false)).Distinct())
            _ = await RequireDescriptorAsync(access, reference, scope, fence, token).ConfigureAwait(false);
        Check(fence, token);
    }

    internal static async Task AuthorizeToolOutputAsync(LlmToolOutput output,
        IInferenceResourceAccess access, Action fence, CancellationToken token)
    {
        try
        {
            InferenceResourceSelection selection = await ReadSelectionAsync(access, fence, token).ConfigureAwait(false);
            Scope scope = new(selection.WorkspaceId, selection.SessionId);
            foreach (LlmToolOutputSessionResource part in output.Parts.OfType<LlmToolOutputSessionResource>())
            {
                _ = await RequireDescriptorAsync(access, part.Resource, scope, fence, token).ConfigureAwait(false);
                if (!selection.SelectedResources.Contains(part.Resource))
                    throw new ArgumentException("A registered tool artifact must already be selected in the caller-owned session.", nameof(output));
            }
            Check(fence, token);
        }
        catch (Exception error) { ExceptionDispatchInfo.Capture(AsResourceFailure(error, token)).Throw(); throw; }
    }

    internal static bool IsResourceFailure(Exception error) => error is InferenceResourceOperationException or InferenceResourceOperationCanceledException
        || error is AggregateException aggregate && aggregate.InnerExceptions.Any(IsResourceFailure);

    internal static bool IsCleanupFailure(Exception error) => error switch
    {
        InferenceResourceOperationException resource => resource.Failure.Phase == InferenceResourceOperationPhase.Cleanup,
        InferenceResourceOperationCanceledException resource => resource.Failure.Phase == InferenceResourceOperationPhase.Cleanup,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsCleanupFailure),
        _ => false
    };

    internal static Exception AsCleanupFailure(Exception cleanup, InferenceResourceReference? reference = null, object? refusedOwner = null)
    {
        if (IsCleanupFailure(cleanup))
        {
            if (refusedOwner is not null) RetainCleanupOwner(cleanup, refusedOwner);
            return cleanup;
        }
        var facts = new InferenceResourceOperationFailure(InferenceResourceOperationPhase.Cleanup, reference,
            InferenceResourceCommitOutcome.NotAttempted);
        Exception failure = cleanup is OperationCanceledException canceled
            ? new InferenceResourceOperationCanceledException(facts, canceled.CancellationToken, cleanup)
            : new InferenceResourceOperationException(InferenceFailureCode.ExecutionFailed,
                "The owned resource could not be released.", facts, cleanup);
        if (refusedOwner is not null) RetainCleanupOwner(failure, refusedOwner);
        return failure;
    }

    private static void RetainCleanupOwner(Exception failure, object owner)
    {
        switch (failure)
        {
            case InferenceResourceOperationException resource when resource.Failure.Phase == InferenceResourceOperationPhase.Cleanup:
                resource.RetainCleanupOwner(owner);
                break;
            case InferenceResourceOperationCanceledException resource when resource.Failure.Phase == InferenceResourceOperationPhase.Cleanup:
                resource.RetainCleanupOwner(owner);
                break;
            case AggregateException aggregate:
                RetainCleanupOwner(aggregate.InnerExceptions.First(IsCleanupFailure), owner);
                break;
        }
    }

    internal static Exception AsResourceFailure(Exception error, CancellationToken token)
    {
        if (IsResourceFailure(error)) return error;
        var facts = new InferenceResourceOperationFailure(InferenceResourceOperationPhase.Materialize, null,
            InferenceResourceCommitOutcome.NotAttempted);
        return error is OperationCanceledException
            ? new InferenceResourceOperationCanceledException(facts, token, error)
            : new InferenceResourceOperationException(InferenceFailureCode.ExecutionFailed,
                "The admitted resource operation failed.", facts, error);
    }

    internal static async Task<LlmResourcePreparation> PrepareAsync(LlmConnection connection, LlmChatRequest request,
        XaiFileClient files, Action fence, CancellationToken token,
        Action<InferenceResourceReference, LlmProviderFile?>? observeUpload = null)
    {
        ValidateArguments(request);
        var operation = new LlmResourcePreparation(request);
        InferenceResourceOperationPhase phase = InferenceResourceOperationPhase.ReadSelection;
        InferenceResourceReference? current = null;
        CountingReadStream? reading = null;
        try
        {
            Check(fence, token);
            if (request.Resources is not { } access) return operation;
            InferenceResourceSelection selection = await ReadSelectionAsync(access, fence, token).ConfigureAwait(false);
            request = AppendSelection(request, selection);
            Scope scope = new(selection.WorkspaceId, selection.SessionId);
            operation._readScope = scope;
            operation.SelectionVersion = new(selection.WorkspaceId, selection.SessionId, selection.Revision);
            CheckScope(access, scope, fence, token);
            foreach (InferenceResourceReference reference in References(request).Distinct())
            {
                current = reference;
                phase = InferenceResourceOperationPhase.OpenRead;
                var opened = await operation.OpenNamedAsync(access, reference, descriptor =>
                {
                    if (string.IsNullOrWhiteSpace(descriptor.FileName))
                        throw new NotSupportedException("The file destination requires an observed resource filename.");
                }, fence, token).ConfigureAwait(false);
                InferenceResourceDescriptor descriptor = opened.Descriptor;
                reading = opened.Content;
                phase = InferenceResourceOperationPhase.Materialize;
                LlmProviderFile uploaded = await files.UploadAsync(connection, reading, descriptor.FileName!, null, fence, token,
                    onDispatched: observeUpload is null ? null : () => observeUpload(reference, null),
                    onObserved: observeUpload is null ? null : observed => observeUpload(reference, observed)).ConfigureAwait(false);
                operation.VerifyRead(access, descriptor, reading, fence, token);
                operation._files.Add(reference, uploaded);
                reading = null;
            }
            operation.Request = request with
            {
                Resources = null,
                Messages = request.Messages.Select(message => message.ContinuationState is not null ? message : message with
                {
                    Attachments = message.Attachments.Select(attachment => attachment is LlmResourceAttachment logical
                        ? (LlmMessageAttachment)new LlmFileReference(new ProviderFileReference(operation._files[logical.Resource].Id))
                        : attachment).ToArray()
                }).ToArray()
            };
            Check(fence, token);
            return operation;
        }
        catch (Exception original)
        {
            var facts = new InferenceResourceOperationFailure(phase, current,
                InferenceResourceCommitOutcome.NotAttempted, reading?.BytesRead);
            Exception error = original is OperationCanceledException
                ? new InferenceResourceOperationCanceledException(facts, token, original)
                : new InferenceResourceOperationException(InferenceFailureCode.ExecutionFailed,
                    "The resource could not be supplied to the selected destination.", facts, original);
            try { await operation.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
    }

    internal SentToolPart Project(InferenceResourceReference reference) => _files.TryGetValue(reference, out var file)
        ? new SentNativeFile(file.Id)
        : throw new InvalidOperationException("The admitted tool resource has no materialized file receipt.");

    internal static LlmChatRequest AppendSelection(LlmChatRequest request, InferenceResourceSelection selection)
    {
        var present = References(request).ToHashSet();
        InferenceResourceReference[] additions = selection.SelectedResources.Where(reference => !present.Contains(reference)).ToArray();
        if (additions.Length == 0) return request;
        int destination = -1;
        for (int index = request.Messages.Count - 1; index >= 0; index--)
            if (request.Messages[index] is { Role: "user", ContinuationState: null }) { destination = index; break; }
        if (destination < 0) throw new ArgumentException("Selected resources require an actual supplied user prompt.", nameof(request));
        LlmChatMessage[] messages = request.Messages.ToArray();
        messages[destination] = messages[destination] with
        {
            Attachments = [.. messages[destination].Attachments, .. additions.Select(reference => new LlmResourceAttachment(reference))]
        };
        return request with { Messages = messages };
    }

    private static IEnumerable<InferenceResourceReference> References(LlmChatRequest request, bool actualOnly = true) => request.Messages
        .Where(message => !actualOnly || message.ContinuationState is null)
        .SelectMany(message => message.Attachments.OfType<LlmResourceAttachment>().Select(part => part.Resource)
            .Concat(message.ToolResults.OfType<LlmToolResult>().SelectMany(result => result.Output.Parts)
                .OfType<LlmToolOutputSessionResource>().Select(part => part.Resource)));

    private static Scope ReadScope(IInferenceResourceAccess access, Action fence, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(access);
        Check(fence, token);
        string workspace = access.WorkspaceId;
        Check(fence, token);
        string session = access.SessionId;
        Check(fence, token);
        ArgumentException.ThrowIfNullOrEmpty(workspace);
        ArgumentException.ThrowIfNullOrEmpty(session);
        return new(workspace, session);
    }

    private static void CheckScope(IInferenceResourceAccess access, Scope scope, Action fence, CancellationToken token)
    {
        if (ReadScope(access, fence, token) != scope) throw InvalidScope();
    }

    private static void ValidateImmutableFacts(InferenceResourceDescriptor lookup, InferenceResourceDescriptor handle)
    {
        if (lookup.SessionId != handle.SessionId
            || lookup.FileName is { } name && handle.FileName is { } observedName && name != observedName
            || lookup.ContentType is { } type && handle.ContentType is { } observedType && type != observedType
            || lookup.Bytes is { } bytes && handle.Bytes is { } observedBytes && bytes != observedBytes)
            throw new InvalidOperationException("The resource handle contradicts the observed immutable content metadata.");
    }

    private static void ValidateReference(InferenceResourceReference reference)
    { ArgumentNullException.ThrowIfNull(reference); ArgumentException.ThrowIfNullOrEmpty(reference.WorkspaceId); ArgumentException.ThrowIfNullOrEmpty(reference.Id); }
    private static void ValidateSelection(InferenceResourceSelection selection, Scope scope)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.WorkspaceId != scope.WorkspaceId || selection.SessionId != scope.SessionId || selection.Revision < 0) throw InvalidScope();
        ArgumentNullException.ThrowIfNull(selection.SelectedResources);
        var seen = new HashSet<InferenceResourceReference>();
        foreach (var reference in selection.SelectedResources)
        { ValidateReference(reference); if (reference.WorkspaceId != scope.WorkspaceId || !seen.Add(reference)) throw InvalidScope(); }
    }
    private static InferenceResourceDescriptor ValidateDescriptor(InferenceResourceDescriptor? descriptor, InferenceResourceReference reference, Scope scope)
    {
        if (descriptor is null) throw new InvalidOperationException("The referenced committed content is unavailable.");
        if (descriptor.Reference != reference || reference.WorkspaceId != scope.WorkspaceId
            || descriptor.SessionId is { } session && session != scope.SessionId || descriptor.Bytes is < 0) throw InvalidScope();
        return new(LlmAttachments.CopyResource(descriptor.Reference), descriptor.SessionId, descriptor.FileName, descriptor.ContentType, descriptor.Bytes);
    }
    private static ArgumentException InvalidScope() => new("The resource does not belong to the admitted workspace/session scope.");
    internal static void Check(Action fence, CancellationToken token) { token.ThrowIfCancellationRequested(); fence(); token.ThrowIfCancellationRequested(); }

    public ValueTask DisposeAsync()
    { lock (_gate) return new(_retirement ??= RetireAsync()); }
    private async Task RetireAsync()
    {
        var failures = new List<Exception>();
        var failedHandles = new List<(IInferenceResourceReadHandle Handle, InferenceResourceReference Reference)>();
        foreach (var owned in _handles)
        {
            try { await owned.Handle.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                failedHandles.Add(owned);
                failures.Add(AsCleanupFailure(cleanup, owned.Reference, owned.Handle));
            }
        }
        _handles.Clear();
        _handles.AddRange(failedHandles);
        _files.Clear();
        if (_request is not null) _request = _request with { Resources = null };
        if (failures.Count == 0) return;
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException(failures);
    }

    private sealed record Scope(string WorkspaceId, string SessionId);
    internal sealed class CountingReadStream(Stream inner, Action fence, CancellationToken owner) : Stream
    {
        internal long BytesRead { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        { Check(fence, owner); int read = inner.Read(buffer, offset, count); BytesRead = checked(BytesRead + read); Check(fence, owner); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Check(fence, owner);
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead = checked(BytesRead + read);
            Check(fence, owner);
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
