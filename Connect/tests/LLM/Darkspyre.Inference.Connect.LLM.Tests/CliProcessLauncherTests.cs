using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class CliProcessLauncherTests
{
    [Fact]
    public void NativePlanKeepsArgumentsStructured()
    {
        string[] arguments = ["", "two words", "a\"b", "%PATH%!", "line\nbreak", "last\\", "&|<>^()", "\u03A9"];
        var start = new ProcessStartInfo();

        CliProcessLauncher.ConfigureWindows(start, "provider.exe", arguments);

        Assert.Equal("provider.exe", start.FileName);
        Assert.Equal(arguments, start.ArgumentList);
        Assert.Empty(start.Arguments);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NpmNodePlanPreservesLiteralArgumentsAndLocalNodePrecedence(bool adjacentNode, bool crlf)
    {
        using var fixture = new LauncherFixture();
        string shim = NpmShim("entry.js");
        string command = fixture.Write("claude.cmd", crlf ? shim.Replace("\n", "\r\n", StringComparison.Ordinal) : shim);
        string entry = fixture.Write("entry.js", "// fixture entry");
        string pathNode = fixture.Write(Path.Combine("runtime", "node.exe"), string.Empty);
        string node = adjacentNode ? fixture.Write("node.exe", string.Empty) : pathNode;
        string[] arguments = ["", "json \"quote\"", "%PATH%!", "line\nbreak", "last\\", "&|<>^()", "\u03A9"];
        var start = new ProcessStartInfo();
        start.Environment["PATH"] = Path.GetDirectoryName(pathNode);
        start.Environment["PATHEXT"] = ".COM;.js;.EXE;.CMD;.BAT";

        CliProcessLauncher.ConfigureWindows(start, command, arguments);

        Assert.Equal(node, start.FileName);
        Assert.Equal(new[] { entry }.Concat(arguments), start.ArgumentList);
        Assert.Equal(".COM;.EXE;.CMD;.BAT", start.Environment["PATHEXT"]);
        Assert.Empty(start.Arguments);
    }

    [Fact]
    public void BareCommandFindsItsBatchFileOnPathBeforeTheExtensionlessNpmShellFile()
    {
        using var fixture = new LauncherFixture();
        string command = fixture.Write("agy.cmd", "@echo off\r\n");
        _ = fixture.Write("agy", "#!/bin/sh\n");
        var start = new ProcessStartInfo();
        start.Environment["PATH"] = fixture.Root;

        CliProcessLauncher.ConfigureWindows(start, "agy", ["--version"]);

        Assert.EndsWith("cmd.exe", start.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"\"{command}\"", start.Arguments, StringComparison.Ordinal);
        Assert.Empty(start.ArgumentList);
    }

    [Fact]
    public void NativeExecutableTakesPrecedenceWithinTheSamePathDirectory()
    {
        using var fixture = new LauncherFixture();
        string executable = fixture.Write("claude.exe", string.Empty);
        _ = fixture.Write("claude.cmd", "@echo off\r\n");
        var start = new ProcessStartInfo();
        start.Environment["PATH"] = fixture.Root;

        CliProcessLauncher.ConfigureWindows(start, "claude", ["--version"]);

        Assert.Equal(executable, start.FileName);
        Assert.Equal(["--version"], start.ArgumentList);
        Assert.Empty(start.Arguments);
    }

    [Theory]
    [InlineData("echo modified\n", "entry.js")]
    [InlineData("", "C:\\entry.js")]
    [InlineData("", "\\entry.js")]
    public void ModifiedOrNonRelativeNpmShimDoesNotBypassItsBatchInstructions(string prefix, string entry)
    {
        using var fixture = new LauncherFixture();
        string command = fixture.Write("claude.cmd", prefix + NpmShim(entry));
        _ = fixture.Write("node.exe", string.Empty);
        _ = fixture.Write("entry.js", string.Empty);

        Assert.Throws<ArgumentException>(() =>
            CliProcessLauncher.ConfigureWindows(new ProcessStartInfo(), command, ["not%batch-safe"]));
    }

    [Fact]
    public void MissingNodeEntryUsesTheBatchFallback()
    {
        using var fixture = new LauncherFixture();
        string command = fixture.Write("claude.cmd", NpmShim("missing.js"));
        _ = fixture.Write("node.exe", string.Empty);
        var start = new ProcessStartInfo();

        CliProcessLauncher.ConfigureWindows(start, command, ["--version"]);

        Assert.EndsWith("cmd.exe", start.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(start.ArgumentList);
    }

    [Theory]
    [InlineData(".js")]
    [InlineData(".mjs")]
    [InlineData(".cjs")]
    public void ExplicitJavaScriptEntryUsesStructuredNodeArguments(string extension)
    {
        using var fixture = new LauncherFixture();
        string entry = fixture.Write("provider" + extension, string.Empty);
        string node = fixture.Write("node.exe", string.Empty);
        var start = new ProcessStartInfo();

        CliProcessLauncher.ConfigureWindows(start, entry, ["json \"quote\"", "last\\"]);

        Assert.Equal(node, start.FileName);
        Assert.Equal([entry, "json \"quote\"", "last\\"], start.ArgumentList);
        Assert.Empty(start.Arguments);
    }

    [Fact]
    public void BatchPlanQuotesSpacesUnicodeAndSupportedMetacharactersWithoutCrtEscaping()
    {
        string arguments = CliProcessLauncher.BuildBatchArguments(
            "cmd.exe", "C:\\tools & apps\\provider.cmd", ["", "two words", "\u03A9", "&|<>^()", "a\\b"]);

        Assert.Equal("/d /q /v:off /s /c \"\"C:\\tools & apps\\provider.cmd\" \"\" \"two words\" \"\u03A9\" \"&|<>^()\" \"a\\b\"\"", arguments);
    }

    [Theory]
    [InlineData("secret\"quote")]
    [InlineData("secret%PATH%")]
    [InlineData("secret!expand!")]
    [InlineData("secret\rreturn")]
    [InlineData("secret\nline")]
    [InlineData("secret\0nul")]
    [InlineData("secret\\")]
    public void BatchFallbackRejectsUnrepresentableArgumentsWithoutEchoingThem(string value)
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() =>
            CliProcessLauncher.BuildBatchArguments("cmd.exe", "provider.cmd", [value]));

        Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchLimitIncludesInterpreterQuotesAndSwitches()
    {
        const string interpreter = "C:\\Windows\\System32\\cmd.exe";
        int overhead = interpreter.Length + 3
            + CliProcessLauncher.BuildBatchArguments(interpreter, "provider.cmd", [""]).Length;
        int maximum = CliProcessLauncher.MaximumBatchCommandCharacters - overhead;

        string atLimit = CliProcessLauncher.BuildBatchArguments(interpreter, "provider.cmd", [new string('x', maximum)]);

        Assert.Equal(CliProcessLauncher.MaximumBatchCommandCharacters, interpreter.Length + 3 + atLimit.Length);
        Assert.Throws<ArgumentException>(() =>
            CliProcessLauncher.BuildBatchArguments(interpreter, "provider.cmd", [new string('x', maximum + 1)]));
    }

    [Fact]
    public void NativePlanRejectsNulBeforeStartingAProcess() =>
        Assert.Throws<ArgumentException>(() =>
            CliProcessLauncher.ConfigureWindows(new ProcessStartInfo(), "provider.exe", ["a\0b"]));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeExecutionPreservesAllLiteralArguments(bool antigravity)
    {
        using var fixture = new LauncherFixture();
        string[] arguments = ["", "two words", "a\"b", "%PATH%!", "line\nbreak", "last\\", "&|<>^()", "\u03A9"];

        string output = await RunAsync(antigravity, DotnetHost, [ProbeAssembly, .. arguments], fixture.Root);

        Assert.Equal(arguments, JsonSerializer.Deserialize<string[]>(output));
    }

    [WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchExecutionPreservesSpacesUnicodeMetacharactersAndEmptyArguments(bool antigravity)
    {
        using var fixture = new LauncherFixture();
        string command = fixture.Write("cli & probe.cmd", ProbeShim);
        string[] arguments = ["", "two words", "\u03A9", "& echo escaped > injected.txt", "|<>^()", "a\\b"];

        string output = await RunAsync(antigravity, command, arguments, fixture.Root);

        Assert.Equal(arguments, JsonSerializer.Deserialize<string[]>(output));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "injected.txt")));
    }

    [WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CancellingABatchInvocationEndsItsNativeChild(bool antigravity) => CliProcessTerminationTests.Bounded(async () =>
    {
        using var fixture = new LauncherFixture();
        string command = fixture.Write("cli & probe.cmd", ProbeShim);
        using var cancellation = new CancellationTokenSource();
        int child = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (antigravity)
            {
                await foreach (AntigravityCliProcessEvent value in new AntigravityCliProcessRunner(new AntigravityCliSessionProcessFactory()).StreamAsync(
                    new AntigravityCliInvocation(command, ["--wait"], StandardInput: null, fixture.Root), cancellation.Token))
                {
                    child = CliProcessTerminationTests.ParseProcessId(Assert.IsType<AntigravityCliOutputLine>(value).Line);
                    await cancellation.CancelAsync();
                }
            }
            else
            {
                await foreach (ClaudeCliProcessEvent value in new ClaudeCliProcessRunner().StreamAsync(
                    new ClaudeCliInvocation(command, ["--wait"], StandardInput: null, fixture.Root), cancellation.Token))
                {
                    child = CliProcessTerminationTests.ParseProcessId(Assert.IsType<ClaudeCliOutputLine>(value).Line);
                    await cancellation.CancelAsync();
                }
            }
        });

        await CliProcessTerminationTests.AssertGoneAsync(child);
    });

    [WindowsNodeTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NpmNodeExecutionPreservesQuotesExpansionCharactersAndLineBreaks(bool antigravity)
    {
        using var fixture = new LauncherFixture();
        string command = fixture.Write("cli & probe.cmd", NpmShim("entry.js"));
        _ = fixture.Write("entry.js", "console.log(JSON.stringify(process.argv.slice(2)));\n");
        string[] arguments = ["", "two words", "a\"b", "%PATH%!", "line\nbreak", "last\\", "&|<>^()", "\u03A9"];

        string output = await RunAsync(antigravity, command, arguments, fixture.Root);

        Assert.Equal(arguments, JsonSerializer.Deserialize<string[]>(output));
    }

    private static async Task<string> RunAsync(bool antigravity, string executable, string[] arguments, string workingDirectory)
    {
        if (antigravity)
        {
            AntigravityCliProcessResult result = await new AntigravityCliProcessRunner(new AntigravityCliSessionProcessFactory()).RunAsync(
                new AntigravityCliInvocation(executable, arguments, StandardInput: null, workingDirectory));
            Assert.Equal(0, result.ExitCode);
            return result.StandardOutput;
        }
        else
        {
            ClaudeCliProcessResult result = await new ClaudeCliProcessRunner().RunAsync(
                new ClaudeCliInvocation(executable, arguments, StandardInput: null, workingDirectory));
            Assert.Equal(0, result.ExitCode);
            return result.StandardOutput;
        }
    }

    private static string DotnetHost => Path.GetFullPath(Path.Combine(
        RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));

    private static string ProbeAssembly => Path.Combine(AppContext.BaseDirectory,
        "Darkspyre.Inference.Connect.LLM.ProcessProbe.dll");

    private static string ProbeShim => $"@echo off\r\n\"{DotnetHost}\" \"{ProbeAssembly}\" %*\r\n";

    // Generated npm/cmd-shim argless Node wrapper, with only its entry path substituted.
    private static string NpmShim(string entry) =>
        "@ECHO off\nGOTO start\n:find_dp0\nSET dp0=%~dp0\nEXIT /b\n:start\nSETLOCAL\nCALL :find_dp0\n\n"
        + "IF EXIST \"%dp0%\\node.exe\" (\n  SET \"_prog=%dp0%\\node.exe\"\n) ELSE (\n  SET \"_prog=node\"\n)\n\n"
        + $"endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & set PATHEXT=%PATHEXT:;.JS;=;% & \"%_prog%\"  \"%dp0%\\{entry}\" %*\n";

    private sealed class LauncherFixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("cli-launch \u03A9-").FullName;

        internal string Write(string path, string contents)
        {
            string fullPath = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, contents);
            return fullPath;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

internal class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows for actual cmd.exe/process execution.";
    }
}

internal sealed class WindowsNodeTheoryAttribute : WindowsTheoryAttribute
{
    public WindowsNodeTheoryAttribute()
    {
        if (Skip is not null) return;
        var start = new ProcessStartInfo();
        CliProcessLauncher.ConfigureWindows(start, "node.exe", []);
        if (!File.Exists(start.FileName)) Skip = "Requires an installed Node executable for actual npm entry execution.";
    }
}
