import Foundation
import FoundationModels
import Synchronization

// Owns cancellation intent independently of Apple's isResponding and wrapped cancellation errors.
final class Operation: Sendable {
  private struct State {
    var session: LanguageModelSession?
    var task: Task<Void, Never>?
    var batch: [WireCall]?
    var seen: Set<String> = []
    var hostCancelled = false
    var failure: String?
    var terminal = false
  }
  private let state = Mutex(State())
  func bind(_ session: LanguageModelSession) { state.withLock { $0.session = session } }
  func install(_ task: Task<Void, Never>) {
    let cancel = state.withLock { value in
      guard !value.terminal else { return true }
      value.task = task
      return value.batch != nil || value.hostCancelled || value.failure != nil
    }
    if cancel { task.cancel() }
  }
  func cancel(failure: String? = nil) {
    let task = state.withLock { value -> Task<Void, Never>? in
      guard !value.terminal else { return nil }
      if let failure { value.failure = failure } else { value.hostCancelled = true }
      return value.task
    }
    task?.cancel()
  }

  static func batch(in transcript: Transcript, argument: GeneratedContent, names: Set<String>)
    throws -> [WireCall]
  {
    guard let argumentID = argument.id else { throw HostFailure(code: "protocol_failure") }
    let outputs = Set(
      transcript.compactMap { entry -> String? in
        if case .toolOutput(let output) = entry { return output.id }
        return nil
      })
    let entries = transcript.compactMap { entry -> Transcript.ToolCalls? in
      if case .toolCalls(let calls) = entry,
        calls.contains(where: { $0.arguments.id == argumentID })
      {
        return calls
      }
      return nil
    }
    guard entries.count == 1, let calls = entries.first,
      calls.filter({ $0.arguments.id == argumentID }).count == 1,
      !calls.isEmpty, Set(calls.map(\.id)).count == calls.count,
      calls.allSatisfy({
        !outputs.contains($0.id) && !$0.id.isEmpty && names.contains($0.toolName)
          && $0.arguments.isComplete
      })
    else { throw HostFailure(code: "protocol_failure") }
    return calls.map { WireCall(id: $0.id, name: $0.toolName, arguments: $0.arguments.jsonString) }
  }

  func capture(_ argument: GeneratedContent, names: Set<String>) throws {
    guard let session = state.withLock({ $0.session }) else {
      throw HostFailure(code: "protocol_failure")
    }
    let transcript = session.transcript
    let calls = try Self.batch(in: transcript, argument: argument, names: names)
    let identity = transcript.flatMap { entry -> [Transcript.ToolCall] in
      if case .toolCalls(let calls) = entry { return Array(calls) }
      return []
    }.first { $0.arguments.id == argument.id }?.id
    let task = try state.withLock { value -> Task<Void, Never>? in
      guard !value.terminal, let identity, value.seen.insert(identity).inserted else {
        throw HostFailure(code: "protocol_failure")
      }
      if let existing = value.batch, existing != calls {
        throw HostFailure(code: "protocol_failure")
      }
      value.batch = calls
      return value.task
    }
    task?.cancel()
  }

  // Called only after the native async operation has unwound. Commit under the same lock as
  // host cancellation, then break the session/tool/coordinator reference cycle.
  func finish(
    defaultStatus: String, code: String? = nil, content: String, id: String, writer: EventWriter
  ) throws {
    try state.withLock { value in
      guard !value.terminal else { throw HostFailure(code: "protocol_failure") }
      let failure = value.hostCancelled ? "cancelled" : value.failure
      if let failure {
        try writer.emit(Event(type: "terminal", id: id, status: "error", code: failure))
      } else if let batch = value.batch {
        try writer.emit(Event(type: "batch", id: id, calls: batch))
        try writer.emit(Event(type: "terminal", id: id, content: content, status: "requiresAction"))
      } else {
        try writer.emit(
          Event(type: "terminal", id: id, content: content, status: defaultStatus, code: code))
      }
      value.terminal = true
      value.session = nil
      value.task = nil
    }
  }
}

struct CaptureTool: Tool {
  typealias Arguments = GeneratedContent
  let name: String
  let description: String
  let parameters: GenerationSchema
  let operation: Operation
  let toolNames: Set<String>

  func call(arguments: GeneratedContent) async throws -> String {
    let pending = PendingToolCall()
    return try await withTaskCancellationHandler {
      try await withCheckedThrowingContinuation { continuation in
        guard pending.install(continuation) else { return }
        do { try operation.capture(arguments, names: toolNames) } catch {
          operation.cancel(failure: "protocol_failure")
          pending.finish(.failure(error))
        }
      }
    } onCancel: {
      pending.finish(.failure(CancellationError()))
    }
  }
}
