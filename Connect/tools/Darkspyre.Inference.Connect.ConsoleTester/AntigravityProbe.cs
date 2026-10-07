using System.Diagnostics;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.ConsoleTester;

/// <summary>
/// Explicit qualification of Antigravity native sessions through the public agent contract. Each case runs in
/// a temporary workspace holding one fixture. No configuration is edited and no case is retried.
/// </summary>
internal sealed class AntigravityProbe(LlmAgentProviderService agents, string connectionId, string model, string workspace,
    LlmProviderCapabilities capabilities)
{
    private static readonly JsonElement Schema = JsonElement.Parse("""
        {"type":"object","properties":{"word":{"type":"string"}},"required":["word"]}
        """);

    private readonly string _marker = $"marker-{Guid.NewGuid():N}"[..15];
    private int _turns;

    public int Turns => _turns;

    public IReadOnlyList<ProbeCase> Cases =>
    [
        new("text", TextAsync), new("structured", StructuredAsync), new("native-tools", NativeToolsAsync),
        new("session", SessionAsync), new("resume", ResumeAsync), new("controls", ControlsAsync), new("cancel", CancelAsync)
    ];

    public static async Task<int> RunAsync(LlmProviderService llms, LlmAgentProviderService agents,
        ConsoleInvocation invocation, CancellationToken token)
    {
        var options = ProviderProbeOptions.Parse(invocation.CommandArguments, invocation.Provider.Definition.DefaultModelId);
        bool ready;
        try
        {
            ready = await llms.IsAuthenticatedAsync(invocation.Provider.ConnectionId, token);
        }
        catch (LlmProviderException)
        {
            ready = false;
        }
        Console.WriteLine($"Antigravity probe: CLI {await CliVersionAsync(token)}; explicit subscription turns; no automatic retries.");
        string workspace = Directory.CreateTempSubdirectory("darkspyre-agy-probe").FullName;
        try
        {
            LlmProviderCapabilities capabilities = ready
                ? await llms.GetModelCapabilitiesAsync(invocation.Provider.ConnectionId, options.Model, token)
                : new();
            var probe = new AntigravityProbe(agents, invocation.Provider.ConnectionId, options.Model, workspace, capabilities);
            await File.WriteAllTextAsync(Path.Combine(workspace, "fixture.txt"), probe._marker, token);
            int exitCode = await ProbeRunner.RunAsync(probe.Cases, options, ready, Console.Out, token);
            Console.WriteLine($"Turns sent: {probe.Turns}");
            return exitCode;
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private async Task<string> TextAsync(CancellationToken token)
    {
        await using ILlmAgentSession session = await StartAsync(token);
        TurnSummary turn = await RunAsync(session, Turn("Reply with the single word OK. Use no tools."), token);
        Require(turn.Status == LlmAgentTurnStatus.Completed && turn.Text.Contains("OK", StringComparison.OrdinalIgnoreCase),
            "Expected a completed reply containing OK.");
        Require(turn.StreamedText == turn.Text, "Streamed deltas do not reconcile with the completed message.");
        return $"text streamed and completed; usage {(turn.Usage is null ? "unavailable" : "reported")}";
    }

    private async Task<string> StructuredAsync(CancellationToken token)
    {
        await using ILlmAgentSession session = await StartAsync(token);
        TurnSummary turn = await RunAsync(session, Turn("Return word set to sample. Use no tools.") with { OutputSchema = Schema }, token);
        Require(turn.Status == LlmAgentTurnStatus.Completed && turn.Snapshots == 1, "Expected one canonical structured result.");
        Require(turn.StreamedText.Length == 0, "Provisional answer text leaked into the schema turn.");
        using JsonDocument document = JsonDocument.Parse(turn.Text);
        Require(document.RootElement.TryGetProperty("word", out _), "The canonical result lacks the required property.");
        return "canonical structured output validated; no provisional text streamed";
    }

    private async Task<string> NativeToolsAsync(CancellationToken token)
    {
        await using ILlmAgentSession session = await StartAsync(token);
        string fixture = Path.Combine(workspace, "fixture.txt");
        TurnSummary turn = await RunAsync(session, Turn($"Use view_file to read {fixture} and reply with its contents only."), token);
        Require(turn.Status == LlmAgentTurnStatus.Completed, "The native tool turn did not complete.");
        Require(turn.ToolOutputs > 0, "No native tool observation reported output.");
        Require(turn.Text.Contains(_marker, StringComparison.Ordinal), "The reply does not contain the fixture marker.");
        return "native tool observation with reported output";
    }

    private async Task<string> SessionAsync(CancellationToken token)
    {
        await using ILlmAgentSession session = await StartAsync(token);
        TurnSummary first = await RunAsync(session, Turn($"Remember the code {_marker}. Reply with OK only. Use no tools."), token);
        TurnSummary second = await RunAsync(session, Turn("Which code did I ask you to remember? Reply with it only. Use no tools."), token);
        Require(first.Status == LlmAgentTurnStatus.Completed && second.Status == LlmAgentTurnStatus.Completed,
            "A session turn did not complete.");
        Require(second.Text.Contains(_marker, StringComparison.Ordinal), "The second turn did not recall the first.");
        return "two turns in one process retained context";
    }

    private async Task<string> ResumeAsync(CancellationToken token)
    {
        string threadId;
        await using (ILlmAgentSession session = await StartAsync(token))
        {
            threadId = session.ThreadId;
            TurnSummary first = await RunAsync(session, Turn($"Remember the code {_marker}. Reply with OK only. Use no tools."), token);
            Require(first.Status == LlmAgentTurnStatus.Completed, "The first turn did not complete.");
        }
        await using ILlmAgentSession resumed = await agents.ResumeSessionAsync(connectionId, threadId, new LlmAgentResumeOptions
        {
            ModelId = model,
            Runtime = Runtime()
        }, token);
        Require(resumed.ThreadId == threadId, "Resume returned a different conversation.");
        TurnSummary second = await RunAsync(resumed, Turn("Which code did I ask you to remember? Reply with it only. Use no tools."), token);
        Require(second.Status == LlmAgentTurnStatus.Completed, "The resumed turn did not complete.");
        Require(second.Text.Contains(_marker, StringComparison.Ordinal), "The resumed conversation did not recall the code.");
        return "a new process resumed the exact conversation ID";
    }

    private async Task<string> ControlsAsync(CancellationToken token)
    {
        await using ILlmAgentSession session = await StartAsync(token);
        string threadId = session.ThreadId;
        LlmReasoningLevel? effort = capabilities.ReasoningLevels.Count == 0
            ? null : capabilities.ReasoningLevels.Order().First();
        if (capabilities.ReasoningLevels.Count <= 1)
        {
            LlmReasoningLevel conflict = new[] { LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High }
                .First(level => !capabilities.ReasoningLevels.Contains(level));
            bool rejected = false;
            try
            {
                await Drain(session.RunTurnAsync(Turn("Reply with OK only. Use no tools.") with { ReasoningLevel = conflict }, token));
            }
            catch (InferenceException error) when (error.Code == InferenceFailureCode.InvalidRequest)
            {
                rejected = true;
            }
            Require(rejected, "An unsupported selected-model effort was not rejected locally.");
        }
        TurnSummary selected = await RunAsync(session, Turn("Reply with OK only. Use no tools.") with { ReasoningLevel = effort }, token);
        TurnSummary schema = await RunAsync(session, Turn("Return word set to sample. Use no tools.") with { OutputSchema = Schema }, token);
        TurnSummary plain = await RunAsync(session, Turn("Reply with OK only. Use no tools."), token);
        Require(selected.Status == LlmAgentTurnStatus.Completed && schema.Status == LlmAgentTurnStatus.Completed
            && plain.Status == LlmAgentTurnStatus.Completed, "A launch-option change did not complete cleanly.");
        Require(session.ThreadId == threadId, "Changing launch options changed the conversation.");

        await RequireRejectedAsync(() => Drain(session.RunTurnAsync(
            new LlmAgentTurnRequest { Input = [new LlmAgentImageContent("https://example.invalid/image.png")] }, token)));
        await RequireRejectedAsync(() => agents.StartSessionAsync(connectionId, new LlmAgentSessionOptions
        {
            ModelId = model,
            Runtime = new() { WorkingDirectory = workspace, Policy = new LlmAgentHostRuntimePolicy() }
        }, token));
        return "supported controls, schema and schema removal retained the conversation; unsupported controls, input and host policy rejected";
    }

    private async Task<string> CancelAsync(CancellationToken token)
    {
        await using ILlmAgentSession session = await StartAsync(token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        bool cancelled = false;
        Interlocked.Increment(ref _turns);
        try
        {
            await foreach (LlmAgentEvent item in session.RunTurnAsync(Turn("Write a long story about a lighthouse. Use no tools."), cancel.Token))
            {
                if (item is LlmAgentTextDeltaEvent)
                {
                    await cancel.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested && !token.IsCancellationRequested)
        {
            cancelled = true;
        }
        token.ThrowIfCancellationRequested();
        Require(cancelled, "The turn did not observe cancellation after a text delta.");
        await RequireRejectedAsync(() => Drain(session.RunTurnAsync(Turn("Reply with OK."), token)));
        return "cancellation settled the local process; the session refuses further turns";
    }

    private Task<ILlmAgentSession> StartAsync(CancellationToken token) =>
        agents.StartSessionAsync(connectionId, new LlmAgentSessionOptions { ModelId = model, Runtime = Runtime() }, token);

    private LlmAgentNativeRuntimeOptions Runtime() => new() { WorkingDirectory = workspace, Policy = new LlmAgentProviderRuntimePolicy() };

    private static LlmAgentTurnRequest Turn(string text) => new() { Input = [new LlmAgentTextContent(text)] };

    private async Task<TurnSummary> RunAsync(ILlmAgentSession session, LlmAgentTurnRequest request, CancellationToken token)
    {
        Interlocked.Increment(ref _turns);
        var summary = new TurnSummary();
        await foreach (LlmAgentEvent item in session.RunTurnAsync(request, token))
        {
            switch (item)
            {
                case LlmAgentTextDeltaEvent delta:
                    summary.StreamedText += delta.Delta;
                    break;
                case LlmAgentStructuredOutputSnapshotEvent:
                    summary.Snapshots++;
                    break;
                case LlmAgentMessageCompletedEvent message:
                    summary.Text = message.Text;
                    break;
                case LlmAgentToolEvent { Output: not null }:
                    summary.ToolOutputs++;
                    break;
                case LlmAgentUsageEvent usage:
                    summary.Usage = usage.Usage;
                    break;
                case LlmAgentTurnCompletedEvent completed:
                    summary.Completion = completed;
                    break;
            }
        }
        if (summary.Completion is { Status: not LlmAgentTurnStatus.Completed } failure)
        {
            using var diagnostics = new StringWriter();
            Program.WriteAgentCompletion(failure, diagnostics);
            throw new ProbeFailure(diagnostics.ToString().TrimEnd());
        }
        return summary;
    }

    private static async Task Drain(IAsyncEnumerable<LlmAgentEvent> events)
    {
        await foreach (LlmAgentEvent _ in events)
        {
        }
    }

    private static async Task RequireRejectedAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
        {
            return;
        }
        throw new ProbeFailure("An unsupported operation was accepted.");
    }

    private static async Task<string> CliVersionAsync(CancellationToken token)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("agy", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process is null)
            {
                return "unavailable";
            }
            string version = (await process.StandardOutput.ReadToEndAsync(token)).Trim();
            await process.WaitForExitAsync(token);
            return process.ExitCode == 0 && version.Length is > 0 and < 64 ? version : "unavailable";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "unavailable";
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ProbeFailure(message);
        }
    }

    private sealed class TurnSummary
    {
        public LlmAgentTurnCompletedEvent? Completion { get; set; }

        public LlmAgentTurnStatus? Status => Completion?.Status;

        public string StreamedText { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public int Snapshots { get; set; }

        public int ToolOutputs { get; set; }

        public LlmUsage? Usage { get; set; }
    }
}
