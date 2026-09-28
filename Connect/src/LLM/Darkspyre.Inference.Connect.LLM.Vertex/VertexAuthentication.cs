using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

internal sealed record VertexAccess(string Token, string Scope)
{
    public override string ToString() => "VertexAccess { protected }";
}

internal interface IVertexAuthentication
{
    Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken);
}

/// <summary>Google SDK token ownership/refresh; the host's durable credential is never replaced.</summary>
internal sealed class VertexAuthentication : IVertexAuthentication
{
    private readonly ConcurrentDictionary<string, Lazy<GoogleCredential>> _credentials = new(StringComparer.Ordinal);
    private readonly string _ambientScope = Guid.NewGuid().ToString("N");
    private readonly Func<string, string, GoogleCredential> _createCredential;
    private readonly Func<(string Path, bool Required)> _credentialPath;

    public VertexAuthentication() : this(CredentialFactory.FromJson, AdcPath) { }

    internal VertexAuthentication(Func<string, string, GoogleCredential> createCredential,
        Func<(string Path, bool Required)> credentialPath)
    { _createCredential = createCredential; _credentialPath = credentialPath; }

    private static (string Path, bool Required) AdcPath()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        string config = Environment.GetEnvironmentVariable("CLOUDSDK_CONFIG")
            ?? (OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "gcloud")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "gcloud"));
        return (explicitPath ?? Path.Combine(config, "application_default_credentials.json"), explicitPath is not null);
    }

    private const string CloudScope = "https://www.googleapis.com/auth/cloud-platform";

    public async Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken)
    {
        VertexEndpoints.Validate(connection);
        try
        {
            string key;
            GoogleCredential credential;
            if (connection.Vertex!.AuthenticationMode == LlmGoogleAuthenticationMode.ServiceAccount)
            {
                string json = LlmCredential.Parse(connection.Credential)?.Google?.ServiceAccountJson
                    ?? throw new InvalidOperationException();
                ValidateServiceAccount(json);
                key = Hash("service-account\n" + json);
                credential = _credentials.GetOrAdd(key, _ => new Lazy<GoogleCredential>(() => _createCredential(json, "service_account").CreateScoped(CloudScope))).Value;
            }
            else
            {
                var (path, required) = _credentialPath();
                if (required || File.Exists(path))
                {
                    // Ambient ADC files are trusted environment configuration, not host-supplied
                    // provider JSON. Re-read to detect replacements, including a principal change.
                    string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                    using JsonDocument parsed = JsonDocument.Parse(json);
                    string kind = parsed.RootElement.GetProperty("type").GetString() ?? throw new InvalidOperationException();
                    key = Hash(Path.GetFullPath(path) + "\n" + json);
                    credential = _credentials.GetOrAdd(key, _ => new Lazy<GoogleCredential>(() => _createCredential(json, kind).CreateScoped(CloudScope))).Value;
                }
                else
                {
                    // No file: let Google's ADC discovery select the attached environment credential.
                    credential = (await GoogleCredential.GetApplicationDefaultAsync(cancellationToken).ConfigureAwait(false)).CreateScoped(CloudScope);
                    key = credential.UnderlyingCredential is ComputeCredential compute
                        ? Hash("attached-account\n" + await compute.GetDefaultServiceAccountEmailAsync(cancellationToken).ConfigureAwait(false))
                        : _ambientScope;
                }
            }
            string token = await credential.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token)) { throw new InvalidOperationException(); }
            string scope = Hash($"{key}\n{connection.Vertex.ProjectId}\n{connection.Vertex.Location}\n{connection.Vertex.AuthenticationMode}");
            return new(token, scope);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // SDK/parser diagnostics may contain credential material. Never retain them as an inner exception.
            throw new LlmProviderException("Google authentication failed. Verify the configured credential source and permissions.", LlmTransport.GoogleVertexAi);
        }
    }

    internal static void ValidateServiceAccount(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.GetProperty("type").GetString() != "service_account"
            || root.GetProperty("token_uri").GetString() != "https://oauth2.googleapis.com/token"
            || string.IsNullOrWhiteSpace(root.GetProperty("client_email").GetString())
            || string.IsNullOrWhiteSpace(root.GetProperty("private_key").GetString())
            || (root.TryGetProperty("universe_domain", out var universe) && universe.GetString() != "googleapis.com"))
        {
            throw new ArgumentException("Expected a Google service-account credential with the standard token endpoint.");
        }
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
