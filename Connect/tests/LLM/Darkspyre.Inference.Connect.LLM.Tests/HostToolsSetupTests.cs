using System.Text.Json.Nodes;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>AGY's settings file belongs to the user; the host-tools allow rule is added without touching anything else.</summary>
public sealed class HostToolsSetupTests
{
    [Fact]
    public async Task AllowRuleIsAddedOnceAndOtherSettingsAreKept()
    {
        string directory = Directory.CreateTempSubdirectory("agy-settings").FullName;
        try
        {
            string path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, """
                {"trustedWorkspaces":["/work"],"permissions":{"allow":["mcp(other/*)"],"deny":["shell(rm)"]}}
                """);

            await AntigravityHostToolsSetup.AllowAsync(path, CancellationToken.None);
            await AntigravityHostToolsSetup.AllowAsync(path, CancellationToken.None);

            JsonObject settings = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            Assert.Equal("/work", settings["trustedWorkspaces"]![0]!.GetValue<string>());
            Assert.Equal(["mcp(other/*)", AntigravityHostToolsSetup.AllowRule],
                settings["permissions"]!["allow"]!.AsArray().Select(rule => rule!.GetValue<string>()));
            Assert.Equal("shell(rm)", settings["permissions"]!["deny"]![0]!.GetValue<string>());
            Assert.Single(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
