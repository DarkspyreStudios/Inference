using System.Globalization;
using System.Net;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class VertexCachePrefixTests
{
    private const string Model = "google/gemini-2.5-flash";
    private const int InlineBytes = 10 * 1024 * 1024;

    [Theory]
    [InlineData(10_000_001)]
    [InlineData(InlineBytes)]
    public async Task PublishedAmbiguousInlineIntervalIsSentUnchanged(int bytes)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var auth = new Authentication();
        await using var facade = new LlmProviderService(new Source(Connection()), [new VertexLlmClient(http, auth)]);
        var caches = await facade.BindContextCacheAsync("connection");
        _ = await caches.CreateAsync(Request() with
        {
            Messages = [new("user", "prefix")
        { Attachments = [new LlmAttachment { Kind = LlmBinaryKind.Image, FileName = "image.png", MediaType = "image/png", Data = new byte[bytes] }] }]
        });
        using JsonDocument body = JsonDocument.Parse(handler.CacheBody!);
        Assert.Equal(bytes, body.RootElement.GetProperty("contents")[0].GetProperty("parts")[1].GetProperty("inlineData").GetProperty("data").GetBytesFromBase64().Length);
        Assert.Single(handler.Paths); Assert.Equal(1, auth.Calls);
    }

    [Fact]
    public async Task KnownAggregateInlineMediaRefusesBeforeAuthentication()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var auth = new Authentication();
        await using var facade = new LlmProviderService(new Source(Connection()), [new VertexLlmClient(http, auth)]);
        var caches = await facade.BindContextCacheAsync("connection");
        var part = new LlmAttachment { Kind = LlmBinaryKind.Image, FileName = "image.png", MediaType = "image/png", Data = new byte[InlineBytes / 2 + 1] };
        await Assert.ThrowsAsync<NotSupportedException>(() => caches.CreateAsync(Request() with
        { Messages = [new("user", "prefix") { Attachments = [part, part] }] }));
        Assert.Equal(0, auth.Calls); Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task CanonicalTextHasNoInventedInlineMediaLimit()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        await using var facade = new LlmProviderService(new Source(Connection()), [new VertexLlmClient(http, new Authentication())]);
        var caches = await facade.BindContextCacheAsync("connection");
        string text = new('x', InlineBytes + 1);
        _ = await caches.CreateAsync(Request() with { Instructions = text, Messages = [new("user", text)] });
        using JsonDocument body = JsonDocument.Parse(handler.CacheBody!);
        Assert.Equal(text, body.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal(text, body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Single(handler.Paths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedDuplicatesOpenOnceAndCloseBeforePostWithoutSelection(bool staging)
    {
        var resources = new Resources("abc"u8.ToArray(), 3);
        using var handler = new Handler { BeforePost = () => Assert.Equal(1, resources.Closed) }; using var http = new HttpClient(handler);
        var auth = new Authentication();
        await using var facade = new LlmProviderService(new Source(Connection(staging)), [new VertexLlmClient(http, auth)]);
        var caches = await facade.BindContextCacheAsync("connection");
        var part = new LlmResourceAttachment(resources.Reference);
        _ = await caches.CreateAsync(Request() with { Resources = resources, Messages = [new("user", "prefix") { Attachments = [part, part] }] });
        Assert.Equal(1, resources.Opened); Assert.Equal(1, resources.Closed); Assert.Equal(0, resources.SelectionCalls);
        Assert.Equal(1, auth.Calls);
        using JsonDocument body = JsonDocument.Parse(handler.CacheBody!);
        var parts = body.RootElement.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal(parts[1].GetRawText(), parts[2].GetRawText());
        if (staging)
        {
            Assert.Equal(2, handler.Paths.Count); Assert.Equal("abc"u8.ToArray(), handler.UploadBytes);
            Assert.All(handler.Authorization, value => Assert.Equal("Bearer controlled", value));
            Assert.Equal("text/plain", parts[1].GetProperty("fileData").GetProperty("mimeType").GetString());
            string uri = parts[1].GetProperty("fileData").GetProperty("fileUri").GetString()!;
            Assert.StartsWith("gs://cache-bucket/prefix/", uri, StringComparison.Ordinal); Assert.DoesNotContain('#', uri);
            Assert.Contains("ifGenerationMatch=0", handler.Paths[0], StringComparison.Ordinal);
        }
        else { Assert.Single(handler.Paths); Assert.Equal("abc"u8.ToArray(), parts[1].GetProperty("inlineData").GetProperty("data").GetBytesFromBase64()); }
    }

    [Fact]
    public async Task ClonedRawOccurrencesDeduplicateExactBytesAndMimeWithoutDroppingOrder()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        await using var facade = new LlmProviderService(new Source(Connection(true)), [new VertexLlmClient(http, new Authentication())]);
        var caches = await facade.BindContextCacheAsync("connection");
        LlmAttachment Image(string mime, string text) => new()
        { Kind = LlmBinaryKind.Image, FileName = "image", MediaType = mime, Data = System.Text.Encoding.UTF8.GetBytes(text) };
        _ = await caches.CreateAsync(Request() with
        {
            Messages = [new("user", "prefix")
        { Attachments = [Image("image/png", "abc"), Image("image/png", "abc"), Image("image/jpeg", "abc"), Image("image/png", "xyz")] }]
        });
        Assert.Equal(4, handler.Paths.Count);
        Assert.Equal(2, handler.UploadBodies.Count(bytes => bytes.AsSpan().SequenceEqual("abc"u8)));
        Assert.Equal(1, handler.UploadBodies.Count(bytes => bytes.AsSpan().SequenceEqual("xyz"u8)));
        using JsonDocument body = JsonDocument.Parse(handler.CacheBody!);
        var parts = body.RootElement.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal(5, parts.GetArrayLength());
        Assert.Equal("prefix", parts[0].GetProperty("text").GetString());
        string UriAt(int index) => parts[index].GetProperty("fileData").GetProperty("fileUri").GetString()!;
        Assert.Equal(UriAt(1), UriAt(2)); Assert.NotEqual(UriAt(1), UriAt(3)); Assert.NotEqual(UriAt(1), UriAt(4));
        Assert.Equal("image/jpeg", parts[3].GetProperty("fileData").GetProperty("mimeType").GetString());
        Assert.Equal("image/png", parts[4].GetProperty("fileData").GetProperty("mimeType").GetString());
    }

    [Fact]
    public async Task UnknownNamedLengthReadsOnceThroughRemainingOccurrenceBudgetWithoutTruncating()
    {
        var resources = new Resources(new byte[InlineBytes / 2 + 1], null);
        using var handler = new Handler(); using var http = new HttpClient(handler);
        await using var facade = new LlmProviderService(new Source(Connection()), [new VertexLlmClient(http, new Authentication())]);
        var caches = await facade.BindContextCacheAsync("connection");
        var part = new LlmResourceAttachment(resources.Reference);
        var error = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => caches.CreateAsync(Request() with
        { Resources = resources, Messages = [new("user", "prefix") { Attachments = [part, part] }] }));
        Assert.IsType<NotSupportedException>(error.InnerException); Assert.Empty(handler.Paths);
        Assert.Equal(1, resources.Opened); Assert.Equal(1, resources.Closed);
        Assert.Equal(InlineBytes / 2 + 1, resources.BytesRead);
    }

    [Fact]
    public async Task KnownNamedOversizeRefusesBeforeOpening()
    {
        var resources = new Resources([], InlineBytes + 1);
        using var handler = new Handler(); using var http = new HttpClient(handler);
        await using var facade = new LlmProviderService(new Source(Connection()), [new VertexLlmClient(http, new Authentication())]);
        var caches = await facade.BindContextCacheAsync("connection");
        await Assert.ThrowsAsync<InferenceResourceOperationException>(() => caches.CreateAsync(Request() with
        { Resources = resources, Messages = [new("user", "prefix") { Attachments = [new LlmResourceAttachment(resources.Reference)] }] }));
        Assert.Equal(0, resources.Opened); Assert.Empty(handler.Paths);
    }

    [Theory]
    [InlineData("bucket")]
    [InlineData("name")]
    [InlineData("generation")]
    [InlineData("size")]
    [InlineData("mime")]
    public async Task ContradictoryStagingFactsPreventCachePostAndPreserveSafePartialEffects(string field)
    {
        var resources = new Resources("abc"u8.ToArray(), null);
        using var handler = new Handler { BadField = field }; using var http = new HttpClient(handler);
        await using var facade = new LlmProviderService(new Source(Connection(true)), [new VertexLlmClient(http, new Authentication())]);
        var caches = await facade.BindContextCacheAsync("connection");
        var error = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => caches.CreateAsync(Request() with
        { Resources = resources, Messages = [new("user", "prefix") { Attachments = [new LlmResourceAttachment(resources.Reference)] }] }));
        Assert.Equal(3, error.Failure.BytesTransferred); Assert.Equal(InferenceResourceCommitOutcome.NotAttempted, error.Failure.CommitOutcome);
        Assert.DoesNotContain("private-secret", error.ToString(), StringComparison.Ordinal);
        Assert.Single(handler.Paths); Assert.Equal(1, resources.Closed);
    }

    [Fact]
    public async Task ConfiguredStagingStreamsUnknownLengthAndRawMediaWithoutInventedLength()
    {
        var resources = new Resources("abc"u8.ToArray(), null);
        using var handler = new Handler(); using var http = new HttpClient(handler);
        await using var facade = new LlmProviderService(new Source(Connection(true)), [new VertexLlmClient(http, new Authentication())]);
        var caches = await facade.BindContextCacheAsync("connection");
        _ = await caches.CreateAsync(Request() with
        {
            Resources = resources,
            Messages = [new("user", "prefix")
        { Attachments = [new LlmResourceAttachment(resources.Reference), new LlmAttachment { Kind = LlmBinaryKind.Image, FileName = "image.gif", MediaType = "image/gif", Data = "raw"u8.ToArray() }] }]
        });
        Assert.Null(handler.Lengths[0]); Assert.True(handler.Chunked[0]); Assert.Equal(3, handler.Lengths[1]);
        Assert.Equal(3, handler.Paths.Count); Assert.Equal(1, resources.Closed);
        using JsonDocument body = JsonDocument.Parse(handler.CacheBody!);
        Assert.Equal("image/gif", body.RootElement.GetProperty("contents")[0].GetProperty("parts")[2].GetProperty("fileData").GetProperty("mimeType").GetString());
    }

    [Fact]
    public async Task OpaqueObjectPrefixIsEncodedOnceOnlyForHttpAndRetainedInNativeUri()
    {
        const string prefix = "path/+/%2F/\u00e9/";
        var resources = new Resources("abc"u8.ToArray(), 3);
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var connection = Connection(true) with { Vertex = Connection(true).Vertex! with { CacheStaging = new("cache-bucket", prefix) } };
        await using var facade = new LlmProviderService(new Source(connection), [new VertexLlmClient(http, new Authentication())]);
        var caches = await facade.BindContextCacheAsync("connection");
        _ = await caches.CreateAsync(Request() with
        {
            Resources = resources,
            Messages = [new("user", "prefix")
        { Attachments = [new LlmResourceAttachment(resources.Reference)] }]
        });
        Assert.StartsWith(prefix, handler.UploadName!, StringComparison.Ordinal);
        Assert.Contains("%252F", handler.Paths[0], StringComparison.Ordinal);
        using JsonDocument body = JsonDocument.Parse(handler.CacheBody!);
        Assert.Equal("gs://cache-bucket/" + handler.UploadName,
            body.RootElement.GetProperty("contents")[0].GetProperty("parts")[1].GetProperty("fileData").GetProperty("fileUri").GetString());
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData(null)]
    public async Task HandleMaySupplyPreviouslyUnknownMimeWithoutGuessing(string? observedMime)
    {
        var resources = new Resources("abc"u8.ToArray(), 3, null, observedMime);
        using var handler = new Handler(); using var http = new HttpClient(handler);
        await using var facade = new LlmProviderService(new Source(Connection()), [new VertexLlmClient(http, new Authentication())]);
        var caches = await facade.BindContextCacheAsync("connection");
        var request = Request() with
        {
            Resources = resources,
            Messages = [new("user", "prefix")
        { Attachments = [new LlmResourceAttachment(resources.Reference)] }]
        };
        if (observedMime is null) { await Assert.ThrowsAsync<InferenceResourceOperationException>(() => caches.CreateAsync(request)); Assert.Empty(handler.Paths); }
        else { _ = await caches.CreateAsync(request); Assert.Single(handler.Paths); }
        Assert.Equal(1, resources.Opened); Assert.Equal(1, resources.Closed);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task StagingCancellationPreservesOnlyActualSafeResponseFacts(HttpStatusCode status)
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new InterruptedUploadHandler(status); using var http = new HttpClient(handler);
        using var source = new MemoryStream("abc"u8.ToArray(), writable: false);
        var error = await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => VertexCacheFiles.UploadAsync(
            http, new("controlled", "source"), new("cache-bucket", "prefix/"), "text/plain", 3,
            null, source, token =>
            {
                if (handler.Returned) cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        var interrupted = Assert.IsType<OperationCanceledException>(error.InnerException);
        var observed = Assert.IsType<LlmProviderException>(interrupted.InnerException);
        Assert.Equal(status, observed.StatusCode); Assert.Equal("observed-request", observed.RequestId);
        Assert.Equal(TimeSpan.FromSeconds(5), observed.RetryAfter);
        Assert.Null(observed.InnerException); Assert.DoesNotContain("private-secret", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(InferenceResourceOperationPhase.Materialize, error.Failure.Phase);
        Assert.Equal(InferenceResourceCommitOutcome.NotAttempted, error.Failure.CommitOutcome);
        Assert.Equal(3, error.Failure.BytesTransferred); Assert.Equal(1, handler.Sends);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NamedReadRetirementDrainsAndNeverCreatesCache(bool staging, bool ignoresCancellation)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resources = new Resources("abc"u8.ToArray(), null)
        {
            ReadWait = token =>
            {
                entered.TrySetResult();
                return ignoresCancellation ? release.Task : release.Task.WaitAsync(token);
            }
        };
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var facade = new LlmProviderService(new Source(Connection(staging)), [new VertexLlmClient(http, new Authentication())]);
        Task<LlmContextCache>? create = null; Task? retirement = null;
        try
        {
            var caches = await facade.BindContextCacheAsync("connection");
            create = caches.CreateAsync(Request() with
            {
                Resources = resources,
                Messages = [new("user", "prefix")
            { Attachments = [new LlmResourceAttachment(resources.Reference)] }]
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = facade.DisposeAsync().AsTask();
            if (ignoresCancellation) { Assert.False(retirement.IsCompleted); Assert.False(create.IsCompleted); }
            release.TrySetResult();
            var error = await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => create);
            Assert.Equal(InferenceResourceCommitOutcome.NotAttempted, error.Failure.CommitOutcome);
            await retirement;
            Assert.Null(handler.CacheBody); Assert.Equal(staging ? 1 : 0, handler.Paths.Count);
            Assert.Equal(1, resources.Opened); Assert.Equal(1, resources.Closed); Assert.Equal(0, resources.SelectionCalls);
        }
        finally
        {
            retirement ??= facade.DisposeAsync().AsTask(); release.TrySetResult();
            try { if (create is not null) { try { await create; } catch (OperationCanceledException) { } } }
            finally { await retirement; }
        }
    }

    private sealed class InterruptedUploadHandler(HttpStatusCode status) : HttpMessageHandler
    {
        internal int Sends { get; private set; }
        internal bool Returned { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Sends++; Assert.Equal("abc"u8.ToArray(), await request.Content!.ReadAsByteArrayAsync(token));
            var response = new HttpResponseMessage(status) { Content = new StringContent("private-secret") };
            response.Headers.TryAddWithoutValidation("x-request-id", "observed-request");
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(5));
            Returned = true;
            return response;
        }
    }

    private static LlmContextCacheCreate Request() => new()
    {
        ModelId = Model,
        Correlation = new("operation", "display"),
        TimeToLive = TimeSpan.FromHours(1),
        Messages = [new("user", "prefix")]
    };
    private static LlmConnection Connection(bool staging = false) => new()
    {
        Transport = LlmTransport.GoogleVertexAi,
        Version = "1",
        Endpoint = "https://aiplatform.googleapis.com",
        Vertex = new()
        {
            ProjectId = "project-id",
            Location = "global",
            CacheStaging = staging ? new("cache-bucket", "prefix/") : null
        },
        CapabilityOverrides = new() { SupportsModelListing = false }
    };
    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => Task.FromResult<LlmConnection?>(connection);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class Authentication : IVertexAuthentication
    {
        internal int Calls { get; private set; }
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new VertexAccess("controlled", "source")); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];
        internal List<string?> Authorization { get; } = [];
        internal List<long?> Lengths { get; } = [];
        internal List<bool> Chunked { get; } = [];
        internal byte[]? UploadBytes { get; private set; }
        internal List<byte[]> UploadBodies { get; } = [];
        internal string? UploadName { get; private set; }
        internal string? CacheBody { get; private set; }
        internal Action? BeforePost { get; init; }
        internal string? BadField { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Paths.Add(request.RequestUri!.PathAndQuery); Authorization.Add(request.Headers.Authorization?.ToString());
            if (request.RequestUri.Host == "storage.googleapis.com")
            {
                Lengths.Add(request.Content!.Headers.ContentLength); Chunked.Add(request.Headers.TransferEncodingChunked == true);
                UploadBytes = await request.Content.ReadAsByteArrayAsync(token);
                UploadBodies.Add(UploadBytes);
                string name = Uri.UnescapeDataString(request.RequestUri.Query.Split('&').Single(part => part.StartsWith("name=", StringComparison.Ordinal))[5..]);
                UploadName = name;
                var facts = new Dictionary<string, string>
                {
                    ["bucket"] = "cache-bucket",
                    ["name"] = name,
                    ["generation"] = "42",
                    ["size"] = UploadBytes.Length.ToString(CultureInfo.InvariantCulture),
                    ["contentType"] = request.Content.Headers.ContentType!.MediaType!
                };
                if (BadField is { } field) facts[field == "mime" ? "contentType" : field] = "private-secret";
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(facts)) };
            }
            BeforePost?.Invoke(); CacheBody = await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"name\":\"projects/123456/locations/global/cachedContents/cache-1\",\"model\":\"models/gemini-2.5-flash\",\"expireTime\":\"2050-01-01T00:00:00Z\"}") };
        }
    }
    private sealed class Resources(byte[] bytes, long? known, string? descriptorMime = "text/plain", string? handleMime = "text/plain") : IInferenceResourceAccess
    {
        internal InferenceResourceReference Reference { get; } = new("workspace", "resource");
        public string WorkspaceId => "workspace";
        public string SessionId => "session";
        internal int Opened { get; private set; }
        internal int Closed { get; private set; }
        internal long BytesRead { get; private set; }
        internal int SelectionCalls { get; private set; }
        internal Func<CancellationToken, Task>? ReadWait { get; init; }
        private InferenceResourceDescriptor Descriptor => new(Reference, SessionId, "source", descriptorMime, known);
        private InferenceResourceDescriptor ObservedDescriptor => Descriptor with { ContentType = handleMime };
        public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => Task.FromResult<InferenceResourceDescriptor?>(Descriptor);
        public Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default)
        { Opened++; return Task.FromResult<IInferenceResourceReadHandle>(new Read(this, new HeldStream(this, bytes))); }
        public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) { SelectionCalls++; throw new InvalidOperationException(); }
        public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        private sealed class Read(Resources owner, MemoryStream stream) : IInferenceResourceReadHandle
        {
            public InferenceResourceDescriptor Resource => owner.ObservedDescriptor;
            public Stream Content => stream;
            public ValueTask DisposeAsync() { owner.BytesRead = stream.Position; owner.Closed++; stream.Dispose(); return ValueTask.CompletedTask; }
        }
        private sealed class HeldStream(Resources owner, byte[] bytes) : MemoryStream(bytes, writable: false)
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (owner.ReadWait is { } wait) await wait(cancellationToken);
                return await base.ReadAsync(buffer, CancellationToken.None);
            }
        }
    }
}
