namespace Darkspyre.Inference.Common;

public enum InferenceImplementationKind
{
    Connect,
    Tensor
}

public sealed record InferenceImplementationDescriptor(
    string Id,
    string DisplayName,
    InferenceImplementationKind Kind);

public sealed record InferenceProvenance
{
    public required string ImplementationId { get; init; }

    public required string ModelId { get; init; }

    public string? ModelRevision { get; init; }

    public string? Runtime { get; init; }

    public string? RuntimeVersion { get; init; }

    public string? Backend { get; init; }

    public TimeSpan? Elapsed { get; init; }
}

public enum InferenceFailureCode
{
    InvalidRequest,
    UnsupportedFeature,
    ModelUnavailable,
    ModelCorrupt,
    BackendUnavailable,
    CapacityExhausted,
    ExecutionFailed
}

public sealed class InferenceException : Exception
{
    public InferenceException(InferenceFailureCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public InferenceException(InferenceFailureCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public InferenceFailureCode Code { get; }
}
