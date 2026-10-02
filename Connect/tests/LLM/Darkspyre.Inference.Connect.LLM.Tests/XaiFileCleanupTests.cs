using System.Net;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiFileCleanupTests
{
    private const string FileJson = "{\"id\":\"file-a\",\"filename\":\"a\",\"bytes\":3,\"created_at\":0,\"object\":\"file\"}";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OperationAndResponseCleanupFailuresRemainIndependent(int route)
    {
        var primary = new OperationCanceledException("controlled owner fence");
        var cleanup = new IOException("controlled response cleanup");
        bool returned = false;
        var content = new Content(route == 2 ? "abc" : FileJson, cleanup);
        using var handler = new Handler((_, _) =>
        {
            returned = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler);
        await using var adapter = new XaiFileClient(http);
        using var stream = new BorrowedStream();

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => Invoke(adapter, route, stream, () =>
        {
            if (returned) throw primary;
        }));

        Assert.Collection(failure.InnerExceptions, error => Assert.Same(primary, error), error => Assert.Same(cleanup, error));
        Assert.Equal(1, content.Disposals);
        Assert.Equal(0, stream.Disposals);
        Assert.Equal(0, handler.Disposals);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SuccessfulOperationDoesNotHideResponseCleanupFailure(int route)
    {
        var cleanup = new IOException("controlled response cleanup");
        var content = new Content(route == 2 ? "abc" : FileJson, cleanup);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        await using var adapter = new XaiFileClient(http);
        using var stream = new BorrowedStream();

        Assert.Same(cleanup, await Assert.ThrowsAsync<IOException>(() => Invoke(adapter, route, stream, () => { })));
        Assert.Equal(1, content.Disposals);
        Assert.Equal(0, stream.Disposals);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OperationFailureKeepsItsIdentityWhenCleanupSucceeds(int route)
    {
        var primary = new OperationCanceledException("controlled owner fence");
        bool returned = false;
        var content = new Content(route == 2 ? "abc" : FileJson, null);
        using var http = new HttpClient(new Handler((_, _) =>
        {
            returned = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        await using var adapter = new XaiFileClient(http);
        using var stream = new BorrowedStream();

        Assert.Same(primary, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(adapter, route, stream, () =>
        {
            if (returned) throw primary;
        })));
        Assert.Equal(1, content.Disposals);
        Assert.Equal(0, stream.Disposals);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task BothOwnedCleanupFailuresRemainVisibleWithOperationFailure(int route)
    {
        var primary = new OperationCanceledException("controlled owner fence");
        var responseCleanup = new IOException("controlled response cleanup");
        var requestCleanup = new IOException("controlled request cleanup");
        var responseContent = new Content(FileJson, responseCleanup);
        var requestContent = new Content("request", requestCleanup);
        bool returned = false;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            request.Content?.Dispose();
            request.Content = requestContent;
            returned = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = responseContent });
        }));
        await using var adapter = new XaiFileClient(http);
        using var stream = new BorrowedStream();

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => Invoke(adapter, route, stream, () =>
        {
            if (returned) throw primary;
        }));
        Assert.Collection(failure.Flatten().InnerExceptions,
            error => Assert.Same(primary, error), error => Assert.Same(responseCleanup, error), error => Assert.Same(requestCleanup, error));
        Assert.Equal(1, responseContent.Disposals);
        Assert.Equal(1, requestContent.Disposals);
        Assert.Equal(0, stream.Disposals);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task InvalidProtocolAndCleanupFailuresAreBothReported(int route)
    {
        var cleanup = new IOException("controlled response cleanup");
        var content = new Content("{", cleanup);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        await using var adapter = new XaiFileClient(http);
        using var stream = new BorrowedStream();

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => Invoke(adapter, route, stream, () => { }));
        Assert.Collection(failure.InnerExceptions,
            error => Assert.IsType<LlmProviderException>(error), error => Assert.Same(cleanup, error));
        Assert.Equal(1, content.Disposals);
        Assert.Equal(0, stream.Disposals);
    }

    [Fact]
    public async Task PartialDestinationFailureIsNotHiddenByResponseCleanup()
    {
        var primary = new IOException("controlled partial destination failure");
        var cleanup = new IOException("controlled response cleanup");
        var content = new Content("abc", cleanup);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        await using var adapter = new XaiFileClient(http);
        using var stream = new FailingDestination(primary);

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => Invoke(adapter, 2, stream, () => { }));
        Assert.Collection(failure.InnerExceptions, error => Assert.Same(primary, error), error => Assert.Same(cleanup, error));
        Assert.Equal("abc"u8.ToArray(), stream.ToArray());
        Assert.Equal(1, content.Disposals);
        Assert.Equal(0, stream.Disposals);
    }

    private static Task Invoke(XaiFileClient adapter, int route, Stream stream, Action fence) => route switch
    {
        0 => adapter.UploadAsync(Connection(), stream, "a", null, fence, CancellationToken.None),
        1 => adapter.GetAsync(Connection(), "file-a", fence, CancellationToken.None),
        2 => adapter.DownloadAsync(Connection(), "file-a", stream, null, fence, CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(route))
    };

    private static LlmConnection Connection() => new()
    {
        Transport = LlmTransport.XaiResponses,
        Endpoint = "https://api.x.ai/v1",
        Version = "v1",
        Credential = new LlmCredential { OAuth = new() { AccessToken = "oauth-token" } }.Serialize()
    };

    private sealed class Content(string body, Exception? failure) : StringContent(body)
    {
        internal int Disposals { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposals++;
                base.Dispose(disposing);
                if (failure is not null) throw failure;
            }
            else base.Dispose(disposing);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Disposals { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }

    private class BorrowedStream : MemoryStream
    {
        internal int Disposals { get; private set; }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }

    private sealed class FailingDestination(Exception failure) : BorrowedStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            throw failure;
        }
    }
}
