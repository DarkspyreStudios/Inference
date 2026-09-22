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
        "secret_access_key"
    ];

    public static string Sanitize(string body, LlmCredential? credential)
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

        string sanitized = SanitizeJson(body) ?? body;
        if (!string.IsNullOrWhiteSpace(credential?.ApiKey))
        {
            sanitized = sanitized.Replace(credential.ApiKey, "[REDACTED]", StringComparison.Ordinal);
        }

        if (credential is not null)
        {
            foreach (string secret in credential.SecretHeaders.Values.Where(value =>
                         !string.IsNullOrWhiteSpace(value)))
            {
                sanitized = sanitized.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            }

            foreach (string secret in CredentialSecrets(credential))
            {
                sanitized = sanitized.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            }
        }

        return sanitized.Length <= MaximumLength ? sanitized : sanitized[..MaximumLength];
    }

    private static IEnumerable<string> CredentialSecrets(LlmCredential credential)
    {
        string?[] candidates =
        [
            credential.OAuth?.AccessToken,
            credential.OAuth?.RefreshToken,
            credential.OAuth?.IdToken,
            credential.Aws?.AccessKeyId,
            credential.Aws?.SecretAccessKey,
            credential.Aws?.SessionToken
        ];
        return candidates.Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>();
    }

    private static string? SanitizeJson(string body)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(body);
            RedactNode(root);
            return root?.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void RedactNode(JsonNode? node)
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
                        RedactNode(value);
                    }
                }

                break;

            case JsonArray arrayNode:
                foreach (JsonNode? value in arrayNode)
                {
                    RedactNode(value);
                }

                break;
        }
    }
}
