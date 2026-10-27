using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.ConsoleTester;

/// <summary>The tester's data folder for provider files, such as Antigravity's call scratch and host-tools service: <c>data/</c> beside the tester.</summary>
internal sealed class ConsoleDataFolder : ILlmDataFolder
{
    public ValueTask<string> ReadAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Path.Combine(AppContext.BaseDirectory, "data"));
}
