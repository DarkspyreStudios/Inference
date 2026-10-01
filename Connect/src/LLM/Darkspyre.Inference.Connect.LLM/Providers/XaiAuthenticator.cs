using System.Net.Http.Headers;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed class XaiAuthenticator(HttpClient http) : OAuthAuthenticatorBase(http), IXaiDeviceLogin
{
    private const string AuthBase = "https://auth.x.ai";
    private const string Scopes = "openid profile email offline_access grok-cli:access api:access";

    public override LlmTransport Transport => LlmTransport.XaiResponses;
    protected override string ClientId => "b1a00492-073a-47ea-816f-4c329264a828";
    protected override string TokenEndpoint => $"{AuthBase}/oauth2/token";

    public async Task<LlmDeviceCode> StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var request = FormRequest($"{AuthBase}/oauth2/device/code", new()
        {
            ["client_id"] = ClientId,
            ["scope"] = Scopes
        });
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        await XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, cancellationToken: cancellationToken);
        using JsonDocument document = await ParseJsonAsync(response, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        JsonElement root = document.RootElement;
        string deviceCode = RequiredString(root, "device_code");
        string userCode = RequiredString(root, "user_code");
        string verificationUri = RequiredString(root, "verification_uri");
        ValidateVerificationUri(verificationUri);
        if (OptionalString(root, "verification_uri_complete") is { } completeUri)
        {
            ValidateVerificationUri(completeUri);
            verificationUri = completeUri;
        }

        int expiresIn = PositiveSeconds(root, "expires_in") ?? throw ProtocolFailure();
        int interval = PositiveSeconds(root, "interval") ?? 5;
        return new LlmDeviceCode(verificationUri, userCode, deviceCode, interval)
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
        };
    }

    public async Task<LlmCredential> CompleteAsync(
        string deviceAuthId,
        string userCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceAuthId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userCode);
        cancellationToken.ThrowIfCancellationRequested();
        using var request = FormRequest(TokenEndpoint, new()
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = deviceAuthId,
            ["client_id"] = ClientId
        });
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            string? code = ReadDeviceError(body);
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                if (code is "authorization_pending" or "slow_down")
                {
                    throw new LlmAuthPendingException("Grok login is waiting for browser approval.")
                    {
                        SlowDown = code == "slow_down"
                    };
                }

                if (code is "access_denied" or "expired_token")
                {
                    throw new LlmProviderException("Grok device authorization ended; start sign-in again.",
                        Transport, response.StatusCode, code,
                        sanitizedResponseBody: SecretRedactor.Sanitize(body, null, [deviceAuthId, userCode]));
                }
            }

            await XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, null,
                [deviceAuthId, userCode], cancellationToken);
        }

        using JsonDocument document = await ParseJsonAsync(response, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        LlmCredential credential = BuildCredential(document.RootElement, null);
        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.x.ai/v1/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.OAuth!.AccessToken);
        using HttpResponseMessage meResponse = await Http.SendAsync(meRequest, cancellationToken);
        await XaiErrorClassifier.ThrowIfUnsuccessfulAsync(meResponse, credential,
            [deviceAuthId, userCode], cancellationToken);
        using JsonDocument me = await ParseJsonAsync(meResponse, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string userId = RequiredString(me.RootElement, "user_id");
        if (me.RootElement.TryGetProperty("team_blocked", out JsonElement blocked))
        {
            if (blocked.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw ProtocolFailure();
            if (blocked.GetBoolean())
            {
                throw XaiErrorClassifier.EntitlementFailure(System.Net.HttpStatusCode.Forbidden,
                    "The Grok account's team is blocked.");
            }
        }

        return credential with { OAuth = credential.OAuth with { AccountId = userId } };
    }

    protected override HttpRequestMessage BuildRefreshRequest(LlmOAuthTokens tokens) =>
        FormRequest(TokenEndpoint, new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken!,
            ["client_id"] = ClientId
        });

    protected override Task ValidateRefreshResponseAsync(
        HttpResponseMessage response,
        LlmCredential credential,
        CancellationToken cancellationToken) =>
        XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, credential, [], cancellationToken);

    protected override LlmCredential MapTokens(JsonElement tokenResponse, LlmOAuthTokens previous) =>
        BuildCredential(tokenResponse, previous);

    private static LlmCredential BuildCredential(JsonElement root, LlmOAuthTokens? previous)
    {
        string accessToken = RequiredString(root, "access_token");
        if (!AuthenticationHeaderValue.TryParse($"Bearer {accessToken}", out AuthenticationHeaderValue? header)
            || header.Parameter != accessToken) throw ProtocolFailure();
        if (!string.Equals(RequiredString(root, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            throw ProtocolFailure();
        }

        int? expiresIn = PositiveSeconds(root, "expires_in");
        string? idToken = OptionalString(root, "id_token") ?? previous?.IdToken;
        JsonElement? claims = Jwt.DecodePayload(idToken);
        string? scope = OptionalString(root, "scope", allowEmpty: true);
        return new LlmCredential
        {
            OAuth = new LlmOAuthTokens
            {
                AccessToken = accessToken,
                RefreshToken = OptionalString(root, "refresh_token") ?? previous?.RefreshToken,
                ExpiresAtUnixSeconds = expiresIn is { } seconds
                    ? DateTimeOffset.UtcNow.AddSeconds(seconds).ToUnixTimeSeconds()
                    : Jwt.ReadExpiration(accessToken)?.ToUnixTimeSeconds(),
                IdToken = idToken,
                Email = claims is { } payload ? LlmJson.GetString(payload, "email") ?? previous?.Email : previous?.Email,
                Scopes = scope is null ? previous?.Scopes : scope.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                AccountId = previous?.AccountId
            }
        };
    }

    private static HttpRequestMessage FormRequest(string endpoint, Dictionary<string, string> values) =>
        new(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(values) };

    private static string RequiredString(JsonElement root, string name) =>
        OptionalString(root, name) ?? throw ProtocolFailure();

    private static string? OptionalString(JsonElement root, string name, bool allowEmpty = false)
    {
        if (root.ValueKind != JsonValueKind.Object) throw ProtocolFailure();
        if (!root.TryGetProperty(name, out JsonElement value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw ProtocolFailure();
        string text = value.GetString()!;
        if (!allowEmpty && string.IsNullOrWhiteSpace(text)) throw ProtocolFailure();
        return text;
    }

    private static int? PositiveSeconds(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) throw ProtocolFailure();
        if (!root.TryGetProperty(name, out _)) return null;
        return LlmJson.GetInt(root, name) is > 0 and var seconds ? seconds : throw ProtocolFailure();
    }

    private static void ValidateVerificationUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) throw ProtocolFailure();
    }

    private static string? ReadDeviceError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return LlmJson.GetString(document.RootElement, "error");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LlmProviderException ProtocolFailure() =>
        new("Grok returned an incomplete or invalid authorization response.", LlmTransport.XaiResponses,
            providerCode: "xai_authorization_protocol_error");
}
