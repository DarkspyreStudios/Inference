using System.Globalization;

namespace Darkspyre.Inference.Common.Setup;

/// <summary>The size text setup surfaces show.</summary>
public static class SetupByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// Formats <paramref name="bytes"/> in binary units with at most one decimal place, invariant culture:
    /// "512 B", "1.5 KB", "465 MB", "1.5 GB", "16.2 TB". Units are B, KB, MB, GB and TB of 1024.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative.</exception>
    public static string Format(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {Units[unit]}");
    }
}
