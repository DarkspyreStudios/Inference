using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.ConsoleTester;

/// <summary>
/// Explicit qualification of retained CLI native sessions through the public agent contract. Each case runs in
/// an isolated temporary workspace. No configuration is edited and no case is retried.
/// </summary>
internal sealed class NativeCliProbe(LlmAgentProviderService agents, string connectionId, string model, string workspace,
    LlmProviderCapabilities capabilities, LlmTransport transport, ClaudeCliRecorder? recorder)
{
    private const string BridgePrefix = "mcp__darkspyre-inference-connect__";

    private static readonly JsonElement Schema = JsonElement.Parse("""
        {"type":"object","properties":{"word":{"type":"string"}},"required":["word"]}
        """);

    private readonly string _marker = $"marker-{Guid.NewGuid():N}"[..15];
    private int _turns;

    public int Turns => _turns;

    public IReadOnlyList<ProbeCase> Cases => transport == LlmTransport.AnthropicClaudeCli
        ? [new("cancel", CancelAsync), new("isolation", IsolationAsync), new("large-result", LargeResultAsync),
            new("return-requests", ReturnRequestsAsync)]
        :
    [
        new("text", TextAsync), new("structured", StructuredAsync), new("native-tools", NativeToolsAsync),
        new("session", SessionAsync), new("resume", ResumeAsync), new("controls", ControlsAsync), new("cancel", CancelAsync)
    ];

    public static async Task<int> RunAsync(LlmProviderService llms, LlmAgentProviderService agents,
        ConsoleInvocation invocation, ClaudeCliRecorder? recorder, CancellationToken token)
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
        bool claude = invocation.Provider.Transport == LlmTransport.AnthropicClaudeCli;
        Console.WriteLine($"{(claude ? "Claude" : "Antigravity")} probe: CLI {await CliVersionAsync(claude ? "claude" : "agy", token)}; explicit subscription turns; no automatic retries.");
        string workspace = Directory.CreateTempSubdirectory("darkspyre-native-cli-probe").FullName;
        try
        {
            LlmProviderCapabilities capabilities = ready
                ? await llms.GetModelCapabilitiesAsync(invocation.Provider.ConnectionId, options.Model, token)
                : new();
            var probe = new NativeCliProbe(agents, invocation.Provider.ConnectionId, options.Model, workspace, capabilities,
                invocation.Provider.Transport, recorder);
            if (!claude) await File.WriteAllTextAsync(Path.Combine(workspace, "fixture.txt"), probe._marker, token);
            int exitCode = await ProbeRunner.RunAsync(probe.Cases, options, ready, Console.Out, token);
            Console.WriteLine($"Turn attempts: {probe.Turns}");
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
            string prompt = transport == LlmTransport.AnthropicClaudeCli
                ? "Write a 200-word description of a lighthouse. Use no tools."
                : "Write a long story about a lighthouse. Use no tools.";
            await foreach (LlmAgentEvent item in session.RunTurnAsync(Turn(prompt), cancel.Token))
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
        if (transport == LlmTransport.AnthropicClaudeCli)
            return "caller cancellation observed after a text delta; owned session disposal completed";
        await RequireRejectedAsync(() => Drain(session.RunTurnAsync(Turn("Reply with OK."), token)));
        return "cancellation settled the local process; the session refuses further turns";
    }

    /// <summary>
    /// Under --safe-mode the CLI's init lists only bridge tools and reports the bridge server connected; the turn's
    /// usage events carry the model.
    /// </summary>
    private async Task<string> IsolationAsync(CancellationToken token)
    {
        await using ILlmAgentSession session = await agents.StartSessionAsync(connectionId, new LlmAgentSessionOptions
        {
            ModelId = model,
            Runtime = Runtime(),
            Tools = [Tool("probe_echo", "Returns its text.")],
            ToolHandler = (_, _) => Task.FromResult(LlmToolOutput.FromText("echo"))
        }, token);
        TurnSummary turn = await RunAsync(session, Turn("Reply with the single word OK. Use no tools."), token);
        ClaudeCliRecorder.Recording process = recorder!.Latest();
        Require(process.Arguments.Contains("--safe-mode"), "The CLI was not launched with --safe-mode.");
        JsonObject init = process.Output().FirstOrDefault(line => line["type"]?.GetValue<string>() == "system"
            && line["subtype"]?.GetValue<string>() == "init") ?? throw new ProbeFailure("The CLI reported no init message.");
        string[] tools = [.. (init["tools"] as JsonArray ?? []).Select(tool => tool?.GetValue<string>() ?? string.Empty)];
        string[] outside = [.. tools.Where(tool => !tool.StartsWith(BridgePrefix, StringComparison.Ordinal))];
        Require(outside.Length == 0, $"init.tools lists tools outside the bridge: {string.Join(", ", outside)}.");
        Require(tools.Contains(BridgePrefix + "probe_echo"), "init.tools lacks the Host tool.");
        string? bridge = (init["mcp_servers"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(server => server["name"]?.GetValue<string>() == "darkspyre-inference-connect")?["status"]?.GetValue<string>();
        Require(bridge == "connected", $"The bridge server status is '{bridge ?? "absent"}'.");
        Require(turn.Models.Count > 0 && turn.Models.All(id => id is not null), "A usage event lacks the model.");
        return $"init.tools [{string.Join(", ", tools)}]; bridge connected under --safe-mode; usage model {turn.Models[^1]}";
    }

    /// <summary>A 40,000-token Host tool result, above the CLI's 25,000-token MCP output cap, reaches the model whole.</summary>
    private async Task<string> LargeResultAsync(CancellationToken token)
    {
        string code = $"code{Guid.NewGuid():N}"[..12];
        (string filler, long tokens) = LimitProbe.Filler(40_000);
        await using ILlmAgentSession session = await agents.StartSessionAsync(connectionId, new LlmAgentSessionOptions
        {
            ModelId = model,
            Runtime = Runtime(),
            Tools = [Tool("probe_document", "Returns a long document.")],
            ToolHandler = (_, _) => Task.FromResult(LlmToolOutput.FromText($"{filler}\nCODE: {code}"))
        }, token);
        TurnSummary turn = await RunAsync(session, Turn(
            "Call probe_document once. Its output ends with a line 'CODE: <word>'. Reply with that word only."), token);
        Require(turn.Tools.Count(tool => tool.Tool == "probe_document" && tool.Output is not null) == 1,
            "The tool did not run exactly once.");
        Require(turn.Text.Contains(code, StringComparison.Ordinal),
            $"The reply does not contain the code from the end of the result: '{turn.Text}'.");
        Require(turn.Models.Count > 0 && turn.Models.All(id => id is not null), "A usage event lacks the model.");
        return $"a {tokens}-token tool result reached the model whole; usage model {turn.Models[^1]}";
    }

    /// <summary>
    /// A ReturnRequests turn ends with ToolRequests and the call's name and arguments; no result is written to the
    /// CLI, and the CLI process exits.
    /// </summary>
    private async Task<string> ReturnRequestsAsync(CancellationToken token)
    {
        await using ILlmAgentSession session = await agents.StartSessionAsync(connectionId, new LlmAgentSessionOptions
        {
            ModelId = model,
            Runtime = Runtime(),
            Tools = [Tool("probe_lookup", "Looks up a key.")],
            ToolHandling = LlmAgentToolHandling.ReturnRequests
        }, token);
        TurnSummary turn = await RunAsync(session, Turn("Call probe_lookup with text set to alpha. Do nothing else."), token);
        Require(turn.Completion?.Reason == LlmAgentCompletionReason.ToolRequests,
            $"The turn ended with {turn.Completion?.Reason.ToString() ?? "no completion"}, not ToolRequests.");
        LlmAgentToolEvent call = turn.Tools.SingleOrDefault(tool => tool.Tool == "probe_lookup")
            ?? throw new ProbeFailure("No probe_lookup call was returned.");
        Require(call.Status == "returned" && call.Output is null, $"The call has status '{call.Status}' or an output.");
        Require(call.Arguments.TryGetProperty("text", out JsonElement text) && text.GetString() == "alpha",
            $"The call's arguments are {call.Arguments.GetRawText()}.");
        ClaudeCliRecorder.Recording process = recorder!.Latest();
        string[] calls = [.. process.Output()
            .Where(line => line["type"]?.GetValue<string>() == "control_request"
                && line["request"]?["message"]?["method"]?.GetValue<string>() == "tools/call")
            .Select(line => line["request_id"]?.GetValue<string>() ?? string.Empty)];
        Require(calls.Length > 0, "The CLI asked the bridge for no tools/call.");
        bool answered = process.Input().Any(line => line["type"]?.GetValue<string>() == "control_response"
            && calls.Contains(line["response"]?["request_id"]?.GetValue<string>()));
        Require(!answered, "A tools/call response was written to the CLI.");
        bool exited = await process.ExitedAsync(TimeSpan.FromSeconds(10), token);
        Require(exited, "The CLI process is still running 10 seconds after the turn.");
        return $"ToolRequests with probe_lookup {call.Arguments.GetRawText()}; no result written; the CLI exited";
    }

    private static LlmAgentToolDefinition Tool(string name, string description) => new()
    {
        Name = name,
        Description = description,
        InputSchema = JsonElement.Parse("""{"type":"object","properties":{"text":{"type":"string"}}}""")
    };

    private Task<ILlmAgentSession> StartAsync(CancellationToken token) =>
        agents.StartSessionAsync(connectionId, new LlmAgentSessionOptions { ModelId = model, Runtime = Runtime() }, token);

    private LlmAgentNativeRuntimeOptions Runtime() => new()
    {
        WorkingDirectory = workspace,
        Policy = transport == LlmTransport.AnthropicClaudeCli
            ? new LlmAgentHostRuntimePolicy { Sandbox = LlmAgentSandboxMode.ReadOnly, ApprovalPolicy = LlmAgentApprovalPolicy.Never }
            : new LlmAgentProviderRuntimePolicy()
    };

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
                case LlmAgentToolEvent tool:
                    summary.Tools.Add(tool);
                    if (tool.Output is not null) summary.ToolOutputs++;
                    break;
                case LlmAgentUsageEvent usage:
                    summary.Usage = usage.Usage;
                    summary.Models.Add(usage.ModelId);
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

    private static async Task<string> CliVersionAsync(string executable, CancellationToken token)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, "--version")
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

        public List<LlmAgentToolEvent> Tools { get; } = [];

        public List<string?> Models { get; } = [];
    }
}

