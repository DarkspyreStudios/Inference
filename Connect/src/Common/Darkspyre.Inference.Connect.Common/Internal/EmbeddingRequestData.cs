using System.Collections.ObjectModel;
using System.Collections.Frozen;
using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class EmbeddingRequestData
{
    internal static LlmEmbeddingRequest Snapshot(LlmEmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Inputs);
        ArgumentNullException.ThrowIfNull(request.ProviderOptions);
        return request with
        {
            Inputs = Array.AsReadOnly(request.Inputs.ToArray()),
            ProviderOptions = CopyOptions(request.ProviderOptions)
        };
    }

    internal static LlmConnection Snapshot(LlmConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(connection.ProviderOptions);
        ArgumentNullException.ThrowIfNull(connection.AdditionalHeaders);
        ArgumentNullException.ThrowIfNull(connection.ModelCapabilityOverrides);
        return connection with
        {
            ProviderOptions = CopyOptions(connection.ProviderOptions),
            AdditionalHeaders = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(connection.AdditionalHeaders, StringComparer.OrdinalIgnoreCase)),
            CapabilityOverrides = CopyOverrides(connection.CapabilityOverrides),
            ModelCapabilityOverrides = new ReadOnlyDictionary<string, LlmCapabilityOverrides>(
                connection.ModelCapabilityOverrides.ToDictionary(pair => pair.Key,
                    pair => CopyOverrides(pair.Value)!, StringComparer.Ordinal))
        };
    }

    private static LlmCapabilityOverrides? CopyOverrides(LlmCapabilityOverrides? value)
        => value is null ? null : value with { ReasoningLevels = value.ReasoningLevels?.ToFrozenSet() };

    private static ReadOnlyDictionary<string, JsonElement> CopyOptions(IReadOnlyDictionary<string, JsonElement> options) =>
        new(options.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal));
}
