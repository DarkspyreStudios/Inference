using System.Text.RegularExpressions;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static partial class AccountUsageSanitizer
{
    public static string Sanitize(string summary, LlmCredential? credential) =>
        EmailAddress().Replace(
            SecretRedactor.Sanitize(summary, credential),
            "[REDACTED EMAIL]");

    [GeneratedRegex(
        @"(?<![\w.!#$%&'*+/=?^`{|}~-])[A-Z0-9.!#$%&'*+/=?^`{|}~-]+@[A-Z0-9-]+(?:\.[A-Z0-9-]+)+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EmailAddress();
}
