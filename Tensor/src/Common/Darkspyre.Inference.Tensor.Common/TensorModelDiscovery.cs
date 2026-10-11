namespace Darkspyre.Inference.Tensor.Common;

/// <summary>The state of a source operation: available, or the reason it failed, such as a required sign-in, gated or denied access, a missing item, a transient failure, unverifiable content or an invalid request.</summary>
public enum TensorSourceState
{
    /// <summary>The operation succeeded and its result is usable.</summary>
    Available,
    /// <summary>The source requires a sign-in before it serves the item.</summary>
    AuthenticationRequired,
    /// <summary>The item requires accepting the repository's access terms first.</summary>
    Gated,
    /// <summary>The source or host refuses access to the item.</summary>
    AccessDenied,
    /// <summary>The source has no item at the requested location.</summary>
    NotFound,
    /// <summary>The operation failed for a temporary reason, and a later attempt can succeed.</summary>
    TransientFailure,
    /// <summary>The content does not match, or cannot be checked against, what the source reported for it.</summary>
    Unverified,
    /// <summary>The query, location, range or HTTP setup of the request is invalid.</summary>
    InvalidRequest
}
/// <summary>A problem a source reports.</summary>
/// <param name="Code">A stable, machine-readable code such as <c>source-timeout</c>.</param>
/// <param name="Message">A message for the user.</param>
public sealed record TensorSourceIssue(string Code, string Message);
/// <summary>The outcome of a source operation.</summary>
/// <typeparam name="T">The type of the result value.</typeparam>
/// <param name="State">Whether the operation succeeded, or why it did not.</param>
/// <param name="Value">The result, when <paramref name="State"/> is <see cref="TensorSourceState.Available"/>.</param>
/// <param name="Issues">Problems the source reported, or null when there are none.</param>
public sealed record TensorSourceResult<T>(TensorSourceState State, T? Value = default, IReadOnlyList<TensorSourceIssue>? Issues = null) where T : class;
/// <summary>One page of items from a source.</summary>
/// <typeparam name="T">The type of the items.</typeparam>
/// <param name="Items">The items on this page.</param>
/// <param name="Continuation">The token that requests the next page, or null on the last page.</param>
public sealed record TensorSourcePage<T>(IReadOnlyList<T> Items, string? Continuation = null);
/// <summary>Asks a source for one page of repositories matching a search.</summary>
/// <param name="Search">The search text.</param>
/// <param name="Continuation">The continuation token from the previous page, or null for the first page.</param>
/// <param name="PageSize">The maximum number of items to return.</param>
public sealed record TensorSourceQuery(string Search = "", string? Continuation = null, int PageSize = 50);
/// <summary>Asks a source for one page of a repository's revisions.</summary>
/// <param name="Repository">The repository name.</param>
/// <param name="Continuation">The continuation token from the previous page, or null for the first page.</param>
/// <param name="PageSize">The maximum number of items to return.</param>
public sealed record TensorRepositoryQuery(string Repository, string? Continuation = null, int PageSize = 50);
/// <summary>Asks a source for the selectable artifacts at one repository revision.</summary>
/// <param name="Repository">The repository name.</param>
/// <param name="Revision">The repository revision.</param>
public sealed record TensorVariantQuery(string Repository, string Revision);
/// <summary>Names the candidate to resolve.</summary>
/// <param name="Repository">The source repository, or null for a local file or URL.</param>
/// <param name="Revision">The repository revision, or null for a local file or URL.</param>
/// <param name="Variant">The path of the primary artifact.</param>
public sealed record TensorCandidateSelection(string? Repository, string? Revision, string Variant);
/// <summary>A repository returned by a source search.</summary>
/// <param name="Repository">The repository name.</param>
/// <param name="Revision">The repository's current revision, when the source reports it.</param>
/// <param name="License">The license the source reports, when known.</param>
/// <param name="IsPrivate">Whether the repository is private, or null when unknown.</param>
/// <param name="IsGated">Whether the repository requires accepting access terms, or null when unknown.</param>
public sealed record TensorSourceModel(string Repository, string? Revision, string? License, bool? IsPrivate, bool? IsGated);
/// <summary>One revision of a source repository.</summary>
/// <param name="Revision">The revision identifier.</param>
/// <param name="CreatedAt">When the revision was created, or null when unknown.</param>
public sealed record TensorSourceRevision(string Revision, DateTimeOffset? CreatedAt);

/// <summary>Repository metadata is evidence, not a promise that the checkpoint can load. Remote text is inert data.</summary>
public sealed record TensorCandidateMetadata(string? Architecture, long? ContextLength, bool? HasChatTemplate);
/// <summary>One file a source offers, with what the source reports about it.</summary>
/// <param name="Path">The file path inside the repository or location.</param>
/// <param name="Length">The file size in bytes, or null when the source does not report it.</param>
/// <param name="Sha256">The SHA-256 hash, or null when the source does not report it.</param>
/// <param name="Provenance">Where the file was discovered.</param>
/// <param name="DownloadUri">The URI the file is read or downloaded from.</param>
public sealed record TensorSourceArtifact(string Path, long? Length, string? Sha256, TensorArtifactProvenance Provenance, Uri DownloadUri);
/// <summary>A model found at a source, with the evidence and artifacts needed to resolve it into a pack.</summary>
/// <param name="SourceId">The id of the source that produced the candidate, such as a repository source id, <c>local</c> or <c>url</c>.</param>
/// <param name="Selection">The repository, revision and primary artifact the candidate describes.</param>
/// <param name="Metadata">Repository metadata read for the candidate.</param>
/// <param name="License">The license the source reports, when known.</param>
/// <param name="IsPrivate">Whether the repository is private, or null when unknown.</param>
/// <param name="IsGated">Whether the repository requires accepting access terms, or null when unknown.</param>
/// <param name="Artifacts">The artifacts available for the candidate.</param>
public sealed record TensorModelCandidate(
    string SourceId, TensorCandidateSelection Selection, TensorCandidateMetadata Metadata,
    string? License, bool? IsPrivate, bool? IsGated, IReadOnlyList<TensorSourceArtifact> Artifacts)
{
/// <summary>Maps each resolved artifact role to the path of the artifact that fills it.</summary>
    public IReadOnlyDictionary<string, string> ArtifactRoles { get; init; } = new Dictionary<string, string>();
}

