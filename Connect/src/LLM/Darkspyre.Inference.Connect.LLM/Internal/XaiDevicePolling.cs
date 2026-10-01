namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class XaiDevicePolling
{
    internal static async Task WaitAsync(LlmDeviceCode code, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ThrowIfExpired(code);
        TimeSpan delay = TimeSpan.FromSeconds(Math.Max(1, code.IntervalSeconds));
        if (code.ExpiresAt is { } expiry && expiry - DateTimeOffset.UtcNow < delay)
            delay = expiry - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        ThrowIfExpired(code);
    }

    internal static LlmDeviceCode AfterPending(LlmDeviceCode code, LlmAuthPendingException pending) =>
        pending.SlowDown ? code with { IntervalSeconds = checked(code.IntervalSeconds + 5) } : code;

    private static void ThrowIfExpired(LlmDeviceCode code)
    {
        if (code.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new LlmProviderException("Grok device authorization expired. Start sign-in again.",
                LlmTransport.XaiResponses, providerCode: "expired_token");
    }
}
