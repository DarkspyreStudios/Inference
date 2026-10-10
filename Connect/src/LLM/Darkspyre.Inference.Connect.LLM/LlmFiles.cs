using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Connect.LLM;

public sealed record LlmFileCapabilities
{
    public InferenceSupport Upload { get; init; }
    public InferenceSupport List { get; init; }
    public InferenceSupport Get { get; init; }
    public InferenceSupport Download { get; init; }
    public InferenceSupport Delete { get; init; }
    public InferenceSupport TextDownload { get; init; }
    public InferenceSupport PublicUrls { get; init; }
}
