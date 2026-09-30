using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.ProcessProbe;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (args is ["--wait"])
        {
            Console.WriteLine(Environment.ProcessId);
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        }
        else
        {
            Console.WriteLine(JsonSerializer.Serialize(args));
        }
    }
}
