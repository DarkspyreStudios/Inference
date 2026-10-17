namespace Darkspyre.Inference.Connect.LLM;

/// <summary>A device authorization code the user enters at a verification page.</summary>
/// <param name="VerificationUrl">The page where the user enters the code.</param>
/// <param name="UserCode">The code the user enters.</param>
/// <param name="DeviceAuthId">The provider identifier used to poll for completion.</param>
/// <param name="IntervalSeconds">The polling interval, in seconds.</param>
public sealed record LlmDeviceCode(
    string VerificationUrl,
    string UserCode,
    string DeviceAuthId,
    int IntervalSeconds)
{
    /// <summary>The provider-reported expiry, or null when the provider supplies none.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Returns a diagnostic string without authorization secrets.</summary>
    public override string ToString() => "LlmDeviceCode { Redacted }";

    /// <summary>Returns the code to poll next: five seconds slower after an RFC 8628 slow_down, otherwise unchanged.</summary>
    internal LlmDeviceCode AfterPending(LlmAuthPendingException pending) =>
        pending.SlowDown ? this with { IntervalSeconds = checked(IntervalSeconds + 5) } : this;
}

/// <summary>An in-progress browser authorization with PKCE.</summary>
public sealed record LlmAuthorizationSession
{
    /// <summary>The URL the user opens to authorize.</summary>
    public required string AuthorizeUrl { get; init; }

    /// <summary>The state value that the redirect must return.</summary>
    public required string State { get; init; }

    /// <summary>The PKCE code verifier for the token exchange.</summary>
    public required string CodeVerifier { get; init; }

    /// <summary>The redirect URI registered for the authorization.</summary>
    public required string RedirectUri { get; init; }

    /// <summary>Returns a diagnostic string without authorization secrets.</summary>
    public override string ToString() => "LlmAuthorizationSession { Redacted }";
}

/// <summary>The authorization code and state returned by a redirect.</summary>
/// <param name="Code">The authorization code.</param>
/// <param name="State">The state value from the redirect.</param>
public sealed record LlmRedirectResult(string Code, string State)
{
    /// <summary>Returns a diagnostic string without authorization secrets.</summary>
    public override string ToString() => "LlmRedirectResult { Redacted }";

    /// <summary>Parses a pasted redirect URL with code and state query values, or a pasted <c>code#state</c> string. Throws <see cref="FormatException"/> when neither form is present.</summary>
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

/// <summary>Thrown while polling a device authorization that the user has not completed yet.</summary>
/// <param name="message">The exception message.</param>
public sealed class LlmAuthPendingException(string message) : Exception(message)
{
    /// <summary>The RFC 8628 slow_down response adds five seconds to subsequent polling intervals.</summary>
    public bool SlowDown { get; init; }
}
