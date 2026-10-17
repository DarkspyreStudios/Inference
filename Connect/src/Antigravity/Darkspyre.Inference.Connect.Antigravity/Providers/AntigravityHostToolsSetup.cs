using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Connect.HostTools;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// AGY's global configuration for host tools: one MCP server entry that starts the host-tools MCP program, and the
/// permission rule that lets headless AGY call its tools. AGY reads both only from the user's global configuration.
/// </summary>
internal static class AntigravityHostToolsSetup
{
    /// <summary>The allow rule for every tool of the host-tools server. A rule without "/tool" does not match on AGY 1.3.2.</summary>
    internal static readonly string AllowRule = $"mcp({HostToolsProtocol.ServerName}/*)";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HashSet<string> Configured = new(StringComparer.Ordinal);

    /// <summary>The MCP program in hosttools/mcp/ beside this library.</summary>
    internal static string McpProgram { get; } = Path.Combine(
        Path.GetDirectoryName(typeof(AntigravityHostToolsSetup).Assembly.Location)
            ?? throw new InvalidDataException("The Antigravity provider has no physical location."),
        "hosttools", "mcp", HostToolsProtocol.McpExecutableName + (OperatingSystem.IsWindows() ? ".exe" : ""));

    /// <summary>
    /// The AGY CLI settings file that holds permission rules (https://antigravity.google/docs/permissions?tab=cli).
    /// </summary>
    internal static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli", "settings.json");

    /// <summary>
    /// Points AGY's host-tools server entry at this build's MCP program and adds the allow rule when it is missing. It runs
    /// once per process for each AGY executable.
    /// </summary>
    /// <gotcha>Another Darkspyre build that opens an AGY session afterwards points the shared entry at its own MCP program.</gotcha>
    internal static async Task EnsureAsync(IAntigravityCliRunner runner, string executable, string workingDirectory,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Configured.Contains(executable)) return;
            // "mcp add" adds or replaces the entry, so a moved or rebuilt app replaces a stale program path.
            AntigravityCliProcessResult added = await runner.RunAsync(new AntigravityCliInvocation(executable,
                ["mcp", "add", HostToolsProtocol.ServerName, McpProgram], null, workingDirectory), cancellationToken).ConfigureAwait(false);
            if (added.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Antigravity CLI could not register the host-tools MCP server (exit {added.ExitCode}): {added.StandardError.Trim()}");
            }
            await AllowAsync(SettingsPath, cancellationToken).ConfigureAwait(false);
            Configured.Add(executable);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Adds <see cref="AllowRule"/> to permissions.allow in <paramref name="path"/>, keeping every other setting.</summary>
    internal static async Task AllowAsync(string path, CancellationToken cancellationToken)
    {
        JsonObject settings = File.Exists(path)
            ? JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)) as JsonObject
                ?? throw new InvalidDataException($"The Antigravity CLI settings file is not a JSON object: {path}")
            : [];
        if (settings["permissions"] is not JsonObject permissions) settings["permissions"] = permissions = [];
        if (permissions["allow"] is not JsonArray allow) permissions["allow"] = allow = [];
        if (allow.Any(rule => rule?.GetValueKind() == JsonValueKind.String && rule.GetValue<string>() == AllowRule)) return;
        allow.Add(AllowRule);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // AGY may read the file at any time; a renamed complete file never shows it a partial write.
        string staged = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(staged, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken)
            .ConfigureAwait(false);
        File.Move(staged, path, overwrite: true);
    }
}
