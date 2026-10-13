using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Image;

/// <summary>The interchangeable contract for in-process image generators.</summary>
public interface ITensorImageInference : IImageInference, ITensorModelInstance;
