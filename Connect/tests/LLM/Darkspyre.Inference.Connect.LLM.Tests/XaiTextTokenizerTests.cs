using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiTextTokenizerTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("\u00e9\ud83d\ude42\u0000<literal>")]
    public async Task EmptySequencesAndOpaqueTextArePreserved(string text)
    {
        using var handler = new Handler { ResponseBody = "{\"token_ids\":[]}" };
        using var http = new HttpClient(handler);
        LlmTokenization result = await new XaiResponsesLlmClient(http).TokenizeAsync(Connection(), " manual-alias ", text);
        Assert.True(result.Tokens.Count == 0);
        using JsonDocument sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal(text, sent.RootElement.GetProperty("text").GetString());
        Assert.Equal(" manual-alias ", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("application/json", handler.Accept);
        Assert.Equal("application/json; charset=utf-8", handler.ContentType);
    }

    [Theory]
    [InlineData("not-json-secret")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"token_ids\":null}")]
    [InlineData("{\"token_ids\":{}}")]
    [InlineData("{\"token_ids\":[null]}")]
    [InlineData("{\"token_ids\":[{}]}")]
    [InlineData("{\"token_ids\":[{\"token_id\":-1,\"string_token\":\"secret\",\"token_bytes\":[]}]}")]
    [InlineData("{\"token_ids\":[{\"token_id\":4294967296,\"string_token\":\"secret\",\"token_bytes\":[]}]}")]
    [InlineData("{\"token_ids\":[{\"token_id\":1.5,\"string_token\":\"secret\",\"token_bytes\":[]}]}")]
    [InlineData("{\"token_ids\":[{\"token_id\":\"1\",\"string_token\":\"secret\",\"token_bytes\":[]}]}")]
    [InlineData("{\"token_ids\":[{\"token_id\":1,\"token_bytes\":[]}]}")]
    [InlineData("{\"token_ids\":[{\"token_id\":1,\"string_token\":null,\"token_bytes\":[]}]}")]
    [InlineData("{\"token_ids\":[{\"token_id\":1,\"string_token\":3,\"token_bytes\":[]}]}")]
    [InlineData("{\"token_ids\":[{\"token_id\":1,\"string_token\":\"secret\"}]}")]
    [InlineData("{\"token_ids\":[{\"token_id\":1,\"string_token\":\"secret\",\"token_bytes\":\"/w==\"}]}")]
    [InlineData("{\"token_ids\":[{\"token_id\":1,\"string_token\":\"secret\",\"token_bytes\":[-1]}]}")]
    [InlineData("{\"token_ids\":[{\"token_id\":1,\"string_token\":\"secret\",\"token_bytes\":[256]}]}")]
    [InlineData("{\"token_ids\":[{\"token_id\":1,\"string_token\":\"secret\",\"token_bytes\":[1.5]}]}")]
    [InlineData("{\"token_ids\":[{\"token_id\":1,\"string_token\":\"secret\",\"token_bytes\":[\"1\"]}]}")]
    [InlineData("{\"token_ids\":[{\"token_id\":1,\"string_token\":\"secret\",\"token_bytes\":[null]}]}")]
    public async Task MalformedTokensUseFixedProtocolErrorsWithoutRawBodies(string body)
    {
        using var handler = new Handler { ResponseBody = body };
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<LlmProviderException>(() =>
            new XaiResponsesLlmClient(http).TokenizeAsync(Connection(), "model", "text"));
        Assert.Equal("Grok tokenization returned an invalid token sequence.", error.Message);
        Assert.Equal("request-identity", error.RequestId);
        Assert.Equal(string.Empty, error.SanitizedResponseBody);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("api")]
    [InlineData("mixed")]
    [InlineData("secret-header")]
    [InlineData("aws")]
    [InlineData("google")]
    [InlineData("whitespace")]
    public async Task OAuthOnlyAuthenticationPreflightRunsBeforeNetwork(string kind)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        LlmCredential oauth = LlmCredential.Parse(Connection().Credential)!;
        LlmConnection connection = Connection() with
        {
            Credential = kind switch
            {
                "none" => null,
                "api" => new LlmCredential { ApiKey = "secret-key" }.Serialize(),
                "mixed" => (oauth with { ApiKey = "secret-key" }).Serialize(),
                "secret-header" => (oauth with { SecretHeaders = new Dictionary<string, string> { ["x-api-key"] = "secret-key" } }).Serialize(),
                "aws" => (oauth with { Aws = new() { AccessKeyId = "secret-key", SecretAccessKey = "secret-key" } }).Serialize(),
                "google" => (oauth with { Google = new() { ServiceAccountJson = "{}" } }).Serialize(),
                _ => (oauth with { OAuth = oauth.OAuth! with { AccessToken = "bad token" } }).Serialize()
            }
        };
        var error = await Assert.ThrowsAsync<InferenceException>(() => new XaiResponsesLlmClient(http).TokenizeAsync(connection, "model", "text"));
        Assert.Equal(InferenceFailureCode.AuthenticationRequired, error.Code);
        Assert.DoesNotContain("secret-key", error.ToString());
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("https://example.test/v1")]
    [InlineData("http://api.x.ai/v1")]
    [InlineData("https://api.x.ai/v1?redirect=other")]
    [InlineData("https://us.api.x.ai/v1")]
    public async Task EndpointMismatchDoesNotDispatch(string endpoint)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new XaiResponsesLlmClient(http).TokenizeAsync(
            Connection() with { Endpoint = endpoint }, "model", "text"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AdditionalHeadersAreForwardedAndProviderErrorsAreSanitizedWithoutRetry()
    {
        using var handler = new Handler { Status = HttpStatusCode.BadRequest, ResponseBody = "{\"error\":{\"message\":\"controlled rejected\",\"code\":\"invalid_request\"}}" };
        using var http = new HttpClient(handler);
        LlmConnection connection = Connection() with { AdditionalHeaders = new Dictionary<string, string> { ["x-client-request-id"] = "trace" } };
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => new XaiResponsesLlmClient(http).TokenizeAsync(connection, "model", "text"));
        Assert.Equal("trace", handler.AdditionalHeader);
        Assert.Null(error.ProviderCode);
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.DoesNotContain("controlled", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, InferenceFailureCode.AuthenticationRequired)]
    [InlineData(HttpStatusCode.PaymentRequired, InferenceFailureCode.AccessDenied)]
    public async Task NamedAuthenticationAndEntitlementFailuresUseTheSharedPolicy(HttpStatusCode status, InferenceFailureCode code)
    {
        using var handler = new Handler { Status = status, ResponseBody = "controlled rejected" };
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InferenceException>(() => new XaiResponsesLlmClient(http).TokenizeAsync(Connection(), "model", "text"));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("controlled", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CallerCanceledBeforeDispatchNeverUsesHttp()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new XaiResponsesLlmClient(http).TokenizeAsync(Connection(), "model", "text", caller.Token));
        Assert.Equal(0, handler.Calls);
    }
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
        Version = "v1",
        Transport = LlmTransport.XaiResponses,
        Xai = new() { Region = region },
        Endpoint = new XaiConnectionOptions { Region = region }.GetEndpoint(),
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
        internal string ResponseBody { get; init; } = """{"token_ids":[{"token_id":4294967295,"string_token":"display","token_bytes":[255,0,195]},{"token_id":4294967295,"string_token":"","token_bytes":[]}]}""";
        internal HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        internal int Calls { get; private set; }
        internal string? Uri { get; private set; }
        internal string? Body { get; private set; }
        internal string? Authorization { get; private set; }
        internal string? Accept { get; private set; }
        internal string? ContentType { get; private set; }
        internal string? AdditionalHeader { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Uri = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization!.ToString();
            Accept = request.Headers.Accept.Single().ToString();
            ContentType = request.Content!.Headers.ContentType!.ToString();
            AdditionalHeader = request.Headers.TryGetValues("x-client-request-id", out IEnumerable<string>? values) ? values.Single() : null;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(Status)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
            };
            response.Headers.Add("x-request-id", "request-identity");
            return response;
        }
    }
}
