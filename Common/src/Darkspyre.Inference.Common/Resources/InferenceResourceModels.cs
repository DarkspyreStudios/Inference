using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Common.Resources;

/// <summary>An immutable content identity. The reference grants no access.</summary>
public sealed record InferenceResourceReference(string WorkspaceId, string Id)
{
    /// <summary>The workspace that holds the content.</summary>
    [JsonRequired]
    public string WorkspaceId { get; init; } = ResourceContract.RequiredId(WorkspaceId, nameof(WorkspaceId));

    /// <summary>The content identity within the workspace.</summary>
    [JsonRequired]
    public string Id { get; init; } = ResourceContract.RequiredId(Id, nameof(Id));
}

/// <summary>Observed content metadata. Null byte counts remain unknown.</summary>
public sealed record InferenceResourceDescriptor(
    InferenceResourceReference Reference,
    string? SessionId, string? FileName, string? ContentType, long? Bytes)
{
    /// <summary>The content this metadata describes.</summary>
    [JsonRequired]
    public InferenceResourceReference Reference { get; init; } = Reference ?? throw new ArgumentNullException(nameof(Reference));

    /// <summary>The session that owns the content, or null for workspace ownership.</summary>
    public string? SessionId { get; init; } = ResourceContract.OptionalId(SessionId, nameof(SessionId));

    /// <summary>The file name recorded with the content, when known.</summary>
    public string? FileName { get; init; } = FileName;

    /// <summary>The media type recorded with the content, when known.</summary>
    public string? ContentType { get; init; } = ContentType;

    /// <summary>The content size in bytes, or null when unknown.</summary>
    public long? Bytes { get; init; } = ResourceContract.Nonnegative(Bytes, nameof(Bytes));

    /// <summary>Returns a fixed text that leaves out the resource metadata, so logs never show it.</summary>
    public override string ToString() => "InferenceResourceDescriptor { <resource metadata> }";
}

/// <summary>Metadata for new content. A null session selects workspace ownership.</summary>
public sealed record InferenceResourceCreate(
    string? SessionId, string? FileName = null,
    string? ContentType = null, long? ExpectedBytes = null)
{
    /// <summary>The session that owns the new content, or null for workspace ownership.</summary>
    public string? SessionId { get; init; } = ResourceContract.OptionalId(SessionId, nameof(SessionId));

    /// <summary>The file name to record with the content.</summary>
    public string? FileName { get; init; } = FileName;

    /// <summary>The media type to record with the content.</summary>
    public string? ContentType { get; init; } = ContentType;

    /// <summary>The size the writer expects to write, in bytes, when known.</summary>
    public long? ExpectedBytes { get; init; } = ResourceContract.Nonnegative(ExpectedBytes, nameof(ExpectedBytes));

    /// <summary>Returns a fixed text that leaves out the resource metadata, so logs never show it.</summary>
    public override string ToString() => "InferenceResourceCreate { <resource metadata> }";
}

/// <summary>A version belongs to one logical workspace/session, not a provider thread.</summary>
public sealed record InferenceResourceSelectionVersion(string WorkspaceId, string SessionId, long Revision)
{
    /// <summary>The workspace the selection belongs to.</summary>
    [JsonRequired]
    public string WorkspaceId { get; init; } = ResourceContract.RequiredId(WorkspaceId, nameof(WorkspaceId));

    /// <summary>The session the selection belongs to.</summary>
    [JsonRequired]
    public string SessionId { get; init; } = ResourceContract.RequiredId(SessionId, nameof(SessionId));

    /// <summary>The selection revision.</summary>
    [JsonRequired]
    public long Revision { get; init; } = ResourceContract.Nonnegative(Revision, nameof(Revision));
}

/// <summary>An ordered, copied membership snapshot from the caller-owned selection authority.</summary>
public sealed record InferenceResourceSelection(
    string WorkspaceId, string SessionId, long Revision,
    IReadOnlyList<InferenceResourceReference> SelectedResources)
{
    /// <summary>The workspace the selection belongs to.</summary>
    [JsonRequired]
    public string WorkspaceId { get; init; } = ResourceContract.RequiredId(WorkspaceId, nameof(WorkspaceId));

    /// <summary>The session the selection belongs to.</summary>
    [JsonRequired]
    public string SessionId { get; init; } = ResourceContract.RequiredId(SessionId, nameof(SessionId));

    /// <summary>The selection revision this snapshot reflects.</summary>
    [JsonRequired]
    public long Revision { get; init; } = ResourceContract.Nonnegative(Revision, nameof(Revision));

    private IReadOnlyList<InferenceResourceReference> _selectedResources = ResourceContract.CopySelection(WorkspaceId, SelectedResources);

    /// <summary>The selected content in order. Each reference is unique and belongs to <see cref="WorkspaceId"/>.</summary>
    [JsonRequired]
    public IReadOnlyList<InferenceResourceReference> SelectedResources
    {
        get => _selectedResources;
        init => _selectedResources = ResourceContract.CopySelection(WorkspaceId, value);
    }
}

/// <summary>Options for committing written content.</summary>
public sealed record InferenceResourceCommitOptions
{
    /// <summary>Publish content and additive session selection atomically in the caller store.</summary>
    public bool SelectForSession { get; init; }
}

