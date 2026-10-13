using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Support for each native file resource operation on a provider route.</summary>
public sealed record LlmFileCapabilities
{
    /// <summary>Uploading a file to the provider.</summary>
    public InferenceSupport Upload { get; init; }
    /// <summary>Listing the provider's stored files.</summary>
    public InferenceSupport List { get; init; }
    /// <summary>Reading one stored file's metadata.</summary>
    public InferenceSupport Get { get; init; }
    /// <summary>Downloading a stored file's content.</summary>
    public InferenceSupport Download { get; init; }
    /// <summary>Deleting a stored file.</summary>
    public InferenceSupport Delete { get; init; }
    /// <summary>Downloading a stored file's content as text.</summary>
    public InferenceSupport TextDownload { get; init; }
    /// <summary>Creating and revoking public URLs for a stored file.</summary>
    public InferenceSupport PublicUrls { get; init; }
}
