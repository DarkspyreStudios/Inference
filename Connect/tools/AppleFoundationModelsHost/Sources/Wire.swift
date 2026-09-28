import Darwin
import Foundation
import Synchronization

struct HostFailure: Error { let code: String }

final class SchemaNode: Codable, Sendable {
  let kind: String
  let description: String?
  let properties: [SchemaProperty]?
  let items: SchemaNode?
  let choices: [String]?
  let minimum: String?
  let maximum: String?
  let minItems: Int?
  let maxItems: Int?
}
struct SchemaProperty: Codable, Sendable {
  let name: String
  let required: Bool
  let schema: SchemaNode
}
struct WireTool: Codable, Sendable {
  let name: String
  let description: String?
  let schema: SchemaNode
}
struct WireCall: Codable, Sendable, Equatable {
  let id: String
  let name: String
  let arguments: String
}
struct WireResult: Codable, Sendable {
  let id: String
  let name: String
  let text: String
}
struct WireMessage: Codable, Sendable {
  let role: String
  let content: String
  let calls: [WireCall]
  let results: [WireResult]
}
struct GenerationRequest: Codable, Sendable {
  let instructions: String?
  let messages: [WireMessage]
  let tools: [WireTool]
  let schema: SchemaNode?
  let temperature: Double?
  let maximumResponseTokens: Int?
}
struct Command: Codable, Sendable {
  let command: String
  let id: String
  let request: GenerationRequest?
}
struct Hello: Encodable {
  let type = "hello"
  let `protocol` = "darkspyre.apple-fm"
  let major = 1
  let helperVersion = "1.0.0-dev"
  let osVersion = ProcessInfo.processInfo.operatingSystemVersionString
  let availability: String
  let contextSize: Int
}
struct Event: Encodable {
  let type: String
  let id: String
  var sequence: Int = 0
  var content: String? = nil
  var status: String? = nil
  var code: String? = nil
  var calls: [WireCall]? = nil
}

// The pipe carries opaque user/model content. It is encoded as JSON data, never evaluated,
// printed as diagnostics, persisted or interpolated into commands.
final class EventWriter: Sendable {
  private struct State {
    var sequence = 0
    var terminal = false
  }
  private let state = Mutex(State())
  private let output: FileHandle
  let maximumBytes: Int
  init(output: FileHandle = .standardOutput, maximumBytes: Int = 1_048_576) {
    self.output = output
    self.maximumBytes = maximumBytes
  }
  func hello(_ hello: Hello) throws { try write(hello) }
  func emit(_ event: Event) throws {
    try state.withLock { state in
      guard !state.terminal else { throw HostFailure(code: "protocol_failure") }
      var next = event
      state.sequence += 1
      next.sequence = state.sequence
      try write(next)
      if event.type == "terminal" { state.terminal = true }
    }
  }
  private func write(_ value: some Encodable) throws {
    var data = try JSONEncoder().encode(value)
    guard data.count <= maximumBytes else { throw HostFailure(code: "protocol_failure") }
    data.append(10)
    try output.write(contentsOf: data)
  }
}

// One detached reader owns this object; a bounded byte buffer prevents readLine from
// allocating an unlimited string when the peer fails to delimit its frame.
final class CommandReader: @unchecked Sendable {
  private let input: FileHandle
  private let maximumBytes: Int
  private var buffer = Data()
  init(input: FileHandle = .standardInput, maximumBytes: Int) {
    self.input = input
    self.maximumBytes = maximumBytes
  }
  func next() throws -> Command? {
    while true {
      if let newline = buffer.firstIndex(of: 10) {
        let data = buffer.prefix(upTo: newline)
        guard data.count <= maximumBytes else { throw HostFailure(code: "protocol_failure") }
        let command = try JSONDecoder().decode(Command.self, from: data)
        buffer.removeSubrange(...newline)
        return command
      }
      guard buffer.count <= maximumBytes else { throw HostFailure(code: "protocol_failure") }
      // FileHandle.read(upToCount:) may wait to fill its request on a pipe. POSIX read
      // returns currently available bytes, so a complete frame works while stdin stays
      // open for later cancellation.
      var bytes = [UInt8](repeating: 0, count: 4096)
      let count = Darwin.read(input.fileDescriptor, &bytes, bytes.count)
      if count < 0 {
        if errno == EINTR { continue }
        throw HostFailure(code: "protocol_failure")
      }
      if count == 0 {
        if !buffer.isEmpty { throw HostFailure(code: "protocol_failure") }
        return nil
      }
      buffer.append(contentsOf: bytes.prefix(count))
    }
  }
}
