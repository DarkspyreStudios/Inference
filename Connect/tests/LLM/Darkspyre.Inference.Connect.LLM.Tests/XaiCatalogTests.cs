using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiCatalogTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ConcurrentFamiliesShareGenericButKeepTheirOwnObservedMembership()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/models")
            {
                entered.TrySetResult();
                await release.Task;
            }
            return Response(request.RequestUri.AbsolutePath);
        });
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        var cache = new LlmModelListCache();
        using var caller = new CancellationTokenSource();
        Task<IReadOnlyList<LlmModel>>? abandoned = null;
        Task<IReadOnlyList<LlmModel>>[] waiting = [];
        try
        {
            abandoned = Get(cache, client, ModelCatalogFamily.Chat, caller.Token);
            await entered.Task.WaitAsync(Bound);
            waiting = [.. Enum.GetValues<ModelCatalogFamily>().Select(family => Get(cache, client, family))];
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(Bound));
            release.TrySetResult();
            IReadOnlyList<LlmModel>[] models = await Task.WhenAll(waiting).WaitAsync(Bound);
            Assert.Equal(1, handler.Paths.Count(path => path == "/v1/models"));
            Assert.Equal(5, handler.Paths.Count);
            Assert.Equal("grok-4.7", Assert.Single(models[0]).Id);
            Assert.Equal("image-model", Assert.Single(models[1]).Id);
            Assert.Equal("video-model", Assert.Single(models[2]).Id);
            Assert.Equal("embedding-model", Assert.Single(models[3]).Id);
            Assert.Equal(128, Assert.Single(models[0]).MaxContextTokens);
            Assert.NotNull(Assert.Single(models[0]).Capabilities);
            for (int index = 1; index < models.Length; index++) Assert.Null(Assert.Single(models[index]).Capabilities);
            Assert.False(Assert.Single(models[1]).CatalogEntry!.Value.TryGetProperty("input_modalities", out _));
            Assert.Equal(0, Assert.Single(models[2]).CatalogEntry!.Value.GetProperty("observed_zero").GetInt32());
            Assert.Equal(JsonValueKind.Null, Assert.Single(models[3]).CatalogEntry!.Value.GetProperty("observed_null").ValueKind);
            for (int index = 0; index < models.Length; index++)
                Assert.Same(models[index], await Get(cache, client, (ModelCatalogFamily)index));
            Assert.Equal(5, handler.Paths.Count);
        }
        finally
        {
            Task retirement = cache.DisposeAsync().AsTask();
            release.TrySetResult();
            try
            {
                if (abandoned is not null) await DrainAsync(abandoned);
                foreach (Task task in waiting) await DrainAsync(task);
            }
            finally { await retirement.WaitAsync(Bound); }
        }
    }

    [Fact]
    public async Task ExplicitRefreshInvalidatesGenericAndEveryPreviouslyPublishedFamily()
    {
        using var handler = new Handler(request => Task.FromResult(Response(request.RequestUri!.AbsolutePath)));
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        await using var cache = new LlmModelListCache();
        IReadOnlyList<LlmModel> image = await Get(cache, client, ModelCatalogFamily.Image);
        IReadOnlyList<LlmModel> chat = await Get(cache, client, ModelCatalogFamily.Chat);
        IReadOnlyList<LlmModel> refreshed = await cache.RefreshAsync("selected", new("v1", null), client, Connection,
            ModelCatalogFamily.Chat, CancellationToken.None);
        Assert.NotSame(chat, refreshed);
        Assert.NotSame(image, await Get(cache, client, ModelCatalogFamily.Image));
        Assert.Equal(2, handler.Paths.Count(path => path == "/v1/models"));
        Assert.Equal(2, handler.Paths.Count(path => path == "/v1/language-models"));
        Assert.Equal(2, handler.Paths.Count(path => path == "/v1/image-generation-models"));
        Assert.Equal(6, handler.Paths.Count);
    }

    [Theory]
    [InlineData("credential")]
    [InlineData("region")]
    [InlineData("endpoint-text")]
    [InlineData("tier")]
    [InlineData("agents")]
    [InlineData("headers")]
    [InlineData("options")]
    [InlineData("override")]
    [InlineData("model-override")]
    [InlineData("reasoning-set")]
    public async Task EveryAllowedSnapshotComponentInvalidatesUnderTheSameVersion(string component)
    {
        using var handler = new Handler(request => Task.FromResult(Response(request.RequestUri!.AbsolutePath)));
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        await using var cache = new LlmModelListCache();
        IReadOnlyList<LlmModel> original = await Get(cache, client, ModelCatalogFamily.Chat);
        LlmConnection changed = component switch
        {
            "credential" => Connection with { Credential = new LlmCredential { OAuth = new() { AccessToken = "other-token" } }.Serialize() },
            "region" => Connection with { Endpoint = "https://us.api.x.ai/v1", Xai = new() { Region = XaiRegion.UnitedStates } },
            "endpoint-text" => Connection with { Endpoint = "https://api.x.ai/v1/" },
            "tier" => Connection with { Xai = new() { ServiceTier = XaiServiceTier.Priority } },
            "agents" => Connection with { Xai = new() { MultiAgent = new(4) } },
            "headers" => Connection with { AdditionalHeaders = new Dictionary<string, string> { ["X-Catalog"] = "changed" } },
            "options" => Connection with { ProviderOptions = new Dictionary<string, JsonElement> { ["observed"] = Json("0") } },
            "override" => Connection with { CapabilityOverrides = new() { SupportsTemperature = false } },
            "model-override" => Connection with { ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides> { ["grok-4.7"] = new() { SupportsTemperature = false } } },
            _ => Connection with { CapabilityOverrides = new() { ReasoningLevels = new HashSet<LlmReasoningLevel> { LlmReasoningLevel.Low } } }
        };
        IReadOnlyList<LlmModel> current = await cache.GetAsync("selected", new("v1", null), client, changed, ModelCatalogFamily.Chat, CancellationToken.None);
        Assert.NotSame(original, current);
        Assert.Equal(2, handler.Paths.Count(path => path == "/v1/models"));
        Assert.Equal(4, handler.Paths.Count);
    }

    [Fact]
    public async Task CopiedDictionaryAndSetEqualityDoesNotInventReplacement()
    {
        using var handler = new Handler(request => Task.FromResult(Response(request.RequestUri!.AbsolutePath)));
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        await using var cache = new LlmModelListCache();
        LlmConnection first = Connection with
        {
            AdditionalHeaders = new Dictionary<string, string> { ["X-Catalog"] = "value" },
            CapabilityOverrides = new() { ReasoningLevels = new HashSet<LlmReasoningLevel> { LlmReasoningLevel.Low, LlmReasoningLevel.High } },
            ProviderOptions = new Dictionary<string, JsonElement> { ["observed"] = Json("{\"a\":0,\"b\":null}") }
        };
        IReadOnlyList<LlmModel> models = await cache.GetAsync("selected", new("v1", null), client, first, ModelCatalogFamily.Chat, CancellationToken.None);
        LlmConnection second = first with
        {
            AdditionalHeaders = new Dictionary<string, string> { ["x-catalog"] = "value" },
            CapabilityOverrides = new() { ReasoningLevels = new HashSet<LlmReasoningLevel> { LlmReasoningLevel.High, LlmReasoningLevel.Low } },
            ProviderOptions = new Dictionary<string, JsonElement> { ["observed"] = Json("{\"b\":null,\"a\":0}") }
        };
        Assert.Same(models, await cache.GetAsync("selected", new("v1", null), client, second, ModelCatalogFamily.Chat, CancellationToken.None));
        Assert.Equal(2, handler.Paths.Count);
    }

    [Fact]
    public void CatalogProjectionCopiesAllowedDataAndDropsUnrelatedRuntimeGraphs()
    {
        var headers = new Dictionary<string, string> { ["X-Catalog"] = "original" };
        var levels = new HashSet<LlmReasoningLevel> { LlmReasoningLevel.Low };
        var options = new Dictionary<string, JsonElement> { ["original"] = Json("false") };
        LlmConnection source = Connection with
        {
            Acp = new()
            {
                Profile = new() { Id = "unused", DeclaredRuntimeVersion = "1" },
                State = new LlmAgentIsolatedRuntimeState("/unused"),
                Authentication = new LlmAcpExternalAuthentication()
            },
            Copilot = new()
            {
                Profile = new() { Id = "unused", DeclaredRuntimeVersion = "1" },
                Authentication = new LlmCopilotRuntimeAccount()
            },
            Azure = new(),
            Vertex = new(),
            ChatCompletions = new(),
            RuntimeLaunchInputs = new() { Environment = [new("UNUSED", "runtime-secret")] },
            Catalog = [new("unused-catalog", "Unused")],
            ResolvedContinuationScope = "unused-scope",
            CacheCredentialEvidence = "unused-cache-evidence",
            AdditionalHeaders = headers,
            ProviderOptions = options,
            CapabilityOverrides = new() { ReasoningLevels = levels },
            ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides>
            { ["grok-4.7"] = new() { ReasoningLevels = levels } }
        };
        Assert.Throws<NotSupportedException>(() => CatalogConnectionData.FreezeXai(source));
        LlmConnection frozen = CatalogConnectionData.FreezeXai(source with { Acp = null, Copilot = null, RuntimeLaunchInputs = null });
        headers["X-Catalog"] = "changed";
        levels.Add(LlmReasoningLevel.High);
        options["original"] = Json("true");
        Assert.Equal("original", frozen.AdditionalHeaders["X-Catalog"]);
        Assert.False(frozen.ProviderOptions["original"].GetBoolean());
        Assert.Equal(LlmReasoningLevel.Low, Assert.Single(frozen.CapabilityOverrides!.ReasoningLevels!));
        Assert.Equal(LlmReasoningLevel.Low, Assert.Single(frozen.ModelCapabilityOverrides["grok-4.7"].ReasoningLevels!));
        Assert.Null(frozen.Acp);
        Assert.Null(frozen.Copilot);
        Assert.Null(frozen.Azure);
        Assert.Null(frozen.Vertex);
        Assert.Null(frozen.ChatCompletions);
        Assert.Null(frozen.RuntimeLaunchInputs);
        Assert.Null(frozen.Catalog);
        Assert.Null(frozen.ResolvedContinuationScope);
        Assert.Null(frozen.CacheCredentialEvidence);
        Assert.Null(frozen.ContinuationCodec);
        Assert.Null(frozen.AccountUsageObservationBinding);
        Assert.Null(frozen.CacheBindings);
        Assert.Null(frozen.CacheAdmission);
        Assert.Null(frozen.CacheOperation);
    }

    [Fact]
    public void ExactIdsWinAndAmbiguousAliasesDoNotInventGenericFactsOrProfiles()
    {
        IReadOnlyList<JsonElement> generic = Rows("""
            [{"id":"first","aliases":["shared"],"context_length":111},
             {"id":"second","aliases":["shared"],"context_length":222},
             {"id":"exact","aliases":["shared"],"actual_zero":0}]
            """);
        IReadOnlyList<LlmModel> ambiguous = XaiCatalog.Project(generic, Rows("""[{"id":"shared","observed_null":null}]"""));
        Assert.Null(Assert.Single(ambiguous).MaxContextTokens);
        Assert.Equal(JsonValueKind.Null, Assert.Single(ambiguous).CatalogEntry!.Value.GetProperty("observed_null").ValueKind);
        IReadOnlyList<LlmModel> exact = XaiCatalog.Project(generic, Rows("""[{"id":"exact","aliases":["shared"]}]"""));
        Assert.Equal(0, Assert.Single(exact).CatalogEntry!.Value.GetProperty("actual_zero").GetInt32());
        IReadOnlyList<LlmModel> profiles = XaiCatalog.Project([], Rows("""
            [{"id":"grok-4.7-a","aliases":["both"]},{"id":"grok-4.7-b","aliases":["both"]},{"id":"both"}]
            """));
        Assert.Equal("both", XaiCatalog.FindModel(profiles, "both")!.Id);
        Assert.Null(XaiCatalog.FindModel(profiles.Where(model => model.Id != "both").ToArray(), "both"));
        Assert.Equal("grok-4.7-a", XaiCatalog.FindModel(profiles, "grok-4.7-a")!.Id);
    }

    [Fact]
    public async Task DisabledChatListingPreservesTheExistingNoHttpOverride()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("No listing is admitted."));
        using var http = new HttpClient(handler);
        await using var cache = new LlmModelListCache();
        Assert.Empty(await cache.GetAsync("selected", new("v1", null), new XaiResponsesLlmClient(http),
            Connection with { CapabilityOverrides = new() { SupportsModelListing = false } }, ModelCatalogFamily.Chat, CancellationToken.None));
        Assert.Empty(handler.Paths);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task InvalidCachedVersionsRefuseWithoutHttp(string? version)
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("No invalid version is admitted."));
        using var http = new HttpClient(handler);
        await using var cache = new LlmModelListCache();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => cache.GetAsync("selected", new(version!, null), new XaiResponsesLlmClient(http),
            Connection with { Version = version! }, ModelCatalogFamily.Chat, CancellationToken.None));
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task ForeignEnumerationRunsOutsideGateAndCannotInstallAfterRetirement()
    {
        var cache = new LlmModelListCache();
        Task? retirement = null;
        var headers = new EnumeratingHeaders(() =>
        {
            Task.Run(() => { retirement = cache.DisposeAsync().AsTask(); }).WaitAsync(Bound).GetAwaiter().GetResult();
        });
        using var handler = new Handler(_ => throw new InvalidOperationException("A retired cache cannot send."));
        using var http = new HttpClient(handler);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync("selected", new("v1", null), new XaiResponsesLlmClient(http),
                Connection with { AdditionalHeaders = headers }, ModelCatalogFamily.Chat, CancellationToken.None));
            await retirement!.WaitAsync(Bound);
            Assert.Equal(1, headers.Callbacks);
            Assert.Empty(handler.Paths);
        }
        finally { await (retirement ?? cache.DisposeAsync().AsTask()).WaitAsync(Bound); }
    }

    [Fact]
    public async Task ActualResponseCleanupFailureIsSharedByRetirementAndNeverRetries()
    {
        var content = new RefusingContent();
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        var cache = new LlmModelListCache();
        try
        {
            InferenceResourceOperationException failure = await Assert.ThrowsAsync<InferenceResourceOperationException>(
                () => Get(cache, new XaiResponsesLlmClient(http), ModelCatalogFamily.Chat));
            Assert.Equal(InferenceResourceOperationPhase.Cleanup, failure.Failure.Phase);
            Assert.DoesNotContain("untrusted-cleanup-secret", failure.ToString(), StringComparison.Ordinal);
            Assert.Same(failure, await Assert.ThrowsAsync<InferenceResourceOperationException>(() => cache.DisposeAsync().AsTask()));
            Assert.Same(failure, await Assert.ThrowsAsync<InferenceResourceOperationException>(() => cache.DisposeAsync().AsTask()));
            Assert.Equal(1, content.CleanupCalls);
            Assert.Single(handler.Paths);
        }
        finally { content.AllowCleanup = true; content.Dispose(); }
    }

    [Fact]
    public async Task ReaderPreservesIndependentPrimaryAndSafeCleanupFailuresInOrder()
    {
        var primary = new InvalidOperationException("controlled owned operation failure");
        var content = new RefusingContent();
        bool observed = false;
        using var handler = new Handler(_ =>
        {
            observed = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler);
        try
        {
            AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => XaiCatalog.ReadAsync(http,
                Connection, ModelCatalogPart.Generic, () => { if (observed) throw primary; }, CancellationToken.None));
            Assert.Equal(2, failure.InnerExceptions.Count);
            Assert.Same(primary, failure.InnerExceptions[0]);
            var cleanup = Assert.IsType<InferenceResourceOperationException>(failure.InnerExceptions[1]);
            Assert.Equal(InferenceResourceOperationPhase.Cleanup, cleanup.Failure.Phase);
            Assert.DoesNotContain("untrusted-cleanup-secret", failure.ToString(), StringComparison.Ordinal);
            Assert.Equal(1, content.CleanupCalls);
            Assert.Single(handler.Paths);
        }
        finally { content.AllowCleanup = true; content.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupRetainsTheActualSafePrivateCauseAndRepeatedRetirementCarrier(bool withPrimary)
    {
        var content = new RefusingContent();
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(withPrimary ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
        { Content = content }));
        using var http = new HttpClient(handler);
        var cache = new LlmModelListCache();
        try
        {
            Exception operation;
            if (withPrimary)
            {
                AggregateException aggregate = await Assert.ThrowsAsync<AggregateException>(() => Get(cache, new XaiResponsesLlmClient(http), ModelCatalogFamily.Chat));
                Assert.Equal(2, aggregate.InnerExceptions.Count);
                Assert.IsType<LlmProviderException>(aggregate.InnerExceptions[0]);
                operation = aggregate.InnerExceptions[1];
            }
            else operation = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => Get(cache, new XaiResponsesLlmClient(http), ModelCatalogFamily.Chat));
            Assert.Same(content.Failure, OriginalCleanupCause(operation));
            Assert.Same(operation, await Assert.ThrowsAsync<InferenceResourceOperationException>(() => cache.DisposeAsync().AsTask()));
            Assert.Same(operation, await Assert.ThrowsAsync<InferenceResourceOperationException>(() => cache.DisposeAsync().AsTask()));
            Assert.DoesNotContain("untrusted-cleanup-secret", operation.ToString(), StringComparison.Ordinal);
            Assert.Equal(1, content.CleanupCalls);
            Assert.Single(handler.Paths);
        }
        finally
        {
            try { await cache.DisposeAsync(); } catch (InferenceResourceOperationException) { }
            content.AllowCleanup = true;
            content.Dispose();
        }
    }

    [Fact]
    public async Task CanceledCleanupPreservesItsActualTokenAndSafePrivateOriginal()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var original = new OperationCanceledException("untrusted-cleanup-secret", source.Token);
        var content = new RefusingContent { Failure = original };
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        var cache = new LlmModelListCache();
        try
        {
            var failure = await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => Get(cache, new XaiResponsesLlmClient(http), ModelCatalogFamily.Chat));
            Assert.Equal(source.Token, failure.CancellationToken);
            Assert.Same(original, OriginalCleanupCause(failure));
            Assert.Same(failure, await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => cache.DisposeAsync().AsTask()));
            Assert.Same(failure, await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => cache.DisposeAsync().AsTask()));
            Assert.DoesNotContain("untrusted-cleanup-secret", failure.ToString(), StringComparison.Ordinal);
            Assert.Equal(1, content.CleanupCalls);
        }
        finally
        {
            try { await cache.DisposeAsync(); } catch (InferenceResourceOperationException) { } catch (InferenceResourceOperationCanceledException) { }
            content.AllowCleanup = true;
            content.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualAppliedCustomHeaderValuesCannotEscapeCatalogDiagnostics(bool malformed)
    {
        const string secret = "catalog-header-private";
        using var handler = new Handler(request =>
        {
            Assert.Equal(secret, Assert.Single(request.Headers.GetValues("X-Catalog")));
            return Task.FromResult(new HttpResponseMessage(malformed ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
            { Content = new StringContent("{\"message\":\"" + secret + "\"}") });
        });
        using var http = new HttpClient(handler);
        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() => new XaiResponsesLlmClient(http).ListModelsAsync(
            Connection with { AdditionalHeaders = new Dictionary<string, string> { ["X-Catalog"] = secret } }));
        Assert.DoesNotContain(secret, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, failure.SanitizedResponseBody!, StringComparison.Ordinal);
        Assert.Single(handler.Paths);
    }

    private static Exception OriginalCleanupCause(Exception failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            PropertyInfo? property = current.GetType().GetProperty("Original", BindingFlags.Instance | BindingFlags.NonPublic);
            if (property is null) continue;
            Assert.Same(typeof(XaiCatalog).Assembly, current.GetType().Assembly);
            return Assert.IsAssignableFrom<Exception>(property.GetValue(current));
        }
        Assert.Fail("The safe owned cleanup graph must retain its exact original cause privately.");
        throw new InvalidOperationException("Unreachable assertion.");
    }

    private static LlmConnection Connection => new()
    {
        Version = "v1",
        Transport = LlmTransport.XaiResponses,
        Endpoint = "https://api.x.ai/v1",
        Credential = new LlmCredential { OAuth = new() { AccessToken = "token" } }.Serialize()
    };

    private static Task<IReadOnlyList<LlmModel>> Get(LlmModelListCache cache, XaiResponsesLlmClient client,
        ModelCatalogFamily family, CancellationToken token = default) => cache.GetAsync("selected", new("v1", null), client, Connection, family, token);

    private static HttpResponseMessage Response(string path) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(path switch
        {
            "/v1/models" => """{"data":[{"id":"grok-4.7","context_length":128},{"id":"image-model"},{"id":"video-model"},{"id":"embedding-model"},{"id":"generic-only"}]}""",
            "/v1/language-models" => """{"models":[{"id":"grok-4.7","input_modalities":["text"]}]}""",
            "/v1/image-generation-models" => """{"models":[{"id":"image-model"}]}""",
            "/v1/video-generation-models" => """{"models":[{"id":"video-model","observed_zero":0}]}""",
            "/v1/embedding-models" => """{"models":[{"id":"embedding-model","observed_null":null}]}""",
            _ => throw new InvalidOperationException("Unexpected catalog route.")
        })
    };

    private static JsonElement Json(string json) { using JsonDocument document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    private static JsonElement[] Rows(string json) => Json(json).EnumerateArray().Select(row => row.Clone()).ToArray();
    private static async Task DrainAsync(Task task) { try { await task.WaitAsync(Bound); } catch (OperationCanceledException) { } }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal ConcurrentQueue<string> Paths { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Paths.Enqueue(request.RequestUri!.AbsolutePath); return send(request); }
    }

    private sealed class EnumeratingHeaders(Action enumerate) : IReadOnlyDictionary<string, string>
    {
        private readonly Dictionary<string, string> _data = new() { ["X-Catalog"] = "value" };
        internal int Callbacks { get; private set; }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        { if (Callbacks == 0) { Callbacks++; enumerate(); } return _data.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public int Count => _data.Count;
        public IEnumerable<string> Keys => _data.Keys;
        public IEnumerable<string> Values => _data.Values;
        public string this[string key] => _data[key];
        public bool ContainsKey(string key) => _data.ContainsKey(key);
        public bool TryGetValue(string key, out string value) => _data.TryGetValue(key, out value!);
    }

    private sealed class RefusingContent() : StringContent("{\"data\":[]}")
    {
        internal Exception Failure { get; init; } = new InvalidOperationException("untrusted-cleanup-secret");
        internal int CleanupCalls { get; private set; }
        internal bool AllowCleanup { get; set; }
        protected override void Dispose(bool disposing)
        { CleanupCalls++; if (!AllowCleanup) throw Failure; base.Dispose(disposing); }
    }
}
