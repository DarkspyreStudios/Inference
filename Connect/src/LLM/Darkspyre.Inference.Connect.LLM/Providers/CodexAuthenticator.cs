using System.Net;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed class CodexAuthenticator(HttpClient http)
    : OAuthAuthenticatorBase(http), ICodexDeviceLogin
{
    private const string AuthBaseUrl = "https://auth.openai.com";
    private const int DefaultIntervalSeconds = 5;

    public override LlmTransport Transport => LlmTransport.OpenAiCodex;

    protected override string ClientId => "app_EMoamEEZ73f0CkXaXp7hrann";

    protected override string TokenEndpoint => $"{AuthBaseUrl}/oauth/token";

    public async Task<LlmDeviceCode> StartAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{AuthBaseUrl}/api/accounts/deviceauth/usercode")
        {
            Content = JsonContent.Create(new { client_id = ClientId })
        };
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        await ThrowIfUnsuccessfulAsync(response, "ChatGPT login code request", cancellationToken);
        using JsonDocument document = await ParseJsonAsync(response, cancellationToken);

        string? deviceAuthId = LlmJson.GetString(document.RootElement, "device_auth_id");
        string? userCode = LlmJson.GetString(document.RootElement, "user_code")
            ?? LlmJson.GetString(document.RootElement, "usercode");
        if (string.IsNullOrEmpty(deviceAuthId) || string.IsNullOrEmpty(userCode))
        {
            throw new InvalidOperationException("ChatGPT returned an incomplete device login code.");
        }

        int interval = LlmJson.GetInt(document.RootElement, "interval") ?? DefaultIntervalSeconds;
        return new LlmDeviceCode(
            $"{AuthBaseUrl}/codex/device",
            userCode,
            deviceAuthId,
            interval > 0 ? interval : DefaultIntervalSeconds);
    }

    public async Task<LlmCredential> CompleteAsync(
        string deviceAuthId,
        string userCode,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{AuthBaseUrl}/api/accounts/deviceauth/token")
        {
            Content = JsonContent.Create(new
            {
                device_auth_id = deviceAuthId,
                user_code = userCode
            })
        };
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            throw new LlmAuthPendingException("ChatGPT login is still waiting for browser approval.");
        }

        await ThrowIfUnsuccessfulAsync(response, "ChatGPT login", cancellationToken);
        using JsonDocument document = await ParseJsonAsync(response, cancellationToken);
        string? authorizationCode = LlmJson.GetString(document.RootElement, "authorization_code");
        string? codeVerifier = LlmJson.GetString(document.RootElement, "code_verifier");
        if (string.IsNullOrEmpty(authorizationCode) || string.IsNullOrEmpty(codeVerifier))
        {
            throw new InvalidOperationException("ChatGPT returned an incomplete authorization response.");
        }

        return await ExchangeAuthorizationCodeAsync(
            authorizationCode,
            codeVerifier,
            cancellationToken);
    }

    protected override HttpRequestMessage BuildRefreshRequest(LlmOAuthTokens tokens) =>
        new(HttpMethod.Post, TokenEndpoint)
        {
            Content = JsonContent.Create(new
            {
                client_id = ClientId,
                grant_type = "refresh_token",
                refresh_token = tokens.RefreshToken
            })
        };

    protected override LlmCredential MapTokens(
        JsonElement tokenResponse,
        LlmOAuthTokens previous) =>
        CredentialFromTokens(
            LlmJson.GetString(tokenResponse, "id_token") ?? previous.IdToken,
            LlmJson.GetString(tokenResponse, "access_token") ?? previous.AccessToken,
            LlmJson.GetString(tokenResponse, "refresh_token") ?? previous.RefreshToken);

    private async Task<LlmCredential> ExchangeAuthorizationCodeAsync(
        string authorizationCode,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["redirect_uri"] = $"{AuthBaseUrl}/deviceauth/callback",
            ["client_id"] = ClientId,
            ["code_verifier"] = codeVerifier
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = content };
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        await ThrowIfUnsuccessfulAsync(response, "ChatGPT token exchange", cancellationToken);
        using JsonDocument document = await ParseJsonAsync(response, cancellationToken);
        return CredentialFromTokens(
            LlmJson.GetString(document.RootElement, "id_token"),
            LlmJson.GetString(document.RootElement, "access_token"),
            LlmJson.GetString(document.RootElement, "refresh_token"));
    }

    private static LlmCredential CredentialFromTokens(
        string? idToken,
        string? accessToken,
        string? refreshToken)
    {
        JsonElement? claims = ReadAuthClaims(idToken);
        return new LlmCredential
        {
            OAuth = new LlmOAuthTokens
            {
                AccessToken = accessToken ?? string.Empty,
                RefreshToken = refreshToken,
                IdToken = idToken,
                ExpiresAtUnixSeconds = Jwt.ReadExpiration(accessToken)?.ToUnixTimeSeconds(),
                AccountId = claims is { } auth
                    ? LlmJson.GetString(auth, "chatgpt_account_id")
                    : null,
                PlanType = claims is { } plan
                    ? LlmJson.GetString(plan, "chatgpt_plan_type")
                    : null,
                Email = ReadEmail(idToken)
            }
        };
    }

    private static JsonElement? ReadAuthClaims(string? idToken)
    {
        JsonElement? payload = Jwt.DecodePayload(idToken);
        return payload is { } element
            && element.TryGetProperty("https://api.openai.com/auth", out JsonElement auth)
            && auth.ValueKind == JsonValueKind.Object
                ? auth
                : null;
    }

    private static string? ReadEmail(string? idToken)
    {
        JsonElement? payload = Jwt.DecodePayload(idToken);
        if (payload is not { } element)
        {
            return null;
        }

        string? email = LlmJson.GetString(element, "email");
        if (!string.IsNullOrWhiteSpace(email))
        {
            return email;
        }

        return element.TryGetProperty("https://api.openai.com/profile", out JsonElement profile)
            && profile.ValueKind == JsonValueKind.Object
                ? LlmJson.GetString(profile, "email")
                : null;
    }
}
