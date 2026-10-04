using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Common.Resources;

/// <summary>An immutable content identity. The reference grants no access.</summary>
public sealed record InferenceResourceReference(string WorkspaceId, string Id)
{
    [JsonRequired]
    public string WorkspaceId { get; init; } = ResourceContract.RequiredId(WorkspaceId, nameof(WorkspaceId));

    [JsonRequired]
    public string Id { get; init; } = ResourceContract.RequiredId(Id, nameof(Id));
}

/// <summary>Observed content metadata. Null byte counts remain unknown.</summary>
public sealed record InferenceResourceDescriptor(
    InferenceResourceReference Reference,
    string? SessionId, string? FileName, string? ContentType, long? Bytes)
{
    [JsonRequired]
    public InferenceResourceReference Reference { get; init; } = Reference ?? throw new ArgumentNullException(nameof(Reference));

    public string? SessionId { get; init; } = ResourceContract.OptionalId(SessionId, nameof(SessionId));

    public string? FileName { get; init; } = FileName;

    public string? ContentType { get; init; } = ContentType;

    public long? Bytes { get; init; } = ResourceContract.Nonnegative(Bytes, nameof(Bytes));

    public override string ToString() => "InferenceResourceDescriptor { <resource metadata> }";
}

/// <summary>Metadata for new content. A null session selects workspace ownership.</summary>
public sealed record InferenceResourceCreate(
    string? SessionId, string? FileName = null,
    string? ContentType = null, long? ExpectedBytes = null)
{
    public string? SessionId { get; init; } = ResourceContract.OptionalId(SessionId, nameof(SessionId));

    public string? FileName { get; init; } = FileName;

    public string? ContentType { get; init; } = ContentType;

    public long? ExpectedBytes { get; init; } = ResourceContract.Nonnegative(ExpectedBytes, nameof(ExpectedBytes));

    public override string ToString() => "InferenceResourceCreate { <resource metadata> }";
}

/// <summary>A version belongs to one logical workspace/session, not a provider thread.</summary>
public sealed record InferenceResourceSelectionVersion(string WorkspaceId, string SessionId, long Revision)
{
    [JsonRequired]
    public string WorkspaceId { get; init; } = ResourceContract.RequiredId(WorkspaceId, nameof(WorkspaceId));

    [JsonRequired]
    public string SessionId { get; init; } = ResourceContract.RequiredId(SessionId, nameof(SessionId));

    [JsonRequired]
    public long Revision { get; init; } = ResourceContract.Nonnegative(Revision, nameof(Revision));
}

/// <summary>An ordered, copied membership snapshot from the caller-owned selection authority.</summary>
public sealed record InferenceResourceSelection(
    string WorkspaceId, string SessionId, long Revision,
    IReadOnlyList<InferenceResourceReference> SelectedResources)
{
    [JsonRequired]
    public string WorkspaceId { get; init; } = ResourceContract.RequiredId(WorkspaceId, nameof(WorkspaceId));

    [JsonRequired]
    public string SessionId { get; init; } = ResourceContract.RequiredId(SessionId, nameof(SessionId));

    [JsonRequired]
    public long Revision { get; init; } = ResourceContract.Nonnegative(Revision, nameof(Revision));

    private IReadOnlyList<InferenceResourceReference> _selectedResources = ResourceContract.CopySelection(WorkspaceId, SelectedResources);

    [JsonRequired]
    public IReadOnlyList<InferenceResourceReference> SelectedResources
    {
        get => _selectedResources;
        init => _selectedResources = ResourceContract.CopySelection(WorkspaceId, value);
    }
}

public sealed record InferenceResourceCommitOptions
{
    /// <summary>Publish content and additive session selection atomically in the caller store.</summary>
    public bool SelectForSession { get; init; }
}

public sealed record InferenceResourceCommitResult(
    InferenceResourceDescriptor Resource,
    InferenceResourceSelection? Selection)
{
    [JsonRequired]
    public InferenceResourceDescriptor Resource { get; init; } = Resource ?? throw new ArgumentNullException(nameof(Resource));

    public InferenceResourceSelection? Selection { get; init; } = Selection;
}

public enum InferenceResourceOperationPhase
{
    OpenRead = 0,
    Read = 1,
    OpenWrite = 2,
    Write = 3,
    Commit = 4,
    Materialize = 5,
    ReadSelection = 6,
    Select = 7,
    ReplaceSelection = 8,
    Cleanup = 9
}

public enum InferenceResourceCommitOutcome
{
    NotAttempted = 0,
    NotCommitted = 1,
    Committed = 2,
    Unknown = 3
}

/// <summary>Portable observed effects, not an exception or live storage graph.</summary>
public sealed record InferenceResourceOperationFailure(
    [property: JsonRequired] InferenceResourceOperationPhase Phase,
    InferenceResourceReference? Reference,
    [property: JsonRequired] InferenceResourceCommitOutcome CommitOutcome,
    long? BytesTransferred = null)
{
    public long? BytesTransferred { get; init; } = ResourceContract.Nonnegative(BytesTransferred, nameof(BytesTransferred));

    public InferenceResourceCommitOutcome SelectionOutcome { get; init; } = InferenceResourceCommitOutcome.NotAttempted;

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
