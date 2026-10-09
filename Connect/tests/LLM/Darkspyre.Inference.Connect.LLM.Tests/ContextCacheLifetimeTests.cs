using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ContextCacheLifetimeTests
{
    [Theory]
    [InlineData("image/heic")]
    [InlineData("image/heif")]
    public async Task GeminiPromptImageStagingPreservesHeifMimeAndBorrowedBytes(string mime)
    {
        byte[] data = Convert.FromHexString("000000186674797068656963000000006d69663168656963");
        var handler = new Boundary(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://generativelanguage.googleapis.com/upload/v1beta/files?uploadType=multipart", request.RequestUri!.AbsoluteUri);
            HttpContent[] parts = Assert.IsType<MultipartContent>(request.Content).ToArray();
            Assert.Equal(mime, parts[1].Headers.ContentType!.MediaType);
            Assert.Equal(data, await parts[1].ReadAsByteArrayAsync(token));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject
                {
                    ["file"] = new JsonObject
                    {
                        ["name"] = "files/photo", ["uri"] = "https://generativelanguage.googleapis.com/v1beta/files/photo",
                        ["mimeType"] = mime, ["state"] = "ACTIVE", ["sizeBytes"] = "24"
                    }
                }.ToJsonString())
            };
        });
        using var http = new HttpClient(handler);
        using var source = new MemoryStream(data);
        var connection = new LlmConnection
        {
            Version = "1", Transport = LlmTransport.GoogleGeminiApi,
            Endpoint = GeminiApiLlmClient.DefaultEndpoint,
            Credential = new LlmCredential { ApiKey = "test-key" }.Serialize()
        };
        var descriptor = new InferenceResourceDescriptor(new("workspace", "reference"), "session", "photo.heif", mime, data.Length);

        GeminiCacheFiles.File file = await GeminiCacheFiles.UploadAsync(http, connection, descriptor, source, _ => { }, CancellationToken.None);

        Assert.Equal(mime, file.MimeType);
        Assert.Equal(data.Length, file.Bytes);
        Assert.Equal("ACTIVE", file.State);
        Assert.Equal(1, handler.Sends);
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData("image/heic")]
    [InlineData("image/heif")]
    public void GeminiPromptImageFormatsDoNotWidenCachedFunctionResponseFormats(string mime)
    {
        var descriptor = new InferenceResourceDescriptor(new("workspace", "reference"), "session", "photo.heif", mime, 24);
        GeminiCacheFiles.ValidateDescriptor(descriptor, toolResult: false);
        Assert.Throws<NotSupportedException>(() => GeminiCacheFiles.ValidateDescriptor(descriptor, toolResult: true));
    }

    [Fact]
    public async Task FailedGeminiPrefixUploadPreservesRetryFactsWithoutRetryOrBorrowedStreamDisposal()
    {
        int cleanup = 0;
        var handler = new Boundary((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new RetiringContent("private provider body", () => cleanup++)
            };
            response.Headers.Add("x-request-id", "staging-request");
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(17));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var source = new MemoryStream([1, 2, 3]);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.GoogleGeminiApi,
            Endpoint = GeminiApiLlmClient.DefaultEndpoint,
            Credential = new LlmCredential { ApiKey = "private-test-key" }.Serialize()
        };
        var descriptor = new InferenceResourceDescriptor(new("workspace", "reference"),
            "session", "reference.txt", "text/plain", source.Length);

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() =>
            GeminiCacheFiles.UploadAsync(http, connection, descriptor, source, _ => { }, CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.Equal("staging-request", failure.RequestId);
        Assert.Equal(TimeSpan.FromSeconds(17), failure.RetryAfter);
        Assert.DoesNotContain("private provider body", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-test-key", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.Sends);
        Assert.Equal(1, cleanup);
        Assert.True(source.CanRead);
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task RetirementDrainsAnIgnoringReadAndFencesTheRetainedClientWithoutDisposingBorrowedOwners()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Boundary(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"cachedContents\":[]}") };
        });
        using var http = new HttpClient(handler);
        var source = new Source();
        await using var service = new LlmProviderService(source, [new GeminiApiLlmClient(http)]);
        ILlmContextCacheClient cache = await service.BindContextCacheAsync("connection");
        Assert.Equal(0, handler.Sends);

        Task<LlmContextCachePage> read = cache.ListAsync(new(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task retirement = service.DisposeAsync().AsTask();
        try
        {
            Assert.False(retirement.IsCompleted);
            Assert.False(read.IsCompleted);
        }
        finally { release.TrySetResult(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        int reads = source.Reads;
        var reference = new LlmExplicitCacheReference("cachedContents/imported", "gemini-2.5-flash", "imported-scope");
        Func<Task>[] retiredOperations =
        [
            () => cache.CreateAsync(new() { ModelId = reference.ModelId,
                Correlation = new("operation", "explicit-correlation"), TimeToLive = TimeSpan.FromMinutes(1) }),
            () => cache.GetAsync(reference),
            () => cache.ListAsync(new(1)),
            () => cache.UpdateExpiryAsync(reference, DateTimeOffset.UtcNow.AddMinutes(1)),
            () => cache.DeleteAsync(reference)
        ];
        foreach (Func<Task> invoke in retiredOperations)
            await Assert.ThrowsAsync<ObjectDisposedException>(invoke);
        Assert.Equal(reads, source.Reads);
        Assert.Equal(1, handler.Sends);
        Assert.False(source.Disposed);
        Assert.False(handler.Disposed);
        using HttpResponseMessage borrowedResponse = await http.GetAsync("https://borrowed.example.test/");
        Assert.Equal("{\"cachedContents\":[]}", await borrowedResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ACreateReceiptObservedBeforeRetirementSurvivesAsCancellationEvidenceWithoutRetry()
    {
        Task? retirement = null;
        LlmProviderService? service = null;
        JsonElement submitted = default;
        var handler = new Boundary(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            submitted = JsonElement.Parse(await request.Content!.ReadAsStringAsync(token));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new RetiringContent("""
                    {"name":"cachedContents/created","model":"models/gemini-2.5-flash",
                     "displayName":"explicit-correlation","expireTime":"2030-01-01T00:00:00Z"}
                    """, () => retirement = service!.DisposeAsync().AsTask())
            };
            response.Headers.Add("x-request-id", "observed-request");
            return response;
        });
        using var http = new HttpClient(handler);
        var source = new Source();
        await using var ownedService = service = new(source, [new GeminiApiLlmClient(http)]);
        ILlmContextCacheClient cache = await service.BindContextCacheAsync("connection");
        var correlation = new LlmContextCacheCreateCorrelation("operation", "explicit-correlation");
        LlmContextCacheCreateCancelledException failure = await Assert.ThrowsAsync<LlmContextCacheCreateCancelledException>(() =>
            cache.CreateAsync(new()
            {
                ModelId = "gemini-2.5-flash",
                Messages = [new("user", "Retain this prefix.")],
                Correlation = correlation,
                TimeToLive = TimeSpan.FromMinutes(1)
            }));

        LlmContextCache receipt = Assert.IsType<LlmContextCache>(failure.ObservedReceipt);
        Assert.Equal(correlation, failure.Correlation);
        Assert.Equal("observed-request", failure.RequestId);
        Assert.Equal("cachedContents/created", receipt.Reference.ResourceName);
        Assert.Equal("gemini-2.5-flash", receipt.Reference.ModelId);
        Assert.Equal(receipt.ExpiresAt, receipt.Reference.ExpiresAt);
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), receipt.ExpiresAt);
        Assert.True(failure.CancellationToken.IsCancellationRequested);
        Assert.Equal("60s", submitted.GetProperty("ttl").GetString());
        Assert.Equal("explicit-correlation", submitted.GetProperty("displayName").GetString());
        Assert.Equal("Retain this prefix.", submitted.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        await Assert.IsAssignableFrom<Task>(retirement).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.GetAsync(receipt.Reference));
        Assert.Equal(1, handler.Sends);
        Assert.False(handler.Disposed);
        Assert.False(source.Disposed);
    }

    private sealed class RetiringContent(string body, Action retire) : StringContent(body)
    {
        private bool _retired;
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_retired) { _retired = true; retire(); }
            base.Dispose(disposing);
        }
    }

    private sealed class Boundary(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Sends { get; private set; }
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Sends++; return send(request, token); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class Source : ILlmConnectionSource, IDisposable
    {
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult<LlmConnection?>(new()
            {
                Version = "1",
                Transport = LlmTransport.GoogleGeminiApi,
                Endpoint = GeminiApiLlmClient.DefaultEndpoint,
                Credential = new LlmCredential { ApiKey = "test-key" }.Serialize()
            });
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }
}
