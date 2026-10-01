using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class OAuthRequestJsonTests
{
    private const string ClaudeClient = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string CodexClient = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string ClaudeScopes = "user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";

    [Fact]
    public async Task ClaudeAuthorizationRetainsExactFieldsOrderAndWebEscaping()
    {
        using var handler = new Handler(async request =>
        {
            Assert.Equal("https://platform.claude.com/v1/oauth/token", request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("{\"grant_type\":\"authorization_code\",\"code\":\"\\u003C\\u0026\\u003E\",\"redirect_uri\":\"https://callback.invalid/?a=1\\u0026b=2\",\"client_id\":\""
                + ClaudeClient + "\",\"code_verifier\":\"verifier\",\"state\":\"state\"}", await request.Content.ReadAsStringAsync());
            return Tokens();
        });
        using var http = new HttpClient(handler);
        LlmCredential credential = await new ClaudeAuthenticator(http).CompleteAsync(new()
        {
            AuthorizeUrl = "https://authorize.invalid",
            State = "state",
            CodeVerifier = "verifier",
            RedirectUri = "https://callback.invalid/?a=1&b=2"
        }, "<&>", "state");
        Assert.Equal("access", credential.OAuth!.AccessToken);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CodexDeviceRequestsRetainExactBytesAndAuthorizationExchangeStaysFormEncoded()
    {
        using var handler = new Handler(async request =>
        {
            string body = await request.Content!.ReadAsStringAsync();
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/accounts/deviceauth/usercode":
                    Assert.Equal("{\"client_id\":\"" + CodexClient + "\"}", body);
                    return Response("{\"device_auth_id\":\"device\",\"user_code\":\"user\",\"interval\":1}");
                case "/api/accounts/deviceauth/token":
                    Assert.Equal("{\"device_auth_id\":\"\\u003C\\u0026\\u003E\",\"user_code\":\"\"}", body);
                    return Response("{\"authorization_code\":\"code\",\"code_verifier\":\"verifier\"}");
                case "/oauth/token":
                    Assert.Equal("application/x-www-form-urlencoded", request.Content.Headers.ContentType!.MediaType);
                    Assert.Contains("grant_type=authorization_code", body, StringComparison.Ordinal);
                    return Tokens();
                default: throw new InvalidOperationException("Unexpected Codex path.");
            }
        });
        using var http = new HttpClient(handler);
        var authenticator = new CodexAuthenticator(http);
        Assert.Equal("device", (await authenticator.StartAsync()).DeviceAuthId);
        Assert.Equal("access", (await authenticator.CompleteAsync("<&>", "")).OAuth!.AccessToken);
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "")]
    [InlineData(false, "<&>")]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(true, "<&>")]
    public async Task RefreshRequestRetainsNullVersusEmptyTokensAndExactWireOrder(bool codex, string? refreshToken)
    {
        using var http = new HttpClient();
        object authenticator = codex ? new CodexAuthenticator(http) : new ClaudeAuthenticator(http);
        using var request = (HttpRequestMessage)authenticator.GetType().GetMethod("BuildRefreshRequest", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(authenticator, [new LlmOAuthTokens { AccessToken = "access", RefreshToken = refreshToken }])!;
        string token = refreshToken is null ? "null" : refreshToken.Length == 0 ? "\"\"" : "\"\\u003C\\u0026\\u003E\"";
        string expected = codex
            ? "{\"client_id\":\"" + CodexClient + "\",\"grant_type\":\"refresh_token\",\"refresh_token\":" + token + "}"
            : "{\"grant_type\":\"refresh_token\",\"refresh_token\":" + token + ",\"client_id\":\"" + ClaudeClient + "\",\"scope\":\"" + ClaudeScopes + "\"}";
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(expected, await request.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualCollectibleAuthenticationGenerationRetiresAfterSettledHttpCallers(bool codex)
    {
        WeakReference generation = ExerciseGeneration(codex);
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive, "The settled OAuth request/credential callers retain their collectible Connect generation.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseGeneration(bool codex)
    {
        string path = typeof(ConnectLlmJson).Assembly.Location;
        var generation = new AuthGeneration(Path.GetDirectoryName(path)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly connect = generation.LoadFromAssemblyPath(path);
            using var handler = new Handler(async request =>
            {
                string body = await request.Content!.ReadAsStringAsync();
                string route = request.RequestUri!.AbsolutePath;
                if (route == "/api/accounts/deviceauth/usercode")
                {
                    Assert.Equal("{\"client_id\":\"" + CodexClient + "\"}", body);
                    return Response("{\"device_auth_id\":\"device\",\"user_code\":\"user\",\"interval\":1}");
                }
                if (route == "/api/accounts/deviceauth/token")
                {
                    Assert.Equal("{\"device_auth_id\":\"device\",\"user_code\":\"user\"}", body);
                    return Response("{\"authorization_code\":\"code\",\"code_verifier\":\"verifier\"}");
                }
                if (request.Content.Headers.ContentType!.MediaType == "application/x-www-form-urlencoded")
                    return Tokens();
                string refresh = codex
                    ? "{\"client_id\":\"" + CodexClient + "\",\"grant_type\":\"refresh_token\",\"refresh_token\":\"refresh\"}"
                    : "{\"grant_type\":\"refresh_token\",\"refresh_token\":\"refresh\",\"client_id\":\"" + ClaudeClient + "\",\"scope\":\"" + ClaudeScopes + "\"}";
                if (body.Contains("\"refresh_token\"", StringComparison.Ordinal))
                {
                    Assert.Equal(refresh, body);
                    return Response("{\"access_token\":\"fresh\",\"refresh_token\":\"refresh\"}");
                }
                Assert.Equal("{\"grant_type\":\"authorization_code\",\"code\":\"code\",\"redirect_uri\":\"https://callback.invalid\",\"client_id\":\""
                    + ClaudeClient + "\",\"code_verifier\":\"verifier\",\"state\":\"state\"}", body);
                return Tokens();
            });
            using var http = new HttpClient(handler);
            Type owner = connect.GetType("Darkspyre.Inference.Connect.LLM.Providers." + (codex ? "CodexAuthenticator" : "ClaudeAuthenticator"), throwOnError: true)!;
            object authenticator = Activator.CreateInstance(owner, http)!;
            object credential;
            if (codex)
            {
                Result(owner.GetMethod("StartAsync")!.Invoke(authenticator, [CancellationToken.None])!);
                credential = Result(owner.GetMethod("CompleteAsync")!.Invoke(authenticator, ["device", "user", CancellationToken.None])!);
            }
            else
            {
                Type sessionType = connect.GetType(typeof(LlmAuthorizationSession).FullName!, throwOnError: true)!;
                object session = Activator.CreateInstance(sessionType)!;
                sessionType.GetProperty("AuthorizeUrl")!.SetValue(session, "https://authorize.invalid");
                sessionType.GetProperty("State")!.SetValue(session, "state");
                sessionType.GetProperty("CodeVerifier")!.SetValue(session, "verifier");
                sessionType.GetProperty("RedirectUri")!.SetValue(session, "https://callback.invalid");
                credential = Result(owner.GetMethod("CompleteAsync")!.Invoke(authenticator, [session, "code", "state", CancellationToken.None])!);
            }
            Assert.Same(connect, credential.GetType().Assembly);
            object tokens = credential.GetType().GetProperty("OAuth")!.GetValue(credential)!;
            tokens.GetType().GetProperty("ExpiresAtUnixSeconds")!.SetValue(tokens, 0L);
            object refreshed = Result(owner.GetMethod("EnsureFreshAsync")!.Invoke(authenticator, [credential, CancellationToken.None])!);
            string serialized = (string)refreshed.GetType().GetMethod("Serialize")!.Invoke(refreshed, null)!;
            Assert.Contains("\"accessToken\":\"fresh\"", serialized, StringComparison.Ordinal);
            Assert.Equal(codex ? 4 : 2, handler.Calls);
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static object Result(object operation)
    {
        ((Task)operation).GetAwaiter().GetResult();
        return operation.GetType().GetProperty("Result")!.GetValue(operation)!;
    }
    private static HttpResponseMessage Tokens() => Response("{\"access_token\":\"access\",\"refresh_token\":\"refresh\"}");
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request); }
    }
    private sealed class AuthGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