/// <summary>Records each Claude CLI session process's arguments and JSON lines for the probe's checks.</summary>
internal sealed class ClaudeCliRecorder(IClaudeCliSessionProcessFactory inner) : IClaudeCliSessionProcessFactory
{
    private readonly List<Recording> _processes = [];

    public async Task<IClaudeCliSessionProcess> StartAsync(ClaudeCliSessionInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        var recording = new Recording(await inner.StartAsync(invocation, cancellationToken), invocation.Arguments);
        lock (_processes) _processes.Add(recording);
        return recording;
    }

    public Recording Latest()
    {
        lock (_processes) return _processes.Count > 0 ? _processes[^1] : throw new ProbeFailure("No Claude CLI process started.");
    }

    internal sealed class Recording(IClaudeCliSessionProcess process, IReadOnlyList<string> arguments) : IClaudeCliSessionProcess
    {
        private readonly List<string> _input = [];
        private readonly List<string> _output = [];

        public IReadOnlyList<string> Arguments => arguments;

        public IReadOnlyList<JsonObject> Input() => Parse(_input);

        public IReadOnlyList<JsonObject> Output() => Parse(_output);

        public async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default)
        {
            lock (_input) _input.Add(line);
            await process.WriteLineAsync(line, cancellationToken);
        }

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
        {
            string? line = await process.ReadLineAsync(cancellationToken);
            if (line is not null) lock (_output) _output.Add(line);
            return line;
        }

