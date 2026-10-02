using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class SecretRedactor
{
    private const int MaximumLength = 4096;

    private static readonly string[] SensitiveNameFragments =
    [
        "authorization",
        "credential",
        "password",
        "secret",
        "api_key",
        "apikey",
        "access_token",
        "refresh_token",
        "id_token",
        "session_token",
        "device_code",
        "user_code",
        "encrypted_content",
        "secret_access_key"
    ];

    public static string Sanitize(
        string body,
        LlmCredential? credential,
        IEnumerable<string>? additionalSecrets = null)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        if (body.Contains("<html", StringComparison.OrdinalIgnoreCase)
            || body.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
        {
            return "Provider returned an HTML error page instead of JSON.";
        }

        string[] secrets = (credential is null ? [] : CredentialSecrets(credential))
            .Concat(additionalSecrets ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();
        string sanitized = SanitizeJson(body, secrets) ?? body;
        sanitized = ReplaceSecrets(sanitized, secrets);

        return sanitized.Length <= MaximumLength ? sanitized : sanitized[..MaximumLength];
    }

    private static IEnumerable<string> CredentialSecrets(LlmCredential credential)
    {
        string?[] candidates =
        [
            credential.ApiKey,
            credential.OAuth?.AccessToken,
            credential.OAuth?.RefreshToken,
            credential.OAuth?.IdToken,
            credential.Aws?.AccessKeyId,
            credential.Aws?.SecretAccessKey,
            credential.Aws?.SessionToken,
            (credential.Azure as LlmAzureSecret.ClientSecret)?.Value,
            (credential.Azure as LlmAzureSecret.CertificatePkcs12)?.Base64,
            (credential.Azure as LlmAzureSecret.CertificatePkcs12)?.Password
        ];
        return candidates.Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>()
            .Concat(credential.SecretHeaders.Values);
    }

    private static string ReplaceSecrets(string value, string[] secrets)
    {
        foreach (string secret in secrets)
        {
            value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        return value;
    }

    private static string? SanitizeJson(string body, string[] secrets)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(body);
            secrets = secrets.Concat(NamedSecrets(root)).Distinct(StringComparer.Ordinal)
                .OrderByDescending(value => value.Length).ToArray();
            root = RedactNode(root, secrets);
            return root?.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            // A truncated JSON envelope can contain a secret whose value cannot be located safely.
            string prefix = body.TrimStart();
            return prefix.StartsWith('{') || prefix.StartsWith('[') || prefix.StartsWith('"')
                ? "Provider returned invalid JSON." : null;
        }
    }

    private static IEnumerable<string> NamedSecrets(JsonNode? node, bool sensitive = false)
    {
        switch (node)
        {
            case JsonObject objectNode:
                foreach ((string name, JsonNode? value) in objectNode)
                    foreach (string secret in NamedSecrets(value, sensitive || SensitiveNameFragments.Any(fragment =>
                                 name.Contains(fragment, StringComparison.OrdinalIgnoreCase))))
                        yield return secret;
                break;
            case JsonArray arrayNode:
                foreach (JsonNode? value in arrayNode)
                    foreach (string secret in NamedSecrets(value, sensitive)) yield return secret;
                break;
            case JsonValue valueNode when sensitive && valueNode.TryGetValue(out string? value)
                && !string.IsNullOrWhiteSpace(value):
                yield return value;
                break;
        }
    }

    private static JsonNode? RedactNode(JsonNode? node, string[] secrets)
    {
        switch (node)
        {
            case JsonObject objectNode:
                foreach ((string key, JsonNode? value) in objectNode.ToArray())
                {
                    if (SensitiveNameFragments.Any(fragment =>
                            key.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                    {
                        objectNode[key] = "[REDACTED]";
                    }
                    else
                    {
                        JsonNode? replacement = RedactNode(value, secrets);
                        if (!ReferenceEquals(value, replacement)) objectNode[key] = replacement;
                    }
                }

                break;

            case JsonArray arrayNode:
                for (int index = 0; index < arrayNode.Count; index++)
                {
                    JsonNode? value = arrayNode[index];
                    JsonNode? replacement = RedactNode(value, secrets);
                    if (!ReferenceEquals(value, replacement)) arrayNode[index] = replacement;
                }

                break;

            case JsonValue valueNode when valueNode.TryGetValue(out string? value):
                return JsonValue.Create(ReplaceSecrets(value, secrets));
        }

        return node;
    }
}
