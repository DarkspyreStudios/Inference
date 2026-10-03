namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Validates a context replacement against the adapter's actual history projection, without I/O.</summary>
internal interface ILlmCompactionValidator
{
    void ValidateCompaction(LlmConnection connection, LlmChatRequest before, LlmChatRequest after);
}
