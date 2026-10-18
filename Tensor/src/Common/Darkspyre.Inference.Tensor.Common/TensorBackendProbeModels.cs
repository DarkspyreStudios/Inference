namespace Darkspyre.Inference.Tensor.Common;

/// <summary>The TensorSharp release a runtime plan is bound to.</summary>
/// <param name="Version">The TensorSharp package version.</param>
public sealed record TensorSharpBuildIdentity(string Version);

/// <summary>One ordered execution candidate. The executing runtime validates its absolute bridge path.</summary>
/// <param name="Id">The candidate's identity.</param>
/// <param name="AbsoluteBridgePath">The absolute path of the candidate's native bridge library.</param>
/// <param name="Variant">The candidate's native build variant.</param>
/// <param name="Build">The managed and native build identity of the candidate.</param>
/// <param name="TargetRid">The runtime identifier the candidate is built for.</param>
public sealed record TensorNativeCandidate(string Id, string AbsoluteBridgePath, string Variant,
    TensorSharpBuildIdentity Build, string TargetRid);

/// <summary>
/// A bound probe supplies its trusted execution plan. Settings supply implementation and backend identity,
/// never executable paths. Null candidates select standard package probing; an empty list permits no
/// ambient native candidate discovery.
/// </summary>
public sealed record TensorBackendProbeRequest
{
    /// <summary>Creates a probe request. Throws <see cref="ArgumentException"/> when the operation identity is empty, an identifier is blank or the candidate list contains null.</summary>
    public TensorBackendProbeRequest(Guid operationId, string implementationId, TensorBackendKind requestedBackend,
        string targetRid, TensorSharpBuildIdentity? expectedTensorSharpBuild = null,
        IReadOnlyList<TensorNativeCandidate>? nativeCandidates = null)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A probe operation requires a nonempty identity.", nameof(operationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(implementationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRid);
        OperationId = operationId;
        ImplementationId = implementationId;
        RequestedBackend = requestedBackend;
        TargetRid = targetRid;
        ExpectedTensorSharpBuild = expectedTensorSharpBuild;
        NativeCandidates = nativeCandidates is null ? null : Array.AsReadOnly(nativeCandidates.ToArray());
        if (NativeCandidates?.Any(candidate => candidate is null) == true)
            throw new ArgumentException("A native candidate list cannot contain null entries.", nameof(nativeCandidates));
    }

    /// <summary>Identifies the probe operation.</summary>
    public Guid OperationId { get; }
    /// <summary>The inference implementation to probe.</summary>
    public string ImplementationId { get; }
    /// <summary>The backend the caller asked for.</summary>
    public TensorBackendKind RequestedBackend { get; }
    /// <summary>The runtime identifier the native code targets.</summary>
    public string TargetRid { get; }
    /// <summary>The TensorSharp build the probe expects to load, or null for no check.</summary>
    public TensorSharpBuildIdentity? ExpectedTensorSharpBuild { get; }
    /// <summary>The native candidates to try in order, or null for standard package probing.</summary>
    public IReadOnlyList<TensorNativeCandidate>? NativeCandidates { get; }
}

/// <summary>The availability of a tensor backend.</summary>
public enum TensorBackendAvailabilityState
{
    /// <summary>Availability has not been determined.</summary>
    Unknown,
    /// <summary>The implementation does not support the backend.</summary>
    Unsupported,
    /// <summary>The backend is supported but cannot run here.</summary>
    Unavailable,
    /// <summary>The backend can run.</summary>
    Available,
    /// <summary>A previous native failure left state behind, so the process must restart before the backend can run.</summary>
    RequiresProcessRestart
}

/// <summary>A phase of a backend probe.</summary>
public enum TensorBackendProbePhase
{
    /// <summary>Checking the candidate before loading it.</summary>
    Validation,
    /// <summary>Loading the native library.</summary>
    Load,
    /// <summary>Initializing the backend.</summary>
    Initialization
}
/// <summary>Whether a device for one backend was detected.</summary>
/// <param name="Backend">The backend.</param>
/// <param name="IsAvailable">Whether a device for the backend is available, or null when unknown.</param>
public sealed record TensorBackendDeviceCapability(TensorBackendKind Backend, bool? IsAvailable);
/// <summary>Whether one native candidate is installed.</summary>
/// <param name="CandidateId">The candidate identifier.</param>
/// <param name="IsInstalled">Whether the candidate is installed, or null when unknown.</param>
public sealed record TensorNativeCandidatePresence(string CandidateId, bool? IsInstalled);
/// <summary>A failure recorded while probing one native candidate.</summary>
/// <param name="CandidateId">The candidate the failure concerns, or null when it concerns none.</param>
/// <param name="Phase">The probe phase that failed.</param>
/// <param name="Code">A stable code for the failure.</param>
/// <param name="Message">A description of the failure.</param>
/// <param name="NativeStateMayRemain">Whether native state may remain loaded in the process after the failure.</param>
public sealed record TensorBackendCandidateDiagnostic(string? CandidateId, TensorBackendProbePhase Phase,
    string Code, string Message, bool NativeStateMayRemain = false);

