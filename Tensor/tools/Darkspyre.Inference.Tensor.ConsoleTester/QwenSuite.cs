using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Tensor.ConsoleTester;

/// <summary>The test calls: a chat, thinking, a tool round trip and structured output, each streamed to the console.</summary>
internal sealed class QwenSuite(ILlmInference engine, string modelId)
{
    private static readonly JsonElement TimeSchema = JsonElement.Parse("""{"type":"object","properties":{},"additionalProperties":false}""");
    private static readonly JsonElement CitySchema = JsonElement.Parse("""
        {"type":"object","properties":{"city":{"type":"string"},"country":{"type":"string"},"population":{"type":"integer"}},
         "required":["city","country","population"],"additionalProperties":false}
        """);

    public async Task<bool> RunAsync(ConsoleOptions options, CancellationToken cancellationToken)
    {
        if (options.Prompt is not null)
        {
            return (await StreamAsync("prompt", Request(options, options.Prompt), cancellationToken)) is not null;
        }

        bool passed = true;
        foreach (string test in options.Suite)
        {
            passed &= test switch
            {
                "chat" => await ChatAsync(options, cancellationToken),
                "think" => await ThinkAsync(options, cancellationToken),
                "tools" => await ToolsAsync(options, cancellationToken),
                "json" => await JsonAsync(options, cancellationToken),
                _ => throw new InvalidOperationException(test)
            };
        }

        Console.WriteLine(passed ? "All test calls passed." : "Some test calls failed.");
        return passed;
    }

    private async Task<bool> ChatAsync(ConsoleOptions options, CancellationToken cancellationToken)
    {
        LlmInferenceResult? result = await StreamAsync("chat", Request(options, "In one sentence, what is a GGUF file?"), cancellationToken);
        return Check(result is { Text.Length: > 0 }, "the answer is empty");
    }

    private async Task<bool> ThinkAsync(ConsoleOptions options, CancellationToken cancellationToken)
    {
        LlmInferenceRequest request = Request(options, "A bat and a ball cost 1.10 in total. The bat costs 1.00 more than the ball. What does the ball cost?")
            with
        { ReasoningLevel = LlmReasoningLevel.High, MaxOutputTokens = Math.Max(options.MaxTokens, 2048) };
        LlmInferenceResult? result = await StreamAsync("think", request, cancellationToken);
        return Check(result is { Text.Length: > 0 }, "the answer is empty");
    }

    private async Task<bool> ToolsAsync(ConsoleOptions options, CancellationToken cancellationToken)
    {
        LlmToolDefinition getTime = new() { Name = "get_time", Description = "Returns the current local date and time.", InputSchema = TimeSchema };
        LlmInferenceRequest request = Request(options, "What time is it right now? Use the get_time tool.") with { Tools = [getTime] };
        LlmInferenceResult? first = await StreamAsync("tools: call", request, cancellationToken);
        if (!Check(first is { ToolCalls.Count: > 0 }, "the model made no tool call")) { return false; }

        LlmToolCall call = first!.ToolCalls[0];
        string now = DateTime.Now.ToString("F", CultureInfo.InvariantCulture);
        Console.WriteLine($"  -> ran {call.Name}({call.ArgumentsJson}) = {now}");
        LlmInferenceRequest followUp = request with
        {
            Messages =
            [
                .. request.Messages,
                new("assistant", first.Text) { ToolCalls = first.ToolCalls },
                new("user", string.Empty) { ToolResults = [new(call.Id, LlmToolOutput.FromText(now))] }
            ]
        };
        LlmInferenceResult? answer = await StreamAsync("tools: answer", followUp, cancellationToken);
        return Check(answer is { Text.Length: > 0 }, "the answer after the tool result is empty");
    }

    private async Task<bool> JsonAsync(ConsoleOptions options, CancellationToken cancellationToken)
    {
        LlmInferenceRequest request = Request(options, "Describe the capital of France.") with
        {
            StructuredOutput = new() { Name = "city", Schema = CitySchema }
        };
        LlmInferenceResult? result = await StreamAsync("json", request, cancellationToken);
        return Check(result?.StructuredOutput is { IsValid: true }, "the output does not match the schema");
    }

    private LlmInferenceRequest Request(ConsoleOptions options, string prompt) => new()
    {
        ModelId = modelId,
        Messages = [new("user", prompt)],
        MaxOutputTokens = options.MaxTokens,
        ReasoningLevel = LlmReasoningLevel.None
    };

    /// <summary>Streams one request: thinking in brackets, the answer as it arrives, then timing and usage.</summary>
    private async Task<LlmInferenceResult?> StreamAsync(string name, LlmInferenceRequest request, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"== {name}");
        Stopwatch clock = Stopwatch.StartNew();
        TimeSpan? firstToken = null;
        bool thinking = false;
        try
        {
            await foreach (LlmStreamEvent item in engine.StreamAsync(request, cancellationToken))
            {
                firstToken ??= item is LlmTextDeltaEvent or LlmReasoningDeltaEvent ? clock.Elapsed : null;
                switch (item)
                {
                    case LlmReasoningDeltaEvent reasoning:
                        if (!thinking) { Console.Write("[thinking] "); thinking = true; }
                        Console.Write(reasoning.Delta);
                        break;
                    case LlmTextDeltaEvent text:
                        if (thinking) { Console.WriteLine(" [/thinking]"); thinking = false; }
                        Console.Write(text.Delta);
                        break;
                    case LlmToolCallCompletedEvent tool:
                        Console.WriteLine();
                        Console.Write($"[tool call] {tool.ToolCall.Name}({tool.ToolCall.ArgumentsJson})");
                        break;
                    case LlmCompletionEvent completion:
                        LlmInferenceResult result = completion.Result;
                        double seconds = clock.Elapsed.TotalSeconds;
                        double rate = result.Usage?.OutputTokens is > 0 and { } tokens && firstToken is { } first
                            ? tokens / Math.Max(0.001, seconds - first.TotalSeconds) : 0;
                        Console.WriteLine();
                        Console.WriteLine($"  {result.Status} ({result.FinishReason}); prompt {result.Usage?.InputTokens} + output {result.Usage?.OutputTokens} tokens; "
                            + $"first token {firstToken?.TotalSeconds:F1}s; {rate:F1} tokens/s; total {seconds:F1}s");
                        return result;
                }
            }
        }
        catch (Darkspyre.Inference.Common.InferenceException exception)
        {
            Console.WriteLine();
            Console.WriteLine($"  FAILED {exception.Code}: {exception.Message}");
            return null;
        }

        Console.WriteLine();
        Console.WriteLine("  FAILED: the stream ended without a completion.");
        return null;
    }

    private static bool Check(bool condition, string failure)
    {
        if (!condition) { Console.WriteLine($"  FAILED: {failure}."); }
        return condition;
    }
}
