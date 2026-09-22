using System.Runtime.CompilerServices;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class Sse
{
    public static async IAsyncEnumerable<string> ReadDataPayloadsAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (dataLines.Count > 0)
                {
                    yield return string.Join('\n', dataLines);
                    dataLines.Clear();
                }

                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                string value = line[5..].TrimStart(' ');
                dataLines.Add(value);
            }
        }

        if (dataLines.Count > 0)
        {
            yield return string.Join('\n', dataLines);
        }
    }
}