/// <summary>
/// A prerequisite observation, not a model allocation guarantee. ActualBackend identifies a successfully
/// initialized backend only. Device detection, candidate presence and initialization remain separate.
/// </summary>
public sealed record TensorBackendAvailability
{
    /// <summary>Creates an availability observation. Throws <see cref="ArgumentException"/> when <paramref name="actualBackend"/> and <paramref name="isInitialized"/> disagree.</summary>
    public TensorBackendAvailability(Guid operationId, string implementationId, TensorBackendKind requestedBackend,
        TensorBackendAvailabilityState state, string reason, DateTimeOffset observedAt, string targetRid,
        TensorBackendKind? actualBackend = null, TensorSharpBuildIdentity? tensorSharpBuild = null,
        string? loadedCandidateId = null, bool? isInitialized = null,
        IReadOnlyList<TensorBackendDeviceCapability>? detectedDeviceCapabilities = null,
        IReadOnlyList<TensorNativeCandidatePresence>? candidatePresence = null,
        IReadOnlyList<TensorBackendCandidateDiagnostic>? candidateDiagnostics = null)
    {
        if (actualBackend is not null && (isInitialized != true || actualBackend == TensorBackendKind.Auto || !Enum.IsDefined(actualBackend.Value)))
            throw new ArgumentException("An actual backend requires a successful initialization and a concrete backend identity.", nameof(actualBackend));
        if (isInitialized == true && actualBackend is null)
            throw new ArgumentException("Successful initialization requires the actual backend identity.", nameof(actualBackend));
        OperationId = operationId;
        ImplementationId = implementationId;
        RequestedBackend = requestedBackend;
        State = state;
        Reason = reason;
        ObservedAt = observedAt;
        TargetRid = targetRid;
        ActualBackend = actualBackend;
        TensorSharpBuild = tensorSharpBuild;
        LoadedCandidateId = loadedCandidateId;
        IsInitialized = isInitialized;
        DetectedDeviceCapabilities = Array.AsReadOnly((detectedDeviceCapabilities ?? []).ToArray());
        CandidatePresence = Array.AsReadOnly((candidatePresence ?? []).ToArray());
        CandidateDiagnostics = Array.AsReadOnly((candidateDiagnostics ?? []).ToArray());
    }

    /// <summary>Identifies the probe operation.</summary>
    public Guid OperationId { get; }
    /// <summary>The inference implementation probed.</summary>
    public string ImplementationId { get; }
    /// <summary>The backend the caller asked for.</summary>
    public TensorBackendKind RequestedBackend { get; }
    /// <summary>The availability of the requested backend.</summary>
    public TensorBackendAvailabilityState State { get; }
    /// <summary>True when <see cref="State"/> is <see cref="TensorBackendAvailabilityState.Available"/>.</summary>
    public bool IsAvailable => State == TensorBackendAvailabilityState.Available;
    /// <summary>A description of the result for the user.</summary>
    public string Reason { get; }
    /// <summary>When the observation was made.</summary>
    public DateTimeOffset ObservedAt { get; }
    /// <summary>The runtime identifier the native code targets.</summary>
    public string TargetRid { get; }
    /// <summary>The backend that initialized, or null when none did.</summary>
    public TensorBackendKind? ActualBackend { get; }
    /// <summary>The TensorSharp build that loaded, or null when none did.</summary>
    public TensorSharpBuildIdentity? TensorSharpBuild { get; }
    /// <summary>The native candidate that loaded, or null when none did.</summary>
    public string? LoadedCandidateId { get; }
    /// <summary>Whether the backend initialized, or null when not attempted.</summary>
    public bool? IsInitialized { get; }
    /// <summary>The devices detected for each backend.</summary>
    public IReadOnlyList<TensorBackendDeviceCapability> DetectedDeviceCapabilities { get; }
    /// <summary>Whether each native candidate is installed.</summary>
    public IReadOnlyList<TensorNativeCandidatePresence> CandidatePresence { get; }
    /// <summary>The failures recorded for native candidates.</summary>
    public IReadOnlyList<TensorBackendCandidateDiagnostic> CandidateDiagnostics { get; }

    /// <summary>Creates an observation for a request, stamped with the current time, without initialization details.</summary>
    public static TensorBackendAvailability Observe(TensorBackendProbeRequest request, TensorBackendAvailabilityState state,
        string reason, IReadOnlyList<TensorBackendDeviceCapability>? detectedDeviceCapabilities = null,
        IReadOnlyList<TensorBackendCandidateDiagnostic>? candidateDiagnostics = null) => new(
            request.OperationId, request.ImplementationId, request.RequestedBackend, state, reason, DateTimeOffset.UtcNow,
            request.TargetRid, detectedDeviceCapabilities: detectedDeviceCapabilities, candidateDiagnostics: candidateDiagnostics);
}

/// <summary>Checks whether a tensor backend can run in this process.</summary>
public interface ITensorBackendProbe
{
    /// <summary>Probes the requested backend and returns what was observed.</summary>
    Task<TensorBackendAvailability> CheckAsync(TensorBackendProbeRequest request, CancellationToken cancellationToken = default);
}
