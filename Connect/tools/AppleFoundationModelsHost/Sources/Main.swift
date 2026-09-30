import Darwin
import Foundation
import FoundationModels

@main struct HostMain {
  static func main() async {
    signal(SIGPIPE, SIG_IGN)
    let arguments = CommandLine.arguments
    guard arguments.count == 3, arguments[1] == "--max-frame-bytes",
      let maximum = Int(arguments[2]), maximum > 0, maximum <= 16_777_216
    else { exit(2) }
    let writer = EventWriter(maximumBytes: maximum)
    let reader = CommandReader(maximumBytes: maximum)
    do {
      let model = SystemLanguageModel.default
      let availability: String
      switch model.availability {
      case .available: availability = "available"
      case .unavailable(let reason):
        switch reason {
        case .deviceNotEligible: availability = "deviceNotEligible"
        case .appleIntelligenceNotEnabled: availability = "appleIntelligenceNotEnabled"
        case .modelNotReady: availability = "modelNotReady"
        @unknown default: availability = "unavailable"
        }
      }
      try writer.hello(Hello(availability: availability, contextSize: model.contextSize))
      guard let command = try await Task.detached(operation: { try reader.next() }).value,
        !command.id.isEmpty
      else { exit(2) }
      if command.command == "probe" {
        try writer.emit(Event(type: "terminal", id: command.id, status: "ready"))
        return
      }
      guard command.command == "generate", let request = command.request else {
        try writer.emit(
          Event(type: "terminal", id: command.id, status: "error", code: "invalid_request"))
        return
      }
      guard availability == "available" else {
        try writer.emit(
          Event(type: "terminal", id: command.id, status: "error", code: "model_unavailable"))
        return
      }
      let operation = Operation()
      let task = Task<Void, Never> {
        await generate(request, id: command.id, operation: operation, writer: writer)
      }
      operation.install(task)
      // stdin remains open for cancellation. This single detached reader ends with the process;
      // it owns no native session and cannot keep inference alive after main returns.
      let control = Task.detached {
        do {
          while let next = try reader.next() {
            if next.id != command.id || next.command != "cancel" {
              operation.cancel(failure: "protocol_failure")
            } else {
              operation.cancel()
            }
          }
          operation.cancel()
        } catch { operation.cancel(failure: "protocol_failure") }
      }
      await task.value
      control.cancel()
    } catch {
      // Raw framework/decoder errors may contain user content. Never send them to stderr.
      exit(2)
    }
  }

  private static func generate(
    _ request: GenerationRequest, id: String, operation: Operation, writer: EventWriter
  ) async {
    var content = ""
    var status = "unknown"
    var code: String?
    do {
      let options = try request.generationOptions()
      let names = Set(request.tools.map(\.name))
      guard names.count == request.tools.count else { throw HostFailure(code: "invalid_request") }
      let tools = try request.tools.map { tool in
        CaptureTool(
          name: tool.name, description: tool.description ?? "",
          parameters: try Schemas.build(tool.schema),
          operation: operation, toolNames: names)
      }
      let (transcript, prompt) = try History.build(request, tools: tools)
      let output = try request.schema.map { try Schemas.build($0) }
      let session = LanguageModelSession(tools: tools, transcript: transcript)
      operation.bind(session)
      try Task.checkCancellation()
      let streams = request.stream ?? true
      if let output, streams {
        for try await snapshot in session.streamResponse(
          to: prompt, schema: output, options: options)
        {
          try Task.checkCancellation()
          content = snapshot.rawContent.jsonString
          try writer.emit(Event(type: "structured", id: id, content: content))
        }
      } else if let output {
        let response = try await session.respond(to: prompt, schema: output, options: options)
        try Task.checkCancellation()
        content = response.rawContent.jsonString
        try writer.emit(Event(type: "structured", id: id, content: content))
      } else if streams {
        for try await snapshot in session.streamResponse(to: prompt, options: options) {
          try Task.checkCancellation()
          content = snapshot.content
          try writer.emit(Event(type: "text", id: id, content: content))
        }
      } else {
        let response = try await session.respond(to: prompt, options: options)
        try Task.checkCancellation()
        content = response.content
        try writer.emit(Event(type: "text", id: id, content: content))
      }
      try Task.checkCancellation()
    } catch let error as HostFailure {
      status = "error"
      code = error.code
    } catch let error as LanguageModelSession.GenerationError {
      status = "error"
      switch error {
      case .exceededContextWindowSize: code = "context_length_exceeded"
      case .assetsUnavailable: code = "model_unavailable"
      case .guardrailViolation:
        status = "refused"
        code = "guardrail_violation"
      case .refusal:
        status = "refused"
        code = "refusal"
      case .unsupportedGuide: code = "unsupported_feature"
      case .unsupportedLanguageOrLocale: code = "unsupported_language"
      case .rateLimited: code = "capacity_exhausted"
      case .concurrentRequests: code = "protocol_failure"
      case .decodingFailure: code = "decoding_failure"
      @unknown default: code = "execution_failed"
      }
    } catch is LanguageModelSession.ToolCallError {
      status = "error"
      code = "tool_call_failed"
    } catch is DecodingError {
      status = "error"
      code = "decoding_failure"
    } catch is GenerationSchema.SchemaError {
      status = "error"
      code = "unsupported_feature"
    } catch is CancellationError {
      status = "error"
      code = "cancelled"
    } catch {
      status = "error"
      code = "execution_failed"
    }
    do {
      try operation.finish(
        defaultStatus: status, code: code, content: content, id: id, writer: writer)
    } catch { exit(2) }
  }
}
