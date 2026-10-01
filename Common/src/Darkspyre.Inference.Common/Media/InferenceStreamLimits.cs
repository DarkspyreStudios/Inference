using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Common.Media;

/// <summary>Explicit caller-local buffering budgets, not provider limits.</summary>
public sealed record InferenceStreamLimits(int MaximumBufferedEvents,
    long MaximumBufferedPayloadBytes, long MaximumMessageBytes)
{
    [JsonRequired]
    public int MaximumBufferedEvents { get; init => field = Positive(value, nameof(MaximumBufferedEvents)); } = Positive(MaximumBufferedEvents, nameof(MaximumBufferedEvents));
    [JsonRequired]
    public long MaximumBufferedPayloadBytes { get; init => field = Positive(value, nameof(MaximumBufferedPayloadBytes)); } = Positive(MaximumBufferedPayloadBytes, nameof(MaximumBufferedPayloadBytes));
    [JsonRequired]
    public long MaximumMessageBytes { get; init => field = Positive(value, nameof(MaximumMessageBytes)); } = Positive(MaximumMessageBytes, nameof(MaximumMessageBytes));

    private static int Positive(int value, string name) => value > 0 ? value : throw new ArgumentOutOfRangeException(name);
    private static long Positive(long value, string name) => value > 0 ? value : throw new ArgumentOutOfRangeException(name);
}
