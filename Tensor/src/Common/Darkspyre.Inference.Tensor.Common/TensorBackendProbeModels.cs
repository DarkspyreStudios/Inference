namespace Darkspyre.Inference.Tensor.Common;

/// <summary>The managed package version and exact native bridge/upstream identity are distinct.</summary>
public sealed record TensorSharpBuildIdentity(string Version, string NativeAbi, string GgmlCommit);

/// <summary>One ordered execution candidate. The executing runtime validates its absolute bridge path.</summary>
public sealed record TensorNativeCandidate(string Id, string AbsoluteBridgePath, string Variant,
    TensorSharpBuildIdentity Build, string TargetRid);

/// <summary>
/// A bound probe supplies its trusted execution plan. Settings supply implementation and backend identity,
/// never executable paths. Null candidates select standard package probing; an empty list permits no
/// ambient native candidate discovery.
/// </summary>
public sealed record TensorBackendProbeRequest
{
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

    public Guid OperationId { get; }
    public string ImplementationId { get; }
    public TensorBackendKind RequestedBackend { get; }
    public string TargetRid { get; }
    public TensorSharpBuildIdentity? ExpectedTensorSharpBuild { get; }
    public IReadOnlyList<TensorNativeCandidate>? NativeCandidates { get; }
}

public enum TensorBackendAvailabilityState { Unknown, Unsupported, Unavailable, Available, RequiresProcessRestart }
public enum TensorBackendProbePhase { Validation, Load, Initialization }
public sealed record TensorBackendDeviceCapability(TensorBackendKind Backend, bool? IsAvailable);
public sealed record TensorNativeCandidatePresence(string CandidateId, bool? IsInstalled);
public sealed record TensorBackendCandidateDiagnostic(string? CandidateId, TensorBackendProbePhase Phase,
    string Code, string Message, bool NativeStateMayRemain = false);

/// <summary>
/// A prerequisite observation, not a model allocation guarantee. ActualBackend identifies a successfully
/// initialized backend only. Device detection, candidate presence and initialization remain separate.
/// </summary>
public sealed record TensorBackendAvailability
{
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

    public Guid OperationId { get; }
    public string ImplementationId { get; }
    public TensorBackendKind RequestedBackend { get; }
    public TensorBackendAvailabilityState State { get; }
    public bool IsAvailable => State == TensorBackendAvailabilityState.Available;
    public string Reason { get; }
    public DateTimeOffset ObservedAt { get; }
    public string TargetRid { get; }
    public TensorBackendKind? ActualBackend { get; }
    public TensorSharpBuildIdentity? TensorSharpBuild { get; }
    public string? LoadedCandidateId { get; }
    public bool? IsInitialized { get; }
    public IReadOnlyList<TensorBackendDeviceCapability> DetectedDeviceCapabilities { get; }
    public IReadOnlyList<TensorNativeCandidatePresence> CandidatePresence { get; }
    public IReadOnlyList<TensorBackendCandidateDiagnostic> CandidateDiagnostics { get; }

    public static TensorBackendAvailability Observe(TensorBackendProbeRequest request, TensorBackendAvailabilityState state,
        string reason, IReadOnlyList<TensorBackendDeviceCapability>? detectedDeviceCapabilities = null,
        IReadOnlyList<TensorBackendCandidateDiagnostic>? candidateDiagnostics = null) => new(
            request.OperationId, request.ImplementationId, request.RequestedBackend, state, reason, DateTimeOffset.UtcNow,
            request.TargetRid, detectedDeviceCapabilities: detectedDeviceCapabilities, candidateDiagnostics: candidateDiagnostics);
}

public interface ITensorBackendProbe
{
    Task<TensorBackendAvailability> CheckAsync(TensorBackendProbeRequest request, CancellationToken cancellationToken = default);
}
