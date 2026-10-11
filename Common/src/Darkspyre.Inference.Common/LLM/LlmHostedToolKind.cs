namespace Darkspyre.Inference.Common.LLM;

/// <summary>Provider-hosted tool categories. These identities do not enable a provider capability.</summary>
public enum LlmHostedToolKind
{
    /// <summary>Web search.</summary>
    WebSearch = 0,
    /// <summary>Search of social media posts.</summary>
    SocialSearch = 1,
    /// <summary>Code execution in a provider sandbox.</summary>
    CodeExecution = 2,
    /// <summary>Image generation.</summary>
    ImageGeneration = 3,
    /// <summary>Search over uploaded documents or files.</summary>
    DocumentSearch = 4,
    /// <summary>A remote MCP server the provider calls.</summary>
    RemoteMcp = 5,
    /// <summary>Image understanding.</summary>
    ImageUnderstanding = 6,
    /// <summary>Video understanding.</summary>
    VideoUnderstanding = 7
}
