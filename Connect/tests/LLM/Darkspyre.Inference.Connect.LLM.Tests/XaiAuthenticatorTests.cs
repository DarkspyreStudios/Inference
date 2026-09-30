using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiAuthenticatorTests
{
    private const string ClientId = "b1a00492-073a-47ea-816f-4c329264a828";
    private const string DeviceReply = """
        {"device_code":"device+secret&","user_code":"USER-CODE","verification_uri":"https://auth.x.ai/device","expires_in":600,"interval":7}
        """;
    private const string TokenReply = """
        {"access_token":"access-secret","refresh_token":"refresh-secret","token_type":"Bearer","expires_in":21600,"scope":"openid api:access"}
        """;
    private const string MeReply = """{"user_id":"user-id","team_id":"team-id","team_blocked":false}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeviceRequestUsesPublicClientAndMapsProviderExpiryAndVerificationUri(bool complete)
    {
        string body = complete ? DeviceReply.Replace("\"interval\":7", "\"interval\":7,\"verification_uri_complete\":\"https://auth.x.ai/device?user_code=USER-CODE\"") : DeviceReply;
        using var handler = new Handler((_, _) => Task.FromResult(Json(body)));
        using var http = new HttpClient(handler);
        var auth = new XaiAuthenticator(http);
        DateTimeOffset before = DateTimeOffset.UtcNow;
        LlmDeviceCode code = await auth.StartAsync();
        Assert.Equal(LlmTransport.XaiResponses, auth.Transport);
        Sent request = Assert.Single(handler.Requests);
        Assert.Equal("https://auth.x.ai/oauth2/device/code", request.Uri);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        Assert.Null(request.Authorization);
        Assert.Equal(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = "openid profile email offline_access grok-cli:access api:access"
        }, Form(request.Body));
        Assert.Equal("device+secret&", code.DeviceAuthId);
        Assert.Equal("USER-CODE", code.UserCode);
        Assert.Equal(7, code.IntervalSeconds);
        Assert.Equal(complete ? "https://auth.x.ai/device?user_code=USER-CODE" : "https://auth.x.ai/device", code.VerificationUrl);
        Assert.InRange(code.ExpiresAt!.Value, before.AddSeconds(600), DateTimeOffset.UtcNow.AddSeconds(600));
        Assert.DoesNotContain("USER-CODE", code.ToString());
    }

    [Fact]
    public async Task MissingIntervalUsesRfcDefault()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(DeviceReply.Replace(",\"interval\":7", ""))));
        using var http = new HttpClient(handler);
        Assert.Equal(5, (await new XaiAuthenticator(http).StartAsync()).IntervalSeconds);
    }

    [Theory]
    [InlineData("device_code", "null")]
    [InlineData("user_code", "\"\"")]
    [InlineData("expires_in", "null")]
    [InlineData("expires_in", "0")]
    [InlineData("expires_in", "-1")]
    [InlineData("expires_in", "\"600\"")]
    [InlineData("interval", "0")]
    [InlineData("interval", "-1")]
    [InlineData("interval", "null")]
    [InlineData("verification_uri", "\"http://auth.x.ai/device\"")]
    [InlineData("verification_uri", "\"javascript:secret\"")]
    [InlineData("verification_uri", "\"https://secret@auth.x.ai/device\"")]
    [InlineData("verification_uri_complete", "\"relative\"")]
    public async Task InvalidDeviceFieldsFailWithoutEchoingResponse(string field, string value)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(WithField(DeviceReply, field, value))));
        using var http = new HttpClient(handler);
        LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() => new XaiAuthenticator(http).StartAsync());
        Assert.Equal("xai_authorization_protocol_error", error.ProviderCode);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("authorization_pending", false)]
    [InlineData("slow_down", true)]
    public async Task TokenPollReturnsPendingOnceWithoutRetry(string code, bool slowDown)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(
            JsonSerializer.Serialize(new { error = code, error_description = "device+secret& USER-CODE" }), HttpStatusCode.BadRequest)));
        using var http = new HttpClient(handler);
        LlmAuthPendingException error = await Assert.ThrowsAsync<LlmAuthPendingException>(() => new XaiAuthenticator(http).CompleteAsync("device+secret&", "USER-CODE"));
        Assert.Equal(slowDown, error.SlowDown);
        Sent request = Assert.Single(handler.Requests);
        Assert.Equal("https://auth.x.ai/oauth2/token", request.Uri);
        Assert.Equal(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = "device+secret&",
            ["client_id"] = ClientId
        }, Form(request.Body));
        Assert.DoesNotContain("secret", error.ToString());
        Assert.DoesNotContain("USER-CODE", error.ToString());
    }

    [Theory]
    [InlineData("access_denied")]
    [InlineData("expired_token")]
    public async Task TerminalDeviceErrorsKeepNamedCodeAndRedactSecrets(string code)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(
            JsonSerializer.Serialize(new { error = code, error_description = "device+secret& USER-CODE" }), HttpStatusCode.BadRequest)));
        using var http = new HttpClient(handler);
        LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() => new XaiAuthenticator(http).CompleteAsync("device+secret&", "USER-CODE"));
        Assert.Equal(code, error.ProviderCode);
        Assert.DoesNotContain("device+secret&", error.ToString() + error.SanitizedResponseBody);
        Assert.DoesNotContain("USER-CODE", error.ToString() + error.SanitizedResponseBody);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ExchangeChecksMeExactlyOnceAndMapsOAuthOnlyMetadata()
    {
        string idToken = JwtToken("""{"email":"person@example.test","grok_plan":"invented"}""");
        string accessToken = JwtToken("""{"exp":1}""");
        string body = WithField(WithField(TokenReply, "access_token", JsonSerializer.Serialize(accessToken)), "id_token", JsonSerializer.Serialize(idToken));
        using var handler = new Handler((request, _) => Task.FromResult(Json(request.Method == HttpMethod.Get ? MeReply : body)));
        using var http = new HttpClient(handler);
        DateTimeOffset before = DateTimeOffset.UtcNow;
        LlmCredential credential = await new XaiAuthenticator(http).CompleteAsync("device", "user");
        LlmOAuthTokens tokens = Assert.IsType<LlmOAuthTokens>(credential.OAuth);
        Assert.Null(credential.ApiKey);
        Assert.Empty(credential.SecretHeaders);
        Assert.Equal(accessToken, tokens.AccessToken);
        Assert.Equal("refresh-secret", tokens.RefreshToken);
        Assert.Equal(idToken, tokens.IdToken);
        Assert.Equal("person@example.test", tokens.Email);
        Assert.Equal("user-id", tokens.AccountId);
        Assert.Null(tokens.PlanType);
        Assert.Equal(["openid", "api:access"], tokens.Scopes);
        Assert.InRange(tokens.ExpiresAtUnixSeconds!.Value, before.AddSeconds(21600).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddSeconds(21600).ToUnixTimeSeconds());
        Assert.Equal(2, handler.Requests.Count);
        Sent me = handler.Requests[1];
        Assert.Equal(HttpMethod.Get, me.Method);
        Assert.Equal("https://api.x.ai/v1/me", me.Uri);
        Assert.Equal("Bearer " + accessToken, me.Authorization);
        Assert.Null(me.Body);
    }

    [Theory]
    [InlineData("true", "user-id", true)]
    [InlineData("\"true\"", "user-id", false)]
    [InlineData("false", "", false)]
    public async Task BlockedOrInvalidMeNeverReturnsCredential(string blocked, string userId, bool entitlement)
    {
        string me = WithField(WithField(MeReply, "team_blocked", blocked), "user_id", JsonSerializer.Serialize(userId));
        using var handler = new Handler((request, _) => Task.FromResult(Json(request.Method == HttpMethod.Get ? me : TokenReply)));
        using var http = new HttpClient(handler);
        var auth = new XaiAuthenticator(http);
        if (entitlement)
        {
            InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => auth.CompleteAsync("device", "user"));
            Assert.Equal(InferenceFailureCode.AccessDenied, error.Code);
            Assert.Equal("xai_entitlement_required", Assert.IsType<LlmProviderException>(error.InnerException).ProviderCode);
        }
        else
        {
            await Assert.ThrowsAsync<LlmProviderException>(() => auth.CompleteAsync("device", "user"));
        }
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("access_token", "null")]
    [InlineData("access_token", "\"\"")]
    [InlineData("access_token", "\"unsafe\\r\\nsecret\"")]
    [InlineData("token_type", "null")]
    [InlineData("token_type", "\"Basic\"")]
    [InlineData("refresh_token", "\"\"")]
    [InlineData("id_token", "42")]
    [InlineData("scope", "[]")]
    [InlineData("expires_in", "0")]
    [InlineData("expires_in", "\"3600\"")]
    public async Task InvalidTokensNeverReachMeOrFallbackToOldAccessToken(string field, string value)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(WithField(TokenReply, field, value))));
        using var http = new HttpClient(handler);
        var auth = new XaiAuthenticator(http);
        await Assert.ThrowsAsync<LlmProviderException>(() => auth.CompleteAsync("device", "user"));
        await Assert.ThrowsAsync<LlmProviderException>(() => auth.EnsureFreshAsync(Expired()));
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Post, request.Method));
    }

    [Fact]
    public async Task RefreshRotatesPairAndPreservesAccountMetadataWithoutCallingMe()
    {
        string reply = """{"access_token":"new-access","refresh_token":"new-refresh","token_type":"bearer","expires_in":7200,"scope":"api:access"}""";
        using var handler = new Handler((_, _) => Task.FromResult(Json(reply)));
        using var http = new HttpClient(handler);
        LlmCredential previous = Expired();
        LlmCredential refreshed = await new XaiAuthenticator(http).EnsureFreshAsync(previous);
        Assert.Equal("old-access", previous.OAuth!.AccessToken);
        Assert.Equal("new-access", refreshed.OAuth!.AccessToken);
        Assert.Equal("new-refresh", refreshed.OAuth.RefreshToken);
        Assert.Equal("user-id", refreshed.OAuth.AccountId);
        Assert.Equal(previous.OAuth.IdToken, refreshed.OAuth.IdToken);
        Assert.Equal("person@example.test", refreshed.OAuth.Email);
        Assert.Null(refreshed.OAuth.PlanType);
        Assert.Equal(["api:access"], refreshed.OAuth.Scopes);
        Sent request = Assert.Single(handler.Requests);
        Assert.Equal("https://auth.x.ai/oauth2/token", request.Uri);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        Assert.Equal(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = "old-refresh",
            ["client_id"] = ClientId
        }, Form(request.Body));
        Assert.Null(request.Authorization);
    }

    [Fact]
    public async Task RefreshCanRetainRefreshTokenAndScopesButNeverOldExpiry()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json("""{"access_token":"new-opaque-token","token_type":"Bearer"}""")));
        using var http = new HttpClient(handler);
        LlmCredential previous = Expired();
        LlmCredential fresh = await new XaiAuthenticator(http).EnsureFreshAsync(previous);
        Assert.Equal("old-refresh", fresh.OAuth!.RefreshToken);
        Assert.Equal(previous.OAuth!.Scopes, fresh.OAuth.Scopes);
        Assert.Null(fresh.OAuth.ExpiresAtUnixSeconds);
    }

    [Theory]
    [InlineData("{\"exp\":2000000000}", 2000000000L)]
    [InlineData("{\"exp\":9223372036854775807}", null)]
    [InlineData("{\"exp\":\"2000000000\"}", null)]
    [InlineData("[]", null)]
    [InlineData("null", null)]
    public async Task AccessJwtExpiryFallbackHandlesUntrustedClaims(string payload, long? expiry)
    {
        string token = JwtToken(payload);
        string reply = JsonSerializer.Serialize(new { access_token = token, token_type = "Bearer" });
        using var handler = new Handler((request, _) => Task.FromResult(Json(request.Method == HttpMethod.Get ? MeReply : reply)));
        using var http = new HttpClient(handler);
        LlmCredential credential = await new XaiAuthenticator(http).CompleteAsync("device", "user");
        Assert.Equal(expiry, credential.OAuth!.ExpiresAtUnixSeconds);
    }

    [Theory]
    [InlineData(120, false)]
    [InlineData(30, true)]
    [InlineData(-1, true)]
    public async Task SharedRefreshSkewControlsNetworkAccess(int seconds, bool refreshes)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(TokenReply)));
        using var http = new HttpClient(handler);
        LlmCredential previous = Expired() with { OAuth = Expired().OAuth! with { ExpiresAtUnixSeconds = DateTimeOffset.UtcNow.AddSeconds(seconds).ToUnixTimeSeconds() } };
        LlmCredential actual = await new XaiAuthenticator(http).EnsureFreshAsync(previous);
        Assert.Equal(refreshes ? 1 : 0, handler.Requests.Count);
        if (!refreshes) Assert.Same(previous, actual);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("poll")]
    [InlineData("me")]
    [InlineData("refresh")]
    public async Task CancellationStopsAnActiveHttpOperation(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (request, token) =>
        {
            if (stage == "me" && request.Method == HttpMethod.Post) return Json(TokenReply);
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{}");
        });
        using var http = new HttpClient(handler);
        var auth = new XaiAuthenticator(http);
        Task operation = stage == "start" ? auth.StartAsync(cancellation.Token)
            : stage == "refresh" ? auth.EnsureFreshAsync(Expired(), cancellation.Token)
            : auth.CompleteAsync("device", "user", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(stage == "me" ? 2 : 1, handler.Requests.Count);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("poll")]
    [InlineData("me")]
    [InlineData("refresh")]
    public async Task LateHttpSuccessDoesNotWinCancellation(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler((request, _) =>
        {
            if (stage == "me" && request.Method == HttpMethod.Post) return Task.FromResult(Json(TokenReply));
            cancellation.Cancel();
            return Task.FromResult(Json(stage == "start" ? DeviceReply : stage == "me" ? MeReply : TokenReply));
        });
        using var http = new HttpClient(handler);
        var auth = new XaiAuthenticator(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stage == "start" ? auth.StartAsync(cancellation.Token)
            : stage == "refresh" ? auth.EnsureFreshAsync(Expired(), cancellation.Token)
            : (Task)auth.CompleteAsync("device", "user", cancellation.Token));
        Assert.Equal(stage == "me" ? 2 : 1, handler.Requests.Count);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("poll")]
    [InlineData("me")]
    [InlineData("refresh")]
    public async Task MalformedJsonFailsWithoutRawBodyOrInnerException(string stage)
    {
        using var handler = new Handler((request, _) => Task.FromResult(Json(
            stage == "me" && request.Method == HttpMethod.Post ? TokenReply : "{secret")));
        using var http = new HttpClient(handler);
        var auth = new XaiAuthenticator(http);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => stage == "start" ? auth.StartAsync()
            : stage == "refresh" ? auth.EnsureFreshAsync(Expired()) : (Task)auth.CompleteAsync("device", "user"));
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task RefreshFailureRedactsEveryPreviousSecretWithoutRetryOrMe()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(
            "old-access old-refresh old-id", HttpStatusCode.PaymentRequired)));
        using var http = new HttpClient(handler);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => new XaiAuthenticator(http).EnsureFreshAsync(Expired()));
        Assert.Equal(InferenceFailureCode.AccessDenied, error.Code);
        Assert.DoesNotContain("old-access", error.ToString());
        Assert.DoesNotContain("old-refresh", error.ToString());
        Assert.DoesNotContain("old-id", error.ToString());
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("keep")]
    [InlineData("replace")]
    [InlineData("delete")]
    public async Task RealAuthenticatorRefreshSavesRotatedPairOnlyAgainstCapturedVersion(string mutation)
    {
        var source = new Source(new LlmConnection
        {
            Endpoint = "https://api.x.ai/v1",
            Version = "v1",
            Transport = LlmTransport.XaiResponses,
            Credential = Expired().Serialize()
        });
        using var handler = new Handler((_, _) =>
        {
            if (mutation == "delete") source.Current = null;
            if (mutation == "replace") source.Current = source.Current! with { Version = "v2", Credential = "replacement" };
            return Task.FromResult(Json(TokenReply));
        });
        using var http = new HttpClient(handler);
        await using var access = new LlmConnectionAccess(source, [new XaiAuthenticator(http)]);
        if (mutation == "keep")
        {
            LlmConnection current = await access.AcquireAsync("connection");
            Assert.Equal("access-secret", LlmCredential.Parse(current.Credential)!.OAuth!.AccessToken);
            Assert.Equal("refresh-secret", LlmCredential.Parse(source.Current!.Credential)!.OAuth!.RefreshToken);
            Assert.Equal("user-id", LlmCredential.Parse(source.Current.Credential)!.OAuth!.AccountId);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => access.AcquireAsync("connection"));
            if (mutation == "delete") Assert.Null(source.Current);
            else Assert.Equal("replacement", source.Current!.Credential);
        }

        Assert.Equal("v1", source.ExpectedVersion);
        Assert.Equal(1, source.Saves);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(401, true)]
    [InlineData(402, true)]
    [InlineData(403, false)]
    public async Task MeFailureUsesSharedClassifierWithoutRetryRefreshOrReturningCredential(int status, bool named)
    {
        using var handler = new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? Json("access-secret refresh-secret device+secret& USER-CODE", (HttpStatusCode)status) : Json(TokenReply)));
        using var http = new HttpClient(handler);
        var auth = new XaiAuthenticator(http);
        Exception error = named
            ? await Assert.ThrowsAsync<InferenceException>(() => auth.CompleteAsync("device+secret&", "USER-CODE"))
            : await Assert.ThrowsAsync<LlmProviderException>(() => auth.CompleteAsync("device+secret&", "USER-CODE"));
        Assert.DoesNotContain("access-secret", error.ToString());
        Assert.DoesNotContain("refresh-secret", error.ToString());
        Assert.DoesNotContain("device+secret&", error.ToString());
        Assert.DoesNotContain("USER-CODE", error.ToString());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task UnknownPollFailureRedactsProtocolSecretsAndIsNotPending()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(
            "device+secret& USER-CODE", HttpStatusCode.Forbidden)));
        using var http = new HttpClient(handler);
        LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() => new XaiAuthenticator(http).CompleteAsync("device+secret&", "USER-CODE"));
        Assert.DoesNotContain("device+secret&", error.ToString());
        Assert.DoesNotContain("USER-CODE", error.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AlreadyCancelledCallsDoNotTouchHttpEvenForAnUnexpiredCredential()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var http = new HttpClient(handler);
        var auth = new XaiAuthenticator(http);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.StartAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.CompleteAsync("device", "user", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.EnsureFreshAsync(
            Expired() with { OAuth = Expired().OAuth! with { ExpiresAtUnixSeconds = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() } }, cancellation.Token));
        Assert.Empty(handler.Requests);
    }

    private static LlmCredential Expired() => new()
    {
        OAuth = new()
        {
            AccessToken = "old-access",
            RefreshToken = "old-refresh",
            IdToken = "old-id",
            ExpiresAtUnixSeconds = 1,
            Email = "person@example.test",
            AccountId = "user-id",
            Scopes = ["openid", "api:access"]
        }
    };

    private static string WithField(string body, string field, string value)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(body)!;
        node[field] = System.Text.Json.Nodes.JsonNode.Parse(value);
        return node.ToJsonString();
    }

    private static string JwtToken(string payload) => "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";

    private static Dictionary<string, string> Form(string? body) => body!.Split('&')
        .Select(part => part.Split('=', 2)).ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Sent(HttpMethod Method, string? Uri, string? ContentType, string? Body, string? Authorization);

    private sealed class Source(LlmConnection current) : ILlmConnectionSource
    {
        public LlmConnection? Current { get; set; } = current;
        public int Saves { get; private set; }
        public string? ExpectedVersion { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        {
            Saves++;
            ExpectedVersion = expectedVersion;
            if (Current is null || Current.Version != expectedVersion) throw new InvalidOperationException("Connection changed.");
            Current = Current with { Credential = credentialJson, Version = "saved" };
            return Task.CompletedTask;
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method, request.RequestUri?.AbsoluteUri, request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken), request.Headers.Authorization?.ToString()));
            return await respond(request, cancellationToken);
        }
    }
}
