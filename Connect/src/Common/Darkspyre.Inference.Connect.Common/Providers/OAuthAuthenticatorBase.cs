using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal abstract class OAuthAuthenticatorBase(HttpClient http) : ILlmAuthenticator
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);

    protected HttpClient Http { get; } = http;

    public abstract LlmTransport Transport { get; }

    protected abstract string ClientId { get; }

    protected abstract string TokenEndpoint { get; }

    public async Task<LlmCredential> EnsureFreshAsync(
        LlmCredential credential,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeInputAdmission.ValidateCredential(Transport, credential);
        if (credential.OAuth is null || !ShouldRefresh(credential.OAuth))
        {
            return credential;
        }

        if (string.IsNullOrEmpty(credential.OAuth.RefreshToken))
        {
            throw new InvalidOperationException(
                $"{Transport} credentials have expired and have no refresh token; sign in again.");
        }

        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken); cancellationToken = consumption.Token;
        LlmCredential refreshed;
        {
            using HttpRequestMessage request = BuildRefreshRequest(credential.OAuth);
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await ValidateRefreshResponseAsync(response, credential, cancellationToken);
            using JsonDocument document = await ParseJsonAsync(response, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            refreshed = MapTokens(document.RootElement, credential.OAuth);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return consumption.Publish(refreshed);
    }

    protected virtual Task ValidateRefreshResponseAsync(
        HttpResponseMessage response,
        LlmCredential credential,
        CancellationToken cancellationToken) =>
        ThrowIfUnsuccessfulAsync(response, $"{Transport} token refresh", cancellationToken, credential);

    protected abstract HttpRequestMessage BuildRefreshRequest(LlmOAuthTokens tokens);

    protected abstract LlmCredential MapTokens(
        JsonElement tokenResponse,
        LlmOAuthTokens previous);

    protected static async Task<JsonDocument> ParseJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        JsonDocument? result = null;
        try
        {
            await using (Stream stream = await SdkHttpConsumption.ReadAsStreamAsync(response.Content, cancellationToken))
            {
                try { result = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken); }
                catch (JsonException)
                {
                    throw new InvalidOperationException("The authorization server returned invalid JSON.");
                }
            }
            return result;
        }
        catch
        {
            result?.Dispose();
            throw;
        }
    }

    protected static async Task ThrowIfUnsuccessfulAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken,
        LlmCredential? credential = null)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken);
        string safe = SecretRedactor.Sanitize(body, credential);
        throw new InvalidOperationException(
            $"{operation} failed ({(int)response.StatusCode}): {safe}");
    }

    /// <summary>Returns the device-code response's RFC 8628 <c>verification_uri_complete</c> when it is an absolute
    /// https URL without user info, so the verification page opens with the user code already filled in. Otherwise
    /// returns <paramref name="verificationUrl"/>, where the user types the code.</summary>
    protected static string PrefilledVerificationUrl(JsonElement response, string verificationUrl) =>
        LlmJson.GetString(response, "verification_uri_complete") is { } complete
        && Uri.TryCreate(complete, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
            ? complete
            : verificationUrl;

    private static bool ShouldRefresh(LlmOAuthTokens tokens)
    {
        DateTimeOffset? expiry = tokens.ExpiresAtUnixSeconds is { } seconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : Jwt.ReadExpiration(tokens.AccessToken);
        return expiry is { } value && value <= DateTimeOffset.UtcNow + RefreshSkew;
    }
}
