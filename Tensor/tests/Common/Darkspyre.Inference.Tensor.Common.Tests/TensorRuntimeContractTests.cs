using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorRuntimeContractTests
{
    private static readonly TensorSharpBuildIdentity Build = new("version", "abi", "ggml-commit");
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static TheoryData<string, string> RequiredRuntimeFacts => new()
    {
        { "File", "RelativePath" }, { "File", "Size" }, { "File", "Sha256" },
        { "Candidate", "Identity" }, { "Candidate", "Backend" }, { "Candidate", "Files" },
        { "Plan", "Build" }, { "Plan", "TargetRid" },
        { "Backend", "Backend" }, { "Backend", "Build" }, { "Backend", "TargetRid" },
        { "Backend", "Variant" }, { "Backend", "CandidateId" }, { "Backend", "NativeSourceCommit" },
        { "Diagnostic", "Code" }, { "Diagnostic", "Message" },
        { "Shutdown", "State" }, { "Shutdown", "OperationId" }, { "Shutdown", "ObservedAt" },
        { "Shutdown", "OutstandingLeases" }, { "Shutdown", "NativeStateMayRemain" }, { "Shutdown", "Diagnostics" }
    };

    [Theory]
    [MemberData(nameof(RequiredRuntimeFacts))]
    public void OwnedRuntimeMetadataRejectsEveryMissingRequiredFact(string family, string fact)
    {
        (string json, JsonTypeInfo metadata) = RuntimeFixture(family);
        JsonObject incomplete = JsonNode.Parse(json)!.AsObject();
        Assert.True(incomplete.Remove(fact));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(incomplete.ToJsonString(), metadata));
    }

    [Fact]
    public void PlansDeepSnapshotOrderedCandidatesFilesAndBuildIdentities()
    {
        TensorRuntimeNativeFile[] files = [new("native/bridge.so", 4, Hash), new("native/dependency.so", 0, Hash)];
        TensorRuntimeCandidate candidate = Candidate(files: files);
        TensorRuntimeCandidate[] candidates = [candidate, Candidate("second")];
        var plan = new TensorRuntimePlan(Build, "linux-x64", candidates);
        IReadOnlyList<TensorRuntimeCandidate> snapshot = plan.Candidates!;
        files[0] = new("changed.so", 99, Hash);
        candidates[0] = Candidate("replacement");
        Assert.Equal(["first", "second"], snapshot.Select(static item => item.Identity.Id).ToArray());
        Assert.Equal("native/bridge.so", snapshot[0].Files[0].RelativePath);
        Assert.Equal(4, snapshot[0].Files[0].Size);
        Assert.NotSame(Build, plan.Build);
        Assert.NotSame(candidate, snapshot[0]);
        Assert.NotSame(candidate.Identity, snapshot[0].Identity);
        Assert.NotSame(candidate.Identity.Build, snapshot[0].Identity.Build);
        Assert.NotSame(candidate.Files[0], snapshot[0].Files[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<TensorRuntimeCandidate>)snapshot)[0] = candidate);
        Assert.Throws<NotSupportedException>(() => ((IList<TensorRuntimeNativeFile>)candidate.Files)[0] = files[0]);
    }

    [Fact]
    public void NullAndEmptyCandidatesRemainDifferentPoliciesInOwnedJson()
    {
        var standard = new TensorRuntimePlan(Build, "linux-x64");
        var empty = new TensorRuntimePlan(Build, "linux-x64", []);
        Assert.Null(standard.Candidates);
        Assert.Empty(empty.Candidates!);
        Assert.Contains("\"Candidates\":null", JsonSerializer.Serialize(standard, TensorJson.TensorRuntimePlan), StringComparison.Ordinal);
        Assert.Contains("\"Candidates\":[]", JsonSerializer.Serialize(empty, TensorJson.TensorRuntimePlan), StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize(JsonSerializer.Serialize(standard, TensorJson.TensorRuntimePlan), TensorJson.TensorRuntimePlan)!.Candidates);
        Assert.Empty(JsonSerializer.Deserialize(JsonSerializer.Serialize(empty, TensorJson.TensorRuntimePlan), TensorJson.TensorRuntimePlan)!.Candidates!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../bridge.so")]
    [InlineData("native/../bridge.so")]
    [InlineData("native/./bridge.so")]
    [InlineData("native//bridge.so")]
    [InlineData("/native/bridge.so")]
    [InlineData("C:/native/bridge.dll")]
    [InlineData("native\\bridge.dll")]
    [InlineData("native/bridge.so:stream")]
    [InlineData("native/bridge\0.so")]
    [InlineData("native/<bridge.so")]
    [InlineData("native/>bridge.so")]
    [InlineData("native/\"bridge.so")]
    [InlineData("native/bridge|.so")]
    [InlineData("native/bridge?.so")]
    [InlineData("native/bridge*.so")]
    [InlineData("native/bridge.so.")]
    [InlineData("native/bridge.so ")]
    [InlineData("native./bridge.so")]
    [InlineData("native /bridge.so")]
    public void UnsafePortableFilePathsRefuseWithoutFilesystemWork(string path) =>
        Assert.Throws<ArgumentException>(() => new TensorRuntimeNativeFile(path, 0, Hash));

    [Fact]
    public void FileIntegrityDeclarationsRequireExactHashAndNonnegativeSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TensorRuntimeNativeFile("bridge.so", -1, Hash));
        Assert.Throws<ArgumentException>(() => new TensorRuntimeNativeFile("bridge.so", 0, "hash"));
        Assert.Throws<ArgumentException>(() => new TensorRuntimeNativeFile("bridge.so", 0, Hash.ToUpperInvariant()));
        Assert.Throws<ArgumentException>(() => new TensorRuntimeNativeFile("bridge.so", 0, new('g', 64)));
        Assert.Throws<ArgumentNullException>(() => new TensorRuntimeNativeFile("bridge.so", 0, null!));
        Assert.Equal(0, new TensorRuntimeNativeFile("native/bridge.so", 0, Hash).Size);
    }

    [Theory]
    [InlineData(TensorBackendKind.Auto)]
    [InlineData(TensorBackendKind.Cpu)]
    [InlineData(TensorBackendKind.Cuda)]
    [InlineData(TensorBackendKind.Mlx)]
    [InlineData(TensorBackendKind.CoreMl)]
    [InlineData((TensorBackendKind)999)]
    public void CandidateBackendIsNotAnEngineBackendAlias(TensorBackendKind backend) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Candidate(backend: backend));

    [Theory]
    [InlineData(TensorBackendKind.GgmlCpu)]
    [InlineData(TensorBackendKind.GgmlMetal)]
    [InlineData(TensorBackendKind.GgmlCuda)]
    [InlineData(TensorBackendKind.GgmlVulkan)]
    public void EveryConcreteGgmlCandidateIsAcceptedAsAnInertDeclaration(TensorBackendKind backend) =>
        Assert.Equal(backend, Candidate(backend: backend).Backend);

    [Fact]
    public void CandidateAndPlanRejectNullEmptyDuplicateAndMismatchedData()
    {
        TensorRuntimeCandidate candidate = Candidate();
        Assert.Throws<ArgumentNullException>(() => new TensorRuntimeCandidate(null!, TensorBackendKind.GgmlCpu, []));
        Assert.Throws<ArgumentNullException>(() => new TensorRuntimeCandidate(candidate.Identity, candidate.Backend, null!));
        Assert.Throws<ArgumentException>(() => Candidate(files: []));
        Assert.Throws<ArgumentException>(() => Candidate(files: [null!]));
        Assert.Throws<ArgumentException>(() => Candidate(files: [new("bridge.so", 0, Hash), new("bridge.so", 0, Hash)]));
        Assert.Throws<ArgumentException>(() => new TensorRuntimePlan(Build, "linux-x64", [null!]));
        Assert.Throws<ArgumentException>(() => new TensorRuntimePlan(Build, "linux-x64", [candidate, candidate]));
        Assert.Throws<ArgumentException>(() => new TensorRuntimePlan(Build with { NativeAbi = "other-abi" }, "linux-x64", [candidate]));
        Assert.Throws<ArgumentException>(() => new TensorRuntimePlan(Build with { Version = "other-version" }, "linux-x64", [candidate]));
        Assert.Throws<ArgumentException>(() => new TensorRuntimePlan(Build with { GgmlCommit = "other-commit" }, "linux-x64", [candidate]));
        Assert.Throws<ArgumentException>(() => new TensorRuntimePlan(Build, "Linux-x64", [candidate]));
        Assert.Throws<ArgumentNullException>(() => new TensorRuntimePlan(null!, "linux-x64"));
        Assert.Throws<ArgumentException>(() => new TensorRuntimePlan(Build with { NativeAbi = "" }, "linux-x64"));
        Assert.Throws<ArgumentException>(() => new TensorRuntimePlan(Build, ""));
        Assert.Throws<ArgumentException>(() => new TensorRuntimeCandidate(candidate.Identity with { Id = "" }, candidate.Backend, candidate.Files));
        Assert.Throws<ArgumentException>(() => new TensorRuntimeCandidate(candidate.Identity with { AbsoluteBridgePath = "" }, candidate.Backend, candidate.Files));
        Assert.Throws<ArgumentException>(() => new TensorRuntimeCandidate(candidate.Identity with { Variant = "" }, candidate.Backend, candidate.Files));
        Assert.Throws<ArgumentException>(() => new TensorRuntimeCandidate(candidate.Identity with { TargetRid = "" }, candidate.Backend, candidate.Files));
    }

    [Fact]
    public void ShutdownObservationsDeepSnapshotOrderedDiagnostics()
    {
        TensorRuntimeDiagnostic[] diagnostics = [new("first", "safe first"), new("second", "safe second")];
        var result = new TensorRuntimeShutdownResult(TensorRuntimeShutdownState.Busy, Guid.NewGuid(),
            DateTimeOffset.UnixEpoch, 2, true, diagnostics);
        diagnostics[0] = new("changed", "changed");
        Assert.Equal(["first", "second"], result.Diagnostics.Select(static item => item.Code).ToArray());
        Assert.NotSame(diagnostics[1], result.Diagnostics[1]);
        Assert.Throws<NotSupportedException>(() => ((IList<TensorRuntimeDiagnostic>)result.Diagnostics)[0] = diagnostics[0]);
        Assert.Throws<ArgumentException>(() => new TensorRuntimeShutdownResult(result.State, Guid.Empty, result.ObservedAt, 0, false, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TensorRuntimeShutdownResult(result.State, result.OperationId, result.ObservedAt, -1, false, []));
        Assert.Throws<ArgumentException>(() => new TensorRuntimeShutdownResult(result.State, result.OperationId, result.ObservedAt, 0, false, [null!]));
        Assert.Throws<ArgumentNullException>(() => new TensorRuntimeShutdownResult(result.State, result.OperationId, result.ObservedAt, 0, false, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TensorRuntimeShutdownResult((TensorRuntimeShutdownState)999, result.OperationId, result.ObservedAt, 0, false, []));
    }

    [Fact]
    public void SuccessfulBackendIdentityPreservesDistinctBuildAndNativeSourceFacts()
    {
        var actual = new TensorRuntimeBackendIdentity(TensorBackendKind.GgmlCpu, Build, "linux-x64", "cpu", null, "native-source");
        Assert.NotSame(Build, actual.Build);
        Assert.Equal("native-source", actual.NativeSourceCommit);
        Assert.Null(actual.CandidateId);
        Assert.Throws<ArgumentException>(() => new TensorRuntimeBackendIdentity(actual.Backend, Build, actual.TargetRid, actual.Variant, "", actual.NativeSourceCommit));
        Assert.Throws<ArgumentException>(() => new TensorRuntimeBackendIdentity(actual.Backend, Build, actual.TargetRid, actual.Variant, null, ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TensorRuntimeBackendIdentity(TensorBackendKind.Cpu, Build, actual.TargetRid, actual.Variant, null, actual.NativeSourceCommit));
    }

    [Fact]
    public void RuntimeDataOnlyExposesValidatedInitOrReadOnlyPropertiesAndNoOwnerMetadataRoot()
    {
        foreach (Type type in new[] { typeof(TensorRuntimeCandidate), typeof(TensorRuntimePlan) })
            Assert.All(type.GetProperties(), static property => Assert.False(property.SetMethod?.IsPublic is true));
        foreach (Type type in new[] { typeof(TensorRuntimeNativeFile), typeof(TensorRuntimeBackendIdentity),
            typeof(TensorRuntimeDiagnostic), typeof(TensorRuntimeShutdownResult) })
            Assert.All(type.GetProperties(), static property => Assert.Contains(typeof(System.Runtime.CompilerServices.IsExternalInit),
                property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers()));
        Assert.True(typeof(ITensorBackendProbe).IsAssignableFrom(typeof(ITensorRuntimeOwner)));
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(ITensorRuntimeOwner)));
        Assert.Null(TensorJson.TensorRuntimePlan.Options.TypeInfoResolver!.GetTypeInfo(typeof(ITensorRuntimeOwner), TensorJson.TensorRuntimePlan.Options));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], Enum.GetValues<TensorRuntimeState>().Select(static state => (int)state).ToArray());
        Assert.Equal([0, 1, 2, 3], Enum.GetValues<TensorRuntimeShutdownState>().Select(static state => (int)state).ToArray());
    }

    [Fact]
    public void PositionalRecordsPreserveNamedArgumentsDeconstructionAndValidatedWithCopies()
    {
        var file = new TensorRuntimeNativeFile(RelativePath: "bridge.so", Size: 0, Sha256: Hash);
        (string path, long size, string hash) = file;
        Assert.Equal("bridge.so", path);
        Assert.Equal(0, size);
        Assert.Equal(Hash, hash);
        Assert.Equal(1, (file with { Size = 1 }).Size);
        Assert.Equal(0, file.Size);
        Assert.Throws<ArgumentOutOfRangeException>(() => file with { Size = -1 });
        Assert.Throws<ArgumentException>(() => file with { RelativePath = "../bridge.so" });
        Assert.Throws<ArgumentException>(() => file with { Sha256 = "hash" });

        var actual = new TensorRuntimeBackendIdentity(Backend: TensorBackendKind.GgmlCpu, Build: Build,
            TargetRid: "linux-x64", Variant: "cpu", CandidateId: null, NativeSourceCommit: "native-source");
        (TensorBackendKind backend, TensorSharpBuildIdentity build, string rid, string variant, string? candidateId, string commit) = actual;
        Assert.Equal(TensorBackendKind.GgmlCpu, backend);
        Assert.Equal(Build, build);
        Assert.NotSame(Build, build);
        Assert.Equal("linux-x64", rid);
        Assert.Equal("cpu", variant);
        Assert.Null(candidateId);
        Assert.Equal("native-source", commit);
        var changed = actual with { Build = Build, CandidateId = "cpu" };
        Assert.NotSame(Build, changed.Build);
        Assert.Null(actual.CandidateId);
        Assert.Throws<ArgumentOutOfRangeException>(() => actual with { Backend = TensorBackendKind.Auto });
        Assert.Throws<ArgumentException>(() => actual with { Build = Build with { NativeAbi = "" } });
        Assert.Throws<ArgumentException>(() => actual with { TargetRid = "" });
        Assert.Throws<ArgumentException>(() => actual with { Variant = "" });
        Assert.Throws<ArgumentException>(() => actual with { CandidateId = "" });
        Assert.Throws<ArgumentException>(() => actual with { NativeSourceCommit = "" });

        var diagnostic = new TensorRuntimeDiagnostic(Code: "safe", Message: "Safe observation.");
        (string code, string message) = diagnostic;
        Assert.Equal("safe", code);
        Assert.Equal("Safe observation.", message);
        Assert.Equal("changed", (diagnostic with { Code = "changed" }).Code);
        Assert.Throws<ArgumentException>(() => diagnostic with { Code = "" });
        Assert.Throws<ArgumentException>(() => diagnostic with { Message = "" });

        TensorRuntimeDiagnostic[] diagnostics = [diagnostic];
        Guid operation = Guid.NewGuid();
        var shutdown = new TensorRuntimeShutdownResult(State: TensorRuntimeShutdownState.Completed, OperationId: operation,
            ObservedAt: DateTimeOffset.UnixEpoch, OutstandingLeases: 0, NativeStateMayRemain: false, Diagnostics: diagnostics);
        (TensorRuntimeShutdownState state, Guid id, DateTimeOffset observedAt, int leases, bool nativeMayRemain,
            IReadOnlyList<TensorRuntimeDiagnostic> observedDiagnostics) = shutdown;
        Assert.Equal(TensorRuntimeShutdownState.Completed, state);
        Assert.Equal(operation, id);
        Assert.Equal(DateTimeOffset.UnixEpoch, observedAt);
        Assert.Equal(0, leases);
        Assert.False(nativeMayRemain);
        Assert.NotSame(diagnostics, observedDiagnostics);
        Assert.NotSame(diagnostic, observedDiagnostics[0]);
        var copy = shutdown with { Diagnostics = diagnostics, OutstandingLeases = 1, NativeStateMayRemain = true };
        diagnostics[0] = diagnostic with { Code = "changed" };
        Assert.Equal("safe", copy.Diagnostics[0].Code);
        Assert.Equal("safe", shutdown.Diagnostics[0].Code);
        Assert.NotSame(copy.Diagnostics[0], shutdown.Diagnostics[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => shutdown with { State = (TensorRuntimeShutdownState)999 });
        Assert.Throws<ArgumentException>(() => shutdown with { OperationId = Guid.Empty });
        Assert.Throws<ArgumentOutOfRangeException>(() => shutdown with { OutstandingLeases = -1 });
        Assert.Throws<ArgumentNullException>(() => shutdown with { Diagnostics = null! });
        Assert.Throws<ArgumentException>(() => shutdown with { Diagnostics = [null!] });
        Assert.Throws<NotSupportedException>(() => ((IList<TensorRuntimeDiagnostic>)copy.Diagnostics)[0] = diagnostic);
    }

    [Fact]
    public void OptionalPlanCandidatesStillPermitOmissionButActualNullCandidateIdentityIsExplicit()
    {
        (string planJson, JsonTypeInfo planMetadata) = RuntimeFixture("Plan");
        JsonObject plan = JsonNode.Parse(planJson)!.AsObject();
        Assert.True(plan.Remove("Candidates"));
        Assert.Null(((TensorRuntimePlan)JsonSerializer.Deserialize(plan.ToJsonString(), planMetadata)!).Candidates);
        (string backendJson, JsonTypeInfo backendMetadata) = RuntimeFixture("Backend");
        Assert.Null(((TensorRuntimeBackendIdentity)JsonSerializer.Deserialize(backendJson, backendMetadata)!).CandidateId);
    }

    [Fact]
    public void PopulatedOwnedJsonCannotBypassCandidateAndPlanValidationOrReplaceSnapshots()
    {
        (string candidateJson, JsonTypeInfo candidateMetadata) = RuntimeFixture("Candidate");
        JsonObject invalid = JsonNode.Parse(candidateJson)!.AsObject();
        invalid["Backend"] = (int)TensorBackendKind.Cpu;
        Assert.Throws<ArgumentOutOfRangeException>(() => JsonSerializer.Deserialize(invalid.ToJsonString(), candidateMetadata));
        invalid = JsonNode.Parse(candidateJson)!.AsObject();
        invalid["Files"] = new JsonArray();
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize(invalid.ToJsonString(), candidateMetadata));
        invalid = JsonNode.Parse(candidateJson)!.AsObject();
        invalid["Files"] = new JsonArray((JsonNode?)null);
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize(invalid.ToJsonString(), candidateMetadata));
        invalid = JsonNode.Parse(candidateJson)!.AsObject();
        invalid["Identity"]!["Id"] = "";
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize(invalid.ToJsonString(), candidateMetadata));

        var original = new TensorRuntimePlan(Build, "linux-x64", [Candidate()]);
        string planJson = JsonSerializer.Serialize(original, TensorJson.TensorRuntimePlan);
        invalid = JsonNode.Parse(planJson)!.AsObject();
        invalid["Candidates"]![0]!["Identity"]!["TargetRid"] = "other-rid";
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize(invalid.ToJsonString(), TensorJson.TensorRuntimePlan));
        invalid = JsonNode.Parse(planJson)!.AsObject();
        invalid["Candidates"]![0]!["Identity"]!["Build"]!["NativeAbi"] = "other-abi";
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize(invalid.ToJsonString(), TensorJson.TensorRuntimePlan));
        invalid = JsonNode.Parse(planJson)!.AsObject();
        invalid["Candidates"]!.AsArray().Add(invalid["Candidates"]![0]!.DeepClone());
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize(invalid.ToJsonString(), TensorJson.TensorRuntimePlan));

        TensorRuntimePlan restored = JsonSerializer.Deserialize(planJson, TensorJson.TensorRuntimePlan)!;
        IReadOnlyList<TensorRuntimeCandidate> candidates = restored.Candidates!;
        Assert.Throws<NotSupportedException>(() => ((IList<TensorRuntimeCandidate>)candidates)[0] = Candidate());
        Assert.Throws<NotSupportedException>(() => ((IList<TensorRuntimeNativeFile>)candidates[0].Files)[0] = new("changed.so", 0, Hash));
        TensorRuntimeShutdownResult shutdown = (TensorRuntimeShutdownResult)JsonSerializer.Deserialize(RuntimeFixture("Shutdown").Json, TensorJson.TensorRuntimeShutdownResult)!;
        Assert.Throws<NotSupportedException>(() => ((IList<TensorRuntimeDiagnostic>)shutdown.Diagnostics).Add(new("changed", "Changed.")));
    }

    private static TensorRuntimeCandidate Candidate(string id = "first", TensorBackendKind backend = TensorBackendKind.GgmlCpu,
        IReadOnlyList<TensorRuntimeNativeFile>? files = null) => new(new(id, "/unused/bridge.so", "cpu", Build, "linux-x64"),
        backend, files ?? [new("bridge.so", 1, Hash)]);

    private static (string Json, JsonTypeInfo Metadata) RuntimeFixture(string family) => family switch
    {
        "File" => (JsonSerializer.Serialize(new TensorRuntimeNativeFile("bridge.so", 0, Hash), TensorJson.TensorRuntimeNativeFile), TensorJson.TensorRuntimeNativeFile),
        "Candidate" => (JsonSerializer.Serialize(Candidate(), TensorJson.TensorRuntimeCandidate), TensorJson.TensorRuntimeCandidate),
        "Plan" => (JsonSerializer.Serialize(new TensorRuntimePlan(Build, "linux-x64"), TensorJson.TensorRuntimePlan), TensorJson.TensorRuntimePlan),
        "Backend" => (JsonSerializer.Serialize(new TensorRuntimeBackendIdentity(TensorBackendKind.GgmlCpu, Build, "linux-x64", "cpu", null, "native-source"),
            TensorJson.TensorRuntimeBackendIdentity), TensorJson.TensorRuntimeBackendIdentity),
        "Diagnostic" => (JsonSerializer.Serialize(new TensorRuntimeDiagnostic("safe", "Safe observation."), TensorJson.TensorRuntimeDiagnostic), TensorJson.TensorRuntimeDiagnostic),
        "Shutdown" => (JsonSerializer.Serialize(new TensorRuntimeShutdownResult(TensorRuntimeShutdownState.Completed, Guid.NewGuid(), DateTimeOffset.UnixEpoch, 0, false, []),
            TensorJson.TensorRuntimeShutdownResult), TensorJson.TensorRuntimeShutdownResult),
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };
}
