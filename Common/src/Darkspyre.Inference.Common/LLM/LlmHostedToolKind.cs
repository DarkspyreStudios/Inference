namespace Darkspyre.Inference.Common.LLM;

/// <summary>Provider-hosted tool categories. These identities do not enable a provider capability.</summary>
public enum LlmHostedToolKind
{
    WebSearch = 0,
    SocialSearch = 1,
    CodeExecution = 2,
    ImageGeneration = 3,
    DocumentSearch = 4,
    RemoteMcp = 5,
    ImageUnderstanding = 6,
    VideoUnderstanding = 7
}
