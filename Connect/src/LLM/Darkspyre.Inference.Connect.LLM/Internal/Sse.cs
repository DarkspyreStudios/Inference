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

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (line is null) { break; }
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
