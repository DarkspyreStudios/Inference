using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Common;

/// <summary>Evidence of support, separate from permission to attempt an operation.</summary>
/// <gotcha>Unknown permits an explicit attempt. It does not establish support or authorize changing the request.</gotcha>
[JsonConverter(typeof(JsonStringEnumConverter<InferenceSupport>))]
public enum InferenceSupport
{
    /// <summary>No affirmative or negative support fact is available.</summary>
    Unknown = 0,

    /// <summary>The selected implementation or provider supports the capability.</summary>
    Supported = 1,

    /// <summary>A known inability or explicit caller restriction prevents the capability.</summary>
    Unsupported = 2
}
