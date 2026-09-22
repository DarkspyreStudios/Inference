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
        if (credential.OAuth is null || !ShouldRefresh(credential.OAuth))
        {
            return credential;
        }

        if (string.IsNullOrEmpty(credential.OAuth.RefreshToken))
        {
            throw new InvalidOperationException(
                $"{Transport} credentials have expired and have no refresh token; sign in again.");
        }

        using HttpRequestMessage request = BuildRefreshRequest(credential.OAuth);
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        await ThrowIfUnsuccessfulAsync(response, $"{Transport} token refresh", cancellationToken);
        using JsonDocument document = await ParseJsonAsync(response, cancellationToken);
        return MapTokens(document.RootElement, credential.OAuth);
    }

    protected abstract HttpRequestMessage BuildRefreshRequest(LlmOAuthTokens tokens);

    protected abstract LlmCredential MapTokens(
        JsonElement tokenResponse,
        LlmOAuthTokens previous);

    protected static async Task<JsonDocument> ParseJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    protected static async Task ThrowIfUnsuccessfulAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        string safe = SecretRedactor.Sanitize(body, credential: null);
        throw new InvalidOperationException(
            $"{operation} failed ({(int)response.StatusCode}): {safe}");
    }

    private static bool ShouldRefresh(LlmOAuthTokens tokens)
    {
        DateTimeOffset? expiry = tokens.ExpiresAtUnixSeconds is { } seconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : Jwt.ReadExpiration(tokens.AccessToken);
        return expiry is { } value && value <= DateTimeOffset.UtcNow + RefreshSkew;
    }
}
