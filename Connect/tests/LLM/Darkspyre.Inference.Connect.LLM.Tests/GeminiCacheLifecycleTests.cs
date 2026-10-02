using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GeminiCacheLifecycleTests
{
    private static readonly DateTimeOffset Expiry = DateTimeOffset.UtcNow.AddHours(2);
    private const string Model = "gemini-3-flash-preview";

    [Fact]
    public async Task PublicBindingCreatesGetsListsUpdatesAndDeletesThroughSelectedSnapshot()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var source = new Source();
        await using var facade = new LlmProviderService(source, [new GeminiApiLlmClient(http)]);
        ILlmContextCacheClient cache = await facade.BindContextCacheAsync("selected");
        Assert.Empty(handler.Requests);
        LlmContextCache created = await cache.CreateAsync(new()
        {
            ModelId = Model,
            Messages = [new("user", "immutable prefix")],
            Correlation = new("operation", "reconciliation"),
            TimeToLive = TimeSpan.FromMinutes(10)
        });
        Assert.Equal(Expiry, created.ExpiresAt);
        Assert.Equal("cachedContents/native", created.Reference.ResourceName);
        Assert.NotEmpty(created.Reference.ScopeId);
        LlmContextCache fetched = await cache.GetAsync(created.Reference);
        Assert.Equal(created.Reference, fetched.Reference);
        LlmContextCachePage page = await cache.ListAsync(new(7, "literal+token"));
        Assert.Single(page.Items);
        Assert.Equal("next", page.NextPageToken);
        handler.Expiry = Expiry.AddHours(1);
        LlmContextCache updated = await cache.UpdateExpiryAsync(created.Reference, handler.Expiry);
        Assert.Equal(handler.Expiry, updated.ExpiresAt);
        Assert.False((await cache.DeleteAsync(updated.Reference)).AlreadyAbsent);
        Assert.Equal(6, source.Reads);
        Assert.Equal(0, source.Saves);
        Assert.Equal(5, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("private-key", request.Key));
    }

    [Fact]
    public async Task KnownCopiedReceiptEnablesCanonicalCachedGenerationAndExactCountingWithoutGet()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var source = new Source();
        var adapter = new GeminiApiLlmClient(http);
        source.Value = source.Value with { CapabilityOverrides = new() { SupportsStreaming = false } };
        await using var facade = new LlmProviderService(source, [adapter]);
        ILlmContextCacheClient cache = await facade.BindContextCacheAsync("selected");
        LlmContextCache receipt = await cache.GetAsync(new("cachedContents/native", Model, "imported"));
        var request = new LlmChatRequest
        {
            ModelId = Model,
            ExplicitCache = receipt.Reference with { },
            Messages = [new("user", "uncached suffix")]
        };
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in facade.StreamAsync("selected", request)) events.Add(item);
        _ = await facade.CountInputTokensAsync("selected", request);
        Assert.Equal(1, handler.Requests.Count(request => request.Method == HttpMethod.Get && request.Path.StartsWith("/v1beta/cachedContents", StringComparison.Ordinal)));
        Request generation = handler.Requests.Single(request => request.Path.EndsWith(":generateContent", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(generation.Body!);
        Assert.Equal("cachedContents/native", body.RootElement.GetProperty("cachedContent").GetString());
        Assert.DoesNotContain("immutable prefix", generation.Body!, StringComparison.Ordinal);
        Request count = handler.Requests.Single(request => request.Path.EndsWith(":countTokens", StringComparison.Ordinal));
        using var counting = JsonDocument.Parse(count.Body!);
        Assert.Equal("cachedContents/native", counting.RootElement.GetProperty("generateContentRequest").GetProperty("cachedContent").GetString());
        Assert.False(adapter.GetCapabilities(source.Value, Model).SupportsExplicitCache);
    }

    [Fact]
    public async Task CachedSuffixConflictsRefuseBeforeProviderEffects()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var source = new Source();
        await using var facade = new LlmProviderService(source, [new GeminiApiLlmClient(http)]);
        ILlmContextCacheClient cache = await facade.BindContextCacheAsync("selected");
        LlmContextCache receipt = await cache.GetAsync(new("cachedContents/native", Model, "imported"));
        int effects = handler.Requests.Count;
        await Assert.ThrowsAsync<ArgumentException>(() => facade.CountInputTokensAsync("selected", new()
        {
            ModelId = Model,
            ExplicitCache = receipt.Reference,
            Instructions = "conflicting system",
            Messages = [new("user", "suffix")]
        }));
        Assert.Equal(effects, handler.Requests.Count);
    }

    [Fact]
    public async Task NamedPrefixReadsOnlyExplicitResourcesAndClosesHandlesBeforeCachePost()
    {
        var resources = new ControlledSessionResources();
        InferenceResourceReference named = await resources.RegisterAsync("named.txt", "text/plain", "actual named bytes");
        _ = await resources.RegisterAsync("unrelated.txt", "text/plain", "must not transfer");
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var facade = new LlmProviderService(new Source(), [new GeminiApiLlmClient(http)]);
        ILlmContextCacheClient cache = await facade.BindContextCacheAsync("selected");
        handler.BeforeCachePost = () => Assert.Equal(1, resources.DisposedReads);
        _ = await cache.CreateAsync(new()
        {
            ModelId = Model,
            Correlation = new("operation", "display"),
            TimeToLive = TimeSpan.FromMinutes(10),
            Resources = resources,
            Messages = [new("user", "named") { Attachments = [new LlmResourceAttachment(named)] }]
        });
        Request upload = handler.Requests.Single(request => request.Path == "/upload/v1beta/files");
        Assert.Contains("actual named bytes", upload.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("must not transfer", upload.Body!, StringComparison.Ordinal);
        Request create = handler.Requests.Single(request => request.Path == "/v1beta/cachedContents");
        Assert.Contains("\"fileUri\":\"https://generativelanguage.googleapis.com/v1beta/files/staged\"", create.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("actual named bytes", create.Body!, StringComparison.Ordinal);
        Assert.Equal(1, resources.DisposedReads);
        Assert.Equal(0, resources.DisposeCalls);
    }

    [Fact]
    public async Task ChangedCredentialUnderSameVersionRequiresAuthorizedGetButArbitraryImportDoesNotAutoGet()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var source = new Source();
        await using var facade = new LlmProviderService(source, [new GeminiApiLlmClient(http)]);
        ILlmContextCacheClient cache = await facade.BindContextCacheAsync("selected");
        LlmContextCache receipt = await cache.GetAsync(new("cachedContents/native", Model, "imported"));
        var request = new LlmChatRequest { ModelId = Model, ExplicitCache = receipt.Reference, Messages = [new("user", "suffix")] };
        source.Value = source.Value with { Credential = new LlmCredential { ApiKey = "replacement" }.Serialize() };
        _ = await facade.CountInputTokensAsync("selected", request);
        Assert.Equal(2, handler.Requests.Count(item => item.Path == "/v1beta/cachedContents/native"));
        Assert.Equal("replacement", handler.Requests.Last().Key);
        int effects = handler.Requests.Count;
        await Assert.ThrowsAsync<ArgumentException>(() => facade.CountInputTokensAsync("selected", request with
        { ExplicitCache = receipt.Reference with { ScopeId = "forged" } }));
        Assert.Equal(effects, handler.Requests.Count);
    }

    [Fact]
    public async Task ANewFacadeRequiresExplicitGetOfPersistedReceipt()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var source = new Source();
        LlmContextCache receipt;
        await using (var first = new LlmProviderService(source, [new GeminiApiLlmClient(http)]))
            receipt = await (await first.BindContextCacheAsync("selected")).GetAsync(new("cachedContents/native", Model, "imported"));
        await using var second = new LlmProviderService(source, [new GeminiApiLlmClient(http)]);
        var request = new LlmChatRequest { ModelId = Model, ExplicitCache = receipt.Reference, Messages = [new("user", "suffix")] };
        int effects = handler.Requests.Count;
        await Assert.ThrowsAsync<ArgumentException>(() => second.CountInputTokensAsync("selected", request));
        Assert.Equal(effects, handler.Requests.Count);
        LlmContextCache rebound = await (await second.BindContextCacheAsync("selected")).GetAsync(receipt.Reference);
        Assert.NotEqual(receipt.Reference.ScopeId, rebound.Reference.ScopeId);
        _ = await second.CountInputTokensAsync("selected", request with { ExplicitCache = rebound.Reference });
    }

    [Fact]
    public async Task ObservedDeletionAllowsOnlyActual404RepeatAndCannotBecomeAccountProof()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var source = new Source(); await using var facade = new LlmProviderService(source, [new GeminiApiLlmClient(http)]);
        ILlmContextCacheClient cache = await facade.BindContextCacheAsync("selected");
        LlmContextCache receipt = await cache.GetAsync(new("cachedContents/native", Model, "imported"));
        Assert.False((await cache.DeleteAsync(receipt.Reference)).AlreadyAbsent);
        handler.Status = HttpStatusCode.NotFound;
        Assert.True((await cache.DeleteAsync(receipt.Reference)).AlreadyAbsent);
        Assert.Equal(2, handler.Requests.Count(request => request.Method == HttpMethod.Delete));
        source.Value = source.Value with { Credential = new LlmCredential { ApiKey = "replacement" }.Serialize() };
        LlmContextCacheUnavailableException unavailable = await Assert.ThrowsAsync<LlmContextCacheUnavailableException>(() => cache.DeleteAsync(receipt.Reference));
        Assert.Equal(LlmContextCacheUnavailableReason.Missing, unavailable.Reason);
        Assert.Equal(2, handler.Requests.Count(request => request.Method == HttpMethod.Delete));
    }

    [Fact]
    public async Task ListingDoesNotAuthorizeAnOtherwiseUnknownReceipt()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        await using var facade = new LlmProviderService(new Source(), [new GeminiApiLlmClient(http)]);
        var page = await (await facade.BindContextCacheAsync("selected")).ListAsync(new(1));
        int effects = handler.Requests.Count;
        await Assert.ThrowsAsync<ArgumentException>(() => facade.CountInputTokensAsync("selected", new()
        { ModelId = Model, ExplicitCache = page.Items[0].Reference, Messages = [new("user", "suffix")] }));
        Assert.Equal(effects, handler.Requests.Count);
    }

    [Fact]
    public async Task ObservedElapsedExpiryRefusesLocallyWithoutClaimingRemoteDeletion()
    {
        using var handler = new Handler { Expiry = DateTimeOffset.UtcNow.AddSeconds(-1) }; using var http = new HttpClient(handler);
        await using var facade = new LlmProviderService(new Source(), [new GeminiApiLlmClient(http)]);
        var receipt = await (await facade.BindContextCacheAsync("selected")).GetAsync(new("cachedContents/native", Model, "imported"));
        int effects = handler.Requests.Count;
        var failure = await Assert.ThrowsAsync<LlmContextCacheUnavailableException>(() => facade.CountInputTokensAsync("selected", new()
        { ModelId = Model, ExplicitCache = receipt.Reference, Messages = [new("user", "suffix")] }));
        Assert.Equal(LlmContextCacheUnavailableReason.Expired, failure.Reason); Assert.Null(failure.StatusCode);
        Assert.Equal(effects, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedMutationIsNotRetriedAndRequiresExplicitReconciliation(bool delete)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        await using var facade = new LlmProviderService(new Source(), [new GeminiApiLlmClient(http)]);
        var cache = await facade.BindContextCacheAsync("selected");
        var receipt = await cache.GetAsync(new("cachedContents/native", Model, "imported"));
        handler.Status = HttpStatusCode.TooManyRequests;
        async Task Mutate() { if (delete) _ = await cache.DeleteAsync(receipt.Reference); else _ = await cache.UpdateExpiryAsync(receipt.Reference, Expiry.AddHours(1)); }
        await Assert.ThrowsAsync<LlmContextCacheMutationUncertainException>(Mutate);
        int effects = handler.Requests.Count;
        await Assert.ThrowsAsync<ArgumentException>(Mutate);
        Assert.Equal(effects, handler.Requests.Count);
        handler.Status = HttpStatusCode.OK;
        var reconciled = await cache.GetAsync(receipt.Reference);
        _ = await facade.CountInputTokensAsync("selected", new()
        { ModelId = Model, ExplicitCache = reconciled.Reference, Messages = [new("user", "suffix")] });
    }

    [Fact]
    public async Task ReadHandleCleanupFailurePreventsCacheDispatchAndPreservesActualCleanup()
    {
        var resources = new ControlledSessionResources();
        var named = await resources.RegisterAsync("source.txt", "text/plain", "actual named bytes");
        var original = new InvalidOperationException("Controlled handle cleanup refusal."); resources.ReadCleanupFailure = original;
        using var handler = new Handler(); using var http = new HttpClient(handler);
        await using var facade = new LlmProviderService(new Source(), [new GeminiApiLlmClient(http)]);
        var cache = await facade.BindContextCacheAsync("selected");
        var failure = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => cache.CreateAsync(new()
        {
            ModelId = Model,
            Correlation = new("operation", "display"),
            TimeToLive = TimeSpan.FromMinutes(1),
            Resources = resources,
            Messages = [new("user", "prefix") { Attachments = [new LlmResourceAttachment(named)] }]
        }));
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, failure.Failure.Phase);
        Assert.Equal(named, failure.Failure.Reference); Assert.Same(original, failure.InnerException);
        Assert.Equal(1, resources.ReadCleanupAttempts); Assert.Equal(0, resources.DisposedReads); Assert.Equal(0, resources.DisposeCalls);
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/v1beta/cachedContents");
    }

    [Fact]
    public async Task CancellationAfterReadCleanupRetainsTransferFactsWithoutCacheDispatch()
    {
        var resources = new ControlledSessionResources();
        var named = await resources.RegisterAsync("source.txt", "text/plain", "actual named bytes");
        using var cancellation = new CancellationTokenSource();
        resources.BeforeReadCleanup = cancellation.Cancel;
        using var handler = new Handler(); using var http = new HttpClient(handler);
        await using var facade = new LlmProviderService(new Source(), [new GeminiApiLlmClient(http)]);
        var cache = await facade.BindContextCacheAsync("selected");
        var failure = await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => cache.CreateAsync(new()
        {
            ModelId = Model,
            Correlation = new("operation", "display"),
            TimeToLive = TimeSpan.FromMinutes(1),
            Resources = resources,
            Messages = [new("user", "prefix") { Attachments = [new LlmResourceAttachment(named)] }]
        }, cancellation.Token));
        Assert.Equal(named, failure.Failure.Reference);
        Assert.Equal(18, failure.Failure.BytesTransferred);
        Assert.Equal(InferenceResourceCommitOutcome.NotAttempted, failure.Failure.CommitOutcome);
        Assert.Equal(1, resources.DisposedReads);
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/v1beta/cachedContents");
    }

    [Theory]
    [InlineData("get")]
    [InlineData("create")]
    [InlineData("count")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task SynchronousRetirementFenceRejectsLateResultsWhileLinkedCancellationIsHeld(string operation)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var source = new Source(); var facade = new LlmProviderService(source, [new GeminiApiLlmClient(http)]);
        var entered = Signal(); var release = Signal(); var stopEntered = Signal(); var stopRelease = Signal();
        CancellationTokenRegistration blocking = default;
        Task? pending = null; Task? retirement = null;
        try
        {
            ILlmContextCacheClient cache = await facade.BindContextCacheAsync("selected");
            LlmContextCache receipt = await cache.GetAsync(new("cachedContents/native", Model, "imported"));
            handler.Hold = async () =>
            {
                blocking = source.LastToken.Register(() => { stopEntered.TrySetResult(); stopRelease.Task.GetAwaiter().GetResult(); });
                entered.TrySetResult(); await release.Task;
            };
            pending = operation switch
            {
                "create" => cache.CreateAsync(new() { ModelId = Model, Correlation = new("operation", "display"), TimeToLive = TimeSpan.FromMinutes(1), Messages = [new("user", "prefix")] }),
                "count" => facade.CountInputTokensAsync("selected", new() { ModelId = Model, ExplicitCache = receipt.Reference, Messages = [new("user", "suffix")] }),
                "update" => cache.UpdateExpiryAsync(receipt.Reference, Expiry.AddHours(1)),
                "delete" => cache.DeleteAsync(receipt.Reference),
                _ => cache.GetAsync(receipt.Reference)
            };
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = facade.DisposeAsync().AsTask();
            await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(retirement.IsCompleted); Assert.False(pending.IsCompleted);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(retirement.IsCompleted);
        }
        finally
        {
            retirement ??= facade.DisposeAsync().AsTask(); release.TrySetResult(); stopRelease.TrySetResult();
            try { if (pending is not null) { try { await pending; } catch (OperationCanceledException) { } } }
            finally { try { await retirement; } finally { blocking.Dispose(); } }
        }
        Assert.Equal(0, source.Saves);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task RepeatedGetCannotDetachAnAdmittedUseFromLaterDeletion()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var facade = new LlmProviderService(new Source(), [new GeminiApiLlmClient(http)]);
        var entered = Signal(); var release = Signal(); Task? count = null;
        try
        {
            var cache = await facade.BindContextCacheAsync("selected");
            var receipt = await cache.GetAsync(new("cachedContents/native", Model, "imported"));
            handler.Hold = async () => { handler.Hold = null; entered.TrySetResult(); await release.Task; };
            count = facade.CountInputTokensAsync("selected", new()
            { ModelId = Model, ExplicitCache = receipt.Reference, Messages = [new("user", "suffix")] });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _ = await cache.GetAsync(receipt.Reference);
            _ = await cache.DeleteAsync(receipt.Reference);
            release.TrySetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => count);
        }
        finally
        {
            release.TrySetResult();
            try { if (count is not null) { try { await count; } catch (InvalidOperationException) { } } }
            finally { await facade.DisposeAsync(); }
        }
    }

    [Fact]
    public async Task EquivalentCopiedReasoningOverridesDoNotBecomeCredentialReplacement()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var source = new Source();
        source.Value = source.Value with { CapabilityOverrides = new() { ReasoningLevels = new HashSet<LlmReasoningLevel> { LlmReasoningLevel.Low } } };
        await using var facade = new LlmProviderService(source, [new GeminiApiLlmClient(http)]);
        var receipt = await (await facade.BindContextCacheAsync("selected")).GetAsync(new("cachedContents/native", Model, "imported"));
        _ = await facade.CountInputTokensAsync("selected", new()
        { ModelId = Model, ExplicitCache = receipt.Reference with { }, Messages = [new("user", "suffix")] });
        Assert.Single(handler.Requests, request => request.Path == "/v1beta/cachedContents/native");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedGetCannotEraseSettledOrUncertainMutation(bool uncertainUpdate)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var facade = new LlmProviderService(new Source(), [new GeminiApiLlmClient(http)]);
        var entered = Signal(); var release = Signal(); Task<LlmContextCache>? reading = null;
        try
        {
            var cache = await facade.BindContextCacheAsync("selected");
            var receipt = await cache.GetAsync(new("cachedContents/native", Model, "imported"));
            handler.Hold = async () => { handler.Hold = null; entered.TrySetResult(); await release.Task; };
            reading = cache.GetAsync(receipt.Reference);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (uncertainUpdate)
            {
                handler.Status = HttpStatusCode.TooManyRequests;
                await Assert.ThrowsAsync<LlmContextCacheMutationUncertainException>(() => cache.UpdateExpiryAsync(receipt.Reference, Expiry.AddHours(1)));
                handler.Status = HttpStatusCode.OK;
            }
            else _ = await cache.DeleteAsync(receipt.Reference);
            release.TrySetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => reading);
            int effects = handler.Requests.Count;
            await Assert.ThrowsAsync<ArgumentException>(() => facade.CountInputTokensAsync("selected", new()
            { ModelId = Model, ExplicitCache = receipt.Reference, Messages = [new("user", "suffix")] }));
            Assert.Equal(effects, handler.Requests.Count);
        }
        finally
        {
            release.TrySetResult();
            try { if (reading is not null) { try { await reading; } catch (InvalidOperationException) { } } }
            finally { await facade.DisposeAsync(); }
        }
    }

    [Fact]
    public async Task OlderDelayedResolutionCannotReplaceNewerValidatedConfiguration()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var source = new Source(); var facade = new LlmProviderService(source, [new GeminiApiLlmClient(http)]);
        var entered = Signal(); var release = Signal(); Task? count = null;
        try
        {
            var cache = await facade.BindContextCacheAsync("selected");
            var receipt = await cache.GetAsync(new("cachedContents/native", Model, "imported"));
            LlmConnection old = source.Value;
            source.ReadOverride = async () => { source.ReadOverride = null; entered.TrySetResult(); await release.Task; return old; };
            count = facade.CountInputTokensAsync("selected", new()
            { ModelId = Model, ExplicitCache = receipt.Reference, Messages = [new("user", "suffix")] });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.Value = old with { Credential = new LlmCredential { ApiKey = "replacement" }.Serialize() };
            LlmContextCache current = await cache.GetAsync(receipt.Reference);
            release.TrySetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => count);
            _ = await facade.CountInputTokensAsync("selected", new()
            { ModelId = Model, ExplicitCache = current.Reference, Messages = [new("user", "current suffix")] });
            Assert.Single(handler.Requests, item => item.Path.EndsWith(":countTokens", StringComparison.Ordinal));
            Assert.Equal("replacement", handler.Requests.Last().Key);
        }
        finally
        {
            release.TrySetResult();
            try { if (count is not null) { try { await count; } catch (InvalidOperationException) { } } }
            finally { await facade.DisposeAsync(); }
        }
    }

    private sealed class Source : ILlmConnectionSource
    {
        internal LlmConnection Value { get; set; } = new()
        {
            Version = "v1",
            Transport = LlmTransport.GoogleGeminiApi,
            Endpoint = GeminiApiLlmClient.DefaultEndpoint,
            Credential = new LlmCredential { ApiKey = "private-key" }.Serialize()
        };
        internal int Reads { get; private set; }
        internal int Saves { get; private set; }
        internal CancellationToken LastToken { get; private set; }
        internal Func<Task<LlmConnection?>>? ReadOverride { get; set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { Assert.Equal("selected", connectionId); LastToken = cancellationToken; Reads++; return ReadOverride?.Invoke() ?? Task.FromResult<LlmConnection?>(Value); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        { Saves++; throw new InvalidOperationException("Gemini does not refresh API keys."); }
    }

    private sealed record Request(HttpMethod Method, string Path, string? Body, string? Key);

    private sealed class Handler : HttpMessageHandler
    {
        internal List<Request> Requests { get; } = [];
        internal DateTimeOffset Expiry { get; set; } = GeminiCacheLifecycleTests.Expiry;
        internal Action? BeforeCachePost { get; set; }
        internal Func<Task>? Hold { get; set; }
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Hold is { } hold && request.RequestUri!.AbsolutePath != "/v1beta/models") await hold();
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method, request.RequestUri!.AbsolutePath, body,
                request.Headers.TryGetValues("x-goog-api-key", out var values) ? values.Single() : null));
            string receipt = JsonSerializer.Serialize(new { name = "cachedContents/native", model = "models/" + Model, expireTime = Expiry.ToString("O"), usageMetadata = new { totalTokenCount = 0 } });
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/v1beta/cachedContents") BeforeCachePost?.Invoke();
            string response = request.Method == HttpMethod.Delete ? "{}"
                : request.RequestUri.AbsolutePath == "/upload/v1beta/files" ? "{\"file\":{\"name\":\"files/staged\",\"uri\":\"https://generativelanguage.googleapis.com/v1beta/files/staged\",\"mimeType\":\"text/plain\",\"state\":\"ACTIVE\",\"sizeBytes\":\"18\"}}"
                : request.RequestUri.AbsolutePath == "/v1beta/models" ? "{\"models\":[]}"
                : request.RequestUri.AbsolutePath.EndsWith(":countTokens", StringComparison.Ordinal) ? "{\"totalTokens\":9,\"cachedContentTokenCount\":7}"
                : request.RequestUri.AbsolutePath.EndsWith(":generateContent", StringComparison.Ordinal) ? "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"answer\"}]},\"finishReason\":\"STOP\"}]}"
                : request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/v1beta/cachedContents" ? "{\"cachedContents\":[" + receipt + "],\"nextPageToken\":\"next\"}"
                : receipt;
            return new(Status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
