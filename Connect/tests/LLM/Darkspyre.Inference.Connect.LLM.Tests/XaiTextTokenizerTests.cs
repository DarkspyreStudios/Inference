using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiTextTokenizerTests
{
    [Theory]
    [InlineData(XaiRegion.Global)]
    [InlineData(XaiRegion.UnitedStates)]
    public async Task TokenizerPreservesExactInputAndProviderTokens(XaiRegion region)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        ILlmTextTokenizerClient tokenizer = Assert.IsAssignableFrom<ILlmTextTokenizerClient>(client);
        string text = " \t<text>\u00e9\ud83d\ude42\n";
        LlmConnection connection = Connection(region);
        LlmTokenization result = await tokenizer.TokenizeAsync(connection, "manual-alias", text);
        Assert.Equal(connection.Endpoint + "/tokenize-text", handler.Uri);
        Assert.Equal("Bearer controlled", handler.Authorization);
        using JsonDocument request = JsonDocument.Parse(handler.Body!);
        Assert.Equal("manual-alias", request.RootElement.GetProperty("model").GetString());
        Assert.Equal(text, request.RootElement.GetProperty("text").GetString());
        Assert.Equal(2, request.RootElement.EnumerateObject().Count());
        Assert.Equal(2, result.Tokens.Count);
        Assert.Equal(uint.MaxValue, result.Tokens[0].Id);
        Assert.Equal("display", result.Tokens[0].Text);
        Assert.Equal(new byte[] { 255, 0, 195 }, result.Tokens[0].Bytes.ToArray());
        Assert.Equal(uint.MaxValue, result.Tokens[1].Id);
        Assert.Empty(result.Tokens[1].Bytes.ToArray());
        Assert.False((object)client is ILlmProviderTokenCounter);
    }

    [Fact]
    public async Task FacadeDispatchesThroughTheSelectedTokenizerWithoutDiscovery()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var source = new Source();
        await using var service = new LlmProviderService(source, [new XaiResponsesLlmClient(http)]);
        ILlmTextTokenizer tokenizer = Assert.IsAssignableFrom<ILlmTextTokenizer>(service);
        LlmTokenization result = await tokenizer.TokenizeAsync("connection", "manual-alias", "");
        Assert.Equal(2, result.Tokens.Count);
        Assert.Equal(1, source.Reads);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://api.x.ai/v1/tokenize-text", handler.Uri);
    }

    private static LlmConnection Connection(XaiRegion region = XaiRegion.Global) => new()
    {
        Version = "v1", Transport = LlmTransport.XaiResponses,
        Xai = new() { Region = region }, Endpoint = new XaiConnectionOptions { Region = region }.GetEndpoint(),
        Credential = new LlmCredential { OAuth = new() { AccessToken = "controlled", AccountId = "account" } }.Serialize()
    };

    private sealed class Source : ILlmConnectionSource
    {
        internal int Reads { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult<LlmConnection?>(Connection()); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No save expected.");
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal string? Uri { get; private set; }
        internal string? Body { get; private set; }
        internal string? Authorization { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Uri = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization!.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"token_ids":[{"token_id":4294967295,"string_token":"display","token_bytes":[255,0,195]},{"token_id":4294967295,"string_token":"","token_bytes":[]}]}""", Encoding.UTF8, "application/json")
            };
        }
    }
}
