using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Examples;

internal static class WeatherTool
{
    private static readonly JsonElement InputSchema = JsonElement.Parse("""
        {"type":"object","properties":{"city":{"type":"string","description":"City name, for example Chicago."}},"required":["city"],"additionalProperties":false}
        """);

    public static LlmToolDefinition ChatDefinition => new()
    {
        Name = "get_weather",
        Description = "Return the example weather fixture for one city.",
        InputSchema = InputSchema.Clone(),
        Strict = true
    };

    public static LlmAgentToolDefinition AgentDefinition => new()
    {
        Name = "get_weather",
        Description = "Return the example weather fixture for one city.",
        InputSchema = InputSchema.Clone()
    };

    public static LlmToolResult Execute(LlmToolCall call)
    {
        try
        {
            using JsonDocument arguments = JsonDocument.Parse(call.ArgumentsJson);
            (string content, bool success) = ExecuteValidated(call.Name, arguments.RootElement);
            return new LlmToolResult(call.Id, LlmToolOutput.FromText(content, isError: !success));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return new LlmToolResult(
                call.Id,
                LlmToolOutput.FromText("""{"error":"Invalid get_weather arguments."}""", isError: true));
        }
    }

    public static Task<LlmToolOutput> ExecuteAsync(
        LlmAgentToolCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            (string content, bool success) = ExecuteValidated(call.Name, call.Arguments);
            return Task.FromResult(LlmToolOutput.FromText(content, isError: !success));
        }
        catch (ArgumentException)
        {
            return Task.FromResult(LlmToolOutput.FromText("Invalid get_weather arguments.", isError: true));
        }
    }

    private static (string Content, bool Success) ExecuteValidated(
        string toolName,
        JsonElement arguments)
    {
        if (!string.Equals(toolName, "get_weather", StringComparison.Ordinal)
            || arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("city", out JsonElement cityElement)
            || cityElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(cityElement.GetString())
            || arguments.EnumerateObject().Any(property => property.Name != "city"))
        {
            throw new ArgumentException("The tool call did not match the registered schema.");
        }

        string city = cityElement.GetString()!.Trim();
        int? temperature = city.ToLowerInvariant() switch
        {
            "chicago" => 72,
            "london" => 61,
            "tokyo" => 77,
            _ => null
        };
        return temperature is null
            ? (new JsonObject { ["city"] = city, ["error"] = "No fixture data is available." }.ToJsonString(), false)
            : (new JsonObject
            {
                ["city"] = city,
                ["temperature_f"] = temperature,
                ["conditions"] = "clear"
            }.ToJsonString(), true);
    }
}
