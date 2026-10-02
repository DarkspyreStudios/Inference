using System.Net;
using System.Text;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiFileFacadeTests
{
    private const string File = "{\"id\":\"file-a\",\"filename\":\"actual.txt\",\"bytes\":3,\"created_at\":0,\"object\":\"file\"}";

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
        await using var service = new LlmProviderService(new Source(), []);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.GetAsync("selected", "file-a"));
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
        public LlmConnection Connection { get; init; } = XaiFileFacadeTests.Connection();
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public string? ExpectedVersion { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult<LlmConnection?>(Connection); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        { Writes++; ExpectedVersion = expectedVersion; return Task.CompletedTask; }
    }

    private sealed class Authenticator : ILlmAuthenticator
    {
        public LlmTransport Transport => LlmTransport.XaiResponses;
        public int Calls { get; private set; }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(credential with { OAuth = new() { AccessToken = "refreshed-token" } }); }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }
}
