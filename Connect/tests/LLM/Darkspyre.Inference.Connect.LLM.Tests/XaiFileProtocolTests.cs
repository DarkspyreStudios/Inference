using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
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
        Transport = LlmTransport.XaiResponses, Endpoint = "https://api.x.ai/v1", Version = "v1",
        Credential = new LlmCredential { OAuth = new() { AccessToken = "oauth-token" } }.Serialize()
    };

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }

    private sealed class BorrowedSource : MemoryStream
    {
        public BorrowedSource(string value) : base(1024)
        {
            Write(Encoding.UTF8.GetBytes(value));
            Position = 0;
        }
        public int Disposals { get; private set; }
        public int Flushes { get; private set; }
        public int Seeks { get; private set; }
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override void Flush() { Flushes++; }
        public override Task FlushAsync(CancellationToken cancellationToken) { Flushes++; return Task.CompletedTask; }
        public override long Seek(long offset, SeekOrigin loc) { Seeks++; throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) { Disposals++; base.Dispose(disposing); }
    }
}
