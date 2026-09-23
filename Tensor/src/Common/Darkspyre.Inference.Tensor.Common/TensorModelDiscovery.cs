namespace Darkspyre.Inference.Tensor.Common;

public enum TensorSourceState { Available, AuthenticationRequired, Gated, AccessDenied, NotFound, TransientFailure, Unverified, InvalidRequest }
public sealed record TensorSourceIssue(string Code, string Message);
public sealed record TensorSourceResult<T>(TensorSourceState State, T? Value = default, IReadOnlyList<TensorSourceIssue>? Issues = null) where T : class;
public sealed record TensorSourcePage<T>(IReadOnlyList<T> Items, string? Continuation = null);
public sealed record TensorSourceQuery(string Search = "", string? Continuation = null, int PageSize = 50);
public sealed record TensorRepositoryQuery(string Repository, string? Continuation = null, int PageSize = 50);
public sealed record TensorVariantQuery(string Repository, string Revision);
public sealed record TensorCandidateSelection(string Repository, string Revision, string Variant);
public sealed record TensorSourceModel(string Repository, string? Revision, string? License, bool? IsPrivate, bool? IsGated);
public sealed record TensorSourceRevision(string Revision, DateTimeOffset? CreatedAt);

/// <summary>Repository metadata is evidence, not a promise that the checkpoint can load. Remote text is inert data.</summary>
public sealed record TensorCandidateMetadata(string? Architecture, long? ContextLength, bool? HasChatTemplate);
public sealed record TensorSourceArtifact(string Path, long? Length, string? Sha256, TensorArtifactProvenance Provenance, Uri DownloadUri);
public sealed record TensorModelCandidate(
    string SourceId, TensorCandidateSelection Selection, TensorCandidateMetadata Metadata,
    string? License, bool? IsPrivate, bool? IsGated, IReadOnlyList<TensorSourceArtifact> Artifacts);

public enum TensorMetadataReadKind { HeaderRange, CompleteMetadataFile }
public sealed record TensorMetadataRead(long Offset, int Length, TensorMetadataReadKind Kind = TensorMetadataReadKind.HeaderRange);
public sealed record TensorMetadataContent(ReadOnlyMemory<byte> Bytes, long Offset, long TotalLength);

/// <summary>Read-only source discovery. None of these operations installs or loads model weights.</summary>
public interface ITensorModelSource
{
    string Id { get; }
    Task<TensorSourceResult<TensorSourcePage<TensorSourceModel>>> SearchAsync(TensorSourceQuery query, CancellationToken cancellationToken = default);
    Task<TensorSourceResult<TensorSourcePage<TensorSourceRevision>>> ListRevisionsAsync(TensorRepositoryQuery query, CancellationToken cancellationToken = default);
    Task<TensorSourceResult<TensorSourcePage<TensorSourceArtifact>>> ListVariantsAsync(TensorVariantQuery query, CancellationToken cancellationToken = default);
    Task<TensorSourceResult<TensorModelCandidate>> GetCandidateAsync(TensorCandidateSelection selection, CancellationToken cancellationToken = default);
    /// <summary>Explicit bounded inspection only. Unsupported range responses never trigger a full weight download.</summary>
    Task<TensorSourceResult<TensorMetadataContent>> ReadMetadataAsync(TensorSourceArtifact artifact, TensorMetadataRead read, CancellationToken cancellationToken = default);
}

/// <summary>One-way, short-lived host credential. Never persist in model metadata or display in settings.</summary>
public sealed class TensorSourceCredential
{
    public TensorSourceCredential(string bearerToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        if (bearerToken.Any(char.IsWhiteSpace) || bearerToken.Any(char.IsControl)) { throw new ArgumentException("Invalid bearer credential.", nameof(bearerToken)); }
        BearerToken = bearerToken;
    }
    public string BearerToken { get; }
    public override string ToString() => "[protected source credential]";
}

/// <summary>The callback receives the original, exact origin. Credentials are never forwarded across redirect origins.</summary>
public delegate ValueTask<TensorSourceCredential?> TensorSourceAccess(Uri origin, CancellationToken cancellationToken);
