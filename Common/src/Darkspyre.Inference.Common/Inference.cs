namespace Darkspyre.Inference.Common;

public enum InferenceImplementationKind
{
    Connect,
    Tensor
}

public enum InferenceCapability
{
    Text,
    Image,
    Audio,
    Decision
}

public enum InferenceTask
{
    TextGeneration,
    ImageGeneration,
    SpeechSynthesis,
    AudioGeneration,
    SpeechTranscription,
    Decision
}

public static class InferenceTaskExtensions
{
    public static InferenceCapability GetCapability(this InferenceTask task) => task switch
    {
        InferenceTask.TextGeneration => InferenceCapability.Text,
        InferenceTask.ImageGeneration => InferenceCapability.Image,
        InferenceTask.SpeechSynthesis or InferenceTask.AudioGeneration or InferenceTask.SpeechTranscription
            => InferenceCapability.Audio,
        InferenceTask.Decision => InferenceCapability.Decision,
        _ => throw new ArgumentOutOfRangeException(nameof(task))
    };
}

public sealed record InferenceImplementationDescriptor
{
    public InferenceImplementationDescriptor(
        string id, string displayName, InferenceImplementationKind kind, params InferenceTask[] tasks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(tasks);
        if (tasks.Length == 0 || tasks.Any(task => !Enum.IsDefined(task)) || !Enum.IsDefined(kind))
        {
            throw new ArgumentException("An implementation must declare valid tasks and family.", nameof(tasks));
        }

        Id = id;
        DisplayName = displayName;
        Kind = kind;
        Tasks = Array.AsReadOnly(tasks.Distinct().ToArray());
    }

    public string Id { get; }
    public string DisplayName { get; }
    public InferenceImplementationKind Kind { get; }
    public IReadOnlyList<InferenceTask> Tasks { get; }
}

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
    ExecutionFailed,
    AuthenticationRequired,
    AccessDenied,
    TimedOut,
    ProtocolFailure
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
