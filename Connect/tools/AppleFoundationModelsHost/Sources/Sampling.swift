import FoundationModels

extension GenerationRequest {
  func generationOptions() throws -> GenerationOptions {
    if let temperature, !temperature.isFinite || temperature < 0 || temperature > 1 {
      throw HostFailure(code: "invalid_request")
    }
    if let maximumResponseTokens, maximumResponseTokens <= 0 {
      throw HostFailure(code: "invalid_request")
    }
    if let topP, !topP.isFinite || topP <= 0 || topP > 1 {
      throw HostFailure(code: "invalid_request")
    }
    if let topK, topK <= 0 { throw HostFailure(code: "invalid_request") }
    if topP != nil && topK != nil { throw HostFailure(code: "unsupported_feature") }
    let mode: GenerationOptions.SamplingMode?
    if let topK {
      mode = .random(top: topK, seed: seed)
    } else if topP != nil || seed != nil {
      mode = .random(probabilityThreshold: topP ?? 1, seed: seed)
    } else {
      mode = nil
    }
    return GenerationOptions(
      sampling: mode, temperature: temperature, maximumResponseTokens: maximumResponseTokens)
  }
}
