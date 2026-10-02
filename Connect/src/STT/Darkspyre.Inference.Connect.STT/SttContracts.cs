using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.STT;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.STT;

public enum SttTransport { XaiStt = 0 }

/// <summary>Host-selected connection data. Live authorization is borrowed and cannot be exported.</summary>
public sealed record SttConnection
{
    public required SttTransport Transport { get; init => field = SttData.Transport(value); }
    public required Uri Endpoint { get; init => field = SttData.Required(value); }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public XaiConnectionSelection? Xai { get; init; }
    public IProviderAuthorization? Authorization { get; init; }
    /// <summary>Short-lived protected headers. Never display or log their values.</summary>
    public IReadOnlyDictionary<string, string> AdditionalHeaders { get; init => field = SttData.Headers(value); } = SttData.Headers(new Dictionary<string, string>());
    public override string ToString() => "SttConnection { <data> }";
}

public sealed record SttSetupConfiguration(SttConnection Connection)
{
    [JsonRequired] public SttConnection Connection { get; init => field = SttData.Required(value); } = SttData.Required(Connection);
    public SpeechTranscriptionOptions Transcription { get; init => field = SttData.Required(value); } = new();
    public SpeechStreamOptions Streaming { get; init => field = SttData.Required(value); } = new();
    public InferenceStreamLimits? StreamLimits { get; init; }
    public override string ToString() => "SttSetupConfiguration { <data> }";
}

public sealed record SttSetupHeaderChanges(IReadOnlyDictionary<string, string> Set, IReadOnlyList<string> Remove)
{
    [JsonRequired] public IReadOnlyDictionary<string, string> Set { get; init => field = SttData.Headers(value); } = SttData.Headers(Set);
    [JsonRequired] public IReadOnlyList<string> Remove { get; init => field = SttData.Strings(value); } = SttData.Strings(Remove);
    public override string ToString() => "SttSetupHeaderChanges { <data> }";
}

public sealed record SttSetupChanges(SetupConfigurationIdentity Identity, SttSetupConfiguration Configuration, SttSetupHeaderChanges Headers)
{
    [JsonRequired] public SetupConfigurationIdentity Identity { get; init => field = SttData.Required(value); } = SttData.Required(Identity);
    [JsonRequired] public SttSetupConfiguration Configuration { get; init => field = SttData.Required(value); } = SttData.Required(Configuration);
    [JsonRequired] public SttSetupHeaderChanges Headers { get; init => field = SttData.Required(value); } = SttData.Required(Headers);
    public override string ToString() => "SttSetupChanges { <data> }";
}

/// <summary>The selected adapter borrows family authorization and workspace access; sessions own streaming resources.</summary>
public interface ISttProvider
{
    SttTransport Transport { get; }
    InferenceSetupDefinition SetupDefinition { get; }
    ISpeechTranscriptionClient Bind(SttConnection connection, XaiFamilyBinding? xai = null,
        IInferenceResourceAccess? resources = null);
}

internal static class SttData
{
    internal static T Required<T>(T value) where T : class => value ?? throw new ArgumentNullException(nameof(value));
    internal static SttTransport Transport(SttTransport value) => Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    internal static ReadOnlyDictionary<string, string> Headers(IReadOnlyDictionary<string, string> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var copied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string item) in value) { ArgumentNullException.ThrowIfNull(item); copied.Add(key, item); }
        return new(copied);
    }
    internal static ReadOnlyCollection<string> Strings(IReadOnlyList<string> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string[] copy = value.ToArray();
        if (copy.Any(item => item is null)) throw new ArgumentException("Header removals cannot contain null.", nameof(value));
        return Array.AsReadOnly(copy);
    }
}
