using System.Text.Json;
using Darkspyre.Inference.Common;
using GpuArbiter.Gpu;

namespace GpuArbiter.Tensor;

public sealed class ApiException(int status, string message, string? parameter = null) : Exception(message)
{
    public int Status { get; } = status;
    public string? Parameter { get; } = parameter;

    public static async Task WriteAsync(HttpContext context, Exception error)
    {
        if (context.RequestAborted.IsCancellationRequested) return;
        var (status, message, parameter) = error switch
        {
            ApiException api => (api.Status, api.Message, api.Parameter),
            GpuException gpu => (gpu.StatusCode, gpu.Message, (string?)null),
            InferenceException inference => (inference.Code is InferenceFailureCode.InvalidRequest or InferenceFailureCode.UnsupportedFeature ? 400 : 503,
                "Tensor inference rejected or could not execute the request", (string?)null),
            JsonException or ArgumentException or FormatException or KeyNotFoundException => (400, "Invalid request", (string?)null),
            BadHttpRequestException request => (request.StatusCode, "Request exceeds limits or is malformed", (string?)null),
            _ => (503, "Tensor inference is unavailable", (string?)null)
        };
        var payload = new { error = new { message, type = status < 500 ? "invalid_request_error" : "server_error", param = parameter, code = status } };
        if (context.Response.HasStarted)
        {
            if (context.Response.ContentType == "text/event-stream")
            {
                await context.Response.WriteAsync($"event: error\ndata: {JsonSerializer.Serialize(payload)}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
            else context.Abort();
        }
        else
        {
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(payload, context.RequestAborted);
        }
    }
}
