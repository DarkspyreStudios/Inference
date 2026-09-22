namespace Darkspyre.Inference.Connect.LLM;

public sealed record LlmDeviceCode(
    string VerificationUrl,
    string UserCode,
    string DeviceAuthId,
    int IntervalSeconds);

public sealed record LlmAuthorizationSession
{
    public required string AuthorizeUrl { get; init; }

    public required string State { get; init; }

    public required string CodeVerifier { get; init; }

    public required string RedirectUri { get; init; }
}

public sealed record LlmRedirectResult(string Code, string State)
{
    public static LlmRedirectResult ParsePastedCode(string pasted)
    {
        string value = (pasted ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw new FormatException("Paste the complete authorization code.");
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https")
        {
            Dictionary<string, string> query = ParseQuery(uri.Query);
            if (query.TryGetValue("code", out string? queryCode)
                && query.TryGetValue("state", out string? queryState)
                && queryCode.Length > 0
                && queryState.Length > 0)
            {
                return new LlmRedirectResult(queryCode, queryState);
            }

            throw new FormatException("That URL does not contain an authorization code and state.");
        }

        int separator = value.IndexOf('#', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new FormatException("Paste the whole code, including the '#' and state value.");
        }

        return new LlmRedirectResult(value[..separator], value[(separator + 1)..]);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in query.TrimStart('?').Split(
                     '&',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                pairs[Uri.UnescapeDataString(part[..equals])] =
                    Uri.UnescapeDataString(part[(equals + 1)..]);
            }
        }

        return pairs;
    }
}

public sealed class LlmAuthPendingException(string message) : Exception(message);
