using System.Text.Json;
using Xunit;

namespace GpuArbiter.Splash.Tests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("splash-packed-q4", 1, true)]
    [InlineData("unknown-format", 1, false)]
    [InlineData("splash-packed-q4", 2, false)]
    public void InstalledSchemaThreePackValidatesFormatObjectAndArtifactLengths(string format, int size, bool valid)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AGENTS.md"))) repo = repo.Parent;
        Assert.NotNull(repo);
        var directory = Path.Combine(repo.FullName, "tmp", "splash-test-packs", Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var part in new[] { "target", "draft", "tokenizer" }) Directory.CreateDirectory(Path.Combine(directory, part));
            var binary = Path.Combine(directory, "target", "tiny.bin");
            File.WriteAllBytes(binary, [0]);
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
            {
                schema_version = 3, format = new { name = format }, artifacts = new[] { new { path = "target/tiny.bin", size } }
            }));
            var options = new SplashOptions
            {
                Models = [new() { Pack = directory }], Python = binary, Server = binary, Binary = binary,
                LogDir = directory, ScratchDir = directory
            };
            if (valid) options.Validate();
            else Assert.Throws<InvalidOperationException>(() => options.Validate());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void DuplicateModelsAndCollidingPrivatePortFailConfiguration()
    {
        var options = new SplashOptions { Models = [new(), new()] };
        Assert.Throws<InvalidOperationException>(() => options.Validate(checkAssets: false));
        options.Models.RemoveAt(1);
        options.Port = 8084;
        Assert.Throws<InvalidOperationException>(() => options.Validate(checkAssets: false));
    }
}
