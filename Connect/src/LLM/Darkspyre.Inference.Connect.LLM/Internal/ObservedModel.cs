using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class ObservedModel
{
    internal static string? Read(JsonElement root, string fieldName, LlmTransport transport,
        LlmConnection connection, LlmCredential? credential, HttpRequestMessage? sentMessage,
        string? previous = null, IEnumerable<string>? extraSecrets = null)
    {
        if (root.ValueKind != JsonValueKind.Object) throw Invalid(transport);
        JsonElement? observed = null;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name != fieldName) continue;
            if (observed.HasValue) throw Invalid(transport);
            observed = property.Value;
        }
        if (observed is not { } value || value.ValueKind == JsonValueKind.Null) return previous;
        if (value.ValueKind != JsonValueKind.String) throw Invalid(transport);
        return Read(value.GetString(), transport, connection, credential, sentMessage, previous, extraSecrets);
    }

    internal static string? Read(string? model, LlmTransport transport,
        LlmConnection connection, LlmCredential? credential, HttpRequestMessage? sentMessage,
        string? previous = null, IEnumerable<string>? extraSecrets = null)
    {
        if (model is null) return previous;
        if (string.IsNullOrWhiteSpace(model)) throw Invalid(transport);
        IEnumerable<string> protectedValues = connection.AdditionalHeaders.Values;
        if (sentMessage is not null)
            protectedValues = protectedValues.Concat(sentMessage.Headers.SelectMany(header => header.Value));
        if (sentMessage?.Headers.Authorization?.Parameter is { } token)
            protectedValues = protectedValues.Append(token);
        if (extraSecrets is not null) protectedValues = protectedValues.Concat(extraSecrets);
        if (SecretRedactor.ContainsProtectedValue(model, credential, protectedValues)) throw Invalid(transport);
        if (previous is not null && previous != model) throw Invalid(transport);
        return model;
    }

    private static LlmProviderException Invalid(LlmTransport transport) => new(
        "The provider returned an invalid model observation.", transport, providerCode: "invalid_model_observation");
}
