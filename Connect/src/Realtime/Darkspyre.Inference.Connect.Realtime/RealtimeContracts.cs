using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Realtime;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Realtime;

public enum RealtimeTransport { XaiRealtime = 0 }

public sealed record RealtimeConnection
{
    public required RealtimeTransport Transport { get; init => field = RealtimeData.Defined(value); }
    public required Uri Endpoint { get; init => field = RealtimeData.Required(value); }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public XaiConnectionSelection? Xai { get; init; }
    public IProviderAuthorization? Authorization { get; init; }
    public IReadOnlyDictionary<string, string> AdditionalHeaders { get; init => field = RealtimeData.Map(value, StringComparer.OrdinalIgnoreCase); }
        = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    public override string ToString() => "RealtimeConnection { <connection> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record XaiRealtimeOptions(IReadOnlyDictionary<string, string>? Replace = null) : RealtimeProviderOptions
{
    public string? TranscriptionModel { get; init; }
    public IReadOnlyDictionary<string, string>? Replace { get; init => field = value is null ? null : RealtimeData.Map(value, StringComparer.Ordinal); }
        = Replace is null ? null : RealtimeData.Map(Replace, StringComparer.Ordinal);
    public override string ToString() => "XaiRealtimeOptions { <options> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record XaiRealtimeForceMessageItem(string Text, bool? Interruptible = null) : RealtimeConversationItem
{
    [JsonRequired] public string Text { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Text);
    public override string ToString() => "XaiRealtimeForceMessageItem { <item> }";
}

/// <summary>The adapter owns session resources and validates receipt authority before protocol effects.</summary>
public interface IRealtimeProvider
{
    RealtimeTransport Transport { get; }
    InferenceSetupDefinition SetupDefinition { get; }
    Task<RealtimeCapabilities> GetCapabilitiesAsync(RealtimeConnection connection,
        string? modelId = null, CancellationToken cancellationToken = default);
    Task<IRealtimeVoiceSession> OpenSessionAsync(RealtimeConnection connection, RealtimeSessionConfig config,
        InferenceStreamLimits limits, XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null,
        CancellationToken cancellationToken = default);
}

internal static class RealtimeData
{
    internal static T Required<T>(T value) where T : class => value ?? throw new ArgumentNullException(nameof(value));
    internal static T Defined<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    internal static IReadOnlyDictionary<string, string> Map(IReadOnlyDictionary<string, string> value, StringComparer comparer)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new ReadOnlyDictionary<string, string>(value.ToDictionary(pair => Required(pair.Key), pair => Required(pair.Value), comparer));
    }
    internal static IReadOnlyList<string> List(IReadOnlyList<string> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Array.AsReadOnly(value.Select(Required).ToArray());
    }
}
