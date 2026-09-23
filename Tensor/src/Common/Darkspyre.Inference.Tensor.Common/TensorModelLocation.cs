namespace Darkspyre.Inference.Tensor.Common;

/// <summary>Acquisition input, never a runtime loading path. Untrusted paths/URLs are validated by the source boundary.</summary>
public abstract record TensorModelLocation
{
    public sealed record LocalFile(string Path) : TensorModelLocation;
    public sealed record LocalFolder(string Path, string PrimaryArtifact = "") : TensorModelLocation;
    public sealed record RemoteFile(Uri Url) : TensorModelLocation;
    public sealed record RepositoryArtifact(string SourceId, TensorCandidateSelection Selection) : TensorModelLocation;
}

public sealed record TensorArtifactLocation(string Role, TensorModelLocation Location,
    string? License = null, long? ExpectedLength = null, string? ExpectedSha256 = null);

/// <summary>Family-owned role and conventional folder filename. Defaults remain visible acquisition inputs.</summary>
public sealed record TensorArtifactRequirement(string Role, string FileName, TensorArtifactLocation? Default = null);

public sealed record TensorModelImportRequest(TensorModelSelection Selection, ITensorModelResolver Resolver);
public sealed record TensorModelImportResult(TensorModelResolution Resolution);
