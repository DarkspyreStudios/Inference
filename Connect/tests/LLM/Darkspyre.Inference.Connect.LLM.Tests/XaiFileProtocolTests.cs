using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiFileProtocolTests
{
    private const string File = "{\"id\":\"file-a\",\"filename\":\"actual.txt\",\"bytes\":3,\"created_at\":0,\"object\":\"file\"}";

    [Fact]
    public async Task UploadUsesIncrementalBorrowedSourceAndExpiryBeforeFile()
    {
        using var source = new BorrowedSource("abc");
        string? wire = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.x.ai/v1/files", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer oauth-token", request.Headers.Authorization!.ToString());
            wire = await request.Content!.ReadAsStringAsync();
            return Response(File);
        }));
        await using var adapter = Create(http);
        LlmProviderFile file = await Invoke<LlmProviderFile>(adapter, "UploadAsync", Connection(), source, "actual.txt",
            new LlmFileUploadOptions(TimeSpan.FromHours(1)), (Action)(() => { }), CancellationToken.None);
        Assert.Equal(3, file.Bytes);
        Assert.Equal(DateTimeOffset.UnixEpoch, file.CreatedAt);
        Assert.True(wire!.IndexOf("expires_after", StringComparison.Ordinal) < wire.IndexOf("name=file", StringComparison.Ordinal)
            || wire.IndexOf("expires_after", StringComparison.Ordinal) < wire.IndexOf("name=\"file\"", StringComparison.Ordinal));
        Assert.Contains("3600", wire);
        Assert.Contains("abc", wire);
        Assert.Equal(0, source.Disposals);
        Assert.Equal(0, source.Flushes);
        Assert.Equal(0, source.Seeks);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task ListUsesOneActualDenominatorAndPreservesOptionalToken(int count, bool last)
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            Assert.Contains("limit=2", request.RequestUri!.Query);
            Assert.Contains("pagination_token=a%26b", request.RequestUri.Query);
            Assert.Contains("order=asc", request.RequestUri.Query);
            Assert.Contains("sort_by=size", request.RequestUri.Query);
            return Task.FromResult(Response("{\"data\":[" + string.Join(',', Enumerable.Repeat(File, count)) + "]}"));
        }));
        await using var adapter = Create(http);
        LlmFilePage page = await Invoke<LlmFilePage>(adapter, "ListAsync", Connection(),
            new LlmFileListRequest { Limit = 2, PaginationToken = "a&b", Order = LlmFileOrder.Ascending, Sort = LlmFileSort.Size },
            (Action)(() => { }), CancellationToken.None);
        Assert.Equal(last, page.IsKnownLastPage);
        Assert.Null(page.PaginationToken);
        Assert.Equal(count, page.Items.Count);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a\nb")]
    public async Task LiteralUnsafeIdsRefuseBeforeHttp(string id)
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return Task.FromResult(Response(File)); }));
        await using var adapter = Create(http);
        await Assert.ThrowsAsync<ArgumentException>(() => Invoke<LlmProviderFile>(adapter, "GetAsync", Connection(), id,
            (Action)(() => { }), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DownloadCopiesCompleteBytesWithoutClosingOrFlushingDestination()
    {
        using var destination = new BorrowedSource("");
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("/v1/files/a%2Fb/content", request.RequestUri!.AbsolutePath);
            Assert.Equal("?format=text", request.RequestUri.Query);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("abc")) };
            return Task.FromResult(response);
        }));
        await using var adapter = Create(http);
        ResourceDownloadReceipt receipt = await Invoke<ResourceDownloadReceipt>(adapter, "DownloadAsync", Connection(), "a/b", destination,
            new LlmFileDownloadOptions(LlmFileContentFormat.Text), (Action)(() => { }), CancellationToken.None);
        Assert.Equal(3, receipt.BytesWritten);
        Assert.Equal("abc", Encoding.UTF8.GetString(destination.ToArray()));
        Assert.Equal(0, destination.Disposals);
        Assert.Equal(0, destination.Flushes);
        Assert.Equal(0, destination.Seeks);
    }

    [Fact]
    public async Task GetPreservesOnlyActualNullableMetadata()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("/v1/files/%252e", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Response(File[..^1] + ",\"expires_at\":3600,\"public_url\":\"https://capability.invalid/secret\",\"public_url_expires_at\":null}"));
        }));
        await using var adapter = Create(http);
        LlmProviderFile result = await Invoke<LlmProviderFile>(adapter, "GetAsync", Connection(), "%2e", (Action)(() => { }), CancellationToken.None);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddHours(1), result.ExpiresAt);
        Assert.Null(result.PublicUrlExpiresAt);
        Assert.Null(result.Resource);
        Assert.Equal("https://capability.invalid/secret", result.PublicUrl);
        Assert.DoesNotContain("secret", result.ToString());
    }

    [Theory]
    [InlineData("{\"id\":\"file-a\",\"filename\":\"a\",\"bytes\":0,\"object\":\"file\"}")]
    [InlineData("{\"id\":\"file-a\",\"filename\":\"a\",\"bytes\":0,\"created_at\":null,\"object\":\"file\"}")]
    [InlineData("{\"id\":\"file-a\",\"filename\":\"a\",\"bytes\":-1,\"created_at\":0,\"object\":\"file\"}")]
    [InlineData("{\"id\":\"file-a\",\"filename\":\"a\",\"bytes\":1.2,\"created_at\":0,\"object\":\"file\"}")]
    [InlineData("{\"id\":\"file-a\",\"id\":\"other\",\"filename\":\"a\",\"bytes\":0,\"created_at\":0,\"object\":\"file\"}")]
    [InlineData("{\"id\":\"file-a\",\"filename\":null,\"bytes\":0,\"created_at\":0,\"object\":\"file\"}")]
    [InlineData("{\"id\":\"file-a\",\"filename\":\"a\",\"bytes\":0,\"created_at\":0,\"object\":\"skill\"}")]
    [InlineData("{\"public_url\":\"https://secret.invalid/token\"}")]
    public async Task RequiredMalformedMetadataNeverFabricatesSuccess(string json)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Response(json))));
        await using var adapter = Create(http);
        LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() => Invoke<LlmProviderFile>(adapter,
            "GetAsync", Connection(), "file-a", (Action)(() => { }), CancellationToken.None));
        Assert.Equal("Grok Files returned invalid protocol data.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAndRevokePreserveActualBooleanAndDoNotRetry(bool success)
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            bool revoke = request.RequestUri!.AbsolutePath.EndsWith("/revoke", StringComparison.Ordinal);
            Assert.Equal(revoke ? HttpMethod.Post : HttpMethod.Delete, request.Method);
            Assert.Null(request.Content);
            return Task.FromResult(Response(revoke
                ? $"{{\"id\":\"file-a\",\"revoked\":{success.ToString().ToLowerInvariant()},\"public_url\":null}}"
                : $"{{\"id\":\"file-a\",\"deleted\":{success.ToString().ToLowerInvariant()},\"object\":\"file\"}}"));
        }));
        await using var adapter = Create(http);
        LlmFileDeletion deleted = await Invoke<LlmFileDeletion>(adapter, "DeleteAsync", Connection(), "file-a", (Action)(() => { }), CancellationToken.None);
        XaiPublicUrlRevocation revoked = await Invoke<XaiPublicUrlRevocation>(adapter, "RevokePublicUrlAsync", Connection(), "file-a", (Action)(() => { }), CancellationToken.None);
        Assert.Equal(success, deleted.Deleted);
        Assert.Equal(success, revoked.Revoked);
        Assert.Null(revoked.PublicUrl);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(null, "{}")]
    [InlineData(3600, "{\"expires_after\":3600}")]
    [InlineData(2592000, "{\"expires_after\":2592000}")]
    public async Task PublicUrlCreationHasOnlyExplicitWholeSecondExpiry(int? seconds, string expected)
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++;
            Assert.Equal("/v1/files/file-a/public-url", request.RequestUri!.AbsolutePath);
            Assert.Equal(expected, await request.Content!.ReadAsStringAsync());
            return Response("{\"public_url\":\"https://files.invalid/capability\"}");
        }));
        await using var adapter = Create(http);
        XaiPublicUrl result = await Invoke<XaiPublicUrl>(adapter, "CreatePublicUrlAsync", Connection(), "file-a",
            seconds is { } value ? new XaiPublicUrlOptions(TimeSpan.FromSeconds(value)) : null, (Action)(() => { }), CancellationToken.None);
        Assert.Null(result.ExpiresAt);
        Assert.Equal("https://files.invalid/capability", result.PublicUrl);
        Assert.Equal(1, calls);
        Assert.DoesNotContain("capability", result.ToString());
    }

    [Theory]
    [InlineData(400, "invalid_token", typeof(InferenceException))]
    [InlineData(401, "unknown", typeof(InferenceException))]
    [InlineData(402, "unknown", typeof(InferenceException))]
    [InlineData(403, "unknown", typeof(LlmProviderException))]
    [InlineData(500, "unknown", typeof(LlmProviderException))]
    public async Task FileErrorsClassifyActualBodyWithoutPublishingUrlsOrUnknownProviderData(int status, string code, Type expected)
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent($"{{\"error\":{{\"code\":\"{code}\",\"message\":\"https://secret.invalid/secret-url oauth-token\",\"public_url\":\"secret-url\"}}}}") });
        }));
        await using var adapter = Create(http);
        Exception error = await Assert.ThrowsAsync(expected, () => Invoke<LlmProviderFile>(adapter, "GetAsync", Connection(), "file-a", (Action)(() => { }), CancellationToken.None));
        Assert.DoesNotContain("secret-url", error.ToString());
        Assert.DoesNotContain("oauth-token", error.ToString());
        Assert.DoesNotContain("secret.invalid", error.ToString());
        LlmProviderException provider = error as LlmProviderException ?? Assert.IsType<LlmProviderException>(error.InnerException);
        Assert.Equal((HttpStatusCode)status, provider.StatusCode);
        Assert.Equal("Provider rejected the Files request.", provider.SanitizedResponseBody);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ListPreservesResponseOrderOpaqueFilterAndObservedToken()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Contains("filter=filename%3D%22a%26b%22", request.RequestUri!.Query);
            return Task.FromResult(Response("{\"data\":[" + File.Replace("file-a", "second") + "," + File.Replace("file-a", "first") + "],\"pagination_token\":\"opaque-next\"}"));
        }));
        await using var adapter = Create(http);
        LlmFilePage page = await Invoke<LlmFilePage>(adapter, "ListAsync", Connection(), new LlmFileListRequest { Limit = 2, Filter = "filename=\"a&b\"" }, (Action)(() => { }), CancellationToken.None);
        Assert.Collection(page.Items, file => Assert.Equal("second", file.Id), file => Assert.Equal("first", file.Id));
        Assert.Equal("opaque-next", page.PaginationToken);
        Assert.False(page.IsKnownLastPage);
    }

    [Theory]
    [InlineData("ListAsync", "{\"data\":null}")]
    [InlineData("ListAsync", "{}")]
    [InlineData("DeleteAsync", "{\"id\":\"file-a\",\"object\":\"file\"}")]
    [InlineData("DeleteAsync", "{\"id\":\"file-a\",\"deleted\":null,\"object\":\"file\"}")]
    [InlineData("CreatePublicUrlAsync", "{\"expires_at\":1}")]
    [InlineData("CreatePublicUrlAsync", "{\"public_url\":null}")]
    [InlineData("RevokePublicUrlAsync", "{\"id\":\"file-a\"}")]
    [InlineData("RevokePublicUrlAsync", "{\"id\":null,\"revoked\":false}")]
    public async Task OtherRequiredResponseFactsRefuseMissingAndNull(string method, string json)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Response(json))));
        await using var adapter = Create(http);
        Action fence = () => { };
        await Assert.ThrowsAsync<LlmProviderException>(async () =>
        {
            switch (method)
            {
                case "ListAsync": await Invoke<LlmFilePage>(adapter, method, Connection(), new LlmFileListRequest(), fence, CancellationToken.None); break;
                case "DeleteAsync": await Invoke<LlmFileDeletion>(adapter, method, Connection(), "file-a", fence, CancellationToken.None); break;
                case "CreatePublicUrlAsync": await Invoke<XaiPublicUrl>(adapter, method, Connection(), "file-a", null, fence, CancellationToken.None); break;
                case "RevokePublicUrlAsync": await Invoke<XaiPublicUrlRevocation>(adapter, method, Connection(), "file-a", fence, CancellationToken.None); break;
                default: throw new InvalidOperationException();
            }
        });
    }

    [Theory]
    [InlineData("api-key", typeof(InferenceException))]
    [InlineData("wrong-region", typeof(ArgumentException))]
    [InlineData("protected-authorization", typeof(InvalidOperationException))]
    [InlineData("control-header", typeof(InvalidOperationException))]
    public async Task UnsupportedAuthenticationAndHeaderInputsRefuseBeforeHttp(string scenario, Type expected)
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return Task.FromResult(Response(File)); }));
        await using var adapter = Create(http);
        LlmConnection connection = scenario switch
        {
            "api-key" => Connection() with { Credential = new LlmCredential { ApiKey = "secret-api-key" }.Serialize() },
            "wrong-region" => Connection() with { Endpoint = "https://wrong.invalid/v1" },
            "protected-authorization" => Connection() with { AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "secret-header" } },
            "control-header" => Connection() with { AdditionalHeaders = new Dictionary<string, string> { ["X-Test"] = "a\rb" } },
            _ => throw new InvalidOperationException()
        };
        await Assert.ThrowsAsync(expected, () => Invoke<LlmProviderFile>(adapter, "GetAsync", connection, "file-a", (Action)(() => { }), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task UploadReadsMultipleNonseekableChunksOnlyWhenHttpConsumesRequest()
    {
        string bytes = new('a', 200000);
        using var source = new BorrowedSource(bytes);
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(0, source.Reads);
            Assert.Null(request.Content!.Headers.ContentLength);
            await request.Content.CopyToAsync(Stream.Null);
            Assert.True(source.Reads >= 4);
            return Response(File.Replace("\"bytes\":3", "\"bytes\":200000"));
        }));
        await using var adapter = Create(http);
        LlmProviderFile result = await Invoke<LlmProviderFile>(adapter, "UploadAsync", Connection(), source, "actual.txt", null, (Action)(() => { }), CancellationToken.None);
        Assert.Equal(200000, result.Bytes);
        Assert.Equal(0, source.Disposals);
        Assert.Equal(0, source.Seeks);
    }

    [Fact]
    public async Task PrematureDownloadEofDoesNotReturnCompleteReceipt()
    {
        using var destination = new BorrowedSource("");
        using var http = new HttpClient(new Handler(_ =>
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes("abc"));
            content.Headers.ContentLength = 9;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        await using var adapter = Create(http);
        await Assert.ThrowsAsync<LlmProviderException>(() => Invoke<ResourceDownloadReceipt>(adapter, "DownloadAsync", Connection(), "file-a", destination,
            new LlmFileDownloadOptions(LlmFileContentFormat.Text), (Action)(() => { }), CancellationToken.None));
        Assert.Equal("abc", Encoding.UTF8.GetString(destination.ToArray()));
        Assert.Equal(0, destination.Disposals);
        Assert.Equal(0, destination.Flushes);
    }

    [Theory]
    [InlineData("CreatePublicUrlAsync", "relative/path")]
    [InlineData("CreatePublicUrlAsync", "")]
    [InlineData("CreatePublicUrlAsync", "https://files.invalid/a\nb")]
    [InlineData("GetAsync", "relative/path")]
    [InlineData("GetAsync", "")]
    [InlineData("GetAsync", "https://files.invalid/a\rb")]
    [InlineData("RevokePublicUrlAsync", "relative/path")]
    [InlineData("RevokePublicUrlAsync", "")]
    [InlineData("RevokePublicUrlAsync", "https://files.invalid/a\tb")]
    public async Task InvalidObservedPublicUrlRefusesWithoutPublishingCapability(string method, string url)
    {
        string encoded = JsonSerializer.Serialize(url);
        string body = method switch
        {
            "CreatePublicUrlAsync" => "{\"public_url\":" + encoded + "}",
            "GetAsync" => File[..^1] + ",\"public_url\":" + encoded + "}",
            "RevokePublicUrlAsync" => "{\"id\":\"file-a\",\"revoked\":true,\"public_url\":" + encoded + "}",
            _ => throw new InvalidOperationException()
        };
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Response(body))));
        await using var adapter = Create(http);
        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(async () =>
        {
            if (method == "CreatePublicUrlAsync") await Invoke<XaiPublicUrl>(adapter, method, Connection(), "file-a", null, (Action)(() => { }), CancellationToken.None);
            else if (method == "GetAsync") await Invoke<LlmProviderFile>(adapter, method, Connection(), "file-a", (Action)(() => { }), CancellationToken.None);
            else await Invoke<XaiPublicUrlRevocation>(adapter, method, Connection(), "file-a", (Action)(() => { }), CancellationToken.None);
        });
        Assert.Equal("Grok Files returned invalid protocol data.", failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Theory]
    [InlineData("urn:opaque-provider-capability")]
    [InlineData("https://files.invalid/a%2Fb?token=Sensitive%2BValue&x=1")]
    public async Task ObservedAbsoluteUriKeepsExactStringWithoutExtraSchemePolicy(string url)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Response("{\"public_url\":" + JsonSerializer.Serialize(url) + "}"))));
        await using var adapter = Create(http);
        XaiPublicUrl result = await Invoke<XaiPublicUrl>(adapter, "CreatePublicUrlAsync", Connection(), "file-a", null, (Action)(() => { }), CancellationToken.None);
        Assert.Equal(url, result.PublicUrl);
    }

    [Theory]
    [InlineData(3599)]
    [InlineData(2592001)]
    [InlineData(3600.5)]
    public async Task PublicUrlPublishedExpiryBoundsRefuseBeforeDispatch(double seconds)
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return Task.FromResult(Response("{}")); }));
        await using var adapter = Create(http);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Invoke<XaiPublicUrl>(adapter, "CreatePublicUrlAsync", Connection(), "file-a",
            new XaiPublicUrlOptions(TimeSpan.FromSeconds(seconds)), (Action)(() => { }), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    private static IAsyncDisposable Create(HttpClient http)
    {
        Type type = typeof(XaiResponsesLlmClient).Assembly.GetType("Darkspyre.Inference.Connect.LLM.Providers.XaiFileClient", throwOnError: true)!;
        return (IAsyncDisposable)Activator.CreateInstance(type, http)!;
    }

    private static async Task<T> Invoke<T>(object adapter, string name, params object?[] args)
    {
        try
        {
            return await (Task<T>)adapter.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(adapter, args)!;
        }
        catch (TargetInvocationException error) when (error.InnerException is { } inner)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }

    private static LlmConnection Connection() => new()
    {
        Transport = LlmTransport.XaiResponses,
        Endpoint = "https://api.x.ai/v1",
        Version = "v1",
        Credential = new LlmCredential { OAuth = new() { AccessToken = "oauth-token" } }.Serialize()
    };

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }

    private sealed class BorrowedSource : MemoryStream
    {
        public BorrowedSource(string value) : base(Math.Max(1024, Encoding.UTF8.GetByteCount(value)))
        {
            Write(Encoding.UTF8.GetBytes(value));
            Position = 0;
        }
        public int Disposals { get; private set; }
        public int Flushes { get; private set; }
        public int Seeks { get; private set; }
        public int Reads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReadAsync(buffer, cancellationToken);
        }
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override void Flush() { Flushes++; }
        public override Task FlushAsync(CancellationToken cancellationToken) { Flushes++; return Task.CompletedTask; }
        public override long Seek(long offset, SeekOrigin loc) { Seeks++; throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) { Disposals++; base.Dispose(disposing); }
    }
}
