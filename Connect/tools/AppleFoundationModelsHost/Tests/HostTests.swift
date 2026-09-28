import Foundation
import FoundationModels
import Testing

@testable import AppleFoundationModelsHost

@Suite(.timeLimit(.minutes(1)))
struct HostTests {
  @Test func cancellationRacesRegistration() async {
    await withTaskGroup(of: Void.self) { group in
      for _ in 0..<1000 {
        group.addTask {
          let pending = PendingToolCall()
          let reader = Task {
            try await withCheckedThrowingContinuation { continuation in
              _ = pending.install(continuation)
            }
          }
          pending.finish(.failure(CancellationError()))
          pending.finish(.success("late"))
          do {
            _ = try await reader.value
            Issue.record("Cancellation must win")
          } catch { #expect(error is CancellationError) }
        }
      }
    }
  }

  @Test func batchUsesGenerationIdentityAndExcludesOldEntries() throws {
    let first = GeneratedContent(properties: ["key": "sample"], id: GenerationID())
    let second = GeneratedContent(properties: ["key": "sample"], id: GenerationID())
    let transcript = Transcript(entries: [
      .toolCalls(
        .init([
          .init(
            id: "old", toolName: "lookup",
            arguments: GeneratedContent(properties: ["key": "sample"]))
        ])),
      .toolOutput(.init(id: "old", toolName: "lookup", segments: [History.text("old result")])),
      .toolCalls(
        .init([
          .init(id: "a", toolName: "lookup", arguments: first),
          .init(id: "b", toolName: "lookup", arguments: second),
        ])),
    ])
    let calls = try Operation.batch(in: transcript, argument: second, names: ["lookup"])
    #expect(calls.map(\.id) == ["a", "b"])
    #expect(throws: HostFailure.self) {
      try Operation.batch(
        in: transcript, argument: GeneratedContent(properties: ["key": "sample"]), names: ["lookup"]
      )
    }
    #expect(throws: HostFailure.self) {
      try Operation.batch(in: transcript, argument: second, names: [])
    }
  }

  @Test func cancellationWinsBeforeTerminalCommit() throws {
    let pipe = Pipe()
    let writer = EventWriter(output: pipe.fileHandleForWriting)
    let operation = AppleFoundationModelsHost.Operation()
    operation.cancel()
    try operation.finish(defaultStatus: "unknown", content: "partial", id: "r", writer: writer)
    try pipe.fileHandleForWriting.close()
    let object =
      try JSONSerialization.jsonObject(with: pipe.fileHandleForReading.readDataToEndOfFile())
      as? [String: Any]
    #expect(object?["status"] as? String == "error")
    #expect(object?["code"] as? String == "cancelled")
    #expect(throws: HostFailure.self) {
      try operation.finish(defaultStatus: "unknown", content: "", id: "r", writer: writer)
    }
  }

  @Test func typedHistoryPreservesEmptyMessagesAndCorrelatesResults() throws {
    let request = GenerationRequest(
      instructions: "system",
      messages: [
        WireMessage(role: "user", content: "", calls: [], results: []),
        WireMessage(
          role: "assistant", content: "looking",
          calls: [WireCall(id: "a", name: "lookup", arguments: "{\"key\":\"sample\"}")], results: []
        ),
        WireMessage(
          role: "user", content: "", calls: [],
          results: [WireResult(id: "a", name: "lookup", text: "done")]),
      ], tools: [], schema: nil, temperature: nil, maximumResponseTokens: nil)
    let (transcript, _) = try History.build(request, tools: [])
    #expect(transcript.count == 5)
    guard case .prompt(let prompt) = transcript[1], case .text(let segment) = prompt.segments.first
    else {
      Issue.record("Empty user message was removed")
      return
    }
    #expect(segment.content.isEmpty)
    let invalid = GenerationRequest(
      instructions: nil,
      messages: [
        WireMessage(
          role: "user", content: "", calls: [],
          results: [WireResult(id: "missing", name: "lookup", text: "done")])
      ], tools: [], schema: nil, temperature: nil, maximumResponseTokens: nil)
    #expect(throws: HostFailure.self) { try History.build(invalid, tools: []) }
  }

  @Test func nestedPlanBuildsWithPublicConstructors() throws {
    let json =
      #"{"kind":"object","properties":[{"name":"nested","required":true,"schema":{"kind":"array","minItems":1,"maxItems":2,"items":{"kind":"object","properties":[{"name":"number","required":true,"schema":{"kind":"integer","minimum":"1","maximum":"5"}}]}}}]}"#
    let node = try JSONDecoder().decode(SchemaNode.self, from: Data(json.utf8))
    let schema = try Schemas.build(node)
    #expect(!(try JSONEncoder().encode(schema)).isEmpty)
  }

  @Test func oversizedInputIsRejected() throws {
    let pipe = Pipe()
    try pipe.fileHandleForWriting.write(contentsOf: Data(repeating: 65, count: 32))
    try pipe.fileHandleForWriting.close()
    let reader = CommandReader(input: pipe.fileHandleForReading, maximumBytes: 16)
    #expect(throws: HostFailure.self) { try reader.next() }
  }

  @Test func capturedBatchSettlesBeforeHandoffAndCancellationCanWin() async throws {
    for cancel in [false, true] {
      let argument = GeneratedContent(properties: ["key": "sample"], id: GenerationID())
      let transcript = Transcript(entries: [
        .toolCalls(
          .init([
            .init(id: "first", toolName: "lookup", arguments: argument),
            .init(
              id: "second", toolName: "lookup",
              arguments: GeneratedContent(properties: ["key": "sample"], id: GenerationID())),
          ]))
      ])
      let operation = AppleFoundationModelsHost.Operation()
      operation.bind(LanguageModelSession(transcript: transcript))
      try operation.capture(argument, names: ["lookup"])
      #expect(throws: HostFailure.self) { try operation.capture(argument, names: ["lookup"]) }
      if cancel { operation.cancel() }
      let pipe = Pipe()
      try operation.finish(
        defaultStatus: "error", code: "cancelled", content: "", id: "r",
        writer: EventWriter(output: pipe.fileHandleForWriting))
      try pipe.fileHandleForWriting.close()
      let lines = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        .split(separator: "\n")
      #expect(lines.count == (cancel ? 1 : 2))
      let last = try #require(lines.last)
      let terminal = try #require(
        try JSONSerialization.jsonObject(with: Data(last.utf8)) as? [String: Any])
      #expect(terminal["status"] as? String == (cancel ? "error" : "requiresAction"))
      let task = Task<Void, Never> { while !Task.isCancelled { await Task.yield() } }
      operation.install(task)  // Finishing before task installation must not retain an active task.
      await task.value
    }
  }

  @Test(arguments: [
    #"{"kind":"string","choices":["a","b"]}"#,
    #"{"kind":"string","choices":["constant"]}"#,
    #"{"kind":"boolean"}"#,
    #"{"kind":"integer","minimum":"-9223372036854775808","maximum":"9223372036854775807"}"#,
    #"{"kind":"number","minimum":"-0.5","maximum":"1.5"}"#,
    #"{"kind":"array","items":{"kind":"string"},"minItems":0,"maxItems":0}"#,
    #"{"kind":"object","properties":[{"name":"optional","required":false,"schema":{"kind":"string"}}]}"#,
  ]) func admittedPlansBuild(json: String) throws {
    let plan = try JSONDecoder().decode(SchemaNode.self, from: Data(json.utf8))
    _ = try Schemas.build(plan)
  }

  @Test(arguments: [
    #"{"kind":"string","choices":[]}"#,
    #"{"kind":"array","items":{"kind":"string"},"minItems":2,"maxItems":1}"#,
    #"{"kind":"array","items":{"kind":"string"},"minItems":-1}"#,
    #"{"kind":"integer","minimum":"1.5"}"#,
    #"{"kind":"number","minimum":"nan"}"#,
    #"{"kind":"number","minimum":"5","maximum":"1"}"#,
  ]) func malformedPlansFailBeforeNativeConstruction(json: String) throws {
    let plan = try JSONDecoder().decode(SchemaNode.self, from: Data(json.utf8))
    #expect(throws: HostFailure.self) { try Schemas.build(plan) }
  }

  @Test func completeCommandDoesNotWaitForInputEOF() async throws {
    let pipe = Pipe()
    let data = Data(#"{"command":"probe","id":"r"}"#.utf8) + Data([10])
    try pipe.fileHandleForWriting.write(contentsOf: data)
    // Close only as a bounded escape from the old fill-buffer behavior.
    let closer = Task.detached {
      do {
        try await Task.sleep(for: .seconds(1))
        try pipe.fileHandleForWriting.close()
      } catch {}
    }
    let start = ContinuousClock.now
    let command = try CommandReader(input: pipe.fileHandleForReading, maximumBytes: 1024).next()
    let elapsed = start.duration(to: .now)
    closer.cancel()
    _ = await closer.result
    try? pipe.fileHandleForWriting.close()
    #expect(command?.id == "r")
    #expect(elapsed < .milliseconds(500))
  }

}