/// <summary>The shape of a metadata read: <c>HeaderRange</c> reads a byte range from the start of a weights file, and <c>CompleteMetadataFile</c> reads a whole small metadata file from offset zero.</summary>
public enum TensorMetadataReadKind
{
    /// <summary>The read takes a bounded byte range from the start of a weights file.</summary>
    HeaderRange,
    /// <summary>The read takes a whole small metadata file from offset zero.</summary>
    CompleteMetadataFile
}
/// <summary>A bounded request for an artifact's metadata bytes.</summary>
/// <param name="Offset">The offset in the file of the first byte to read.</param>
/// <param name="Length">The maximum number of bytes to read.</param>
/// <param name="Kind">Whether the request reads a header range or the complete metadata file.</param>
public sealed record TensorMetadataRead(long Offset, int Length, TensorMetadataReadKind Kind = TensorMetadataReadKind.HeaderRange);
/// <summary>Metadata bytes read from an artifact.</summary>
/// <param name="Bytes">The bytes read.</param>
/// <param name="Offset">The offset in the file of the first byte read.</param>
/// <param name="TotalLength">The length of the whole file in bytes.</param>
public sealed record TensorMetadataContent(ReadOnlyMemory<byte> Bytes, long Offset, long TotalLength);

/// <summary>Read-only source discovery. None of these operations installs or loads model weights.</summary>
public interface ITensorArtifactReader
{
/// <summary>Reads a bounded metadata range or the complete metadata file of an artifact without downloading its weights.</summary>
    Task<TensorSourceResult<TensorMetadataContent>> ReadMetadataAsync(TensorSourceArtifact artifact, TensorMetadataRead read, CancellationToken cancellationToken = default);
}

/// <summary>Optional repository browsing adapter. File and URL access do not require a repository.</summary>
public interface ITensorModelSource : ITensorArtifactReader
{
/// <summary>The source id recorded as a candidate's <see cref="TensorModelCandidate.SourceId"/>.</summary>
    string Id { get; }
/// <summary>Searches the source for repositories and returns one page of matches.</summary>
    Task<TensorSourceResult<TensorSourcePage<TensorSourceModel>>> SearchAsync(TensorSourceQuery query, CancellationToken cancellationToken = default);
/// <summary>Lists one page of a repository's revisions.</summary>
    Task<TensorSourceResult<TensorSourcePage<TensorSourceRevision>>> ListRevisionsAsync(TensorRepositoryQuery query, CancellationToken cancellationToken = default);
/// <summary>Lists the artifacts at one repository revision that can be selected as a candidate's primary file.</summary>
    Task<TensorSourceResult<TensorSourcePage<TensorSourceArtifact>>> ListVariantsAsync(TensorVariantQuery query, CancellationToken cancellationToken = default);
/// <summary>Returns the candidate for a repository, revision and primary artifact, with its metadata and artifacts.</summary>
    Task<TensorSourceResult<TensorModelCandidate>> GetCandidateAsync(TensorCandidateSelection selection, CancellationToken cancellationToken = default);
}

/// <summary>
/// One-way, short-lived host credential. Never persist in model metadata or display in settings. It
/// exposes no secret through a public member; only the source HTTP boundary reads its header value.
/// </summary>
public sealed class TensorSourceCredential
{
    private TensorSourceCredential(string scheme, string parameter)
    {
        Scheme = scheme;
        Parameter = parameter;
    }

    internal string Scheme { get; }

    internal string Parameter { get; }

    /// <summary>A bearer token, sent as <c>Authorization: Bearer</c>.</summary>
    public static TensorSourceCredential Bearer(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Any(char.IsWhiteSpace) || token.Any(char.IsControl)) { throw new ArgumentException("Invalid bearer credential.", nameof(token)); }
        return new("Bearer", token);
    }

    /// <summary>A username and password, sent as <c>Authorization: Basic</c>.</summary>
    public static TensorSourceCredential Basic(string username, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentNullException.ThrowIfNull(password);
        if (username.Contains(':', StringComparison.Ordinal) || username.Any(char.IsControl))
        {
            throw new ArgumentException("Invalid Basic username.", nameof(username));
        }
        if (password.Any(char.IsControl)) { throw new ArgumentException("Invalid Basic password.", nameof(password)); }
        return new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{username}:{password}")));
    }

/// <summary>Returns a fixed placeholder, so formatting the credential never reveals its value.</summary>
    public override string ToString() => "[protected source credential]";
}

/// <summary>The callback receives the original, exact origin. Credentials are never forwarded across redirect origins.</summary>
public delegate Task<TensorSourceCredential?> TensorSourceAccess(Uri origin, CancellationToken cancellationToken);
