// DSA-59 prototype change, Copyright (c) 2026 Darkspyre Studios.
// Built only into the explicitly patched research dependency.
import Synchronization

/// One winner between host completion and task cancellation, including before install.
final class PendingToolCall: Sendable {
  private struct State {
    var continuation: CheckedContinuation<String, any Error>?
    var result: Result<String, any Error>?
  }

  private let state = Mutex(State())

  /// Returns false when cancellation/completion already won; no host call is needed.
  func install(_ continuation: CheckedContinuation<String, any Error>) -> Bool {
    let result = state.withLock { state -> Result<String, any Error>? in
      if let result = state.result { return result }
      precondition(state.continuation == nil, "A tool call installs one continuation")
      state.continuation = continuation
      return nil
    }
    if let result {
      continuation.resume(with: result)
      return false
    }
    return true
  }

  func finish(_ result: Result<String, any Error>) {
    let continuation = state.withLock { state -> CheckedContinuation<String, any Error>? in
      guard state.result == nil else { return nil }
      state.result = result
      let continuation = state.continuation
      state.continuation = nil
      return continuation
    }
    // Resumption may run arbitrary code; never resume under the state lock.
    continuation?.resume(with: result)
  }
}
