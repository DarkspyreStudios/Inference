using System.Net;
using System.Text;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GeminiCacheFilesTests
{
    private static readonly string[] ExpectedPaths = ["/upload/v1beta/files", "/v1beta/files/staged"];
    private static LlmConnection Connection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.GoogleGeminiApi,
        Endpoint = GeminiApiLlmClient.DefaultEndpoint,
        Credential = new LlmCredential { ApiKey = "secret" }.Serialize()
    };
    private static InferenceResourceDescriptor Descriptor(long? bytes = 3) => new(new("workspace", "resource"), "session", "source.txt", "text/plain", bytes);

    [Fact]
    public async Task ProcessingWaitUsesSameFixedFileAndConnectionWithoutReupload()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        using var content = new MemoryStream("abc"u8.ToArray());
        int waits = 0;
        GeminiCacheFiles.File file = await GeminiCacheFiles.UploadAsync(http, Connection(), Descriptor(), content,
            static token => token.ThrowIfCancellationRequested(), CancellationToken.None, (delay, token) =>
            { Assert.Equal(TimeSpan.FromSeconds(5), delay); waits++; return Task.CompletedTask; });
        Assert.Equal("ACTIVE", file.State); Assert.Equal(1, waits);
        Assert.Equal(ExpectedPaths, handler.Paths);
        Assert.All(handler.Keys, key => Assert.Equal("secret", key));
        Assert.True(content.CanRead);
    }

    [Fact]
    public async Task UnknownLengthStreamsWithoutFabricatingADeclaredContentLength()
    {
        using var handler = new Handler { InitialState = "ACTIVE" }; using var http = new HttpClient(handler);
        using var content = new MemoryStream("abc"u8.ToArray());
        var file = await GeminiCacheFiles.UploadAsync(http, Connection(), Descriptor(null), content, static _ => { }, CancellationToken.None);
        Assert.Null(handler.DeclaredUploadLength); Assert.Equal(3, file.Bytes); Assert.True(content.CanRead);
    }

    [Fact]
    public async Task IgnoringReadinessDelayDrainsAndRetirementPreventsNextGet()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        using var content = new MemoryStream("abc"u8.ToArray());
        var entered = Signal(); var release = Signal(); var owner = new LlmOperationLifetime();
        var lease = owner.Enter(CancellationToken.None);
        Task<GeminiCacheFiles.File>? upload = null; Task? retirement = null;
        try
        {
            upload = GeminiCacheFiles.UploadAsync(http, Connection(), Descriptor(), content,
                token => Fence(owner, token), lease.Token, async (_, _) => { entered.TrySetResult(); await release.Task; });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = owner.RetireAsync();
            Assert.False(retirement.IsCompleted); Assert.False(upload.IsCompleted);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
            Assert.Single(handler.Paths);
        }
        finally
        {
            retirement ??= owner.RetireAsync(); release.TrySetResult();
            try { if (upload is not null) await Observe(upload); }
            finally { lease.Dispose(); await retirement; }
        }
    }

    [Theory]
    [InlineData("FAILED")]
    [InlineData("STATE_UNSPECIFIED")]
    [InlineData("unknown")]
    public async Task NonReadyStateRefusesWithoutFabricatingActiveOrRetry(string state)
    {
        using var handler = new Handler { InitialState = state }; using var http = new HttpClient(handler);
        using var content = new MemoryStream("abc"u8.ToArray());
        await Assert.ThrowsAsync<LlmProviderException>(() => GeminiCacheFiles.UploadAsync(http, Connection(), Descriptor(), content,
            static _ => { }, CancellationToken.None, static (_, _) => Task.CompletedTask));
        Assert.Single(handler.Paths);
    }

    [Theory]
    [InlineData("uri")]
    [InlineData("name")]
    [InlineData("mime")]
    [InlineData("size")]
    public async Task ContradictoryReadinessMetadataRefusesAtSafeBoundary(string field)
    {
        using var handler = new Handler { ChangedField = field }; using var http = new HttpClient(handler);
        using var content = new MemoryStream("abc"u8.ToArray());
        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() => GeminiCacheFiles.UploadAsync(http, Connection(), Descriptor(), content,
            static _ => { }, CancellationToken.None, static (_, _) => Task.CompletedTask));
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("private-untrusted", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, handler.Paths.Count);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Fence(LlmOperationLifetime owner, CancellationToken token)
    { token.ThrowIfCancellationRequested(); try { owner.ThrowIfStopping(); } catch (ObjectDisposedException) { throw new OperationCanceledException(token); } }
    private static async Task Observe(Task operation) { try { await operation; } catch (OperationCanceledException) { } }

    private sealed class Handler : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];
        internal List<string> Keys { get; } = [];
        internal string InitialState { get; init; } = "PROCESSING";
        internal string? ChangedField { get; init; }
        internal long? DeclaredUploadLength { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Keys.Add(request.Headers.GetValues("x-goog-api-key").Single());
            if (request.Content is not null) { DeclaredUploadLength = request.Content.Headers.ContentLength; _ = await request.Content.ReadAsByteArrayAsync(token); }
            bool upload = request.Method == HttpMethod.Post;
            string name = !upload && ChangedField == "name" ? "other" : "staged";
            string uri = !upload && ChangedField == "uri" ? "https://private-untrusted.invalid/files/staged" : GeminiApiLlmClient.DefaultEndpoint + "/v1beta/files/" + name;
            string mime = !upload && ChangedField == "mime" ? "private-untrusted" : "text/plain";
            string size = !upload && ChangedField == "size" ? "4" : "3";
            string file = "{\"name\":\"files/" + name + "\",\"uri\":\"" + uri + "\",\"mimeType\":\"" + mime
                + "\",\"state\":\"" + (upload ? InitialState : "ACTIVE") + "\",\"sizeBytes\":\"" + size + "\"}";
            return new(HttpStatusCode.OK) { Content = new StringContent(upload ? "{\"file\":" + file + "}" : file, Encoding.UTF8, "application/json") };
        }
    }
}
