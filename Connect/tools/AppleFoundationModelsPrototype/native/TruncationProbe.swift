// DSA-59: inspect the Swift API directly, independent of either C bridge build.
import Foundation
import FoundationModels

let model = SystemLanguageModel.default
guard case .available = model.availability else {
  print("{\"outcome\":\"unavailable\"}")
  exit(3)
}

struct Observation: Encodable {
  let cap: Int
  let content: String
  let rawContentIsComplete: Bool
}

do {
  var observations: [Observation] = []
  for cap in [8, 128] {
    let session = LanguageModelSession(model: model)
    let response = try await session.respond(
      to: "Write a detailed paragraph describing a garden in at least 100 words.",
      options: GenerationOptions(temperature: 0, maximumResponseTokens: cap))
    observations.append(Observation(cap: cap, content: response.content,
                                    rawContentIsComplete: response.rawContent.isComplete))
  }
  let encoder = JSONEncoder()
  encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
  print(String(decoding: try encoder.encode(observations), as: UTF8.self))
} catch {
  // Only this file's synthetic input reaches the framework.
  fputs("Truncation probe failed: \(error)\n", stderr)
  exit(2)
}
