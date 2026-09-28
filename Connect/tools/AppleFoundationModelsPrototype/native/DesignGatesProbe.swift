// DSA-59: bounded direct-Swift experiments, never a shipping connector.
import Foundation
import FoundationModels
import Synchronization

struct CallCapture: Sendable {
  let argument: GeneratedContent
  let transcript: Transcript
}

final class GateRecorder: Sendable {
  private struct State {
    var session: LanguageModelSession?
    var calls: [CallCapture] = []
  }
  private let state = Mutex(State())
  func bind(_ session: LanguageModelSession) { state.withLock { $0.session = session } }
  func unbind() { state.withLock { $0.session = nil } }
  func record(_ argument: GeneratedContent) {
    let session = state.withLock { $0.session }
    guard let session else { return }
    let capture = CallCapture(argument: argument, transcript: session.transcript)
    state.withLock { $0.calls.append(capture) }
  }
  var calls: [CallCapture] { state.withLock { $0.calls } }
}

enum SyntheticToolError: Error { case unavailable }

struct GateTool: Tool {
  typealias Arguments = GeneratedContent
  let name = "lookup_code"
  let description = "Return the current synthetic code for one key. Every reading is a separate call."
  let parameters: GenerationSchema
  let recorder: GateRecorder
  let mode: String
  func call(arguments: GeneratedContent) async throws -> String {
    recorder.record(arguments)
    switch mode {
    case "throw": throw SyntheticToolError.unavailable
    case "error-result": return "{\"isError\":true,\"parts\":[{\"type\":\"text\",\"text\":\"SYNTHETIC_UNAVAILABLE_729\"}]}"
    case "error-marker": return "[Tool error]\nSYNTHETIC_UNAVAILABLE_729"
    case "rebuild", "rebuild-none": return "UNEXPECTED-REEXECUTION"
    default:
      try await Task.sleep(for: .seconds(30))
      return "UNEXPECTED-TIMEOUT"
    }
  }
}

func toolCalls(_ transcript: Transcript) -> [Transcript.ToolCall] {
  transcript.flatMap { entry in
    if case .toolCalls(let calls) = entry { return Array(calls) }
    return []
  }
}

func textSegment(_ text: String) -> Transcript.Segment { .text(.init(content: text)) }

func emit(_ object: [String: Any]) throws {
  let data = try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys])
  print(String(decoding: data, as: UTF8.self))
}

let mode = CommandLine.arguments.dropFirst().first ?? "batch-three"
guard case .available = SystemLanguageModel.default.availability else {
  print("{\"outcome\":\"unavailable\"}")
  exit(3)
}

