namespace Darkspyre.Inference.Tensor.Common;

/// <summary>Acquisition input, never a runtime loading path. Untrusted paths/URLs are validated by the source boundary.</summary>
public abstract record TensorModelLocation
{
    /// <summary>A model file on the local disk.</summary>
    /// <param name="Path">The file path.</param>
    public sealed record LocalFile(string Path) : TensorModelLocation;
    /// <summary>A folder on the local disk that holds a model and its companion files.</summary>
    /// <param name="Path">The folder path.</param>
    /// <param name="PrimaryArtifact">The file name of the primary artifact in the folder, or empty to find the single file that matches the family convention.</param>
    public sealed record LocalFolder(string Path, string PrimaryArtifact = "") : TensorModelLocation;
    /// <summary>A model file at a URL.</summary>
    /// <param name="Url">The file URL.</param>
    public sealed record RemoteFile(Uri Url) : TensorModelLocation;
    /// <summary>A model artifact in a repository source.</summary>
    /// <param name="SourceId">The id of the repository source.</param>
    /// <param name="Selection">The repository, revision and artifact path.</param>
    public sealed record RepositoryArtifact(string SourceId, TensorCandidateSelection Selection) : TensorModelLocation;
}

/// <summary>Where to acquire one artifact of a model pack, with its expected identity when known.</summary>
/// <param name="Role">The family role of the artifact, such as <c>model</c>.</param>
/// <param name="Location">Where to acquire the artifact.</param>
/// <param name="License">The artifact license, or null when unknown.</param>
/// <param name="ExpectedLength">The expected size in bytes, or null when unknown.</param>
/// <param name="ExpectedSha256">The expected SHA-256 hash, or null when unknown.</param>
public sealed record TensorArtifactLocation(string Role, TensorModelLocation Location,
    string? License = null, long? ExpectedLength = null, string? ExpectedSha256 = null);

/// <summary>Family-owned role and conventional folder filename. Defaults remain visible acquisition inputs.</summary>
public sealed record TensorArtifactRequirement(string Role, string FileName, TensorArtifactLocation? Default = null);

/// <summary>A request to import a model selection through a resolver.</summary>
/// <param name="Selection">The model to import.</param>
/// <param name="Resolver">The resolver that evaluates and resolves the selection.</param>
public sealed record TensorModelImportRequest(TensorModelSelection Selection, ITensorModelResolver Resolver);
/// <summary>The result of importing a model.</summary>
/// <param name="Resolution">The resolution of the imported selection.</param>
public sealed record TensorModelImportResult(TensorModelResolution Resolution);
