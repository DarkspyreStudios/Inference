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

        using HttpRequestMessage request = BuildRefreshRequest(credential.OAuth);
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken); cancellationToken = consumption.Token;
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await ValidateRefreshResponseAsync(response, credential, cancellationToken);
        using JsonDocument document = await ParseJsonAsync(response, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        LlmCredential refreshed = MapTokens(document.RootElement, credential.OAuth);
        cancellationToken.ThrowIfCancellationRequested();
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
        await using Stream stream = await SdkHttpConsumption.ReadAsStreamAsync(response.Content, cancellationToken);
        try
        {
            JsonDocument result = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            try { return SdkHttpConsumption.Publish(response.Content, result, cancellationToken); }
            catch { result.Dispose(); throw; }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The authorization server returned invalid JSON.");
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

    private static bool ShouldRefresh(LlmOAuthTokens tokens)
    {
        DateTimeOffset? expiry = tokens.ExpiresAtUnixSeconds is { } seconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : Jwt.ReadExpiration(tokens.AccessToken);
        return expiry is { } value && value <= DateTimeOffset.UtcNow + RefreshSkew;
    }
}
