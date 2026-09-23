using System.Text.Json;
using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

/// <summary>Checkpoint-owned serving parameters. No upstream calibration defaults are substituted.</summary>
internal sealed class LayaAgentConfiguration
{
    private readonly double[] _temperatures;
    private readonly Dictionary<string, double> _byOptions;
    public int MaximumLength { get; }
    public int MaximumHeadLength { get; }

    private LayaAgentConfiguration(int maximumLength, int maximumHeadLength, double[] temperatures, Dictionary<string, double> byOptions)
    {
        MaximumLength = maximumLength;
        MaximumHeadLength = maximumHeadLength;
        _temperatures = temperatures;
        _byOptions = byOptions;
    }

    public static async Task<LayaAgentConfiguration> LoadAsync(PersistenceFileReference source, CancellationToken cancellationToken)
    {
        await using Stream? stream = await source.Store.OpenReadAsync(source.StoreName, source.AssetName, cancellationToken).ConfigureAwait(false);
        if (stream is null) { throw new InvalidDataException("The Laya agent configuration is missing."); }
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Parse(document.RootElement);
    }

    internal static LayaAgentConfiguration Parse(JsonElement root)
    {
        try
        {
            int maximumLength = root.GetProperty("max_len").GetInt32();
            int maximumHeadLength = root.GetProperty("head_max_len").GetInt32();
            if (maximumHeadLength < 16 || maximumLength < (long)maximumHeadLength + 4)
            { throw new InvalidDataException("Laya sequence limits cannot accommodate its head and framing."); }
            double[] temperatures = root.GetProperty("temperature").EnumerateArray().Select(ReadTemperature).ToArray();
            if (temperatures.Length != 3) { throw new InvalidDataException("Laya requires one temperature per question type."); }
            var byOptions = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (JsonProperty bucket in root.GetProperty("temperature_by_options").EnumerateObject())
            {
                if (bucket.Name is not ("choice:2" or "choice:3-5" or "choice:6-10" or "choice:11+" or "score:3-5" or "noul:2")
                    || !byOptions.TryAdd(bucket.Name, ReadTemperature(bucket.Value)))
                { throw new InvalidDataException("Unsupported or duplicate Laya temperature bucket."); }
            }
            return new(maximumLength, maximumHeadLength, temperatures, byOptions);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("Invalid Laya agent configuration.", exception); }
    }

    public double Temperature(int questionType, int optionCount)
    {
        string? bucket = questionType switch
        {
            0 when optionCount == 2 => "choice:2",
            0 when optionCount is >= 3 and <= 5 => "choice:3-5",
            0 when optionCount is >= 6 and <= 10 => "choice:6-10",
            0 when optionCount >= 11 => "choice:11+",
            1 when optionCount is >= 3 and <= 5 => "score:3-5",
            2 when optionCount == 2 => "noul:2",
            _ => null
        };
        return bucket is not null && _byOptions.TryGetValue(bucket, out double temperature) ? temperature : _temperatures[questionType];
    }

    private static double ReadTemperature(JsonElement element)
    {
        double value = element.GetDouble();
        return double.IsFinite(value) && value > 0 ? value : throw new InvalidDataException("Laya temperatures must be finite and positive.");
    }
}
