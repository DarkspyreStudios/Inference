using System.Net;
using System.Text;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiFileFacadeTests
{
    private const string File = "{\"id\":\"file-a\",\"filename\":\"actual.txt\",\"bytes\":3,\"created_at\":0,\"object\":\"file\"}";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FacadeCapabilitiesDoNotClaimAnUnimplementedCustomFilesRoute(bool model)
    {
        var source = new Source();
        var auth = new Authenticator();
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("Unexpected HTTP.")));
        var custom = new CustomClient(new XaiResponsesLlmClient(http));
        await using var service = new LlmProviderService(source, [custom], [auth]);
        LlmProviderCapabilities capabilities = model
            ? await service.GetModelCapabilitiesAsync("selected", "grok-4")
            : await service.GetCapabilitiesAsync("selected");
        Assert.Equal(new LlmFileCapabilities(), capabilities.Files);
        Assert.True(custom.GetCapabilities(Connection()).Files.Upload);
        int reads = source.Reads;
        int refreshes = auth.Calls;
        await Assert.ThrowsAsync<NotSupportedException>(() => service.GetAsync("selected", "file-a"));
        Assert.Equal(reads, source.Reads);
        Assert.Equal(refreshes, auth.Calls);
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("list")]
    [InlineData("get")]
    [InlineData("download")]
    [InlineData("delete")]
    [InlineData("create")]
    [InlineData("revoke")]
    public async Task ActualBuiltinRouteUsesOneSnapshotAndExistingRefreshOwner(string operation)
    {
        var source = new Source();
        var auth = new Authenticator();
        int calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++;
            Assert.Equal("Bearer refreshed-token", request.Headers.Authorization!.ToString());
            if (operation == "upload") Assert.Contains("abc", await request.Content!.ReadAsStringAsync());
            string response = operation switch
            {
                "list" => "{\"data\":[" + File + "]}",
                "delete" => "{\"id\":\"file-a\",\"deleted\":true,\"object\":\"file\"}",
                "create" => "{\"public_url\":\"urn:opaque-capability\"}",
                "revoke" => "{\"id\":\"file-a\",\"revoked\":true}",
                "download" => "abc",
                _ => File
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(response) };
        }));
        await using var service = new LlmProviderService(source, [new XaiResponsesLlmClient(http)], [auth]);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("abc"));
        switch (operation)
        {
            case "upload": Assert.Equal(3, (await service.UploadAsync("selected", stream, "actual.txt")).Bytes); break;
            case "list": Assert.Single((await service.ListAsync("selected", new() { Limit = 2 })).Items); break;
            case "get": Assert.Equal("file-a", (await service.GetAsync("selected", "file-a")).Id); break;
            case "download": Assert.Equal(3, (await service.DownloadAsync("selected", "file-a", stream)).BytesWritten); break;
            case "delete": Assert.True((await service.DeleteAsync("selected", "file-a")).Deleted); break;
            case "create": Assert.Equal("urn:opaque-capability", (await service.CreatePublicUrlAsync("selected", "file-a")).PublicUrl); break;
            case "revoke": Assert.True((await service.RevokePublicUrlAsync("selected", "file-a")).Revoked); break;
        }
        Assert.True(stream.CanRead);
        Assert.Equal(1, calls);
        Assert.Equal(1, source.Reads);
        Assert.Equal(1, source.Writes);
        Assert.Equal("v1", source.ExpectedVersion);
        Assert.Equal(1, auth.Calls);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("header")]
    [InlineData("filename")]
    [InlineData("format")]
    public async Task InvalidArgumentsAndHeadersRefuseBeforeRefreshOrHttp(string invalid)
    {
        var source = new Source
        {
            Connection = Connection() with
            {
                AdditionalHeaders = invalid == "header" ? new Dictionary<string, string> { ["Invalid\nHeader"] = "value" } : new Dictionary<string, string>()
            }
        };
        var auth = new Authenticator();
        int calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
        await using var service = new LlmProviderService(source, [new XaiResponsesLlmClient(http)], [auth]);
        using var stream = new MemoryStream();
        Func<Task> call = () => invalid switch
        {
            "id" => service.GetAsync("selected", ".."),
            "filename" => service.UploadAsync("selected", stream, "bad\nname"),
            "format" => service.DownloadAsync("selected", "file-a", stream, new((LlmFileContentFormat)99)),
            _ => service.GetAsync("selected", "file-a")
        };
        if (invalid == "header") await Assert.ThrowsAsync<InvalidOperationException>(call);
        else await Assert.ThrowsAnyAsync<ArgumentException>(call);
        Assert.Equal(0, auth.Calls);
        Assert.Equal(0, source.Writes);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task BuiltinCapabilitiesExposeOnlyImplementedNativeFiles()
    {
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("Unexpected HTTP.")));
        var client = new XaiResponsesLlmClient(http);
        LlmProviderCapabilities capabilities = client.GetCapabilities(Connection());
        Assert.Equal(new LlmFileCapabilities { Upload = true, List = true, Get = true, Download = true, Delete = true, TextDownload = true, PublicUrls = true }, capabilities.Files);
        Assert.False(capabilities.SupportsFileReferences);
        var source = new Source();
        await using var service = new LlmProviderService(source, []);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.GetAsync("selected", "file-a"));
        using var untouched = new CallbackStream(() => throw new InvalidOperationException("Unexpected stream inspection."));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.UploadAsync("selected", untouched, "file.txt"));
        Assert.Equal(0, source.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BorrowedStreamPreflightCallbackCannotPassSynchronousRetirementFence(bool download)
    {
        var source = new Source();
        var auth = new Authenticator();
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("Unexpected HTTP.")));
        var service = new LlmProviderService(source, [new XaiResponsesLlmClient(http)], [auth]);
        Task? retirement = null;
        using var stream = new CallbackStream(() => { retirement = service.DisposeAsync().AsTask(); return true; });
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download
                ? service.DownloadAsync("selected", "file-a", stream)
                : service.UploadAsync("selected", stream, "file.txt"));
            await retirement!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, auth.Calls);
            Assert.Equal(0, source.Writes);
        }
        finally { await service.DisposeAsync(); }
    }

    [Theory]
    [InlineData("source")]
    [InlineData("refresh")]
    [InlineData("http")]
    [InlineData("save")]
    public async Task RetirementDrainsIgnoringCallbackAndNeverPublishesLateFile(string boundary)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source
        {
            Read = boundary == "source" ? async _ => { entered.SetResult(); await release.Task; return Connection(); } : null,
            Save = boundary == "save" ? async () => { entered.SetResult(); await release.Task; } : null
        };
        var auth = new Authenticator
        {
            Refresh = boundary == "refresh" ? async credential => { entered.SetResult(); await release.Task; return credential; } : null
        };
        int calls = 0;
        using var http = new HttpClient(new Handler(async _ =>
        {
            calls++;
            if (boundary == "http") { entered.SetResult(); await release.Task; }
            return new(HttpStatusCode.OK) { Content = new StringContent(File) };
        }));
        var service = new LlmProviderService(source, [new XaiResponsesLlmClient(http)], [auth]);
        Task<LlmProviderFile> request = service.GetAsync("selected", "file-a");
        Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = service.DisposeAsync().AsTask();
            Task repeated = service.DisposeAsync().AsTask();
            Assert.Same(retirement, repeated);
            Assert.False(retirement.IsCompleted);
            release.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(boundary == "http" ? 1 : 0, calls);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.GetAsync("selected", "file-a"));
        }
        finally
        {
            retirement ??= service.DisposeAsync().AsTask();
            release.TrySetResult();
            try { await request.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task SnapshotHeadersSurviveCallerMutationDuringRefresh()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var headers = new Dictionary<string, string> { ["X-Admitted"] = "original" };
        var source = new Source { Connection = Connection() with { AdditionalHeaders = headers } };
        var auth = new Authenticator { Refresh = async credential => { entered.SetResult(); await release.Task; return credential; } };
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("original", request.Headers.GetValues("X-Admitted").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File) });
        }));
        await using var service = new LlmProviderService(source, [new XaiResponsesLlmClient(http)], [auth]);
        Task<LlmProviderFile> request = service.GetAsync("selected", "file-a");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            headers["X-Admitted"] = "changed";
            headers["Authorization"] = "forbidden-late-change";
            release.SetResult();
            Assert.Equal("file-a", (await request).Id);
            Assert.Equal(1, source.Reads);
            Assert.Equal(0, source.Writes);
        }
        finally
        {
            release.TrySetResult();
            await request.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task FacadeRetirementDrainsActualIgnoringBorrowedDownloadWriteWithoutPublishingReceipt()
    {
        using var destination = new GatedDestination();
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("abc")) })));
        var service = new LlmProviderService(new Source(), [new XaiResponsesLlmClient(http)]);
        Task<ResourceDownloadReceipt> download = service.DownloadAsync("selected", "file-a", destination);
        Task? retirement = null;
        try
        {
            await destination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = service.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            destination.Release.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("abc", Encoding.UTF8.GetString(destination.ToArray()));
            Assert.Equal(0, destination.Disposals);
        }
        finally
        {
            retirement ??= service.DisposeAsync().AsTask();
            destination.Release.TrySetResult();
            try { await download.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CancellationCallbackFaultDoesNotReleaseBorrowedGraphBeforeSourceDrain()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("host-owned cancellation failure");
        var source = new Source
        {
            Read = async token =>
            {
                using var registration = token.Register(() => { callbackEntered.TrySetResult(); throw failure; });
                entered.SetResult();
                await release.Task;
                return Connection();
            }
        };
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("Late HTTP.")));
        var service = new LlmProviderService(source, [new XaiResponsesLlmClient(http)]);
        Task<LlmProviderFile> request = service.GetAsync("selected", "file-a");
        Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = service.DisposeAsync().AsTask();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(retirement.IsCompleted);
            release.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Exception first = await Assert.ThrowsAnyAsync<Exception>(() => retirement);
            Exception repeated = await Assert.ThrowsAnyAsync<Exception>(() => service.DisposeAsync().AsTask());
            Assert.Same(first, repeated);
            Assert.Contains(failure, ((AggregateException)first).Flatten().InnerExceptions);
        }
        finally
        {
            retirement ??= service.DisposeAsync().AsTask();
            release.TrySetResult();
            try { await request.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            try { await retirement.WaitAsync(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        }
    }

    private static LlmConnection Connection() => new()
    {
        Transport = LlmTransport.XaiResponses,
        Endpoint = "https://api.x.ai/v1",
        Version = "v1",
        Credential = new LlmCredential { OAuth = new() { AccessToken = "original-token" } }.Serialize()
    };

    private sealed class Source : ILlmConnectionSource
    {
        public Func<CancellationToken, Task<LlmConnection?>>? Read { get; init; }
        public Func<Task>? Save { get; init; }
        public LlmConnection Connection { get; init; } = XaiFileFacadeTests.Connection();
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public string? ExpectedVersion { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { Reads++; return Read?.Invoke(cancellationToken) ?? Task.FromResult<LlmConnection?>(Connection); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        { Writes++; ExpectedVersion = expectedVersion; return Save?.Invoke() ?? Task.CompletedTask; }
    }

    private sealed class Authenticator : ILlmAuthenticator
    {
        public Func<LlmCredential, Task<LlmCredential>>? Refresh { get; init; }
        public LlmTransport Transport => LlmTransport.XaiResponses;
        public int Calls { get; private set; }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { Calls++; return Refresh?.Invoke(credential) ?? Task.FromResult(credential with { OAuth = new() { AccessToken = "refreshed-token" } }); }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }

    private sealed class CallbackStream(Func<bool> capability) : MemoryStream
    {
        public override bool CanRead => capability();
        public override bool CanWrite => capability();
    }

    private sealed class GatedDestination : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposals { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task;
            Write(buffer.Span);
        }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }

    private sealed class CustomClient(XaiResponsesLlmClient metadata) : ILlmClient
    {
        public LlmTransport Transport => LlmTransport.XaiResponses;
        public LlmProviderDefinition ProviderDefinition => metadata.ProviderDefinition;
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => metadata.GetCapabilities(connection, modelId);
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected catalog.");
    }
}
