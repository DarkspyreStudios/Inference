namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Builds the provider-neutral identifying facts of one managed failure.</summary>
internal static class ManagedFailure
{
    /// <summary>The diagnostic for the chosen kind, including any provider facts the error carries.</summary>
    internal static LlmAgentFailureDiagnostic Of(LlmAgentFailureKind kind, Exception? error)
    {
        Exception? observed = error is null ? null : ManagedAgentSession.Unwrapped(error);
        if (observed is CompactionFailedException { InnerException: { } inner })
            observed = ManagedAgentSession.Unwrapped(inner);
        return observed is LlmProviderException provider
            ? new(kind, (int?)provider.StatusCode, Label(provider.ProviderCode), Label(provider.RequestId))
            : new(kind, null, null, null);
    }

    /// <summary>The diagnostic for a catch-all failure, using the run's fixed local kind when it has one.</summary>
    internal static LlmAgentFailureDiagnostic OfUnclassified(Exception error, LlmAgentFailureKind? local)
    {
        Exception observed = ManagedAgentSession.Unwrapped(error);
        return observed is LlmProviderException provider
            ? Of(provider.IsResponseStreamInterrupted ? LlmAgentFailureKind.StreamInterrupted : LlmAgentFailureKind.ProviderError, provider)
            : new(local ?? LlmAgentFailureKind.Other, null, null, null);
    }

    /// <summary>The unchanged value when it is a short identifier, otherwise null.</summary>
    internal static string? Label(string? value)
    {
        // L14: provider codes and request ids cross this one identifier boundary. Keep at most 128 ASCII identifier
        // characters unchanged; drop anything else so provider prose, bodies and headers cannot enter the diagnostic.
        if (value is not { Length: > 0 and <= 128 }) return null;
        foreach (char character in value)
            if (character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.' or ':'))
                return null;
        return value;
    }
}
