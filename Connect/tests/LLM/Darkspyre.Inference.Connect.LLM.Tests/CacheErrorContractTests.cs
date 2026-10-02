using System.Reflection;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class CacheErrorContractTests
{
    [Theory]
    [InlineData("LlmContextCacheUnavailableException", false)]
    [InlineData("LlmContextCacheMutationUncertainException", false)]
    [InlineData("LlmContextCacheMutationCancelledException", true)]
    public void RequiredPublicErrorsExistWithoutJsonRoots(string name, bool cancelled)
    {
        Type? type = typeof(ConnectLlmJson).Assembly.GetType("Darkspyre.Inference.Connect.LLM." + name);
        Assert.NotNull(type);
        Assert.True(type.IsPublic);
        Assert.True(type.IsSealed);
        Assert.True((cancelled ? typeof(OperationCanceledException) : typeof(Exception)).IsAssignableFrom(type));
        Assert.Null(typeof(ConnectLlmJson).GetProperty(name));
    }

    public static TheoryData<string, int> UndefinedValues => new()
    {
        { "LlmContextCacheUnavailableReason", -1 }, { "LlmContextCacheUnavailableReason", 99 },
        { "LlmContextCacheUnavailableReason", int.MinValue }, { "LlmContextCacheUnavailableReason", int.MaxValue },
        { "LlmContextCacheMutation", -1 }, { "LlmContextCacheMutation", 99 },
        { "LlmContextCacheMutation", int.MinValue }, { "LlmContextCacheMutation", int.MaxValue }
    };

    [Theory, MemberData(nameof(UndefinedValues))]
    public void UndefinedOwnedReadRefuses(string name, int number)
    {
        JsonTypeInfo metadata = Metadata(name);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(number.ToString(System.Globalization.CultureInfo.InvariantCulture), metadata));
    }

    [Theory, MemberData(nameof(UndefinedValues))]
    public void UndefinedOwnedWriteRefuses(string name, int number)
    {
        JsonTypeInfo metadata = Metadata(name);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(Enum.ToObject(metadata.Type, number), metadata));
    }

    [Theory]
    [InlineData("LlmContextCacheUnavailableReason")]
    [InlineData("LlmContextCacheMutation")]
    public void DefinedNumericValuesRoundTrip(string name)
    {
        JsonTypeInfo metadata = Metadata(name);
        foreach (int value in new[] { 0, 1 })
        {
            string json = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            object restored = JsonSerializer.Deserialize(json, metadata)!;
            Assert.Equal(value, Convert.ToInt32(restored, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(json, JsonSerializer.Serialize(restored, metadata));
        }
    }

    [Theory]
    [InlineData(LlmContextCacheUnavailableReason.Missing)]
    [InlineData(LlmContextCacheUnavailableReason.Expired)]
    public void UnavailablePreservesOnlySuppliedFactsAndSafeDiagnostics(LlmContextCacheUnavailableReason reason)
    {
        var reference = Reference();
        var safe = new LlmProviderException("Safe provider failure.", LlmTransport.GoogleGeminiApi, HttpStatusCode.NotFound, requestId: "actual-request");
        var error = new LlmContextCacheUnavailableException(reason, reference, HttpStatusCode.NotFound, "actual-request", safe);
        Assert.Equal(reason, error.Reason);
        Assert.Same(reference, error.Reference);
        Assert.Same(safe, error.InnerException);
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal("actual-request", error.RequestId);
        Assert.Equal("Explicit cache is unavailable; reconcile explicitly.", error.Message);
        Assert.DoesNotContain("private-resource", error.ToString());
        Assert.DoesNotContain("private-model", error.ToString());
        Assert.DoesNotContain("private-scope", error.ToString());
        var unknown = new LlmContextCacheUnavailableException(reason, reference);
        Assert.Null(unknown.StatusCode); Assert.Null(unknown.RequestId); Assert.Null(unknown.InnerException);
    }

    [Fact]
    public void InvalidUnavailableDataRefuses()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmContextCacheUnavailableException((LlmContextCacheUnavailableReason)99, Reference()));
        Assert.Throws<ArgumentNullException>(() => new LlmContextCacheUnavailableException(LlmContextCacheUnavailableReason.Missing, null!));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public void MissingRejectsContradictoryHttpEvidence(HttpStatusCode status)
    {
        Assert.Throws<ArgumentException>(() => new LlmContextCacheUnavailableException(LlmContextCacheUnavailableReason.Missing, Reference(), status));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpdateRejectsContradictoryObservedExpiry(bool cancelled)
    {
        var reference = Reference();
        var invalid = Receipt(reference) with { Reference = reference };
        var safe = new InvalidOperationException("Safe failure.");
        if (cancelled)
            Assert.Throws<ArgumentException>(() => new LlmContextCacheMutationCancelledException(LlmContextCacheMutation.UpdateExpiry, reference, default, safe, observedUpdate: invalid));
        else
            Assert.Throws<ArgumentException>(() => new LlmContextCacheMutationUncertainException(LlmContextCacheMutation.UpdateExpiry, reference, safe, observedUpdate: invalid));
    }

    [Fact]
    public void UnknownReferenceExpiryRemainsUnknownInObservedUpdate()
    {
        var reference = Reference();
        var updated = Receipt(reference) with { Reference = reference with { ExpiresAt = null } };
        var safe = new InvalidOperationException("Safe failure.");
        Assert.Same(updated, new LlmContextCacheMutationUncertainException(LlmContextCacheMutation.UpdateExpiry, reference, safe, observedUpdate: updated).ObservedUpdate);
        Assert.Same(updated, new LlmContextCacheMutationCancelledException(LlmContextCacheMutation.UpdateExpiry, reference, default, safe, observedUpdate: updated).ObservedUpdate);
    }

    [Theory]
    [InlineData(LlmContextCacheMutation.UpdateExpiry, false)]
    [InlineData(LlmContextCacheMutation.UpdateExpiry, true)]
    [InlineData(LlmContextCacheMutation.Delete, false)]
    [InlineData(LlmContextCacheMutation.Delete, true)]
    public void MutationEvidenceAndCancellationRemainActual(LlmContextCacheMutation operation, bool evidence)
    {
        var reference = Reference();
        var updated = evidence && operation == LlmContextCacheMutation.UpdateExpiry ? Receipt(reference) : null;
        var deleted = evidence && operation == LlmContextCacheMutation.Delete ? new LlmContextCacheDeleteResult(false) : null;
        var safe = new AggregateException(new LlmProviderException("Safe provider failure.", LlmTransport.GoogleGeminiApi, HttpStatusCode.BadGateway), new InvalidOperationException("Safe cleanup failure."));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var uncertain = new LlmContextCacheMutationUncertainException(operation, reference, safe, "actual-request", updated, deleted);
        var cancelled = new LlmContextCacheMutationCancelledException(operation, reference, cancellation.Token, safe, "actual-request", updated, deleted);
        foreach (Exception error in new Exception[] { uncertain, cancelled })
        {
            Assert.Same(safe, error.InnerException);
            Assert.Equal("Cache mutation may have committed; reconcile explicitly.", error.Message);
            Assert.DoesNotContain("private-resource", error.ToString());
            Assert.DoesNotContain("private-model", error.ToString());
            Assert.DoesNotContain("private-scope", error.ToString());
        }
        Assert.Equal(operation, uncertain.Operation); Assert.Equal(operation, cancelled.Operation);
        Assert.Same(reference, uncertain.Reference); Assert.Same(reference, cancelled.Reference);
        Assert.Same(updated, uncertain.ObservedUpdate); Assert.Same(updated, cancelled.ObservedUpdate);
        Assert.Same(deleted, uncertain.ObservedDelete); Assert.Same(deleted, cancelled.ObservedDelete);
        Assert.Equal("actual-request", uncertain.RequestId); Assert.Equal("actual-request", cancelled.RequestId);
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        Assert.IsAssignableFrom<OperationCanceledException>(cancelled);
        if (updated is not null) Assert.NotEqual(reference.ExpiresAt, updated.ExpiresAt);
        if (deleted is not null) Assert.False(deleted.AlreadyAbsent);
        var withoutFacts = new LlmContextCacheMutationCancelledException(operation, reference, cancellation.Token);
        Assert.Null(withoutFacts.InnerException); Assert.Null(withoutFacts.RequestId);
        Assert.Null(withoutFacts.ObservedUpdate); Assert.Null(withoutFacts.ObservedDelete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidMutationEvidenceRefuses(bool cancelled)
    {
        var reference = Reference();
        Exception safe = new InvalidOperationException("Safe failure.");
        void Construct(LlmContextCacheMutation operation, LlmExplicitCacheReference selected, LlmContextCache? update, LlmContextCacheDeleteResult? deletion)
        {
            if (cancelled) _ = new LlmContextCacheMutationCancelledException(operation, selected, default, safe, observedUpdate: update, observedDelete: deletion);
            else _ = new LlmContextCacheMutationUncertainException(operation, selected, safe, observedUpdate: update, observedDelete: deletion);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => Construct((LlmContextCacheMutation)99, reference, null, null));
        Assert.Throws<ArgumentNullException>(() => Construct(LlmContextCacheMutation.Delete, null!, null, null));
        Assert.Throws<ArgumentException>(() => Construct(LlmContextCacheMutation.Delete, reference, Receipt(reference), null));
        Assert.Throws<ArgumentException>(() => Construct(LlmContextCacheMutation.UpdateExpiry, reference, null, new(false)));
        Assert.Throws<ArgumentException>(() => Construct(LlmContextCacheMutation.UpdateExpiry, reference, Receipt(reference), new(false)));
        foreach (var other in new[] { reference with { ResourceName = "other" }, reference with { ModelId = "other" }, reference with { ScopeId = "other" } })
            Assert.Throws<ArgumentException>(() => Construct(LlmContextCacheMutation.UpdateExpiry, reference, Receipt(other), null));
        Assert.Throws<ArgumentNullException>(() => new LlmContextCacheMutationUncertainException(LlmContextCacheMutation.Delete, reference, null!));
    }

    [Fact]
    public void ObservedAlreadyAbsentIsNotConfusedWithFalseOrUnknown()
    {
        foreach (bool absent in new[] { false, true })
        {
            var receipt = new LlmContextCacheDeleteResult(absent);
            var error = new LlmContextCacheMutationUncertainException(LlmContextCacheMutation.Delete, Reference(), new InvalidOperationException("Safe failure."), observedDelete: receipt);
            Assert.Equal(absent, error.ObservedDelete!.AlreadyAbsent);
        }
    }

    [Fact]
    public void RuntimeExceptionsAreNotOwnedPortableRoots()
    {
        foreach (Type type in new[] { typeof(LlmContextCacheUnavailableException), typeof(LlmContextCacheMutationUncertainException), typeof(LlmContextCacheMutationCancelledException) })
            Assert.Throws<NotSupportedException>(() => ConnectLlmJson.LlmContextCacheMutation.Options.GetTypeInfo(type));
    }

    [Fact]
    public async Task SafeGoogleBoundaryInnerFailureCannotLeakUntrustedContentIntoNewErrors()
    {
        using var handler = new MaliciousHandler();
        using var http = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/cachedContents/example");
        Exception safe = await Assert.ThrowsAsync<LlmProviderException>(() => GoogleCacheHttp.GetAsync(http, request, LlmTransport.GoogleGeminiApi,
            _ => throw new InvalidOperationException("unreachable"), _ => { }, default));
        var errors = new Exception[]
        {
            new LlmContextCacheUnavailableException(LlmContextCacheUnavailableReason.Expired, Reference(), innerException: safe),
            new LlmContextCacheMutationUncertainException(LlmContextCacheMutation.Delete, Reference(), safe),
            new LlmContextCacheMutationCancelledException(LlmContextCacheMutation.UpdateExpiry, Reference(), default, safe)
        };
        foreach (Exception error in errors)
        {
            Assert.Same(safe, error.InnerException);
            Assert.DoesNotContain("untrusted-token", error.ToString());
            Assert.DoesNotContain("untrusted-prefix", error.ToString());
        }
        Assert.Equal(1, handler.Calls);
    }

    public static int ExerciseForeignContract()
    {
        var tests = new CacheErrorContractTests();
        foreach (string name in new[] { "LlmContextCacheUnavailableReason", "LlmContextCacheMutation" })
        {
            tests.DefinedNumericValuesRoundTrip(name);
            foreach (int number in new[] { -1, 99, int.MinValue, int.MaxValue })
            { tests.UndefinedOwnedReadRefuses(name, number); tests.UndefinedOwnedWriteRefuses(name, number); }
        }
        tests.UnavailablePreservesOnlySuppliedFactsAndSafeDiagnostics(LlmContextCacheUnavailableReason.Missing);
        tests.UnavailablePreservesOnlySuppliedFactsAndSafeDiagnostics(LlmContextCacheUnavailableReason.Expired);
        foreach (LlmContextCacheMutation operation in Enum.GetValues<LlmContextCacheMutation>())
            foreach (bool evidence in new[] { false, true }) tests.MutationEvidenceAndCancellationRemainActual(operation, evidence);
        tests.InvalidMutationEvidenceRefuses(false); tests.InvalidMutationEvidenceRefuses(true);
        foreach (HttpStatusCode status in new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
            tests.MissingRejectsContradictoryHttpEvidence(status);
        tests.UpdateRejectsContradictoryObservedExpiry(false); tests.UpdateRejectsContradictoryObservedExpiry(true);
        tests.UnknownReferenceExpiryRemainsUnknownInObservedUpdate();
        tests.RuntimeExceptionsAreNotOwnedPortableRoots();
        Assert.Same(typeof(InferenceJson).Assembly, Reference().GetType().Assembly);
        return 36;
    }

    [Fact]
    public void ActualForeignErrorAndOwnedMetadataGraphsRetire()
    {
        WeakReference[] roots = ExerciseForeign();
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        foreach (WeakReference root in roots) Assert.False(root.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeign()
    {
        var generation = new AssemblyLoadContext("cache-error-contract", isCollectible: true);
        var roots = new List<WeakReference> { new(generation) };
        try
        {
            foreach (Assembly local in new[] { typeof(InferenceJson).Assembly, typeof(ConnectLlmJson).Assembly, typeof(CacheErrorContractTests).Assembly })
            {
                Assembly actual = generation.LoadFromAssemblyPath(local.Location);
                roots.Add(new(actual));
                Assert.NotSame(local, actual);
                Assert.Equal(local.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
            }
            Assembly owner = generation.Assemblies.Single(assembly => assembly.GetName().Name == typeof(ConnectLlmJson).Assembly.GetName().Name);
            Assembly fixture = generation.Assemblies.Single(assembly => assembly.GetName().Name == typeof(CacheErrorContractTests).Assembly.GetName().Name);
            Type facade = owner.GetType(typeof(ConnectLlmJson).FullName!, true)!;
            Type proofType = fixture.GetType(typeof(CacheErrorContractTests).FullName!, true)!;
            MethodInfo proof = proofType.GetMethod(nameof(ExerciseForeignContract))!;
            roots.Add(new(facade)); roots.Add(new(proofType)); roots.Add(new(proof));
            foreach (string name in new[] { "LlmContextCacheUnavailableReason", "LlmContextCacheMutation" })
            {
                JsonTypeInfo metadata = (JsonTypeInfo)facade.GetProperty(name)!.GetValue(null)!;
                Assert.Same(owner, metadata.Type.Assembly);
                Assert.Same(owner, metadata.Converter.GetType().Assembly);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Converter.GetType().Assembly));
                roots.Add(new(metadata)); roots.Add(new(metadata.Type)); roots.Add(new(metadata.Converter));
                roots.Add(new(metadata.Options.TypeInfoResolver!));
            }
            Assert.Same(fixture, proof.DeclaringType!.Assembly);
            Assert.Equal(36, proof.Invoke(null, null));
            foreach (string name in new[] { nameof(LlmContextCacheUnavailableException), nameof(LlmContextCacheMutationUncertainException), nameof(LlmContextCacheMutationCancelledException) })
            {
                Type type = owner.GetType("Darkspyre.Inference.Connect.LLM." + name, true)!;
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(type.Assembly));
                roots.Add(new(type));
            }
        }
        finally { generation.Unload(); }
        return roots.ToArray();
    }

    private static LlmExplicitCacheReference Reference() => new("private-resource", "private-model", "private-scope", DateTimeOffset.UnixEpoch);
    private static LlmContextCache Receipt(LlmExplicitCacheReference reference) => new(reference with { ExpiresAt = DateTimeOffset.UnixEpoch.AddHours(1) }, "private-display", null, null, DateTimeOffset.UnixEpoch.AddHours(1), new(0, null, 0, null, null));

    private sealed class MaliciousHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new LlmProviderException("untrusted-token untrusted-prefix", LlmTransport.GoogleGeminiApi,
                innerException: new InvalidOperationException("untrusted-token untrusted-prefix"));
        }
    }

    private static JsonTypeInfo Metadata(string name)
    {
        PropertyInfo? getter = typeof(ConnectLlmJson).GetProperty(name);
        Assert.NotNull(getter);
        return Assert.IsAssignableFrom<JsonTypeInfo>(getter.GetValue(null));
    }
}
