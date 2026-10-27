namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>The host data folder of a test: its own temporary folder, deleted with it.</summary>
internal sealed class TestDataFolder : ILlmDataFolder, IDisposable
{
    internal string Path { get; } = Directory.CreateTempSubdirectory("connect-data-").FullName;

    public ValueTask<string> ReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Path);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
