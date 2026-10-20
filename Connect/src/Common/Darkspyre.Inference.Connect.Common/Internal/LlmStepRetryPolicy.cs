namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Why a managed session resends a failed model request.</summary>
internal enum LlmStepRetryKind
{
    /// <summary>A transient service or connection failure.</summary>
    Transient,
    /// <summary>An interrupted stream under the default policy.</summary>
    StreamInterrupted,
    /// <summary>A rate limit.</summary>
    RateLimited,
    /// <summary>A rejected credential the facade renewed.</summary>
    Unauthorized,
    /// <summary>A stream that exceeded its idle deadline.</summary>
    IdleTimeout,
    /// <summary>A service-reported repetition loop.</summary>
    RepetitionLoop
}

/// <summary>The resend counts and exposure of a failed step or summary request.</summary>
internal sealed record LlmStepRetryContext(IReadOnlyDictionary<LlmStepRetryKind, int> Retries, bool OutputExposed,
    bool SideRequest)
{
    /// <summary>The resends of one kind so far.</summary>
    public int Count(LlmStepRetryKind kind) => Retries.GetValueOrDefault(kind);
    /// <summary>The resends of all kinds so far.</summary>
    public int Total => Retries.Values.Sum();
}

/// <summary>One resend granted by the policy.</summary>
internal sealed record LlmStepRetry(LlmStepRetryKind Kind, TimeSpan Delay)
{
    /// <summary>Starts a replacement step even when no output was exposed.</summary>
    public bool NewStep { get; init; }
    /// <summary>A user message for the next send only, never stored in the conversation.</summary>
    public string? Reminder { get; init; }
}

/// <summary>Decides whether a managed session resends a failed request.</summary>
internal interface ILlmStepRetryPolicy
{
    /// <summary>The granted resend, or null when the failure ends the step.</summary>
    LlmStepRetry? Decide(Exception error, LlmStepRetryContext context);
}

/// <summary>The default managed-provider schedule: three attempts before output or for an interrupted stream.</summary>
internal sealed class DefaultStepRetryPolicy : ILlmStepRetryPolicy
{
    internal static readonly DefaultStepRetryPolicy Instance = new();

    /// <summary>The default resend for a failed model step or summary request.</summary>
    public LlmStepRetry? Decide(Exception error, LlmStepRetryContext context)
    {
        if (context.SideRequest)
        {
            if (context.Total >= 2) return null;
            if (RetryDelay(error, context.Total) is { } sideDelay) return new(LlmStepRetryKind.Transient, sideDelay);
            return error is LlmProviderException { IsResponseStreamInterrupted: true }
                ? new(LlmStepRetryKind.StreamInterrupted, TimeSpan.FromSeconds(context.Total + 1)) : null;
        }
        if (error is LlmProviderException { IsResponseStreamInterrupted: true })
            return context.Count(LlmStepRetryKind.StreamInterrupted) < 2
                ? new(LlmStepRetryKind.StreamInterrupted, TimeSpan.FromSeconds(context.Count(LlmStepRetryKind.StreamInterrupted) + 1)) { NewStep = true }
                : null;
        if (context.OutputExposed) return null;
        return context.Count(LlmStepRetryKind.Transient) < 2 && RetryDelay(error, context.Count(LlmStepRetryKind.Transient)) is { } delay
            ? new(LlmStepRetryKind.Transient, delay) : null;
    }

    private static TimeSpan? RetryDelay(Exception error, int attempt)
    {
        if (error is not HttpRequestException && error is not LlmProviderException
            {
                StatusCode: System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.BadGateway
                or System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout
            }) { return null; }
        var delay = (error as LlmProviderException)?.RetryAfter ?? TimeSpan.FromMilliseconds(250 * (attempt + 1));
        return delay >= TimeSpan.Zero && delay <= TimeSpan.FromSeconds(30) ? delay : null;
    }
}
