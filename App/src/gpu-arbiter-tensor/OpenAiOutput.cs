using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace GpuArbiter.Tensor;

internal static class OpenAiOutput
{
    internal static object? Usage(LlmUsage? usage, bool responses) => usage is null ? null : responses
        ? new { input_tokens = usage.InputTokens, output_tokens = usage.OutputTokens, total_tokens = usage.TotalTokens,
            input_tokens_details = new { cached_tokens = usage.CachedInputTokens }, output_tokens_details = new { reasoning_tokens = usage.ReasoningOutputTokens } }
        : (object)new { prompt_tokens = usage.InputTokens, completion_tokens = usage.OutputTokens, total_tokens = usage.TotalTokens,
            prompt_tokens_details = new { cached_tokens = usage.CachedInputTokens }, completion_tokens_details = new { reasoning_tokens = usage.ReasoningOutputTokens } };

    internal static string? Finish(LlmInferenceResult result) => result.ToolCalls.Count > 0 ? "tool_calls"
        : result.Status == LlmCompletionStatus.Incomplete ? "length"
        : result.Status == LlmCompletionStatus.Refused ? "content_filter"
        : result.Status == LlmCompletionStatus.Completed ? "stop" : null;

    internal static object ChatMessage(LlmInferenceResult result, string reasoning) => new
    {
        role = "assistant", content = result.Text, refusal = result.Refusal,
        reasoning_content = string.IsNullOrEmpty(reasoning) ? null : reasoning,
        tool_calls = result.ToolCalls.Count == 0 ? null : result.ToolCalls.Select(call => new
        { id = call.Id, type = "function", function = new { name = call.Name, arguments = call.ArgumentsJson } }).ToArray()
    };

    internal static async Task SseAsync(HttpContext context, object payload, CancellationToken token, string? eventName = null)
    {
        if (eventName is not null) await context.Response.WriteAsync($"event: {eventName}\n", token);
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", token);
        await context.Response.Body.FlushAsync(token);
    }

    internal static void BeginStream(HttpContext context)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
    }
}

internal sealed class ResponsesOutput(string id, string model, long created)
{
    private sealed class Item(string id, string type, string? callId = null, string? name = null)
    {
        public string Id { get; } = id;
        public string Type { get; } = type;
        public string? CallId { get; } = callId;
        public string? Name { get; } = name;
        public StringBuilder Text { get; } = new();
        public object Part() => Type == "refusal"
            ? new { type = "refusal", refusal = Text.ToString() }
            : (object)new { type = "output_text", text = Text.ToString(), annotations = Array.Empty<object>() };
        public object Wire(bool completed) => Type switch
        {
            "function_call" => new { id = Id, type = Type, call_id = CallId, name = Name, arguments = Text.ToString(), status = completed ? "completed" : "in_progress" },
            "reasoning" => new { id = Id, type = Type, summary = Array.Empty<object>(), content = new[] { new { type = "reasoning_text", text = Text.ToString() } } },
            _ => (object)new { id = Id, type = "message", role = "assistant", status = completed ? "completed" : "in_progress",
                content = new[] { Part() } }
        };
    }

    private readonly List<Item> _items = [];
    private int _sequence;
    private object Envelope(string status, LlmUsage? usage = null, string? incompleteReason = null) => new
    {
        id, @object = "response", created_at = created, model, status, store = false,
        output = _items.Select(item => item.Wire(status != "in_progress")).ToArray(),
        usage = OpenAiOutput.Usage(usage, true), error = (object?)null,
        incomplete_details = incompleteReason is null ? null : new { reason = incompleteReason }
    };

    private async Task SendAsync(HttpContext? context, string type, Dictionary<string, object?> data, CancellationToken token)
    {
        if (context is null) return;
        data["type"] = type;
        data["sequence_number"] = _sequence++;
        await OpenAiOutput.SseAsync(context, data, token, type);
    }

    internal async Task StartAsync(HttpContext? context, CancellationToken token)
    {
        await SendAsync(context, "response.created", new() { ["response"] = Envelope("in_progress") }, token);
        await SendAsync(context, "response.in_progress", new() { ["response"] = Envelope("in_progress") }, token);
    }

    private async Task<Item> EnsureAsync(string type, HttpContext? context, CancellationToken token, string? callId = null, string? name = null)
    {
        var item = _items.FirstOrDefault(item => item.Type == type && item.CallId == callId);
        if (item is not null) return item;
        item = new(Guid.NewGuid().ToString("N"), type, callId, name);
        _items.Add(item);
        int index = _items.Count - 1;
        await SendAsync(context, "response.output_item.added", new() { ["output_index"] = index, ["item"] = item.Wire(false) }, token);
        if (type is "message" or "refusal") await SendAsync(context, "response.content_part.added", new()
        { ["output_index"] = index, ["item_id"] = item.Id, ["content_index"] = 0,
            ["part"] = item.Part() }, token);
        return item;
    }

