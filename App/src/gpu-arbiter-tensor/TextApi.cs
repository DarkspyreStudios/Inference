using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Tensor.LLM;
using GpuArbiter.Gpu;

namespace GpuArbiter.Tensor;

public sealed class TextApi(ITensorModelHost models, AdmissionGate admissionGate, TensorOptions options, TimeProvider clock)
{
    internal static async Task ValidateAsync(ITensorLlmInference engine, LlmInferenceRequest request, bool stream, CancellationToken token)
    {
        var capabilities = await engine.GetCapabilitiesAsync(request.ModelId, token);
        void Require(bool supported, string field) { if (!supported) throw new ApiException(400, "Selected engine does not support this feature", field); }
        if (stream) Require(capabilities.SupportsStreaming, "stream");
        if (request.Temperature is not null) Require(capabilities.SupportsTemperature, "temperature");
        if (request.MaxOutputTokens is not null) Require(capabilities.OutputLimitEnforcement == LlmOutputLimitEnforcement.EngineEnforced, "max_output_tokens");
        if (request.ReasoningLevel is { } level) Require(capabilities.ReasoningLevels.Contains(level), "reasoning_effort");
        if (request.StructuredOutput is { } output)
            Require(output.Fallback == LlmStructuredOutputFallback.JsonObject ? capabilities.SupportsJsonObject : capabilities.SupportsJsonSchema, "response_format");
        if (request.Tools.Count > 0) Require(capabilities.SupportsToolUse, "tools");
        if (request.Tools.Any(tool => tool.Strict)) Require(capabilities.Tools.StrictInputSchema != LlmStrictInputSchemaSupport.Unsupported, "strict");
        if (request.ToolChoice is { } choice) Require(capabilities.Tools.ChoiceModes.Contains(choice.Mode), "tool_choice");
        if (request.AllowParallelToolCalls is not null) Require(capabilities.Tools.SupportsParallelCallControl, "parallel_tool_calls");
    }

