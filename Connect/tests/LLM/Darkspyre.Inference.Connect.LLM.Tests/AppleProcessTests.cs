using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AppleProcessTests
{
    [MacTheory]
    [InlineData("startup", InferenceFailureCode.TimedOut)]
    [InlineData("generation", InferenceFailureCode.TimedOut)]
    [InlineData("terminal-hang", InferenceFailureCode.TimedOut)]
    [InlineData("stderr", InferenceFailureCode.ProtocolFailure)]
    [InlineData("malformed", InferenceFailureCode.ProtocolFailure)]
    [InlineData("extra-terminal", InferenceFailureCode.ProtocolFailure)]
    [InlineData("exit", InferenceFailureCode.ExecutionFailed)]
    public async Task FailuresReapOwnedProcessAndNeverDeliverTerminal(string behavior, InferenceFailureCode code)
    {
        using var helper = new FakeHelper(behavior);
        var emitted = new List<JsonElement>();
        var error = await Assert.ThrowsAsync<InferenceException>(async () =>
        {
            await foreach (var frame in new AppleProcessRunner().RunAsync(helper.Path, new("generate", "r"),
                new(StartupTimeoutSeconds: 1, RequestTimeoutSeconds: 2, CancellationGraceSeconds: 1, MaximumStderrBytes: 16)))
                emitted.Add(frame);
        });
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain(emitted, f => f.GetProperty("type").GetString() == "terminal");
        helper.AssertReaped();
    }

    [MacTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationBeforeOrDuringGenerationPreservesIntent(bool afterSnapshot)
    {
        using var helper = new FakeHelper("cancel");
        using var cancellation = new CancellationTokenSource();
        var emitted = new List<JsonElement>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var frame in new AppleProcessRunner().RunAsync(helper.Path, new("generate", "r"),
                new(CancellationGraceSeconds: 1), cancellation.Token))
            {
                emitted.Add(frame);
                if (!afterSnapshot || frame.GetProperty("type").GetString() == "text") cancellation.Cancel();
            }
        });
        Assert.DoesNotContain(emitted, f => f.GetProperty("type").GetString() == "terminal");
        helper.AssertReaped();
    }

    [MacFact]
    public async Task AbandoningEnumerationStopsAndReapsHelper()
    {
        using var helper = new FakeHelper("cancel");
        await foreach (var frame in new AppleProcessRunner().RunAsync(helper.Path, new("generate", "r"), new(CancellationGraceSeconds: 1)))
            if (frame.GetProperty("type").GetString() == "text") break;
        helper.AssertReaped();
    }

    [MacFact]
    public async Task TerminalIsReleasedOnlyAfterSuccessfulChildExit()
    {
        using var helper = new FakeHelper("success");
        var frames = await AppleFoundationModelsTests.Read(new AppleProcessRunner().RunAsync(helper.Path, new("generate", "r"), new()));
        Assert.Equal("terminal", frames[^1].GetProperty("type").GetString());
        helper.AssertReaped();
    }

    private sealed class FakeHelper : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apple-process-test-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(_directory, "helper.sh");
        internal FakeHelper(string behavior)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path, """
                #!/bin/bash
                echo $$ > "$0.pid"
                """ + "\n" + (behavior == "startup" ? "read -r ignored\nexit 0\n" : "") +
                "printf '%s\\n' '" + AppleFoundationModelsTests.Hello + "'\nread -r request\n" + (behavior switch
                {
                    "stderr" => "printf '%050d' 0 >&2\nread -r ignored\n",
                    "malformed" => "echo 'not-json'\nexit 0\n",
                    "exit" => "exit 9\n",
                    "generation" => "read -r ignored\nexit 0\n",
                    "terminal-hang" => "echo '{\"type\":\"terminal\",\"id\":\"r\",\"sequence\":1}'\nread -r ignored\nexit 0\n",
                    "extra-terminal" => "echo '{\"type\":\"terminal\",\"id\":\"r\",\"sequence\":1}'\necho '{\"type\":\"terminal\",\"id\":\"r\",\"sequence\":2}'\n",
                    "cancel" => "echo '{\"type\":\"text\",\"id\":\"r\",\"sequence\":1,\"content\":\"partial\"}'\nread -r ignored\nexit 0\n",
                    _ => "echo '{\"type\":\"terminal\",\"id\":\"r\",\"sequence\":1,\"status\":\"unknown\",\"content\":\"\"}'\n"
                }));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        internal void AssertReaped()
        {
            int pid = int.Parse(File.ReadAllText(Path + ".pid"), System.Globalization.CultureInfo.InvariantCulture);
            try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited, "Owned helper is still running."); }
            catch (ArgumentException) { /* A reaped process no longer has a process table entry. */ }
        }
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}

internal sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute() { if (!Supported) Skip = "Requires an Apple Silicon Mac running macOS 26 or later."; }
    internal static bool Supported => OperatingSystem.IsMacOSVersionAtLeast(26) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
}
internal sealed class MacTheoryAttribute : TheoryAttribute
{
    public MacTheoryAttribute() { if (!MacFactAttribute.Supported) Skip = "Requires an Apple Silicon Mac running macOS 26 or later."; }
}