    internal async Task EventAsync(LlmStreamEvent update, HttpContext? context, CancellationToken token)
    {
        switch (update)
        {
            case LlmTextDeltaEvent text:
                var message = await EnsureAsync("message", context, token);
                message.Text.Append(text.Delta);
                await SendAsync(context, "response.output_text.delta", new() { ["item_id"] = message.Id, ["output_index"] = _items.IndexOf(message), ["content_index"] = 0, ["delta"] = text.Delta, ["logprobs"] = Array.Empty<object>() }, token);
                break;
            case LlmRefusalDeltaEvent refusal:
                var refused = await EnsureAsync("refusal", context, token);
                refused.Text.Append(refusal.Delta);
                await SendAsync(context, "response.refusal.delta", new() { ["item_id"] = refused.Id, ["output_index"] = _items.IndexOf(refused), ["content_index"] = 0, ["delta"] = refusal.Delta }, token);
                break;
            case LlmReasoningDeltaEvent reasoning:
                var thought = await EnsureAsync("reasoning", context, token);
                thought.Text.Append(reasoning.Delta);
                await SendAsync(context, "response.reasoning_text.delta", new() { ["item_id"] = thought.Id, ["output_index"] = _items.IndexOf(thought), ["content_index"] = 0, ["delta"] = reasoning.Delta }, token);
                break;
            case LlmToolCallStartedEvent call:
                await EnsureAsync("function_call", context, token, call.ToolCallId, call.Name);
                break;
            case LlmToolCallArgumentsDeltaEvent arguments:
                var function = _items.FirstOrDefault(item => item.CallId == arguments.ToolCallId)
                    ?? throw new InvalidOperationException("Tool arguments precede the tool call.");
                function.Text.Append(arguments.Delta);
                await SendAsync(context, "response.function_call_arguments.delta", new() { ["item_id"] = function.Id, ["output_index"] = _items.IndexOf(function), ["delta"] = arguments.Delta }, token);
                break;
            case LlmToolCallCompletedEvent completed:
                var finished = await EnsureAsync("function_call", context, token, completed.ToolCall.Id, completed.ToolCall.Name);
                finished.Text.Clear().Append(completed.ToolCall.ArgumentsJson);
                break;
        }
    }

    internal async Task<object> CompleteAsync(LlmInferenceResult result, HttpContext? context, CancellationToken token)
    {
        if (result.Text.Length > 0)
        {
            var message = await EnsureAsync("message", context, token);
            message.Text.Clear().Append(result.Text);
        }
        if (result.Refusal is not null)
        {
            var refusal = await EnsureAsync("refusal", context, token);
            refusal.Text.Clear().Append(result.Refusal);
        }
        foreach (var call in result.ToolCalls)
        {
            var function = await EnsureAsync("function_call", context, token, call.Id, call.Name);
            function.Text.Clear().Append(call.ArgumentsJson);
        }
        foreach (var item in _items)
        {
            var data = new Dictionary<string, object?> { ["item_id"] = item.Id, ["output_index"] = _items.IndexOf(item) };
            if (item.Type == "function_call")
            {
                data["arguments"] = item.Text.ToString();
                await SendAsync(context, "response.function_call_arguments.done", data, token);
            }
            else
            {
                data["content_index"] = 0;
                data[item.Type == "refusal" ? "refusal" : "text"] = item.Text.ToString();
                await SendAsync(context, item.Type == "reasoning" ? "response.reasoning_text.done" : item.Type == "refusal" ? "response.refusal.done" : "response.output_text.done", data, token);
                if (item.Type is "message" or "refusal") await SendAsync(context, "response.content_part.done", new()
                { ["item_id"] = item.Id, ["output_index"] = _items.IndexOf(item), ["content_index"] = 0,
                    ["part"] = item.Part() }, token);
            }
            await SendAsync(context, "response.output_item.done", new() { ["output_index"] = _items.IndexOf(item), ["item"] = item.Wire(true) }, token);
        }
        var status = result.Status is LlmCompletionStatus.Completed or LlmCompletionStatus.RequiresAction or LlmCompletionStatus.Refused ? "completed" : "incomplete";
        var response = Envelope(status, result.Usage, status == "incomplete" ? result.IncompleteReason ?? (result.Status == LlmCompletionStatus.Incomplete ? "max_output_tokens" : "unknown") : null);
        await SendAsync(context, $"response.{status}", new() { ["response"] = response }, token);
        return response;
    }
}
