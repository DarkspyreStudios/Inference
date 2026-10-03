using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class CatalogConnectionData
{
    internal static LlmConnection FreezeXai(LlmConnection source)
    {
        _ = XaiRequestHeaders.Require(source);
        ProviderHttp.ValidateAdditionalHeaders(source.AdditionalHeaders);
        return new()
        {
            Version = source.Version,
            Transport = source.Transport,
            Endpoint = source.Endpoint,
            Credential = source.Credential,
            Xai = source.Xai is not { } options ? null : new()
            {
                Region = options.Region,
                ServiceTier = options.ServiceTier,
                MultiAgent = options.MultiAgent is not { } agents ? null : new(agents.AgentCount)
            },
            AdditionalHeaders = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(source.AdditionalHeaders, StringComparer.OrdinalIgnoreCase)),
            ProviderOptions = new ReadOnlyDictionary<string, JsonElement>(source.ProviderOptions.ToDictionary(
                pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)),
            CapabilityOverrides = Copy(source.CapabilityOverrides),
            ModelCapabilityOverrides = new ReadOnlyDictionary<string, LlmCapabilityOverrides>(
                source.ModelCapabilityOverrides.ToDictionary(pair => pair.Key, pair => Copy(pair.Value)!, StringComparer.Ordinal))
        };
    }

    private static LlmCapabilityOverrides? Copy(LlmCapabilityOverrides? value) => value is null ? null
        : value with { ReasoningLevels = value.ReasoningLevels?.ToFrozenSet() };

    // Only framework-owned frozen data reaches equality under the cache gate.
    internal sealed class Evidence(LlmConnection frozen)
    {
        private readonly LlmTransport _transport = frozen.Transport;
        private readonly string _endpoint = frozen.Endpoint;
        private readonly string? _credential = frozen.Credential;
        private readonly XaiConnectionOptions? _xai = frozen.Xai;
        private readonly IReadOnlyDictionary<string, string> _headers = frozen.AdditionalHeaders;
        private readonly IReadOnlyDictionary<string, JsonElement> _options = frozen.ProviderOptions;
        private readonly LlmCapabilityOverrides? _overrides = frozen.CapabilityOverrides;
        private readonly IReadOnlyDictionary<string, LlmCapabilityOverrides> _models = frozen.ModelCapabilityOverrides;

        internal bool Same(Evidence other) => _transport == other._transport && _endpoint == other._endpoint
            && _credential == other._credential && _xai == other._xai && SameOverrides(_overrides, other._overrides)
            && _headers.Count == other._headers.Count
            && _headers.All(pair => other._headers.TryGetValue(pair.Key, out string? value) && value == pair.Value)
            && _options.Count == other._options.Count
            && _options.All(pair => other._options.TryGetValue(pair.Key, out JsonElement value) && JsonElement.DeepEquals(pair.Value, value))
            && _models.Count == other._models.Count
            && _models.All(pair => other._models.TryGetValue(pair.Key, out LlmCapabilityOverrides? value) && SameOverrides(pair.Value, value));

        private static bool SameOverrides(LlmCapabilityOverrides? left, LlmCapabilityOverrides? right) =>
            left is null || right is null ? left is null && right is null
            : left with { ReasoningLevels = null } == right with { ReasoningLevels = null }
                && (left.ReasoningLevels is null || right.ReasoningLevels is null
                    ? left.ReasoningLevels is null && right.ReasoningLevels is null
                    : left.ReasoningLevels.SetEquals(right.ReasoningLevels));
    }
}
