using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.LLM;

/// <summary>The interchangeable contract for in-process language models.</summary>
public interface ITensorLlmInference : ILlmInference, ITensorModelInstance;