    public async Task HandleAsync(HttpContext context, JsonElement root)
    {
        var entry = models.Get(OpenAiInput.String(root, "model"));
        if (entry.Instance is not ITensorLlmInference engine) throw new ApiException(400, "Selected model is not a language model", "model");
        string route = context.Request.Path.Value!;
        var input = OpenAiInput.Parse(root, route, entry, options.MaximumChoices);
        foreach (var request in input.Requests) await ValidateAsync(engine, request, input.Stream, context.RequestAborted);
        using var lease = await admissionGate.AcquireAsync(input.Alias, context, input.Stream);
        {
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lease.HostStopped);
            var token = execution.Token;
            string id = (route == "/v1/responses" ? "resp_" : route == "/v1/completions" ? "cmpl-" : "chatcmpl-") + Guid.NewGuid().ToString("N");
            long created = clock.GetUtcNow().ToUnixTimeSeconds();
            if (input.Stream) OpenAiOutput.BeginStream(context);
            var responseOutput = route == "/v1/responses" ? new ResponsesOutput(id, input.Alias, created) : null;
            if (responseOutput is not null) await responseOutput.StartAsync(input.Stream ? context : null, token);
            var choices = new List<object>();
            var usages = new List<LlmUsage?>();
            for (int index = 0; index < input.Requests.Count; index++)
            {
                var reasoning = new StringBuilder();
                var calls = new Dictionary<string, int>(StringComparer.Ordinal);
                LlmInferenceResult? result = null;
                async Task Chunk(object delta, string? finish = null) => await OpenAiOutput.SseAsync(context, new
                {
                    id, @object = route == "/v1/completions" ? "text_completion" : "chat.completion.chunk", created, model = input.Alias,
                    choices = route == "/v1/completions" ? new object[] { new { index, text = delta, logprobs = (object?)null, finish_reason = finish } }
                        : new object[] { new { index, delta, logprobs = (object?)null, finish_reason = finish } }
                }, token);
                if (input.Stream && responseOutput is null && route != "/v1/completions") await Chunk(new { role = "assistant", content = "" });
                await foreach (var update in engine.StreamAsync(input.Requests[index], token).WithCancellation(token))
                {
                    if (result is not null) throw new InvalidOperationException("Events follow the terminal result.");
                    if (responseOutput is not null) await responseOutput.EventAsync(update, input.Stream ? context : null, token);
                    if (update is LlmReasoningDeltaEvent thought) reasoning.Append(thought.Delta);
                    if (update is LlmCompletionEvent completion)
                    {
                        if (result is not null) throw new InvalidOperationException("Multiple terminal results.");
                        result = completion.Result;
                        continue;
                    }
                    if (!input.Stream || responseOutput is not null) continue;
                    switch (update)
                    {
                        case LlmTextDeltaEvent text: await Chunk(route == "/v1/completions" ? text.Delta : new { content = text.Delta }); break;
                        case LlmReasoningDeltaEvent thoughtDelta when route != "/v1/completions": await Chunk(new { reasoning_content = thoughtDelta.Delta }); break;
                        case LlmRefusalDeltaEvent refusal when route != "/v1/completions": await Chunk(new { refusal = refusal.Delta }); break;
                        case LlmToolCallStartedEvent call when route != "/v1/completions":
                            calls.Add(call.ToolCallId, calls.Count);
                            await Chunk(new { tool_calls = new[] { new { index = calls[call.ToolCallId], id = call.ToolCallId, type = "function", function = new { name = call.Name, arguments = "" } } } });
                            break;
                        case LlmToolCallArgumentsDeltaEvent arguments when route != "/v1/completions":
                            await Chunk(new { tool_calls = new[] { new { index = calls[arguments.ToolCallId], function = new { arguments = arguments.Delta } } } });
                            break;
                    }
                }
                if (result is null || result.Status == LlmCompletionStatus.Failed) throw new ApiException(503, "Tensor inference did not complete");
                usages.Add(result.Usage);
                if (responseOutput is not null)
                {
                    var response = await responseOutput.CompleteAsync(result, input.Stream ? context : null, token);
                    if (!input.Stream) await context.Response.WriteAsJsonAsync(response, token);
                    return;
                }
                if (input.Stream) await Chunk(route == "/v1/completions" ? "" : new { }, OpenAiOutput.Finish(result));
                else choices.Add(route == "/v1/completions"
                    ? new { index, text = result.Text, logprobs = (object?)null, finish_reason = OpenAiOutput.Finish(result) }
                    : (object)new { index, message = OpenAiOutput.ChatMessage(result, reasoning.ToString()), logprobs = (object?)null, finish_reason = OpenAiOutput.Finish(result) });
            }
            int? Sum(Func<LlmUsage, int?> select) => usages.All(usage => usage is not null && select(usage) is not null) ? usages.Sum(usage => select(usage!)!.Value) : null;
            var usage = usages.All(value => value is null) ? null : new LlmUsage(Sum(value => value.InputTokens), Sum(value => value.OutputTokens), Sum(value => value.TotalTokens))
            { CachedInputTokens = Sum(value => value.CachedInputTokens), ReasoningOutputTokens = Sum(value => value.ReasoningOutputTokens) };
            if (input.Stream)
            {
                if (input.IncludeUsage) await OpenAiOutput.SseAsync(context, new { id, @object = route == "/v1/completions" ? "text_completion" : "chat.completion.chunk", created, model = input.Alias, choices = Array.Empty<object>(), usage = OpenAiOutput.Usage(usage, false) }, token);
                await context.Response.WriteAsync("data: [DONE]\n\n", token);
                await context.Response.Body.FlushAsync(token);
            }
            else await context.Response.WriteAsJsonAsync(new { id, @object = route == "/v1/completions" ? "text_completion" : "chat.completion", created, model = input.Alias, choices, usage = OpenAiOutput.Usage(usage, false) }, token);
        }
    }
}
