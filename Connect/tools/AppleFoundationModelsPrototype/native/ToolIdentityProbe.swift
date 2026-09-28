// DSA-59: inspect Swift argument identity before changing any C bridge ABI.
import Foundation
import FoundationModels
import Synchronization

final class IdentityRecorder: Sendable {
  private let calls = Mutex<[GeneratedContent]>([])
  func record(_ arguments: GeneratedContent) { calls.withLock { $0.append(arguments) } }
  var snapshot: [GeneratedContent] { calls.withLock { $0 } }
}

struct IdentityTool: Tool {
  typealias Arguments = GeneratedContent
  let name = "lookup_code"
  let description = "Returns one current synthetic code for a key. Each reading is a separate call."
  let parameters: GenerationSchema
  let recorder: IdentityRecorder

  func call(arguments: GeneratedContent) async throws -> String {
    recorder.record(arguments)
    // Keep both calls pending until the parent inspects their identities and cancels.
    try await Task.sleep(for: .seconds(30))
    return "UNEXPECTED-TIMEOUT-RESULT"
  }
}

struct IdentityObservation: Encodable {
  let arguments: String
  let hasGenerationID: Bool
  let matchingTranscriptCallIDs: [String]
}

struct IdentityReport: Encodable {
  let outcome: String
  let twoPending: Bool
  let callbackCount: Int
  let transcriptCallCount: Int
  let identicalArguments: Bool
  let distinctNonNilArgumentIDs: Bool
  let callbackIdentity: [IdentityObservation]
  let transcriptCallsWithArgumentID: Int
  let cancellationError: String
  let respondingAfterCancellation: Bool
}

let model = SystemLanguageModel.default
guard case .available = model.availability else {
  print("{\"outcome\":\"unavailable\"}")
  exit(3)
}

do {
  let recorder = IdentityRecorder()
  let parameters = try GenerationSchema(root: DynamicGenerationSchema(name: "LookupArguments", properties: [
    .init(name: "key", schema: DynamicGenerationSchema(type: String.self))
  ]), dependencies: [])
  let tool = IdentityTool(parameters: parameters, recorder: recorder)
  let session = LanguageModelSession(model: model, tools: [tool], instructions:
    "Use lookup_code for every requested reading. Each reading needs a separate call even when keys are identical. Request independent calls together.")
  let response = Task<Void, any Error> {
    _ = try await session.respond(
      to: "Take two separate readings of key sample using two lookup_code calls. Both calls use exactly key sample. Return both codes.",
      options: GenerationOptions(temperature: 0, maximumResponseTokens: 128))
  }
  for _ in 0..<50 {
    if recorder.snapshot.count >= 2 { break }
    try await Task.sleep(for: .milliseconds(100))
  }
  let arguments = recorder.snapshot
  let calls = session.transcript.flatMap { entry -> [Transcript.ToolCall] in
    if case .toolCalls(let calls) = entry { return Array(calls) }
    return []
  }
  let twoPending = arguments.count == 2 && calls.count == 2 && session.isResponding
  let identicalArguments = arguments.count == 2 && arguments[0].jsonString == arguments[1].jsonString
  let observations = arguments.map { argument in
    IdentityObservation(arguments: argument.jsonString, hasGenerationID: argument.id != nil,
      matchingTranscriptCallIDs: calls.filter { argument.id != nil && argument.id == $0.arguments.id }.map(\.id))
  }
  let distinctIDs = arguments.count == 2 && arguments.allSatisfy { $0.id != nil }
    && Set(arguments.compactMap(\.id)).count == arguments.count
  response.cancel()
  var cancellationError = ""
  do { try await response.value }
  catch { cancellationError = String(describing: error) }
  let correlated = distinctIDs && observations.allSatisfy { $0.matchingTranscriptCallIDs.count == 1 }
  let outcome = cancellationError.isEmpty || session.isResponding ? "unexpected"
    : !twoPending || !identicalArguments ? "inconclusive" : correlated ? "observed" : "limitation"
  let report = IdentityReport(outcome: outcome,
    twoPending: twoPending, callbackCount: arguments.count, transcriptCallCount: calls.count,
    identicalArguments: identicalArguments,
    distinctNonNilArgumentIDs: distinctIDs, callbackIdentity: observations,
    transcriptCallsWithArgumentID: calls.filter { $0.arguments.id != nil }.count,
    cancellationError: cancellationError, respondingAfterCancellation: session.isResponding)
  let encoder = JSONEncoder()
  encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
  print(String(decoding: try encoder.encode(report), as: UTF8.self))
  if report.outcome == "inconclusive" || report.outcome == "unexpected" { exit(2) }
} catch {
  FileHandle.standardError.write(Data("Identity probe failed: \(error)\n".utf8))
  exit(2)
}
