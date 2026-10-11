using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Common.Media;

/// <summary>Explicit caller-local buffering budgets, not provider limits.</summary>
public sealed record InferenceStreamLimits(int MaximumBufferedEvents,
    long MaximumBufferedPayloadBytes, long MaximumMessageBytes)
{
    /// <summary>The most events the caller buffers; always positive.</summary>
    [JsonRequired]
    public int MaximumBufferedEvents { get; init => field = Positive(value, nameof(MaximumBufferedEvents)); } = Positive(MaximumBufferedEvents, nameof(MaximumBufferedEvents));
    /// <summary>The most payload bytes the caller buffers across events; always positive.</summary>
    [JsonRequired]
    public long MaximumBufferedPayloadBytes { get; init => field = Positive(value, nameof(MaximumBufferedPayloadBytes)); } = Positive(MaximumBufferedPayloadBytes, nameof(MaximumBufferedPayloadBytes));
    /// <summary>The largest single message the caller accepts, in bytes; always positive.</summary>
    [JsonRequired]
    public long MaximumMessageBytes { get; init => field = Positive(value, nameof(MaximumMessageBytes)); } = Positive(MaximumMessageBytes, nameof(MaximumMessageBytes));

    private static int Positive(int value, string name) => value > 0 ? value : throw new ArgumentOutOfRangeException(name);
    private static long Positive(long value, string name) => value > 0 ? value : throw new ArgumentOutOfRangeException(name);
}
