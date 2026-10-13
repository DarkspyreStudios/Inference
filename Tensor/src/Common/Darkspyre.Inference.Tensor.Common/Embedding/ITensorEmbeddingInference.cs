using Darkspyre.Inference.Common.Embedding;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Embedding;

/// <summary>The contract for in-process embedding models.</summary>
public interface ITensorEmbeddingInference : IEmbeddingInference, ITensorModelInstance;
