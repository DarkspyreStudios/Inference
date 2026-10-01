using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Image;

/// <summary>Short-lived connection data resolved and protected by the host.</summary>
public sealed record ImageConnection
{
    public required ImageTransport Transport { get; init; }

    /// <summary>Base URL for the image provider.</summary>
    public required string Endpoint { get; init; }

    /// <summary>Maximum duration of one provider HTTP operation.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public XaiConnectionSelection? Xai { get; init; }

    public IProviderAuthorization? Authorization { get; init; }

    /// <summary>
    /// Optional provider request headers. Values may contain credentials and must never be logged or
    /// persisted outside the host's protected connection store.
    /// </summary>
    public IReadOnlyDictionary<string, string> AdditionalHeaders { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
