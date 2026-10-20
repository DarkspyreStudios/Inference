using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>The Grok Build CLI's retry schedule against the subscription proxy.</summary>
internal static class XaiStepRetryPolicy
{
    internal const int MaxTransientSends = 15;
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan[] UnauthorizedWaits = [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];
    internal static readonly TimeSpan[] IdleWaits = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
    internal const int MaxRepetitionResamples = 2;

    internal static LlmStepRetry? Decide(Exception error, LlmStepRetryContext context, Func<double> random)
    {
        if (error is LlmProviderException { ShouldRetry: false } or LlmProviderException { IsContextOverflow: true }) return null;
        if (error is LlmProviderException { ProviderCode: "grok_repetition_loop" })
            return context.Count(LlmStepRetryKind.RepetitionLoop) < MaxRepetitionResamples
                ? new(LlmStepRetryKind.RepetitionLoop, TimeSpan.Zero) { NewStep = true, Reminder = XaiRepetitionCheck.Reminder } : null;
        if (error is LlmProviderException { ProviderCode: "grok_stream_idle_timeout" })
            return context.Count(LlmStepRetryKind.IdleTimeout) < IdleWaits.Length
                ? Retry(LlmStepRetryKind.IdleTimeout, IdleWaits[context.Count(LlmStepRetryKind.IdleTimeout)]) : null;
        if (error is InferenceException { Code: InferenceFailureCode.AuthenticationRequired, InnerException: LlmProviderException { StatusCode: System.Net.HttpStatusCode.Unauthorized } })
            return context.Count(LlmStepRetryKind.Unauthorized) < UnauthorizedWaits.Length
                ? Retry(LlmStepRetryKind.Unauthorized, UnauthorizedWaits[context.Count(LlmStepRetryKind.Unauthorized)]) : null;
        TimeSpan? retryAfter = (error as LlmProviderException)?.RetryAfter;
        if (error is LlmProviderException { StatusCode: System.Net.HttpStatusCode.TooManyRequests })
            return context.Total == 0 ? Retry(LlmStepRetryKind.RateLimited,
                retryAfter > TimeSpan.Zero ? Cap(retryAfter.Value, MaxRateLimitWait) : Jitter(TimeSpan.FromSeconds(2))) : null;
        if (error is HttpRequestException || error is LlmProviderException
            {
                StatusCode: >= System.Net.HttpStatusCode.InternalServerError and <= (System.Net.HttpStatusCode)599
                and not (System.Net.HttpStatusCode)525 and not (System.Net.HttpStatusCode)526
            }
            || error is LlmProviderException { IsResponseStreamInterrupted: true })
        {
            int count = context.Count(LlmStepRetryKind.Transient);
            if (count >= MaxTransientSends - 1) return null;
            TimeSpan wait = retryAfter > TimeSpan.Zero ? Cap(retryAfter.Value, MaxBackoff)
                : error is HttpRequestException && count == 0 ? TimeSpan.FromMilliseconds(200)
                : TimeSpan.FromSeconds(Math.Min(2 * Math.Pow(2, count), MaxBackoff.TotalSeconds));
            return Retry(LlmStepRetryKind.Transient, Jitter(wait));
        }
        return null;

        LlmStepRetry Retry(LlmStepRetryKind kind, TimeSpan delay) => new(kind, delay) { NewStep = context.OutputExposed };
        TimeSpan Jitter(TimeSpan delay) => TimeSpan.FromTicks((long)(delay.Ticks * (0.8 + 0.4 * random())));
    }

    private static TimeSpan Cap(TimeSpan delay, TimeSpan cap) => delay < cap ? delay : cap;
}
