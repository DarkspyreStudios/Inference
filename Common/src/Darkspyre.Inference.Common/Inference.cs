namespace Darkspyre.Inference.Common;

/// <summary>The family an inference implementation belongs to.</summary>
public enum InferenceImplementationKind
{
    /// <summary>An implementation that calls a remote provider service.</summary>
    Connect,
    /// <summary>An implementation that runs a model locally on tensors.</summary>
    Tensor
}

/// <summary>The broad media kind an inference task works with.</summary>
public enum InferenceCapability
{
    /// <summary>Text output.</summary>
    Text,
    /// <summary>Image output.</summary>
    Image,
    /// <summary>Audio input or output.</summary>
    Audio,
    /// <summary>Decision output.</summary>
    Decision,
    /// <summary>Video output.</summary>
    Video = 4,
    /// <summary>Vector embedding output.</summary>
    Embedding = 5
}

/// <summary>A unit of work an inference implementation can perform.</summary>
public enum InferenceTask
{
    /// <summary>Generates text from a prompt or conversation.</summary>
    TextGeneration,
    /// <summary>Generates images.</summary>
    ImageGeneration,
    /// <summary>Synthesizes speech from text.</summary>
    SpeechSynthesis,
    /// <summary>Generates non-speech audio.</summary>
    AudioGeneration,
    /// <summary>Transcribes speech audio into text.</summary>
    SpeechTranscription,
    /// <summary>Produces a decision from a decision model.</summary>
    Decision,
    /// <summary>Generates video.</summary>
    VideoGeneration = 6,
    /// <summary>Runs a realtime two-way voice conversation.</summary>
    RealtimeVoice = 7,
    /// <summary>Produces vector embeddings.</summary>
    Embedding = 8
}

/// <summary>Extension methods for <see cref="InferenceTask"/>.</summary>
public static class InferenceTaskExtensions
{
    /// <summary>Returns the capability a task belongs to, and throws <see cref="ArgumentOutOfRangeException"/> for an unknown task.</summary>
    public static InferenceCapability GetCapability(this InferenceTask task) => task switch
    {
        InferenceTask.TextGeneration => InferenceCapability.Text,
        InferenceTask.ImageGeneration => InferenceCapability.Image,
        InferenceTask.SpeechSynthesis or InferenceTask.AudioGeneration or InferenceTask.SpeechTranscription or InferenceTask.RealtimeVoice
            => InferenceCapability.Audio,
        InferenceTask.Decision => InferenceCapability.Decision,
        InferenceTask.VideoGeneration => InferenceCapability.Video,
        InferenceTask.Embedding => InferenceCapability.Embedding,
        _ => throw new ArgumentOutOfRangeException(nameof(task))
    };
}

/// <summary>Describes an inference implementation: its identity, family and the tasks it performs.</summary>
public sealed record InferenceImplementationDescriptor
{
    /// <summary>Creates a descriptor, and throws <see cref="ArgumentException"/> when the id or display name is blank, no tasks are given, or a task or kind is undefined. Duplicate tasks are removed.</summary>
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

    /// <summary>The stable identifier of the implementation.</summary>
    public string Id { get; }
    /// <summary>The name shown to users for the implementation.</summary>
    public string DisplayName { get; }
    /// <summary>The family the implementation belongs to.</summary>
    public InferenceImplementationKind Kind { get; }
    /// <summary>The distinct tasks the implementation performs.</summary>
    public IReadOnlyList<InferenceTask> Tasks { get; }
}

/// <summary>Records which implementation, model and runtime produced an inference result.</summary>
public sealed record InferenceProvenance
{
    /// <summary>The identifier of the implementation that produced the result.</summary>
    public required string ImplementationId { get; init; }

    /// <summary>The identifier of the model used, or null when no model applies.</summary>
    public required string? ModelId { get; init; }

    /// <summary>The model revision used, when known.</summary>
    public string? ModelRevision { get; init; }

    /// <summary>The runtime that executed the model, when known.</summary>
    public string? Runtime { get; init; }

    /// <summary>The version of the runtime, when known.</summary>
    public string? RuntimeVersion { get; init; }

    /// <summary>The compute backend used, when known.</summary>
    public string? Backend { get; init; }

    /// <summary>The time the inference took, when measured.</summary>
    public TimeSpan? Elapsed { get; init; }
}

/// <summary>Classifies why an inference request failed.</summary>
public enum InferenceFailureCode
{
    /// <summary>The request is malformed or has invalid values.</summary>
    InvalidRequest,
    /// <summary>The implementation does not support a requested feature.</summary>
    UnsupportedFeature,
    /// <summary>The requested model is not available.</summary>
    ModelUnavailable,
    /// <summary>The model files are damaged or unreadable.</summary>
    ModelCorrupt,
    /// <summary>The compute backend or service is not available.</summary>
    BackendUnavailable,
    /// <summary>The backend has no capacity left for the request.</summary>
    CapacityExhausted,
    /// <summary>The inference started but failed while running.</summary>
    ExecutionFailed,
    /// <summary>The provider requires authentication.</summary>
    AuthenticationRequired,
    /// <summary>The provider denied access.</summary>
    AccessDenied,
    /// <summary>The request exceeded its time limit.</summary>
    TimedOut,
    /// <summary>The provider returned a response that breaks the expected protocol.</summary>
    ProtocolFailure
}

/// <summary>An inference failure that carries an <see cref="InferenceFailureCode"/>.</summary>
public sealed class InferenceException : Exception
{
    /// <summary>Creates an exception with a failure code and message.</summary>
    public InferenceException(InferenceFailureCode code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Creates an exception with a failure code, message and inner exception.</summary>
    public InferenceException(InferenceFailureCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>The classification of the failure.</summary>
    public InferenceFailureCode Code { get; }
}
