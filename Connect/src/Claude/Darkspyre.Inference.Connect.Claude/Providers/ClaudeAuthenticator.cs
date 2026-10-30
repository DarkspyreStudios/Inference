using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed class ClaudeAuthenticator(HttpClient http)
    : OAuthAuthenticatorBase(http), ILlmBrowserLogin
{
    /// <summary>The redirect page that shows the authorization code for the person to paste back.</summary>
    internal const string ManualRedirectUri = "https://console.anthropic.com/oauth/code/callback";

    private const string AuthorizeUrl = "https://claude.com/cai/oauth/authorize";
    // The scope and lifetime claude setup-token requests: a token the CLI runs on through CLAUDE_CODE_OAUTH_TOKEN.
    private const string Scopes = "user:inference";
    private const int TokenLifetimeSeconds = 31536000;

    public override LlmTransport Transport => LlmTransport.AnthropicClaudeCli;

    protected override string ClientId => "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    protected override string TokenEndpoint => "https://platform.claude.com/v1/oauth/token";

    public LlmAuthorizationSession Start()
    {
        string codeVerifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string codeChallenge = Base64Url(
            SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var query = new Dictionary<string, string>
        {
            ["code"] = "true",
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = ManualRedirectUri,
            ["scope"] = Scopes,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state
        };
        string queryString = string.Join(
            "&",
            query.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
        return new LlmAuthorizationSession
        {
            AuthorizeUrl = $"{AuthorizeUrl}?{queryString}",
            State = state,
            CodeVerifier = codeVerifier,
            RedirectUri = ManualRedirectUri
        };
    }

    public async Task<LlmCredential> CompleteAsync(
        LlmAuthorizationSession session,
        string code,
        string state,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(state, session.State, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claude login state mismatch; start the login again.");
        }

        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken); cancellationToken = consumption.Token;
        LlmCredential result;
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = RequestJson.Content(new JsonObject
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = session.RedirectUri,
                    ["client_id"] = ClientId,
                    ["code_verifier"] = session.CodeVerifier,
                    ["state"] = state,
                    ["expires_in"] = TokenLifetimeSeconds
                })
            };
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
            await ThrowIfUnsuccessfulAsync(response, "Claude token exchange", cancellationToken);
            using JsonDocument document = await ParseJsonAsync(response, cancellationToken);
            result = BuildCredential(document.RootElement, previous: null);
        }
        return consumption.Publish(result);
    }

    protected override HttpRequestMessage BuildRefreshRequest(LlmOAuthTokens tokens) =>
        new(HttpMethod.Post, TokenEndpoint)
        {
            Content = RequestJson.Content(new JsonObject
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.RefreshToken,
                ["client_id"] = ClientId,
                ["scope"] = Scopes
            })
        };

    protected override LlmCredential MapTokens(
        JsonElement tokenResponse,
        LlmOAuthTokens previous) => BuildCredential(tokenResponse, previous);

    private static LlmCredential BuildCredential(
        JsonElement root,
        LlmOAuthTokens? previous)
    {
        string accessToken = LlmJson.GetString(root, "access_token")
            ?? previous?.AccessToken
            ?? string.Empty;
        int? expiresIn = LlmJson.GetInt(root, "expires_in");
        long? expiresAt = expiresIn is { } seconds
            ? DateTimeOffset.UtcNow.AddSeconds(seconds).ToUnixTimeSeconds()
            : Jwt.ReadExpiration(accessToken)?.ToUnixTimeSeconds()
                ?? previous?.ExpiresAtUnixSeconds;
        string? scope = LlmJson.GetString(root, "scope");
        IReadOnlyList<string>? scopes = scope?.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? previous?.Scopes;
        string? email = previous?.Email;
        if (root.TryGetProperty("account", out JsonElement account)
            && account.ValueKind == JsonValueKind.Object)
        {
            email = LlmJson.GetString(account, "email_address") ?? email;
        }

        return new LlmCredential
        {
            OAuth = new LlmOAuthTokens
            {
                AccessToken = accessToken,
                RefreshToken = LlmJson.GetString(root, "refresh_token")
                    ?? previous?.RefreshToken,
                ExpiresAtUnixSeconds = expiresAt,
                Email = email,
                Scopes = scopes
            }
        };
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
