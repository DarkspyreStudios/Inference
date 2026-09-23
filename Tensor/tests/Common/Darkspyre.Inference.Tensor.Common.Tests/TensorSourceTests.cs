using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorSourceTests
{
    private const string Commit = "1111111111111111111111111111111111111111";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task VariantsListMetadataWithoutSelectingOrOpeningAWeightFile()
    {
        int calls = 0;
        using var http = Client((request, _) =>
        {
            calls++;
            Assert.StartsWith("/api/models/owner/model/revision/", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return Task.FromResult(Json(new
            {
                id = "owner/model",
                sha = Commit,
                siblings = new[] { new { rfilename = "first.gguf", size = 11 }, new { rfilename = "second.gguf", size = 17 } }
            }));
        });
        using var source = new HuggingFaceModelSource(http);
        var result = await source.ListVariantsAsync(new("owner/model", "main"));
        Assert.Equal(TensorSourceState.Available, result.State);
        Assert.Equal(2, result.Value!.Items.Count);
        Assert.All(result.Value.Items, item => Assert.Equal(Commit, item.Provenance.Revision));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BoundedHeaderRequestRetainsTheRangeButNotCredentialsAcrossRedirects()
    {
        int calls = 0;
        using var http = Client((request, _) =>
        {
            calls++;
            Assert.Equal("bytes=10-13", request.Headers.Range!.ToString());
            if (calls == 1) { Assert.Equal("secret", request.Headers.Authorization!.Parameter); return Task.FromResult(Redirect("https://cdn.example/header")); }
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(Range([1, 2, 3, 4], 10, 13, 100));
        });
        using var source = new HuggingFaceModelSource(http, (_, _) => ValueTask.FromResult<TensorSourceCredential?>(TensorSourceCredential.Bearer("secret")));
        var result = await source.ReadMetadataAsync(Artifact(100), new(10, 4));
        Assert.Equal(TensorSourceState.Available, result.State);
        Assert.Equal(10, result.Value!.Offset);
        Assert.Equal(100, result.Value.TotalLength);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.Value.Bytes.ToArray());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task IgnoredRangeNeverFallsBackToReadingTheWholeWeightBody()
    {
        int calls = 0;
        using var http = Client((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnreadableContent() });
        });
        using var source = new HuggingFaceModelSource(http);
        var result = await source.ReadMetadataAsync(Artifact(100), new(0, 8));
        Assert.Equal(TensorSourceState.Unverified, result.State);
        Assert.Equal("metadata-range", Assert.Single(result.Issues!).Code);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(1, 4, 100, 4)] // Wrong starting offset.
    [InlineData(0, 7, 100, 4)] // Server sends beyond the requested range.
    [InlineData(0, 3, 101, 4)] // Known file size changed.
    [InlineData(0, 3, 100, 3)] // Truncated body.
    public async Task InconsistentRangeResponsesAreNotCompatibilityEvidence(long from, long to, long total, int bodyLength)
    {
        using var http = Client((_, _) => Task.FromResult(Range(new byte[bodyLength], from, to, total)));
        using var source = new HuggingFaceModelSource(http);
        Assert.Equal(TensorSourceState.Unverified, (await source.ReadMetadataAsync(Artifact(100), new(0, 4))).State);
    }

    [Fact]
    public async Task CompleteSmallMetadataFileRequiresExplicitIntentAndCompleteResponse()
    {
        using var http = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) }));
        using var source = new HuggingFaceModelSource(http);
        var result = await source.ReadMetadataAsync(Artifact(4), new(0, 4, TensorMetadataReadKind.CompleteMetadataFile));
        Assert.Equal(TensorSourceState.Available, result.State);
        Assert.Equal(4, result.Value!.Bytes.Length);
        using var partialHttp = Client((_, _) => Task.FromResult(Range([1, 2, 3, 4], 0, 3, 10)));
        using var partial = new HuggingFaceModelSource(partialHttp);
        Assert.Equal(TensorSourceState.Unverified, (await partial.ReadMetadataAsync(Artifact(10), new(0, 4, TensorMetadataReadKind.CompleteMetadataFile))).State);
    }

    [Fact]
    public async Task MetadataBoundAndPinnedProvenanceAreValidatedBeforeNetworkAccess()
    {
        using var http = Client((_, _) => throw new InvalidOperationException("Must not send."));
        using var source = new HuggingFaceModelSource(http);
        Assert.Equal(TensorSourceState.InvalidRequest, (await source.ReadMetadataAsync(Artifact(100), new(0, 16 * 1024 * 1024 + 1))).State);
        Assert.Equal(TensorSourceState.InvalidRequest, (await source.ReadMetadataAsync(Artifact(100) with { DownloadUri = new("https://elsewhere.example/weight") }, new(0, 4))).State);
        Assert.Equal(TensorSourceState.InvalidRequest, (await source.ReadMetadataAsync(Artifact(100), new(0, 4 * 1024 * 1024 + 1, TensorMetadataReadKind.CompleteMetadataFile))).State);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadMetadataAsync(Artifact(100), new(0, 4), new(true)));
    }

    private static TensorSourceArtifact Artifact(long length) => new("model.gguf", length, Hash,
        new(new("https://huggingface.co/"), "owner/model", Commit, "model.gguf", "test"),
        new($"https://huggingface.co/owner/model/resolve/{Commit}/model.gguf"));
    private static HttpResponseMessage Range(byte[] body, long from, long to, long total) => new(HttpStatusCode.PartialContent)
    { Content = new ByteArrayContent(body) { Headers = { ContentRange = new ContentRangeHeaderValue(from, to, total) } } };
    private sealed class UnreadableContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("Must not read a whole weight response.");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Fact]
    public async Task CandidatePinsRevisionPreservesUnknownsAndDoesNotReadWeights()
    {
        var requests = new List<Uri>();
        using var http = Client((request, _) =>
        {
            requests.Add(request.RequestUri!);
            return Task.FromResult(Json(new
            {
                id = "owner/model",
                sha = Commit,
                @private = true,
                gated = "manual",
                cardData = new { license = "apache-2.0" },
                gguf = new { architecture = "qwen3", context_length = 40960, chat_template = "template" },
                siblings = new object[]
                {
                    new { rfilename = "model.gguf", size = 100L, lfs = new { sha256 = Hash, size = 100L } },
                    new { rfilename = "config.json", blobId = Commit }
                }
            }));
        });
        using var source = new HuggingFaceModelSource(http);
        TensorSourceResult<TensorModelCandidate> result = await source.GetCandidateAsync(new("owner/model", "main", "model.gguf"));
        Assert.Equal(TensorSourceState.Available, result.State);
        TensorModelCandidate candidate = Assert.IsType<TensorModelCandidate>(result.Value);
        Assert.Equal(Commit, candidate.Selection.Revision);
        Assert.True(candidate.IsGated);
        Assert.True(candidate.IsPrivate);
        Assert.Equal(40960, candidate.Metadata.ContextLength);
        Assert.True(candidate.Metadata.HasChatTemplate);
        Assert.Equal(Hash, candidate.Artifacts[0].Sha256);
        Assert.Equal(new Uri($"https://huggingface.co/owner/model/resolve/{Commit}/model.gguf"), candidate.Artifacts[0].DownloadUri);
        Assert.Null(candidate.Artifacts[1].Sha256);
        Assert.Null(candidate.Artifacts[1].Length);
        Assert.Equal("apache-2.0", candidate.Artifacts[0].Provenance.License);
        Assert.Single(requests);
        Assert.StartsWith("/api/models/", requests[0].AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAndRevisionsUseBoundedPagesAndOpaqueCursors()
    {
        var requests = new List<Uri>();
        using var http = Client((request, _) =>
        {
            requests.Add(request.RequestUri!);
            bool commits = request.RequestUri!.AbsolutePath.Contains("commits", StringComparison.Ordinal);
            HttpResponseMessage response = commits ? Json(new[] { new { id = Commit, date = "2026-09-22T12:00:00Z" } })
                : Json(new[] { new { id = "owner/model", sha = Commit, @private = false, gated = false } });
            response.Headers.Add("Link", $"<{request.RequestUri}&cursor=a%2Bb%3D>; rel=\"next\"");
            return Task.FromResult(response);
        });
        using var source = new HuggingFaceModelSource(http);
        TensorSourceResult<TensorSourcePage<TensorSourceModel>> first = await source.SearchAsync(new("qwen", PageSize: 2));
        Assert.Equal("a+b=", first.Value!.Continuation);
        Assert.Null(first.Value.Items[0].License);
        await source.SearchAsync(new("qwen", first.Value.Continuation, 2));
        Assert.Contains("cursor=a%2Bb%3D", requests[1].Query, StringComparison.Ordinal);
        TensorSourceResult<TensorSourcePage<TensorSourceRevision>> revisions = await source.ListRevisionsAsync(new("owner/model", PageSize: 2));
        Assert.Equal(Commit, revisions.Value!.Items.Single().Revision);
        Assert.NotNull(revisions.Value.Items[0].CreatedAt);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TensorSourceState.AuthenticationRequired)]
    [InlineData(HttpStatusCode.Forbidden, TensorSourceState.AccessDenied)]
    [InlineData(HttpStatusCode.NotFound, TensorSourceState.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, TensorSourceState.TransientFailure)]
    [InlineData(HttpStatusCode.ServiceUnavailable, TensorSourceState.TransientFailure)]
    public async Task AccessFailuresAreTypedAndSanitized(HttpStatusCode status, TensorSourceState expected)
    {
        using var http = Client((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("secret-remote-body") }));
        using var source = new HuggingFaceModelSource(http);
        var result = await source.SearchAsync(new());
        Assert.Equal(expected, result.State);
        Assert.DoesNotContain("secret-remote-body", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GatedRepositoryIsNotCollapsedIntoForbidden()
    {
        using var http = Client((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-Error-Code", "GatedRepo");
            return Task.FromResult(response);
        });
        using var source = new HuggingFaceModelSource(http);
        Assert.Equal(TensorSourceState.Gated, (await source.SearchAsync(new())).State);
    }

    [Fact]
    public async Task RedirectsNeverForwardCredentialAcrossOriginOrReattachAfterLeaving()
    {
        var auth = new List<string?>();
        var origins = new List<Uri>();
        int count = 0;
        using var http = Client((request, _) =>
        {
            auth.Add(request.Headers.Authorization?.Parameter);
            HttpResponseMessage response = ++count switch
            {
                1 => Redirect("https://huggingface.co/same-origin"),
                2 => Redirect("https://cdn.example.test/artifact"),
                3 => Redirect("https://huggingface.co/returned"),
                _ => Json(Array.Empty<object>())
            };
            return Task.FromResult(response);
        });
        using var source = new HuggingFaceModelSource(http, (origin, _) =>
        {
            origins.Add(origin);
            return ValueTask.FromResult<TensorSourceCredential?>(TensorSourceCredential.Bearer("sensitive-token"));
        });
        Assert.Equal(TensorSourceState.Available, (await source.SearchAsync(new())).State);
        Assert.Equal(new string?[] { "sensitive-token", "sensitive-token", null, null }, auth);
        Assert.Equal(new Uri("https://huggingface.co/"), Assert.Single(origins));
        Assert.DoesNotContain("sensitive-token", TensorSourceCredential.Bearer("sensitive-token").ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://example.test/data")]
    [InlineData("https://user:password@example.test/data")]
    [InlineData("file:///tmp/data")]
    public async Task UnsafeRedirectIsRejectedBeforeSecondRequest(string redirect)
    {
        int calls = 0;
        using var http = Client((_, _) => { calls++; return Task.FromResult(Redirect(redirect)); });
        using var source = new HuggingFaceModelSource(http);
        Assert.Equal(TensorSourceState.InvalidRequest, (await source.SearchAsync(new())).State);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UntrustedPaginationDoesNotBecomeAnArbitraryRequest()
    {
        using var http = Client((_, _) =>
        {
            var response = Json(Array.Empty<object>());
            response.Headers.Add("Link", "<https://evil.example/api/models?cursor=x>; rel=\"next\"");
            return Task.FromResult(response);
        });
        using var source = new HuggingFaceModelSource(http);
        Assert.Equal(TensorSourceState.Unverified, (await source.SearchAsync(new())).State);
    }

    [Fact]
    public async Task InvalidQueryAndAlreadyCancelledCallDoNotSend()
    {
        using var http = Client((_, _) => throw new InvalidOperationException("Must not send."));
        using var source = new HuggingFaceModelSource(http);
        Assert.Equal(TensorSourceState.InvalidRequest, (await source.SearchAsync(new(PageSize: 101))).State);
        Assert.Equal(TensorSourceState.InvalidRequest, (await source.GetCandidateAsync(new("../bad", "main", "model.gguf"))).State);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.SearchAsync(new(), new CancellationToken(true)));
    }

    [Fact]
    public async Task OversizedMetadataDoesNotGetParsedAsAValidCatalog()
    {
        using var http = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(new string(' ', 4 * 1024 * 1024 + 1)) }));
        using var source = new HuggingFaceModelSource(http);
        Assert.Equal(TensorSourceState.Unverified, (await source.SearchAsync(new())).State);
    }

    [Fact]
    public async Task CallerCancellationPropagatesWhileTimeoutIsTyped()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = Client((_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); throw new InvalidOperationException(); });
        using var source = new HuggingFaceModelSource(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.SearchAsync(new(), cancellation.Token));
        using var timedHttp = Client((_, _) => throw new TaskCanceledException());
        using var timedSource = new HuggingFaceModelSource(timedHttp);
        Assert.Equal(TensorSourceState.TransientFailure, (await timedSource.SearchAsync(new())).State);
    }

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private static HttpResponseMessage Redirect(string uri) => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(uri) } };
}
