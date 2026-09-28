import Foundation
import FoundationModels

enum History {
  static func text(_ value: String) -> Transcript.Segment { .text(.init(content: value)) }

  static func build(_ request: GenerationRequest, tools: [CaptureTool]) throws -> (
    Transcript, Prompt
  ) {
    var entries: [Transcript.Entry] = [
      .instructions(
        .init(
          segments: request.instructions.map { [text($0)] } ?? [],
          toolDefinitions: tools.map { .init(tool: $0) }))
    ]
    var callNames: [String: String] = [:]
    var pending: Set<String> = []
    var prompt = Prompt("")
    for (index, message) in request.messages.enumerated() {
      guard message.role == "user" || message.role == "assistant" else {
        throw HostFailure(code: "unsupported_feature")
      }
      if !pending.isEmpty && message.results.isEmpty { throw HostFailure(code: "invalid_request") }
      if !message.calls.isEmpty {
        guard message.role == "assistant", message.results.isEmpty else {
          throw HostFailure(code: "invalid_request")
        }
        if !message.content.isEmpty {
          entries.append(.response(.init(assetIDs: [], segments: [text(message.content)])))
        }
        var calls: [Transcript.ToolCall] = []
        for call in message.calls {
          guard !call.id.isEmpty, callNames[call.id] == nil else {
            throw HostFailure(code: "invalid_request")
          }
          callNames[call.id] = call.name
          pending.insert(call.id)
          calls.append(
            .init(
              id: call.id, toolName: call.name,
              arguments: try GeneratedContent(json: call.arguments)))
        }
        entries.append(.toolCalls(.init(calls)))
      } else if !message.results.isEmpty {
        guard message.role == "user" else { throw HostFailure(code: "invalid_request") }
        for result in message.results {
          guard pending.remove(result.id) != nil, callNames[result.id] == result.name else {
            throw HostFailure(code: "invalid_request")
          }
          entries.append(
            .toolOutput(.init(id: result.id, toolName: result.name, segments: [text(result.text)])))
        }
        if !message.content.isEmpty {
          entries.append(.prompt(.init(segments: [text(message.content)])))
        }
      } else if message.role == "assistant" {
        entries.append(.response(.init(assetIDs: [], segments: [text(message.content)])))
      } else if index == request.messages.count - 1 {
        prompt = Prompt(message.content)
      } else {
        entries.append(.prompt(.init(segments: [text(message.content)])))
      }
    }
    guard pending.isEmpty else { throw HostFailure(code: "invalid_request") }
    return (Transcript(entries: entries), prompt)
  }
}
