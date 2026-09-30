using System.Collections.Frozen;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

/// <summary>Explicitly reviewed model profiles; catalog membership never grants a future model these capabilities.</summary>
internal sealed record VertexModelProfile(string Id, long ContextTokens, int OutputTokens, IReadOnlySet<string> Locations,
    IReadOnlySet<LlmReasoningLevel> ReasoningLevels)
{
    public bool IsClaude => Id.StartsWith("anthropic/", StringComparison.Ordinal);
    public LlmProviderCapabilities Capabilities => IsClaude ? ClaudeCapabilities : GeminiCapabilities.Create(ReasoningLevels);

    private LlmProviderCapabilities ClaudeCapabilities
    {
        get
        {
            LlmRichContentSupported rich = RichContentRoutes.Anthropic with { MaxBinaryBytes = 5 * 1024 * 1024 };
            return new()
            {
                SupportsStreaming = true,
                SupportsReasoningText = true,
                SupportsStreamingUsage = true,
                SupportsUsageDeltas = true,
                SupportsModelListing = true,
                OutputLimitEnforcement = LlmOutputLimitEnforcement.ProviderEnforced,
                SupportsJsonSchema = true,
                SupportsTemperature = true,
                SupportsToolUse = true,
                Tools = new()
                {
                    ChoiceModes = Enum.GetValues<LlmToolChoiceMode>().ToFrozenSet(),
                    StrictInputSchema = LlmStrictInputSchemaSupport.Supported,
                    SupportsParallelCalls = true,
                    SupportsParallelCallControl = true,
                    SupportsArgumentStreaming = true
                },
                ReasoningLevels = ReasoningLevels,
                SupportsPromptImages = true,
                SupportsPromptDocuments = true,
                RichContentSupported = rich,
                SupportsAgentSessions = true,
                SupportsAgentToolUse = true,
                SupportsAgentTurnInstructions = true,
                Agent = ManagedAgentPolicy.Capabilities with { RichContentSupported = rich }
            };
        }
    }
}

internal static class VertexModelProfiles
{
    // Limits and locations are documented qualification, not evidence of this project's access.
    public static IReadOnlyList<VertexModelProfile> All { get; } =
    [
        new("google/gemini-3-flash-preview", 1_048_576, 65_536,
            new[] { "global" }.ToFrozenSet(StringComparer.Ordinal),
            GeminiReasoning.Get("gemini-3-flash-preview", catalogThinking: null).Levels),
        new("anthropic/claude-sonnet-4-6", 1_000_000, 128_000,
            new[] { "global", "us-east5", "europe-west1", "asia-southeast1" }.ToFrozenSet(StringComparer.Ordinal),
            new[] { LlmReasoningLevel.None, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High }.ToFrozenSet())
    ];

    public static VertexModelProfile Get(string modelId, string location)
    {
        _ = VertexEndpoints.ParseModel(modelId);
        VertexModelProfile profile = All.SingleOrDefault(model => model.Id == modelId)
            ?? throw new NotSupportedException("This Vertex model has no qualified Connect profile.");
        if (!profile.Locations.Contains(location))
        { throw new NotSupportedException("The selected Vertex model is not qualified in this location."); }
        return profile;
    }
}
