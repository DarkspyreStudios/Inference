using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class GoogleCacheRequestOwnershipTests
{
    [Theory]
    [InlineData(false, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(true, HttpStatusCode.ServiceUnavailable)]
    public async Task RefusedRequestCleanupRetainsActualOwnerAndSafeIndependentFailures(bool vertex, HttpStatusCode status)
    {
        using var handler = new Handler(vertex, status, true); using var http = new HttpClient(handler);
        using var source = new MemoryStream("abc"u8.ToArray());
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => Upload(vertex, http, source));
        Exception cleanup = FindCleanup(error);
        var resource = Assert.IsType<InferenceResourceOperationException>(cleanup);
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, resource.Failure.Phase);
        Assert.Equal(1, handler.Content!.Calls); Assert.True(source.CanRead);
        Assert.DoesNotContain("private-request-secret", error.ToString(), StringComparison.Ordinal);
        if (status != HttpStatusCode.OK)
            Assert.Contains(Errors(error), value => value is LlmProviderException { StatusCode: HttpStatusCode.ServiceUnavailable });
        GC.KeepAlive(handler.Request);
    }

    [Fact]
    public async Task GeminiMultipartChildrenHaveExactlyOneDisposalOwner()
    {
        using var handler = new Handler(false, HttpStatusCode.OK, false); using var http = new HttpClient(handler);
        using var source = new MemoryStream("abc"u8.ToArray());
        await Upload(false, http, source);
        Assert.Equal(1, handler.Probe!.Calls); Assert.Equal(1, handler.Content!.Calls); Assert.True(source.CanRead);
    }

    [Fact]
    public void SuccessfullyDisposedMediaReleasesBorrowedSourceAndFenceWhileContentIsRetained()
    {
        var (content, source, fence) = MakeDisposedContent();
        for (int attempt = 0; attempt < 20 && (source.IsAlive || fence.IsAlive); attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(source.IsAlive); Assert.False(fence.IsAlive); GC.KeepAlive(content);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (GoogleCacheMediaContent Content, WeakReference Source, WeakReference Fence) MakeDisposedContent()
    {
        var source = new MemoryStream("abc"u8.ToArray()); var fence = new Fence();
        var content = new GoogleCacheMediaContent(source, "text/plain", 3, 100, fence.Check);
        content.Dispose(); Assert.True(source.CanRead);
        return (content, new(source), new(fence));
    }

    private static async Task Upload(bool vertex, HttpClient http, Stream source)
    {
        if (vertex)
            _ = await VertexCacheFiles.UploadAsync(http, new("controlled", "source"), new("cache-bucket", "prefix/"),
                "text/plain", 3, new("workspace", "resource"), source, static _ => { }, CancellationToken.None);
        else
            _ = await GeminiCacheFiles.UploadAsync(http, new() { Version = "v1", Endpoint = GeminiApiLlmClient.DefaultEndpoint, Transport = LlmTransport.GoogleGeminiApi,
                Credential = new LlmCredential { ApiKey = "controlled" }.Serialize() },
                new(new("workspace", "resource"), "session", "source.txt", "text/plain", 3), source,
                static _ => { }, CancellationToken.None);
    }

    private static Exception FindCleanup(Exception error) => Errors(error).First(value => value is InferenceResourceOperationException
    { Failure.Phase: InferenceResourceOperationPhase.Cleanup });
    private static IEnumerable<Exception> Errors(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
        { foreach (Exception child in aggregate.InnerExceptions) foreach (Exception value in Errors(child)) yield return value; }
        else if (error.InnerException is { } inner) foreach (Exception value in Errors(inner)) yield return value;
    }

    private sealed class Fence { private int _calls; internal void Check(CancellationToken token) { _calls++; token.ThrowIfCancellationRequested(); } }
    private sealed class Handler(bool vertex, HttpStatusCode status, bool refuse) : HttpMessageHandler
    {
        internal HttpRequestMessage? Request { get; private set; }
        internal CountContent? Content { get; private set; }
        internal CountContent? Probe { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Request = request;
            if (request.Content is MultipartContent multipart)
            { Probe = new(new StringContent("probe"), false); multipart.Add(Probe); }
            byte[] bytes = await request.Content!.ReadAsByteArrayAsync(token);
            Content = new(request.Content, refuse); request.Content = Content;
            string body;
            if (vertex)
            {
                string name = Uri.UnescapeDataString(request.RequestUri!.Query.Split('&').Single(part => part.StartsWith("name=", StringComparison.Ordinal))[5..]);
                body = JsonSerializer.Serialize(new Dictionary<string, string>
                { ["bucket"] = "cache-bucket", ["name"] = name, ["generation"] = "42", ["size"] = bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ["contentType"] = "text/plain" });
            }
            else body = "{\"file\":{\"name\":\"files/staged\",\"uri\":\"https://generativelanguage.googleapis.com/v1beta/files/staged\",\"state\":\"ACTIVE\",\"mimeType\":\"text/plain\",\"sizeBytes\":\"3\"}}";
            return new(status) { Content = new StringContent(body) };
        }
    }
    private sealed class CountContent(HttpContent inner, bool refuse) : HttpContent
    {
        internal int Calls { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => inner.CopyToAsync(stream);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Calls++; if (refuse) throw new InvalidOperationException("private-request-secret"); inner.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
