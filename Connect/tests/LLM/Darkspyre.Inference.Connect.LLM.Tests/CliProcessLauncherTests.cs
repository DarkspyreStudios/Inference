using System.Diagnostics;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class CliProcessLauncherTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WindowsNativeInvocationRejectsTheCompleteEscapedCommandBeforeLaunch(bool escaped)
    {
        const string executable = @"C:\Tools\claude.exe";
        // Include the quoted executable, argument separator, argument quotes and terminating NUL.
        string admittedArgument = escaped ? new string('"', (32767 - executable.Length - 6) / 2)
            : new string('x', 32767 - executable.Length - 4);
        var admitted = new ProcessStartInfo();
        admitted.Environment["PATH"] = string.Empty;
        CliProcessLauncher.ConfigureWindows(admitted, executable, [admittedArgument]);
        Assert.Equal(executable, admitted.FileName);
        Assert.Equal(admittedArgument, Assert.Single(admitted.ArgumentList));

        string[] arguments = escaped ? [new string('"', 18_000)] : [new string('x', 20_000), new string('x', 20_000)];
        var start = new ProcessStartInfo();
        start.Environment["PATH"] = string.Empty;

        ArgumentException failure = Assert.Throws<ArgumentException>(() =>
            CliProcessLauncher.ConfigureWindows(start, executable, arguments));

        Assert.Contains("32767", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(arguments[0], failure.Message, StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task InteractiveAntigravityPipeDeliversCompleteLineBeforeInputOrExit()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        // Native init arrives while the CLI awaits input; receipt must not wait for EOF.
        string line = new('x', 1500);
        var process = new Process
        {
            StartInfo = CliProcessLauncher.CreateStartInfo("/bin/bash",
                ["-c", "printf '%s\\n' \"$1\"; IFS= read -r value || :", "interactive-init", line],
                Environment.CurrentDirectory)
        };
        try { Assert.True(process.Start()); }
        catch { process.Dispose(); throw; }
        await using var session = new AntigravityCliSessionProcess(process);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Assert.Equal(line, await session.ReadLineAsync(deadline.Token));
        Assert.False(process.HasExited);

        session.CloseInput();
        var result = await session.WaitForExitAsync(deadline.Token);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
    }

    [UnixFact]
    public async Task UnixLookupUsesPathThenUserLocalBinAndPreservesExplicitPathsAndArguments()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string root = Directory.CreateTempSubdirectory("cli-lookup-").FullName;
        try
        {
            string inherited = Path.Combine(root, "inherited");
            string home = Path.Combine(root, "home");
            string local = Path.Combine(home, ".local", "bin");
            Directory.CreateDirectory(inherited);
            Directory.CreateDirectory(local);
            string inheritedCli = Path.Combine(inherited, "claude");
            string localCli = Path.Combine(local, "claude");
            const string script = "#!/usr/bin/env bash\nprintf '%s\\n' \"$1\"\n";
            File.WriteAllText(inheritedCli, script);
            File.WriteAllText(localCli, script);
            File.SetUnixFileMode(inheritedCli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(localCli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string[] arguments = ["literal ; $HOME \"quoted\" text"];
            var start = CliProcessLauncher.CreateStartInfo("claude", [], root);
            string path = string.Join(Path.PathSeparator, inherited, "/usr/bin", "/bin");
            start.Environment["PATH"] = path;

            CliProcessLauncher.ConfigureUnix(start, "claude", arguments, home);
            Assert.Equal(inheritedCli, start.FileName);
            Assert.Equal(arguments, start.ArgumentList);
            Assert.Equal(path, start.Environment["PATH"]);

            File.SetUnixFileMode(inheritedCli, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            start.ArgumentList.Clear();
            CliProcessLauncher.ConfigureUnix(start, "claude", arguments, home);
            Assert.Equal(localCli, start.FileName);
            File.Delete(inheritedCli);
            using (var process = new Process { StartInfo = start })
            {
                Assert.True(process.Start());
                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, process.ExitCode);
                Assert.Equal(arguments[0], output.TrimEnd('\r', '\n'));
            }

            start.ArgumentList.Clear();
            CliProcessLauncher.ConfigureUnix(start, "/bin/echo", arguments, home);
            Assert.Equal("/bin/echo", start.FileName);
            Assert.Equal(arguments, start.ArgumentList);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
