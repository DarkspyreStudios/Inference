using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GoogleCacheMutationHttpTests
{
    private const string ReceiptBody = "{\"name\":\"cachedContents/id\",\"model\":\"models/m\",\"expireTime\":\"2030-01-01T00:00:00Z\"}";
    private const string Secret = "untrusted-token-private-cache-prefix";
    private static LlmExplicitCacheReference Reference() => new("cachedContents/id", "m", "scope", DateTimeOffset.UnixEpoch);
    private static LlmContextCache Parse(JsonElement root) => GoogleCacheData.Receipt(root, "scope", name => Assert.Equal("cachedContents/id", name), model => { Assert.Equal("models/m", model); return "m"; }, false);

    [Fact]
    public async Task UpdateUsesPatchAndCanonicalReceiptThenDisposesOwnedRequest()
    {
        using var content = new TrackedContent(ReceiptBody);
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler);
        var requestBody = new TrackedContent("{\"expireTime\":\"2030-01-01T00:00:00Z\"}");
        var request = new HttpRequestMessage(HttpMethod.Patch, "https://generativelanguage.googleapis.com/v1beta/cachedContents/id") { Content = requestBody };
        LlmContextCache result = await Update(http, request, Reference(), Parse, _ => { });
        Assert.Equal("cachedContents/id", result.Reference.ResourceName);
        Assert.Equal(DateTimeOffset.Parse("2030-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), result.ExpiresAt);
        Assert.Equal(1, handler.Calls); Assert.Equal(1, content.Disposals);
        Assert.Equal(1, requestBody.Disposals); Assert.Equal(0, handler.Disposals);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.NotFound, true)]
    public async Task DeleteUsesOnlyObservedAcknowledgementAndScopedNotFound(HttpStatusCode status, bool absent)
    {
        using var content = new TrackedContent("untrusted response", forbidRead: true);
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        });
        using var http = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Delete, "https://generativelanguage.googleapis.com/v1beta/cachedContents/id");
        var reference = Reference();
        int authorizations = 0;
        var trace = new List<string>();
        LlmContextCacheDeleteResult result = await Delete(http, request, reference, selected =>
        {
            Assert.Same(reference, selected); authorizations++; trace.Add("authorize"); return true;
        }, _ => trace.Add("fence"));
        Assert.Equal(absent, result.AlreadyAbsent);
        Assert.Equal(absent ? 1 : 0, authorizations);
        if (absent)
        {
            int at = trace.IndexOf("authorize");
            Assert.Equal("fence", trace[at - 1]); Assert.Equal("fence", trace[at + 1]);
        }
        Assert.Equal(0, content.Reads); Assert.Equal(1, content.Disposals); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task UnverifiedNotFoundIsUncertainNotAlreadyAbsent()
    {
        using var content = new TrackedContent("untrusted response", forbidRead: true);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = content }));
        using var http = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Delete, "https://generativelanguage.googleapis.com/v1beta/cachedContents/id");
        var reference = Reference();
        var error = await Assert.ThrowsAsync<LlmContextCacheMutationUncertainException>(() => Delete(http, request, reference, _ => false, _ => { }));
        Assert.Same(reference, error.Reference); Assert.Null(error.ObservedDelete);
        Assert.Equal(HttpStatusCode.NotFound, Assert.IsType<LlmProviderException>(error.InnerException).StatusCode);
        Assert.Equal(0, content.Reads); Assert.Equal(1, content.Disposals); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task WrongMethodRefusesBeforeDispatch()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must not dispatch."));
        using var http = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/cachedContents/id");
        await Assert.ThrowsAsync<ArgumentException>(() => Update(http, request, Reference(), Parse, _ => { }));
        await Assert.ThrowsAsync<ArgumentException>(() => Delete(http, request, Reference(), _ => true, _ => { }));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.BadRequest)]
    [InlineData(false, HttpStatusCode.Unauthorized)]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(false, HttpStatusCode.NotFound)]
    [InlineData(false, HttpStatusCode.TooManyRequests)]
    [InlineData(false, HttpStatusCode.InternalServerError)]
    [InlineData(true, HttpStatusCode.BadRequest)]
    [InlineData(true, HttpStatusCode.Unauthorized)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.NotFound)]
    [InlineData(true, HttpStatusCode.TooManyRequests)]
    [InlineData(true, HttpStatusCode.InternalServerError)]
    public async Task NonSuccessIsUncertainWithoutRetryOrBodyRead(bool update, HttpStatusCode status)
    {
        var content = new TrackedContent(Secret, forbidRead: true);
        using var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = content };
            response.Headers.TryAddWithoutValidation("x-request-id", "actual-request");
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(2));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var request = Request(update);
        int authorizations = 0;
        var error = await Assert.ThrowsAsync<LlmContextCacheMutationUncertainException>(() => Mutate(update, http, request, Parse,
            _ => { authorizations++; return false; }, _ => { }));
        Assert.Equal(update ? LlmContextCacheMutation.UpdateExpiry : LlmContextCacheMutation.Delete, error.Operation);
        Assert.Null(error.ObservedDelete); Assert.Null(error.ObservedUpdate);
        var provider = Assert.IsType<LlmProviderException>(error.InnerException);
        Assert.Equal(status, provider.StatusCode); Assert.Equal("actual-request", error.RequestId);
        Assert.Equal(TimeSpan.FromSeconds(2), provider.RetryAfter);
        Assert.Equal(!update && status == HttpStatusCode.NotFound ? 1 : 0, authorizations);
        Assert.Equal(0, content.Reads); Assert.Equal(1, content.Disposals); Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(Secret, error.ToString());
    }

    [Theory]
    [InlineData(true, "send")]
    [InlineData(false, "send")]
    [InlineData(true, "read")]
    [InlineData(true, "parse")]
    [InlineData(false, "authorize")]
    [InlineData(true, "cleanup")]
    [InlineData(false, "cleanup")]
    public async Task UntrustedFailureGraphsAreSanitized(bool update, string boundary)
    {
        Exception malicious = new LlmProviderException(Secret, LlmTransport.GoogleGeminiApi,
            innerException: new InvalidOperationException(Secret));
        var content = new TrackedContent(ReceiptBody, cleanup: boundary == "cleanup" ? () => throw malicious : null,
            beforeRead: boundary == "read" ? () => throw malicious : null);
        using var handler = new Handler((_, _) => boundary == "send" ? throw malicious
            : Task.FromResult(new HttpResponseMessage(boundary == "authorize" ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        var request = Request(update);
        var error = await Assert.ThrowsAsync<LlmContextCacheMutationUncertainException>(() => Mutate(update, http, request,
            root => boundary == "parse" ? throw malicious : Parse(root), _ => boundary == "authorize" ? throw malicious : true, _ => { }));
        if (boundary == "cleanup")
        {
            var cleanup = Assert.IsType<InferenceResourceOperationException>(error.InnerException);
            Assert.Equal(InferenceResourceOperationPhase.Cleanup, cleanup.Failure.Phase);
            Assert.IsType<LlmProviderException>(cleanup.InnerException);
        }
        else Assert.IsType<LlmProviderException>(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.Equal(boundary == "send" ? 0 : 1, content.Disposals);
        Assert.Equal(1, handler.Calls); Assert.Equal(0, handler.Disposals);
        if (boundary == "cleanup")
        {
            if (update) Assert.NotNull(error.ObservedUpdate);
            else Assert.False(error.ObservedDelete!.AlreadyAbsent);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IndependentOperationAndCleanupFailuresRemainSafeFacts(bool update, bool cancellation)
    {
        var safeToken = new CancellationTokenSource();
        using var caller = safeToken;
        Exception operationFailure = cancellation ? new OperationCanceledException(Secret, caller.Token) : new InvalidOperationException(Secret);
        var content = new TrackedContent(ReceiptBody, cleanup: () => throw new InvalidOperationException(Secret));
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(update ? HttpStatusCode.OK : HttpStatusCode.NotFound) { Content = content }));
        using var http = new HttpClient(handler);
        var request = Request(update);
        Exception error = cancellation
            ? await Assert.ThrowsAsync<LlmContextCacheMutationCancelledException>(() => Mutate(update, http, request,
                _ => throw operationFailure, _ => throw operationFailure, _ => { }, caller.Token))
            : await Assert.ThrowsAsync<LlmContextCacheMutationUncertainException>(() => Mutate(update, http, request,
                _ => throw operationFailure, _ => throw operationFailure, _ => { }, caller.Token));
        var failures = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Equal(2, failures.InnerExceptions.Count);
        var cleanup = Assert.IsType<InferenceResourceOperationException>(failures.InnerExceptions[1]);
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, cleanup.Failure.Phase);
        Assert.Contains("response cleanup failed", Assert.IsType<LlmProviderException>(cleanup.InnerException).Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.Equal(1, content.Disposals); Assert.Equal(1, handler.Calls);
        if (cancellation) Assert.Equal(caller.Token, Assert.IsType<LlmContextCacheMutationCancelledException>(error).CancellationToken);
    }

    [Theory]
    [InlineData("resource")]
    [InlineData("model")]
    [InlineData("scope")]
    [InlineData("expiry")]
    [InlineData("null")]
    public async Task InvalidPositiveUpdateCallbackOutputCannotPublishOrBecomeEvidence(string mismatch)
    {
        var content = new TrackedContent(ReceiptBody);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        var request = Request(true);
        var error = await Assert.ThrowsAsync<LlmContextCacheMutationUncertainException>(() => Update(http, request, Reference(), root =>
        {
            var receipt = Parse(root);
            return mismatch switch
            {
                "resource" => receipt with { Reference = receipt.Reference with { ResourceName = Secret } },
                "model" => receipt with { Reference = receipt.Reference with { ModelId = Secret } },
                "scope" => receipt with { Reference = receipt.Reference with { ScopeId = Secret } },
                "expiry" => receipt with { Reference = receipt.Reference with { ExpiresAt = DateTimeOffset.UnixEpoch } },
                _ => null!
            };
        }, _ => { }));
        Assert.Null(error.ObservedUpdate); Assert.Null(error.ObservedDelete);
        Assert.DoesNotContain(Secret, error.ToString()); Assert.Equal(1, content.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndOwnerFenceBeforeDispatchAreOrdinary(bool update)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must not send."));
        using var http = new HttpClient(handler);
        var request = Request(update);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Mutate(update, http, request, Parse, _ => true, _ => { }, cancellation.Token));
        Assert.IsNotType<LlmContextCacheMutationCancelledException>(error);
        int fences = 0;
        var original = new OperationCanceledException("Owner fence stopped admission.");
        error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Mutate(update, http, request, Parse, _ => true,
            _ => { if (++fences == 2) throw original; }));
        Assert.Same(original, error); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackRetirementAndCleanupRetirementRefuseLateSuccess(bool update)
    {
        bool retiring = false;
        var content = new TrackedContent(ReceiptBody, cleanup: !update ? () => retiring = true : null);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        var request = Request(update);
        var error = await Assert.ThrowsAsync<LlmContextCacheMutationCancelledException>(() => Mutate(update, http, request,
            root => { var receipt = Parse(root); retiring = true; return receipt; }, _ => true,
            token => { if (retiring) throw new OperationCanceledException("Owner retired.", token); }));
        if (update) Assert.NotNull(error.ObservedUpdate);
        else Assert.False(error.ObservedDelete!.AlreadyAbsent);
        Assert.Equal(1, content.Disposals); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AuthorizedNotFoundLateCleanupRetainsObservedAbsenceWithoutSuccess()
    {
        bool retiring = false;
        var content = new TrackedContent(Secret, forbidRead: true, cleanup: () => retiring = true);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = content }));
        using var http = new HttpClient(handler);
        var request = Request(false);
        var error = await Assert.ThrowsAsync<LlmContextCacheMutationCancelledException>(() => Delete(http, request, Reference(), _ => true,
            token => { if (retiring) throw new OperationCanceledException("Owner retired.", token); }));
        Assert.True(error.ObservedDelete!.AlreadyAbsent); Assert.Null(error.ObservedUpdate);
        Assert.Equal(0, content.Reads); Assert.Equal(1, content.Disposals); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AbsenceAuthorityCallbackRetirementCannotPublishAnAbsenceReceipt()
    {
        bool retiring = false;
        var content = new TrackedContent(Secret, forbidRead: true);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = content }));
        using var http = new HttpClient(handler);
        var request = Request(false);
        int authorizations = 0;
        var error = await Assert.ThrowsAsync<LlmContextCacheMutationCancelledException>(() => Delete(http, request, Reference(), _ =>
        { authorizations++; retiring = true; return true; }, token => { if (retiring) throw new OperationCanceledException("Owner retired.", token); }));
        Assert.Null(error.ObservedDelete); Assert.Null(error.ObservedUpdate);
        Assert.Equal(1, authorizations); Assert.Equal(0, content.Reads); Assert.Equal(1, content.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredBorrowedInputsRefuseBeforeAnyCallbackOrDispatch(bool update)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must not send."));
        using var http = new HttpClient(handler);
        var request = Request(update);
        int callbacks = 0;
        Action<CancellationToken> fence = _ => callbacks++;
        Func<JsonElement, LlmContextCache> parse = root => { callbacks++; return Parse(root); };
        Func<LlmExplicitCacheReference, bool> authorize = _ => { callbacks++; return true; };
        Func<Task>[] calls = update
            ? [() => Update(null!, request, Reference(), parse, fence), () => Update(http, null!, Reference(), parse, fence),
                () => Update(http, request, null!, parse, fence), () => Update(http, request, Reference(), null!, fence),
                () => Update(http, request, Reference(), parse, null!)]
            : [() => Delete(null!, request, Reference(), authorize, fence), () => Delete(http, null!, Reference(), authorize, fence),
                () => Delete(http, request, null!, authorize, fence), () => Delete(http, request, Reference(), null!, fence),
                () => Delete(http, request, Reference(), authorize, null!)];
        foreach (Func<Task> call in calls) await Assert.ThrowsAsync<ArgumentNullException>(call);
        Assert.Equal(0, callbacks); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task CancellationAfterResponsePreservesOnlyActualStatusEvidence(HttpStatusCode status)
    {
        using var caller = new CancellationTokenSource();
        bool retiring = false;
        var content = new TrackedContent(Secret, forbidRead: true);
        using var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = content };
            response.Headers.TryAddWithoutValidation("x-request-id", "actual-request");
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(3));
            retiring = true;
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var request = Request(true);
        var error = await Assert.ThrowsAsync<LlmContextCacheMutationCancelledException>(() => Update(http, request, Reference(), Parse,
            token => { if (retiring) throw new OperationCanceledException(Secret, token); }, caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Null(error.ObservedUpdate); Assert.Null(error.ObservedDelete);
        var cancellation = Assert.IsType<OperationCanceledException>(error.InnerException);
        Assert.Equal(caller.Token, cancellation.CancellationToken);
        var facts = Assert.IsType<LlmProviderException>(cancellation.InnerException);
        Assert.Equal(status, facts.StatusCode); Assert.Equal("actual-request", facts.RequestId);
        Assert.Equal("actual-request", error.RequestId); Assert.Equal(TimeSpan.FromSeconds(3), facts.RetryAfter);
        Assert.Equal("Google cache mutation response observed before interruption.", facts.Message);
        Assert.Null(facts.InnerException); Assert.DoesNotContain(Secret, error.ToString());
        Assert.Equal(0, content.Reads); Assert.Equal(1, content.Disposals); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CancellationBeforeAnyResponseDoesNotInventResponseFacts()
    {
        using var caller = new CancellationTokenSource();
        using var handler = new Handler((_, token) => throw new OperationCanceledException(Secret, token));
        using var http = new HttpClient(handler);
        var request = Request(true);
        var error = await Assert.ThrowsAsync<LlmContextCacheMutationCancelledException>(() => Update(http, request, Reference(), Parse, _ => { }, caller.Token));
        var cancellation = Assert.IsType<OperationCanceledException>(error.InnerException);
        Assert.Null(cancellation.InnerException); Assert.Null(error.RequestId);
        Assert.Null(error.ObservedUpdate); Assert.Null(error.ObservedDelete);
        Assert.DoesNotContain(Secret, error.ToString()); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CallerCancellationDrainsCooperativeAndIgnoringSend(bool update, bool ignoring)
    {
        using var caller = new CancellationTokenSource();
        var entered = Signal(); var release = Signal();
        var content = new TrackedContent(ReceiptBody);
        using var handler = new Handler(async (_, token) =>
        {
            entered.TrySetResult();
            if (ignoring) await release.Task;
            else await release.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var http = new HttpClient(handler);
        var request = Request(update);
        Task<object>? operation = null;
        try
        {
            operation = Mutate(update, http, request, Parse, _ => true, _ => { }, caller.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            if (ignoring) Assert.False(operation.IsCompleted);
            release.TrySetResult();
            var error = await Assert.ThrowsAsync<LlmContextCacheMutationCancelledException>(() => operation);
            Assert.Equal(caller.Token, error.CancellationToken);
            Assert.Null(error.ObservedDelete); Assert.Null(error.ObservedUpdate);
            Assert.Equal(ignoring ? 1 : 0, content.Disposals); Assert.Equal(1, handler.Calls);
        }
        finally { release.TrySetResult(); if (operation is not null) await Observe(operation); }
    }

    [Theory]
    [InlineData(false, "send")]
    [InlineData(false, "authorize")]
    [InlineData(false, "cleanup")]
    [InlineData(true, "send")]
    [InlineData(true, "read")]
    [InlineData(true, "cleanup")]
    public async Task DelayedOwnerCancellationFencesAndDrainsActualBoundaries(bool update, string boundary)
    {
        using var caller = new CancellationTokenSource();
        var owner = new LlmOperationLifetime();
        var lease = owner.Enter(caller.Token);
        var anchor = owner.Enter(CancellationToken.None);
        var entered = Signal(); var release = Signal();
        var callbackEntered = Signal(); var callbackRelease = Signal();
        var content = new TrackedContent(ReceiptBody, cleanup: boundary == "cleanup"
            ? () => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); }
        : null,
            entered: boundary == "read" ? entered : null, release: boundary == "read" ? release : null);
        using var handler = new Handler(async (_, _) =>
        {
            if (boundary == "send") { entered.TrySetResult(); await release.Task; }
            return new HttpResponseMessage(boundary == "authorize" ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = content };
        });
        using var http = new HttpClient(handler);
        var request = Request(update);
        Task<object>? operation = null; Task? retirement = null;
        CancellationTokenRegistration blocker = default;
        try
        {
            blocker = anchor.Token.Register(() => { callbackEntered.TrySetResult(); callbackRelease.Task.GetAwaiter().GetResult(); });
            operation = Task.Run(() => Mutate(update, http, request, Parse, _ =>
            {
                entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); return true;
            }, token => Fence(owner, token), lease.Token));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = owner.RetireAsync();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(lease.Token.IsCancellationRequested);
            Assert.False(retirement.IsCompleted); Assert.False(operation.IsCompleted);
            release.TrySetResult();
            var error = await Assert.ThrowsAsync<LlmContextCacheMutationCancelledException>(() => operation);
            Assert.Equal(lease.Token, error.CancellationToken);
            Assert.False(retirement.IsCompleted); Assert.Equal(1, content.Disposals);
            if (boundary == "cleanup")
            {
                if (update) Assert.NotNull(error.ObservedUpdate);
                else Assert.False(error.ObservedDelete!.AlreadyAbsent);
            }
        }
        finally
        {
            retirement ??= owner.RetireAsync(); callbackRelease.TrySetResult(); release.TrySetResult();
            try { if (operation is not null) await Observe(operation); }
            finally
            {
                lease.Dispose(); anchor.Dispose();
                try { await retirement; } finally { blocker.Dispose(); }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualSdkOwnerRetirementRefusesAcknowledgedMutation(bool update)
    {
        var services = new ServiceCollection(); services.AddLlmConnect();
        Task? retirement = null;
        SdkHttpGenerationPool? pool = null;
        var content = new TrackedContent(ReceiptBody, cleanup: !update ? () => retirement = pool!.DisposeAsync().AsTask() : null);
        services.AddHttpClient("GeminiApiLlmClient").ConfigurePrimaryHttpMessageHandler(() =>
            new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        await using var root = services.BuildServiceProvider();
        pool = root.GetRequiredService<SdkHttpGenerationPool>();
        using var http = pool.CreateClient("GeminiApiLlmClient");
        var request = Request(update);
        try
        {
            var error = await Assert.ThrowsAsync<LlmContextCacheMutationCancelledException>(() => Mutate(update, http, request,
                json => { var receipt = Parse(json); retirement = pool.DisposeAsync().AsTask(); Assert.False(retirement.IsCompleted); return receipt; },
                _ => true, _ => { }));
            await retirement!.WaitAsync(TimeSpan.FromSeconds(5));
            if (update) Assert.NotNull(error.ObservedUpdate);
            else Assert.False(error.ObservedDelete!.AlreadyAbsent);
            Assert.Equal(1, content.Disposals);
        }
        finally { await (retirement ?? pool.DisposeAsync().AsTask()); }
    }

    [Fact]
    public void ActualForeignMutationCallbacksAndMetadataDrainAndRetireWithDefaultBorrowersRetained()
    {
        var entered = Signal(); var release = Signal(); var retiring = Signal();
        int sends = 0, fences = 0;
        using var handler = new Handler(async (_, _) =>
        {
            int current = Interlocked.Increment(ref sends);
            if (current == 1) { entered.TrySetResult(); await release.Task; }
            return new HttpResponseMessage(current == 3 ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            { Content = new TrackedContent(ReceiptBody, forbidRead: current >= 3) };
        });
        using var http = new HttpClient(handler);
        Action<CancellationToken> fence = _ => fences++;
        WeakReference[] roots = ExerciseForeign(http, fence, entered, release, retiring);
        Assert.Equal(4, sends); Assert.True(fences >= 12); Assert.Equal(0, handler.Disposals);
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        foreach (WeakReference root in roots) Assert.False(root.IsAlive);
        GC.KeepAlive(http); GC.KeepAlive(fence); GC.KeepAlive(entered); GC.KeepAlive(release); GC.KeepAlive(retiring);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeign(HttpClient http, Action<CancellationToken> fence,
        TaskCompletionSource entered, TaskCompletionSource release, TaskCompletionSource retiring)
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(GoogleCacheMutationHttpTests).Assembly.Location)!);
        var roots = new List<WeakReference> { new(generation) };
        Task<WeakReference[]>? operation = null;
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(GoogleCacheMutationHttpTests).Assembly.Location);
            Assembly connect = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            foreach ((Assembly source, Assembly actual) in new[]
                { (typeof(GoogleCacheMutationHttpTests).Assembly, fixture), (typeof(ConnectLlmJson).Assembly, connect), (typeof(InferenceJson).Assembly, common) })
            {
                Assert.NotSame(source, actual); Assert.Equal(source.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual)); roots.Add(new(actual));
            }
            Type caller = fixture.GetType(typeof(GoogleCacheMutationHttpTests).FullName!, true)!;
            MethodInfo method = caller.GetMethod(nameof(ExerciseMutationsInOwningGeneration))!;
            roots.Add(new(caller)); roots.Add(new(method));
            operation = (Task<WeakReference[]>)method.Invoke(null, [http, fence, entered, retiring])!;
            retiring.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Assert.False(operation.IsCompleted); release.TrySetResult();
            roots.Add(new(operation)); roots.AddRange(operation.GetAwaiter().GetResult());
        }
        finally
        {
            release.TrySetResult();
            try { if (operation is not null) operation.GetAwaiter().GetResult(); }
            finally { generation.Unload(); }
        }
        return roots.ToArray();
    }

    public static async Task<WeakReference[]> ExerciseMutationsInOwningGeneration(HttpClient http,
        Action<CancellationToken> fence, TaskCompletionSource entered, TaskCompletionSource retiring)
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(GoogleCacheMutationHttpTests).Assembly)!;
        if (!generation.IsCollectible || !ReferenceEquals(generation, AssemblyLoadContext.GetLoadContext(typeof(GoogleCacheHttp).Assembly))
            || !ReferenceEquals(generation, AssemblyLoadContext.GetLoadContext(typeof(LlmExplicitCacheReference).Assembly)))
            throw new InvalidOperationException("The real mutation helper, fixture and Common must execute in the private generation.");
        var owner = new LlmOperationLifetime();
        var lease = owner.Enter(CancellationToken.None);
        var heldRequest = Request(true);
        Task<LlmContextCache>? held = null; Task? retirement = null;
        Action<CancellationToken> ownedFence = token => { fence(token); Fence(owner, token); };
        Func<JsonElement, LlmContextCache> parse = Parse;
        Func<LlmExplicitCacheReference, bool> authorize = reference => reference == Reference();
        var roots = new List<WeakReference> { new(owner), new(lease), new(ownedFence), new(parse), new(authorize) };
        try
        {
            held = Update(http, heldRequest, Reference(), parse, ownedFence, lease.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = owner.RetireAsync();
            if (retirement.IsCompleted || held.IsCompleted) throw new InvalidOperationException("Ignoring foreign mutation must remain admitted during retirement.");
            retiring.TrySetResult();
            try { await held; throw new InvalidOperationException("Retired mutation cannot publish a late receipt."); }
            catch (LlmContextCacheMutationCancelledException error)
            {
                if (!ReferenceEquals(error.GetType().Assembly, typeof(GoogleCacheHttp).Assembly)
                    || !ReferenceEquals(error.Reference.GetType().Assembly, typeof(LlmExplicitCacheReference).Assembly))
                    throw new InvalidOperationException("The real mutation error and reference must use the private assemblies.");
                roots.Add(new(error)); roots.Add(new(error.Reference));
            }
        }
        finally
        {
            retirement ??= owner.RetireAsync();
            try { if (held is not null) await Observe(held); }
            finally { lease.Dispose(); await retirement; }
        }
        var updateRequest = Request(true);
        Task<LlmContextCache> updatedTask = Update(http, updateRequest, Reference(), parse, fence);
        var updated = await updatedTask;
        var absentRequest = Request(false);
        Task<LlmContextCacheDeleteResult> absentTask = Delete(http, absentRequest, Reference(), authorize, fence);
        var absent = await absentTask;
        var deleteRequest = Request(false);
        Task<LlmContextCacheDeleteResult> deleteTask = Delete(http, deleteRequest, Reference(), authorize, fence);
        var deleted = await deleteTask;
        var metadata = ConnectLlmJson.LlmContextCache;
        var deleteMetadata = ConnectLlmJson.LlmContextCacheDeleteResult;
        var operationMetadata = ConnectLlmJson.LlmContextCacheMutation;
        var referenceMetadata = InferenceJson.LlmExplicitCacheReference;
        if (!ReferenceEquals(metadata.Type.Assembly, typeof(GoogleCacheHttp).Assembly)
            || !ReferenceEquals(deleteMetadata.Type.Assembly, typeof(GoogleCacheHttp).Assembly)
            || !ReferenceEquals(operationMetadata.Converter.GetType().Assembly, typeof(GoogleCacheHttp).Assembly)
            || !ReferenceEquals(referenceMetadata.Type.Assembly, typeof(LlmExplicitCacheReference).Assembly)
            || !ReferenceEquals(parse.Method.DeclaringType!.Assembly, typeof(GoogleCacheMutationHttpTests).Assembly)
            || !ReferenceEquals(authorize.Method.DeclaringType!.Assembly, typeof(GoogleCacheMutationHttpTests).Assembly))
            throw new InvalidOperationException("Actual owned metadata, converter and callbacks must have private defining provenance.");
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(updated, metadata), metadata)!;
        if (restored.Reference != updated.Reference || !absent.AlreadyAbsent || deleted.AlreadyAbsent)
            throw new InvalidOperationException("Actual foreign mutation and owned codec facts changed.");
        roots.AddRange(new WeakReference[] { new(typeof(GoogleCacheHttp)), new(held!), new(updatedTask), new(absentTask), new(deleteTask),
            new(updated), new(absent), new(deleted), new(restored), new(updated.Reference), new(metadata), new(deleteMetadata),
            new(operationMetadata), new(operationMetadata.Converter), new(metadata.Options.TypeInfoResolver!), new(referenceMetadata) });
        return roots.ToArray();
    }

    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => name.Name is
            "Darkspyre.Inference.Common" or "Darkspyre.Inference.Connect.LLM" or "Darkspyre.Inference.Connect.LLM.Tests"
            ? LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll")) : null;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Fence(LlmOperationLifetime owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { owner.ThrowIfStopping(); }
        catch (ObjectDisposedException) { throw new OperationCanceledException("Owned cache mutation retired.", token); }
    }

    private static async Task Observe(Task operation)
    {
        try { await operation; }
        catch (LlmContextCacheMutationCancelledException) { }
    }

    private static Task<LlmContextCache> Update(HttpClient http, HttpRequestMessage request, LlmExplicitCacheReference reference,
        Func<JsonElement, LlmContextCache> parse, Action<CancellationToken> fence, CancellationToken token = default) =>
        GoogleCacheHttp.UpdateExpiryAsync(http, request, LlmTransport.GoogleGeminiApi, reference, parse, fence, token);

    private static Task<LlmContextCacheDeleteResult> Delete(HttpClient http, HttpRequestMessage request, LlmExplicitCacheReference reference,
        Func<LlmExplicitCacheReference, bool> authorize, Action<CancellationToken> fence, CancellationToken token = default) =>
        GoogleCacheHttp.DeleteAsync(http, request, LlmTransport.GoogleGeminiApi, reference, authorize, fence, token);

    private static async Task<object> Mutate(bool update, HttpClient http, HttpRequestMessage request,
        Func<JsonElement, LlmContextCache> parse, Func<LlmExplicitCacheReference, bool> authorize,
        Action<CancellationToken> fence, CancellationToken token = default)
    {
        if (update) return await Update(http, request, Reference(), parse, fence, token);
        return await Delete(http, request, Reference(), authorize, fence, token);
    }

    private static HttpRequestMessage Request(bool update) => new(update ? HttpMethod.Patch : HttpMethod.Delete,
        "https://generativelanguage.googleapis.com/v1beta/cachedContents/id");

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal int Disposals { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }

    private sealed class TrackedContent(string body, bool forbidRead = false, Action? cleanup = null, Action? beforeRead = null,
        TaskCompletionSource? entered = null, TaskCompletionSource? release = null) : HttpContent
    {
        internal int Reads { get; private set; }
        internal int Disposals { get; private set; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Reads++;
            if (forbidRead) throw new InvalidOperationException("Response body must not be read.");
            beforeRead?.Invoke();
            if (entered is not null) { entered.TrySetResult(); await release!.Task; }
            await stream.WriteAsync(Encoding.UTF8.GetBytes(body));
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { if (disposing) { Disposals++; cleanup?.Invoke(); } base.Dispose(disposing); }
    }
}
