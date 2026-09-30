using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed record AppleTool(string Name, string? Description, AppleSchemaNode Schema);
internal sealed record AppleCall(string Id, string Name, string Arguments);
internal sealed record AppleResult(string Id, string Name, string Text);
internal sealed record AppleMessage(string Role, string Content, IReadOnlyList<AppleCall> Calls, IReadOnlyList<AppleResult> Results);
/// <summary>One generation. <paramref name="Stream"/> false asks the helper for one complete response.</summary>
internal sealed record AppleGeneration(string? Instructions, IReadOnlyList<AppleMessage> Messages,
    IReadOnlyList<AppleTool> Tools, AppleSchemaNode? Schema, double? Temperature, int? MaximumResponseTokens, bool Stream);
internal sealed record AppleCommand(string Command, string Id, AppleGeneration? Request = null);

internal static class AppleProtocol
{
    internal const string Name = "darkspyre.apple-fm";
    internal const string ModelId = "apple-system-default";
    internal static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 128 };

    internal static InferenceException Failure(string message) => new(InferenceFailureCode.ProtocolFailure, message);

    // Values are accepted only from the helper protocol; narrative content is opaque data, not
    // display diagnostics, executable code or a path. Serialization never logs request content.
    internal static string RequiredString(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw Failure("The Apple helper returned an invalid field.");

    internal static long RequiredInteger(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
            ? number : throw Failure("The Apple helper returned an invalid number.");

    internal static void Ready(JsonElement hello)
    {
        if (RequiredString(hello, "availability") == "available") return;
        string reason = RequiredString(hello, "availability");
        throw new InferenceException(InferenceFailureCode.ModelUnavailable, reason switch
        {
            "deviceNotEligible" => "This Mac is not eligible for Apple Intelligence.",
            "appleIntelligenceNotEnabled" => "Enable Apple Intelligence in System Settings, then check again.",
            "modelNotReady" => "The Apple system model is not ready. Let macOS finish preparing it, then check again.",
            _ => "The Apple system model is unavailable on this Mac."
        });
    }

    internal static Exception Error(JsonElement frame)
    {
        string code = RequiredString(frame, "code");
        return code switch
        {
            "context_length_exceeded" => new LlmProviderException("The Apple model context window was exceeded.",
                LlmTransport.AppleFoundationModels, providerCode: code),
            "unsupported_feature" or "unsupported_language" => new NotSupportedException("Apple cannot support this generation request."),
            "model_unavailable" => new InferenceException(InferenceFailureCode.ModelUnavailable, "The Apple system model is unavailable."),
            "capacity_exhausted" => new InferenceException(InferenceFailureCode.CapacityExhausted, "Apple is temporarily unable to accept this request."),
            "invalid_request" => new InferenceException(InferenceFailureCode.InvalidRequest, "The Apple helper rejected the request."),
            "protocol_failure" => Failure("The Apple helper could not preserve the requested protocol or tool-call identity."),
            "cancelled" => new InferenceException(InferenceFailureCode.ExecutionFailed, "The Apple operation was cancelled without a host cancellation request."),
            _ => new InferenceException(InferenceFailureCode.ExecutionFailed, "Apple generation failed.")
        };
    }
}

internal sealed record AppleProcessLimits(int MaximumFrameBytes = 1_048_576, int MaximumStderrBytes = 65_536,
    int StartupTimeoutSeconds = 10, int RequestTimeoutSeconds = 120, int CancellationGraceSeconds = 5)
{
    internal static AppleProcessLimits Read(LlmConnection connection)
    {
        int Read(string name, int fallback, int maximum)
        {
            if (!connection.ProviderOptions.TryGetValue(name, out JsonElement value)) return fallback;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number > 0 && number <= maximum
                ? number : throw new ArgumentException($"Invalid Apple process limit: {name}.");
        }
        string[] names = ["maximumFrameBytes", "maximumStderrBytes", "startupTimeoutSeconds", "requestTimeoutSeconds", "cancellationGraceSeconds"];
        if (connection.ProviderOptions.Keys.Any(key => !names.Contains(key, StringComparer.Ordinal)))
            throw new NotSupportedException("Unknown Apple connection option.");
        return new(Read(names[0], 1_048_576, 16_777_216), Read(names[1], 65_536, 1_048_576),
            Read(names[2], 10, 300), Read(names[3], 120, 86_400), Read(names[4], 5, 60));
    }
}
