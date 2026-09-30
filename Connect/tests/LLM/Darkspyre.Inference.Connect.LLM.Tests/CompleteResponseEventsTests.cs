using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class CompleteResponseEventsTests
{
    [Fact]
    public void EmitsReasoningTextRefusalCallsThenTheCompletion()
    {
        LlmToolCall first = new("call-1", "read_file", """{"path":"a.txt"}""");
        LlmToolCall second = new("call-2", "list_files", "{}");
        LlmChatResult result = Result("Reading.", refusal: "Partly refused.", calls: [first, second]);

        LlmStreamEvent[] events = [.. CompleteResponseEvents.From(result, [new("Thinking.", true), new("More.", false)])];

        Assert.Equal<LlmStreamEvent>(
            [
                new LlmReasoningDeltaEvent("Thinking.", true),
                new LlmReasoningDeltaEvent("More.", false),
                new LlmTextDeltaEvent("Reading."),
                new LlmRefusalDeltaEvent("Partly refused."),
                new LlmToolCallStartedEvent("call-1", "read_file", 0),
                new LlmToolCallArgumentsDeltaEvent("call-1", """{"path":"a.txt"}"""),
                new LlmToolCallCompletedEvent(first),
                new LlmToolCallStartedEvent("call-2", "list_files", 1),
                new LlmToolCallArgumentsDeltaEvent("call-2", "{}"),
                new LlmToolCallCompletedEvent(second),
                new LlmChatCompletionEvent(result)
            ],
            events);
    }

    [Fact]
    public void SkipsEmptyReasoningTextAndArguments()
    {
        LlmToolCall call = new("call-1", "ping", string.Empty);
        LlmChatResult result = Result(string.Empty, calls: [call]);

        LlmStreamEvent[] events = [.. CompleteResponseEvents.From(result, [new(string.Empty, false)])];

        Assert.Equal<LlmStreamEvent>(
            [
                new LlmToolCallStartedEvent("call-1", "ping", 0),
                new LlmToolCallCompletedEvent(call),
                new LlmChatCompletionEvent(result)
            ],
            events);
    }

    [Fact]
    public void ARefusalEqualToTheTextIsEmittedOnceAsText()
    {
        LlmChatResult result = Result("I cannot help with that.", refusal: "I cannot help with that.");

        LlmStreamEvent[] events = [.. CompleteResponseEvents.From(result, [])];

        Assert.Equal<LlmStreamEvent>(
            [new LlmTextDeltaEvent("I cannot help with that."), new LlmChatCompletionEvent(result)],
            events);
    }

    private static LlmChatResult Result(string text, string? refusal = null, IReadOnlyList<LlmToolCall>? calls = null) => new()
    {
        Text = text,
        Refusal = refusal,
        Status = calls is { Count: > 0 } ? LlmCompletionStatus.RequiresAction : LlmCompletionStatus.Completed,
        ToolCalls = calls ?? [],
        Diagnostics = new LlmProviderDiagnostics(200, null, null)
    };
}
