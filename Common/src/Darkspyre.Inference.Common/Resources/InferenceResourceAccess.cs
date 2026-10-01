namespace Darkspyre.Inference.Common.Resources;

/// <summary>Borrowed access to one caller-owned workspace and logical session. Connect does not dispose it.</summary>
public interface IInferenceResourceAccess
{
    string WorkspaceId { get; }
    string SessionId { get; }

    Task<InferenceResourceDescriptor?> GetAsync(
        InferenceResourceReference reference, CancellationToken cancellationToken = default);

    Task<IInferenceResourceReadHandle> OpenReadAsync(
        InferenceResourceReference reference, CancellationToken cancellationToken = default);

    Task<IInferenceResourceWriteHandle> OpenWriteAsync(
        InferenceResourceCreate resource, CancellationToken cancellationToken = default);

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
    InferenceResourceDescriptor Resource { get; }
    Stream Content { get; }
}

/// <summary>One operation-owned staging stream and one commit attempt. Disposal does not delete committed content.</summary>
public interface IInferenceResourceWriteHandle : IAsyncDisposable
{
    InferenceResourceReference Reference { get; }
    Stream Content { get; }

    /// <summary>
    /// Publish complete content. SelectForSession also adds membership in the SAME atomic caller transaction.
    /// Cancellation or lost acknowledgment does not prove rollback. Never retry the commit implicitly.
    /// </summary>
    Task<InferenceResourceCommitResult> CommitAsync(
        long bytesWritten, InferenceResourceCommitOptions? options = null,
        CancellationToken cancellationToken = default);
}