        public Task<ClaudeCliProcessResult> WaitForExitAsync(CancellationToken cancellationToken = default) =>
            process.WaitForExitAsync(cancellationToken);

        public ValueTask DisposeAsync() => process.DisposeAsync();

        /// <summary>Whether no process with this launch's session id remains within the time given.</summary>
        public async Task<bool> ExitedAsync(TimeSpan within, CancellationToken token)
        {
            int index = arguments.ToList().IndexOf("--session-id");
            if (index < 0 || index + 1 >= arguments.Count) throw new ProbeFailure("The launch has no --session-id.");
            var clock = Stopwatch.StartNew();
            while (true)
            {
                using var check = Process.Start(new ProcessStartInfo("pgrep", ["-f", arguments[index + 1]])
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                }) ?? throw new ProbeFailure("pgrep did not start.");
                await check.WaitForExitAsync(token);
                if (check.ExitCode == 1) return true;
                if (clock.Elapsed >= within) return false;
                await Task.Delay(TimeSpan.FromMilliseconds(500), token);
            }
        }

        private static List<JsonObject> Parse(List<string> lines)
        {
            string[] copy;
            lock (lines) copy = [.. lines];
            var parsed = new List<JsonObject>();
            foreach (string line in copy)
            {
                try
                {
                    if (JsonNode.Parse(line) is JsonObject item) parsed.Add(item);
                }
                catch (JsonException)
                {
                }
            }
            return parsed;
        }
    }
}
