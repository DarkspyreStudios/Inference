using Darkspyre.Inference.Common.Decision;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Decision;

/// <summary>The interchangeable contract for in-process decision models.</summary>
public interface ITensorDecisionInference : IDecisionInference, ITensorModelInstance;
