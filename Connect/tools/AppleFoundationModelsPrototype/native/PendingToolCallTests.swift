import Testing
@testable import FoundationModelsCBindings

@Suite(.timeLimit(.minutes(1)))
struct PendingToolCallTests {
  @Test func immediateCompletionAndDuplicateResult() async throws {
    let pending = PendingToolCall()
    let value: String = try await withCheckedThrowingContinuation { continuation in
      #expect(pending.install(continuation))
      pending.finish(.success("first"))
      pending.finish(.success("duplicate"))
      pending.finish(.failure(CancellationError()))
    }
    #expect(value == "first")
  }

  @Test func cancellationBeforeRegistrationSkipsDispatch() async {
    let pending = PendingToolCall()
    pending.finish(.failure(CancellationError()))
    await #expect(throws: CancellationError.self) {
      let _: String = try await withCheckedThrowingContinuation { continuation in
        #expect(!pending.install(continuation))
      }
    }
  }

  @Test func cancellationWhileWaitingIgnoresLateResults() async {
    let pending = PendingToolCall()
    await #expect(throws: CancellationError.self) {
      let _: String = try await withCheckedThrowingContinuation { continuation in
        #expect(pending.install(continuation))
        pending.finish(.failure(CancellationError()))
        pending.finish(.success("late result"))
        pending.finish(.failure(CancellationError()))
      }
    }
  }

  @Test func completionRacesCancellationAndRegistration() async {
    // Exercises both install-before-finish and finish-before-install, with two finishers.
    // A double continuation resume crashes the test process; a lost one hits the suite timeout.
    await withTaskGroup(of: Void.self) { group in
      for _ in 0..<1000 {
        group.addTask {
          let pending = PendingToolCall()
          let reader = Task {
            try await withCheckedThrowingContinuation { continuation in
              _ = pending.install(continuation)
            }
          }
          await withTaskGroup(of: Void.self) { finishers in
            finishers.addTask { pending.finish(.success("completed")) }
            finishers.addTask { pending.finish(.failure(CancellationError())) }
          }
          do { #expect(try await reader.value == "completed") }
          catch { #expect(error is CancellationError) }
        }
      }
    }
  }
}
