namespace Darkspyre.Inference.Common.Resources;

/// <summary>Borrowed access to one caller-owned workspace and logical session. Connect does not dispose it.</summary>
public interface IInferenceResourceAccess
{
    /// <summary>The workspace this access reaches.</summary>
    string WorkspaceId { get; }
    /// <summary>The logical session this access reaches.</summary>
    string SessionId { get; }

    /// <summary>Returns the metadata of the referenced resource, or null when this access has no such resource.</summary>
    Task<InferenceResourceDescriptor?> GetAsync(
        InferenceResourceReference reference, CancellationToken cancellationToken = default);

    /// <summary>Opens the referenced resource for reading.</summary>
    Task<IInferenceResourceReadHandle> OpenReadAsync(
        InferenceResourceReference reference, CancellationToken cancellationToken = default);

    /// <summary>Opens a staging stream for new content described by <paramref name="resource"/>.</summary>
    Task<IInferenceResourceWriteHandle> OpenWriteAsync(
        InferenceResourceCreate resource, CancellationToken cancellationToken = default);

    /// <summary>Returns the session's current resource selection and its revision.</summary>
    Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default);

    /// <summary>Atomically append authorized committed references. Existing members are a no-op.</summary>
    Task<InferenceResourceSelection> SelectAsync(
        IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default);

    /// <summary>Explicit ordered CAS replacement. Removal does not delete content.</summary>
    Task<InferenceResourceSelection> ReplaceSelectionAsync(
        IReadOnlyList<InferenceResourceReference> references, long expectedRevision,
        CancellationToken cancellationToken = default);
}

/// <summary>An operation-owned readable stream. Dispose the handle, not its stream separately.</summary>
public interface IInferenceResourceReadHandle : IAsyncDisposable
{
    /// <summary>The metadata of the resource being read.</summary>
    InferenceResourceDescriptor Resource { get; }
    /// <summary>The resource content. The handle owns the stream.</summary>
    Stream Content { get; }
}

/// <summary>One operation-owned staging stream and one commit attempt. Disposal does not delete committed content.</summary>
public interface IInferenceResourceWriteHandle : IAsyncDisposable
{
    /// <summary>The identity the content receives when committed.</summary>
    InferenceResourceReference Reference { get; }
    /// <summary>The staging stream the operation writes the content to.</summary>
    Stream Content { get; }

    /// <summary>
    /// Publish complete content. SelectForSession also adds membership in the SAME atomic caller transaction.
    /// Cancellation or lost acknowledgment does not prove rollback. Never retry the commit implicitly.
    /// </summary>
    Task<InferenceResourceCommitResult> CommitAsync(
        long bytesWritten, InferenceResourceCommitOptions? options = null,
        CancellationToken cancellationToken = default);
}