/// <summary>The receipt for committed content.</summary>
/// <param name="Resource">The committed content's metadata.</param>
/// <param name="Selection">The session selection after the commit, or null when the commit did not select the content.</param>
public sealed record InferenceResourceCommitResult(
    InferenceResourceDescriptor Resource,
    InferenceResourceSelection? Selection)
{
    /// <summary>The committed content's metadata.</summary>
    [JsonRequired]
    public InferenceResourceDescriptor Resource { get; init; } = Resource ?? throw new ArgumentNullException(nameof(Resource));

    /// <summary>The session selection after the commit, or null when the commit did not select the content.</summary>
    public InferenceResourceSelection? Selection { get; init; } = Selection;
}

/// <summary>The step of a resource operation at which a failure occurred.</summary>
public enum InferenceResourceOperationPhase
{
    /// <summary>Opening content for reading.</summary>
    OpenRead = 0,
    /// <summary>Reading content.</summary>
    Read = 1,
    /// <summary>Opening content for writing.</summary>
    OpenWrite = 2,
    /// <summary>Writing content.</summary>
    Write = 3,
    /// <summary>Committing written content.</summary>
    Commit = 4,
    /// <summary>Materializing content as a local file.</summary>
    Materialize = 5,
    /// <summary>Reading the session selection.</summary>
    ReadSelection = 6,
    /// <summary>Adding content to the session selection.</summary>
    Select = 7,
    /// <summary>Replacing the session selection.</summary>
    ReplaceSelection = 8,
    /// <summary>Cleaning up after the operation.</summary>
    Cleanup = 9
}

/// <summary>What is known about whether a commit took effect.</summary>
public enum InferenceResourceCommitOutcome
{
    /// <summary>No commit was attempted.</summary>
    NotAttempted = 0,
    /// <summary>The commit was attempted and did not take effect.</summary>
    NotCommitted = 1,
    /// <summary>The commit took effect.</summary>
    Committed = 2,
    /// <summary>Whether the commit took effect is unknown.</summary>
    Unknown = 3
}

/// <summary>Portable observed effects, not an exception or live storage graph.</summary>
public sealed record InferenceResourceOperationFailure(
    [property: JsonRequired] InferenceResourceOperationPhase Phase,
    InferenceResourceReference? Reference,
    [property: JsonRequired] InferenceResourceCommitOutcome CommitOutcome,
    long? BytesTransferred = null)
{
    /// <summary>The bytes moved before the failure, when known.</summary>
    public long? BytesTransferred { get; init; } = ResourceContract.Nonnegative(BytesTransferred, nameof(BytesTransferred));

    /// <summary>What is known about whether the selection change took effect.</summary>
    public InferenceResourceCommitOutcome SelectionOutcome { get; init; } = InferenceResourceCommitOutcome.NotAttempted;

    /// <summary>The selection version the operation observed, when known.</summary>
    public InferenceResourceSelectionVersion? SelectionVersion { get; init; }

    /// <summary>Ordered actual commit-and-selection receipts retained after a later failure.</summary>
    public IReadOnlyList<InferenceResourceCommitResult> GeneratedResources
    {
        get;
        init => field = ResourceContract.CopyGeneratedResources(value);
    } = [];
}

internal static class ResourceContract
{
    public static string RequiredId(string value, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, name);
        return value;
    }

    public static string? OptionalId(string? value, string name) => value is null ? null : RequiredId(value, name);

    public static long Nonnegative(long value, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, name);
        return value;
    }

    public static long? Nonnegative(long? value, string name) => value is null ? null : Nonnegative(value.Value, name);

    public static IReadOnlyList<InferenceResourceReference> CopySelection(
        string workspaceId, IReadOnlyList<InferenceResourceReference> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var seen = new HashSet<InferenceResourceReference>();
        var copy = new InferenceResourceReference[resources.Count];
        for (int index = 0; index < resources.Count; index++)
        {
            InferenceResourceReference reference = resources[index];
            ArgumentNullException.ThrowIfNull(reference);
            if (!string.Equals(reference.WorkspaceId, workspaceId, StringComparison.Ordinal) || !seen.Add(reference))
            {
                throw new ArgumentException("Selection references must be unique and belong to the same workspace.", nameof(resources));
            }
            copy[index] = reference;
        }
        return Array.AsReadOnly(copy);
    }

    public static IReadOnlyList<InferenceResourceCommitResult> CopyGeneratedResources(
        IReadOnlyList<InferenceResourceCommitResult> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var copy = new InferenceResourceCommitResult[resources.Count];
        for (int index = 0; index < resources.Count; index++)
        {
            InferenceResourceCommitResult receipt = resources[index];
            ArgumentNullException.ThrowIfNull(receipt);
            InferenceResourceDescriptor resource = receipt.Resource;
            ArgumentNullException.ThrowIfNull(resource);
            ArgumentNullException.ThrowIfNull(resource.Reference);
            InferenceResourceSelection selection = receipt.Selection
                ?? throw new ArgumentException("Generated resources require an actual commit-and-selection receipt.", nameof(resources));
            var reference = new InferenceResourceReference(resource.Reference.WorkspaceId, resource.Reference.Id);
            var selected = new InferenceResourceSelection(selection.WorkspaceId, selection.SessionId,
                selection.Revision, selection.SelectedResources);
            if (reference.WorkspaceId != selected.WorkspaceId
                || resource.SessionId is { } session && session != selected.SessionId
                || !selected.SelectedResources.Contains(reference))
                throw new ArgumentException("The generated resource does not belong to its committed selection.", nameof(resources));
            copy[index] = new(new(reference, resource.SessionId, resource.FileName, resource.ContentType, resource.Bytes), selected);
        }
        return Array.AsReadOnly(copy);
    }
}
