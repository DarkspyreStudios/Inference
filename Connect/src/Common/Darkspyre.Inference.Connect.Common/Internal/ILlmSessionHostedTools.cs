using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>A client's managed sessions add these tools when the step's model offers their kind and the caller has
/// not supplied that kind. An added tool blocks replay only after its call starts.</summary>
internal interface ILlmSessionHostedTools
{
    /// <summary>The session's added hosted tools, in request order.</summary>
    IReadOnlyList<LlmHostedTool> SessionHostedTools { get; }
}
