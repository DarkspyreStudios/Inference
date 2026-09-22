using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Examples;

internal static class WeatherTool
{
    private static readonly JsonElement InputSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            city = new
            {
                type = "string",
                description = "City name, for example Chicago."
            }
        },
        required = new[] { "city" },
        additionalProperties = false
    });

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
            return new LlmToolResult(
                call.Id,
                content,
                IsError: !success);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return new LlmToolResult(
                call.Id,
                JsonSerializer.Serialize(new { error = "Invalid get_weather arguments." }),
                IsError: true);
        }
    }

    public static ValueTask<LlmAgentToolResult> ExecuteAsync(
        LlmAgentToolCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            (string content, bool success) = ExecuteValidated(call.Name, call.Arguments);
            return ValueTask.FromResult(new LlmAgentToolResult
            {
                Success = success,
                Content = [new LlmAgentTextContent(content)]
            });
        }
        catch (ArgumentException)
        {
            return ValueTask.FromResult(new LlmAgentToolResult
            {
                Success = false,
                Content = [new LlmAgentTextContent("Invalid get_weather arguments.")]
            });
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
            ? (JsonSerializer.Serialize(new { city, error = "No fixture data is available." }), false)
            : (JsonSerializer.Serialize(new
            {
                city,
                temperature_f = temperature,
                conditions = "clear"
            }), true);
    }
}
