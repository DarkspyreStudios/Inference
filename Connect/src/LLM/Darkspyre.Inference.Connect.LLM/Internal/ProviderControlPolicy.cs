using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class ProviderControlPolicy
{
    internal static LlmChatRequest Resolve(LlmConnection connection, LlmChatRequest request)
    {
        LlmProviderControls? controls = request.ProviderControls ?? connection.Xai?.MultiAgent;
        if (connection.Transport == LlmTransport.XaiResponses
            && connection.ProviderOptions.Keys.Concat(request.ProviderOptions.Keys).Any(key => key == "agent_count"))
            throw new ArgumentException("Grok agent count uses typed provider controls, not provider options.", nameof(request));
        if (controls is null) return request;
        if (controls is not XaiMultiAgentControls multiAgent || connection.Transport != LlmTransport.XaiResponses)
            throw new NotSupportedException("The selected route does not implement these provider controls.");
        XaiMultiAgentProfile profile = XaiModelProfiles.Get(connection, request.ModelId).MultiAgent
            ?? throw new NotSupportedException("The selected model does not declare multi-agent count support.");
        if (multiAgent.AgentCount is { } count) _ = XaiModelProfiles.MultiAgentEffort(profile, count);
        if (request.ReasoningLevel is not null
            || connection.ProviderOptions.Keys.Concat(request.ProviderOptions.Keys).Any(key => key is "reasoning" or "reasoning_effort"))
            throw new ArgumentException("Typed multi-agent controls cannot be combined with neutral or raw reasoning controls.", nameof(request));
        return request with { ProviderControls = multiAgent };
    }

    internal static void Validate(LlmConnection connection, LlmChatRequest request) => _ = Resolve(connection, request);
}