do {
  let recorder = GateRecorder()
  defer { recorder.unbind() }
  let schema = try GenerationSchema(root: DynamicGenerationSchema(name: "LookupArguments", properties: [
    .init(name: "key", schema: DynamicGenerationSchema(type: String.self))
  ]), dependencies: [])
  let tool = GateTool(parameters: schema, recorder: recorder, mode: mode)
  let instructions = "Use lookup_code for each requested reading, even for identical keys. Request independent readings together. Report every returned value exactly."
  let options = GenerationOptions(temperature: 0, maximumResponseTokens: 256)

  if mode.hasPrefix("batch-") {
    let expected = mode == "batch-one" ? 1 : mode == "batch-two" ? 2 : 3
    let session = LanguageModelSession(tools: [tool], instructions: instructions)
    recorder.bind(session)
    let response = Task<Void, any Error> {
      _ = try await session.respond(to: "Take \(expected) independent readings of key sample, each in a separate lookup_code call with exactly key sample. Return every code.", options: options)
    }
    // Read the first callback's own snapshot; do not wait for an expected callback count
    // or a quiet interval to infer the batch boundary.
    for _ in 0..<15000 {
      if !recorder.calls.isEmpty { break }
      try await Task.sleep(for: .milliseconds(1))
    }
    let first = recorder.calls.first
    let captured = first.map { toolCalls($0.transcript) } ?? []
    let entryCounts = first?.transcript.compactMap { entry -> Int? in
      if case .toolCalls(let calls) = entry { return calls.count }
      return nil
    } ?? []
    let identityMatches = captured.filter { first?.argument.id != nil && $0.arguments.id == first?.argument.id }.count
    response.cancel()
    var cancellation = ""
    do { try await response.value } catch { cancellation = String(describing: error) }
    let complete = captured.allSatisfy { $0.arguments.isComplete }
    let observed = captured.count == expected && complete && identityMatches == 1
      && Set(captured.map(\.id)).count == expected && !cancellation.isEmpty && !session.isResponding
    try emit(["case": mode, "outcome": observed ? "observed" : "inconclusive",
      "firstCallbackBatchSizes": entryCounts, "capturedCallCount": captured.count,
      "argumentsComplete": complete, "callbackIdentityMatches": identityMatches,
      "callbackCountAtSettlement": recorder.calls.count, "cancelled": !cancellation.isEmpty,
      "idle": !session.isResponding])
    if !observed { exit(2) }
  } else if mode == "rebuild" || mode == "rebuild-none" || mode == "rebuild-next-batch" {
    // Rebuild solely from semantic host fields; retain no original Apple transcript,
    // generation IDs, asset IDs, private JSON or provider response-state payload.
    let calls = try ["host-call-a", "host-call-b"].map { id in
      Transcript.ToolCall(id: id, toolName: tool.name, arguments: try GeneratedContent(json: "{\"key\":\"sample\"}"))
    }
    let entries: [Transcript.Entry] = [
      .instructions(.init(segments: [textSegment(instructions)], toolDefinitions: mode == "rebuild-none" ? [] : [.init(tool: tool)])),
      .prompt(.init(segments: [textSegment("Remember the label GARDEN.")])),
      .response(.init(assetIDs: [], segments: [textSegment("I will remember GARDEN.")])),
      .prompt(.init(segments: [textSegment(mode != "rebuild-next-batch"
        ? "Take two readings of sample. Return both results and the remembered label."
        : "Take two readings of sample. After receiving both results, look up key followup once before answering.")])),
      .toolCalls(.init(calls)),
      .toolOutput(.init(id: "host-call-b", toolName: tool.name, segments: [textSegment("LANTERN-842")])),
      .toolOutput(.init(id: "host-call-a", toolName: tool.name, segments: [textSegment("ORCHID-729")]))
    ]
    let session = LanguageModelSession(tools: mode == "rebuild-none" ? [] : [tool], transcript: Transcript(entries: entries))
    recorder.bind(session)
    let restoredExactly = Array(session.transcript) == entries
    if mode != "rebuild-next-batch" {
      let reply = try await session.respond(to: Prompt(""), options: options)
      let preserved = Array(session.transcript.prefix(entries.count)) == entries
      let consumedToolResults = ["ORCHID-729", "LANTERN-842"].allSatisfy { reply.content.contains($0) }
      let consumed = ["GARDEN", "ORCHID-729", "LANTERN-842"].allSatisfy { reply.content.contains($0) }
      let observed = restoredExactly && preserved && consumed && recorder.calls.isEmpty && !session.isResponding
      try emit(["case": mode, "outcome": observed ? "observed" : "limitation", "restoredExactly": restoredExactly,
        "preserved": preserved, "consumedAll": consumed, "consumedToolResults": consumedToolResults,
        "recalledLabel": reply.content.contains("GARDEN"), "callbacks": recorder.calls.count, "reply": reply.content])
    } else {
      let response = Task<Void, any Error> { _ = try await session.respond(to: Prompt(""), options: options) }
      for _ in 0..<15000 {
        if !recorder.calls.isEmpty { break }
        try await Task.sleep(for: .milliseconds(1))
      }
      let snapshot = recorder.calls.first?.transcript ?? Transcript()
      let settled = Set(snapshot.compactMap { entry -> String? in
        if case .toolOutput(let output) = entry { return output.id }
        return nil
      })
      let pending = toolCalls(snapshot).filter { !settled.contains($0.id) }
      response.cancel()
      var cancellation = ""
      do { try await response.value } catch { cancellation = String(describing: error) }
      let key = try pending.first?.arguments.value(String.self, forProperty: "key")
      let observed = restoredExactly && pending.count == 1 && key == "followup"
        && settled == Set(["host-call-a", "host-call-b"]) && !cancellation.isEmpty && !session.isResponding
      try emit(["case": mode, "outcome": observed ? "observed" : "inconclusive",
        "restoredExactly": restoredExactly, "previousResults": settled.count,
        "newPendingCalls": pending.count, "nextKey": key ?? "", "cancelled": !cancellation.isEmpty,
        "idle": !session.isResponding])
      if !observed { exit(2) }
    }
  } else if mode == "throw" || mode == "error-result" || mode == "error-marker" {
    let session = LanguageModelSession(tools: [tool], instructions: instructions + " If a tool reports isError or [Tool error], report its error text and stop. Never retry.")
    recorder.bind(session)
    let before = session.transcript
    var reply = ""
    var typedToolError = false
    var underlyingMatches = false
    do { reply = try await session.respond(to: "Look up key sample once and report the result.", options: options).content }
    catch let error as LanguageModelSession.ToolCallError {
      typedToolError = true
      underlyingMatches = error.underlyingError is SyntheticToolError
    }
    let rollback = session.transcript == before
    let observed = recorder.calls.count == 1 && !session.isResponding && (mode == "throw"
      ? typedToolError && underlyingMatches && rollback
      : !typedToolError && reply.contains("SYNTHETIC_UNAVAILABLE_729") && !rollback)
    try emit(["case": mode, "outcome": observed ? "observed" : "limitation", "typedToolError": typedToolError,
      "underlyingMatches": underlyingMatches, "rolledBack": rollback, "callbacks": recorder.calls.count, "reply": reply])
  } else if mode == "structured-stream" {
    let output = try GenerationSchema(root: DynamicGenerationSchema(name: "Result", properties: [
      .init(name: "label", schema: DynamicGenerationSchema(type: String.self)),
      .init(name: "count", schema: DynamicGenerationSchema(type: Int.self, guides: [.range(1...5)])),
      .init(name: "tags", schema: DynamicGenerationSchema(arrayOf: DynamicGenerationSchema(type: String.self), minimumElements: 2, maximumElements: 2))
    ]), dependencies: [])
    let session = LanguageModelSession()
    var snapshots: [String] = []
    var parseable = true
    for try await snapshot in session.streamResponse(to: "Set label to café 🦊, count to 3 and tags to fresh and local.", schema: output, options: options) {
      let json = snapshot.rawContent.jsonString
      snapshots.append(json)
      parseable = parseable && (try? JSONSerialization.jsonObject(with: Data(json.utf8))) != nil
    }
    let appendOnly = zip(snapshots, snapshots.dropFirst()).allSatisfy { $1.hasPrefix($0) }
    let final = try snapshots.last.map { try JSONSerialization.jsonObject(with: Data($0.utf8)) as? [String: Any] } ?? nil
    // Schema validity and faithful copying of prompted values are separate observations.
    let count = final?["count"] as? Int
    let tags = final?["tags"] as? [String]
    let valid = final?["label"] is String && count.map { (1...5).contains($0) } == true && tags?.count == 2
    let requestedValuesMatched = final?["label"] as? String == "café 🦊" && count == 3 && tags == ["fresh", "local"]
    try emit(["case": mode, "outcome": snapshots.count > 1 && parseable && valid ? "observed" : "inconclusive",
      "snapshotCount": snapshots.count, "parseable": parseable, "jsonAppendOnly": appendOnly,
      "validFinal": valid, "requestedValuesMatched": requestedValuesMatched,
      "snapshots": snapshots, "idle": !session.isResponding])
    if snapshots.count <= 1 || !parseable || !valid { exit(2) }
  } else { throw NSError(domain: "Unknown experiment", code: 2) }
} catch {
  FileHandle.standardError.write(Data("Design gate probe failed: \(error)\n".utf8))
  exit(2)
}
