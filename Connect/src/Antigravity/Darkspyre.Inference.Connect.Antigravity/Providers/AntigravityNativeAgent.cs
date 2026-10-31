using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>What the Antigravity CLI agent runtime declares.</summary>
internal static class AntigravityNativeAgent
{
    /// <summary>
    /// The Antigravity CLI keeps its own conversation, compacts it itself and reopens it by its ID. It runs only its own
    /// tools under the policy configured in its settings, and takes text only. AntigravityConversation measures its own
    /// saved history and compacts it into a new AGY conversation.
    /// </summary>
    public static LlmAgentCapabilities Capabilities { get; } = new()
    {
        ContextImport = LlmAgentContextImport.OpeningContext,
        SupportsShellTools = InferenceSupport.Unsupported,
        SupportsContextCapture = InferenceSupport.Unsupported,
        SupportsContextReplacement = InferenceSupport.Unsupported,
        SupportsContextStatus = InferenceSupport.Supported,
        SupportsCompactionHandoff = InferenceSupport.Unsupported,
        // A conversation reports Unsupported once its open AGY conversation is not the one its thread id names.
        SupportsNativeResume = InferenceSupport.Supported,
        SupportsSteering = InferenceSupport.Unsupported,
        SupportsSteeringAttachments = InferenceSupport.Unsupported,
        SupportsInterrupt = InferenceSupport.Unsupported,
        SupportsExternalToolHandoff = InferenceSupport.Unsupported,
        SupportsCompactNow = InferenceSupport.Supported,
        SupportsFallbackModels = InferenceSupport.Unsupported,
        SupportsTurnCap = InferenceSupport.Unsupported,
        RichContentSupported = RichContentRoutes.TextOnly,
        SupportedRuntimePolicies = FrozenSet.Create(LlmAgentRuntimePolicyKind.ProviderConfigured),
        ResourceObservation = LlmOperationSupport.Unsupported
    };
}
