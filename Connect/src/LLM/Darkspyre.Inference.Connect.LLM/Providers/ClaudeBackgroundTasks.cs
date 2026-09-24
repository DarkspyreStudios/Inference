using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// The Claude CLI's background tasks, maintained from its task messages. A command's output is read from the
/// CLI's output file by byte offset, without the trailer the CLI appends. A subagent's output is the text of its
/// own messages, kept up to the retention limit.
/// </summary>
internal sealed partial class ClaudeBackgroundTasks : IAsyncDisposable
{
    private const int ReadChunkBytes = 64 * 1024;
    private static readonly LlmAgentResourceSupport CommandSupport =
        new(LlmOperationSupport.Supported, LlmOperationSupport.Supported, LlmOperationSupport.Unsupported);
    private static readonly LlmAgentResourceSupport AgentSupport =
        new(LlmOperationSupport.Supported, LlmOperationSupport.Supported, LlmOperationSupport.Unsupported);
    private static readonly LlmAgentResourceSupport OtherSupport = new(LlmOperationSupport.Unknown, LlmOperationSupport.Supported,
        LlmOperationSupport.Unsupported, "The runtime keeps no output this adapter can read for this kind.");

    private readonly string _threadId;
    private readonly long _retentionBytes;
    private readonly Action<LlmAgentEvent> _publish;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Entry> _tasks = new(StringComparer.Ordinal);
    // Tool calls a subagent made, by call ID, to the subagent's task. A command it runs names its parent this way.
    private readonly Dictionary<string, string> _subagentCalls = new(StringComparer.Ordinal);
    // Output files named in a tool result before or after the task started, by the starting call ID.
    private readonly Dictionary<string, string> _outputFiles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _poller;
    private string? _tasksDirectory;
    private string? _lastReported;

    public ClaudeBackgroundTasks(string threadId, long retentionBytes, Action<LlmAgentEvent> publish, TimeSpan pollInterval)
    {
        _threadId = threadId;
        _retentionBytes = retentionBytes;
        _publish = publish;
        _poller = PollAsync(pollInterval, _stop.Token);
    }

    /// <summary>The resource whose end or report arrived since the last call, for the next provider-started turn.</summary>
    public string? TakeLastReported()
    {
        lock (_sync)
        {
            string? reported = _lastReported;
            _lastReported = null;
            return reported;
        }
    }

    public IReadOnlyList<LlmAgentResource> Snapshot()
    {
        lock (_sync)
        {
            return [.. _tasks.Values.Select(entry => entry.Resource)];
        }
    }

    public bool TryGet(string resourceId, out LlmAgentResource resource)
    {
        lock (_sync)
        {
            if (_tasks.TryGetValue(resourceId, out Entry? entry))
            {
                resource = entry.Resource;
                return true;
            }
            resource = null!;
            return false;
        }
    }

    /// <summary>A system message: init, a task message, or the current background set.</summary>
    public void Handle(JsonElement root)
    {
        string subtype = Text(root, "subtype") ?? string.Empty;
        lock (_sync)
        {
            switch (subtype)
            {
                case "init":
                    if (Text(root, "scratchpad_path") is { Length: > 0 } scratchpad
                        && Path.GetDirectoryName(scratchpad) is { Length: > 0 } session)
                    {
                        _tasksDirectory = Path.Combine(session, "tasks");
                    }
                    break;
                case "task_started":
                    Started(root);
                    break;
                case "task_updated":
                    Updated(root);
                    break;
                case "task_progress":
                    Progress(root);
                    break;
                case "task_notification":
                    Notified(root);
                    break;
                case "background_tasks_changed":
                    SetChanged(root);
                    break;
            }
        }
    }

    /// <summary>A top-level tool result; a background command's names the file its output is written to.</summary>
    public void ToolResult(string toolUseId, string text)
    {
        Match match = OutputFilePattern().Match(text);
        if (!match.Success)
        {
            return;
        }
        lock (_sync)
        {
            string path = match.Groups["path"].Value;
            _outputFiles[toolUseId] = path;
            if (_tasks.Values.FirstOrDefault(entry => entry.Resource.OriginToolCallId == toolUseId) is { OutputFile: null } entry)
            {
                entry.OutputFile = path;
                Publish(LlmAgentResourceChange.Updated, entry, entry.Resource with { Support = CommandSupport });
            }
        }
    }

    /// <summary>A message from a subagent: its text becomes the subagent's output.</summary>
    public void SubagentMessage(string parentToolUseId, JsonElement message)
    {
        lock (_sync)
        {
            Entry? agent = _tasks.Values.FirstOrDefault(entry => entry.Resource.OriginToolCallId == parentToolUseId);
            string? agentId = agent?.Resource.ResourceId;
            var text = new StringBuilder();
            if (message.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement block in content.EnumerateArray())
                {
                    switch (Text(block, "type"))
                    {
                        case "text" when Text(block, "text") is { Length: > 0 } value:
                            text.Append(value).Append('\n');
                            break;
                        case "tool_use":
                            if (agentId is not null && Text(block, "id") is { } callId) { _subagentCalls[callId] = agentId; }
                            text.Append('[').Append(Text(block, "name")).Append(' ')
                                .Append(block.TryGetProperty("input", out JsonElement input) ? input.GetRawText() : "{}").Append("]\n");
                            break;
                        case "tool_result":
                            text.Append(ResultText(block)).Append('\n');
                            break;
                    }
                }
            }
            else if (content.ValueKind == JsonValueKind.String)
            {
                text.Append(content.GetString()).Append('\n');
            }
            if (agent is null || text.Length == 0)
            {
                return;
            }
            agent.Buffer ??= new();
            agent.Buffer.Append(Encoding.UTF8.GetBytes(text.ToString()), _retentionBytes);
            _publish(new LlmAgentResourceOutputEvent(_threadId, agent.Resource.ResourceId,
                agent.Buffer.End.ToString(CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>The runtime's process ended: work it had not reported ending ends as Unknown.</summary>
    public void EndAll()
    {
        lock (_sync)
        {
            foreach (Entry entry in _tasks.Values.Where(entry => entry.Resource.Ended is null))
            {
                Publish(LlmAgentResourceChange.Ended, entry, entry.Resource with
                {
                    State = LlmAgentResourceState.Unknown,
                    Ended = DateTimeOffset.UtcNow
                });
            }
        }
    }

    public async Task<LlmAgentResourceOutput> ReadAsync(string resourceId, string? cursor, CancellationToken cancellationToken)
    {
        long offset = 0;
        if (cursor is not null && !long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset))
        {
            throw new ArgumentException("The output cursor is not one this session issued.", nameof(cursor));
        }
        string? path;
        long? end;
        bool closed;
        lock (_sync)
        {
            if (!_tasks.TryGetValue(resourceId, out Entry? entry))
            {
                return Unavailable();
            }
            closed = entry.Resource.Ended is not null;
            if (entry.Buffer is { } buffer)
            {
                return buffer.Read(offset, ReadChunkBytes, closed);
            }
            path = entry.OutputFile;
            end = entry.TrailerStart;
        }
        if (path is null || !File.Exists(path))
        {
            return closed ? Unavailable() : new(LlmAgentResourceOutputStatus.NoData, ReadOnlyMemory<byte>.Empty, Cursor(offset), "0", false);
        }
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long readable = end ?? file.Length;
        if (offset >= readable)
        {
            return new(LlmAgentResourceOutputStatus.NoData, ReadOnlyMemory<byte>.Empty, Cursor(offset), "0", closed);
        }
        byte[] data = new byte[(int)Math.Min(ReadChunkBytes, readable - offset)];
        file.Position = offset;
        int read = await file.ReadAtLeastAsync(data, data.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        long next = offset + read;
        return new(LlmAgentResourceOutputStatus.Data, data.AsMemory(0, read), Cursor(next), "0", closed && next >= readable);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _poller.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }

    private void Started(JsonElement root)
    {
        if (Text(root, "task_id") is not { Length: > 0 } id)
        {
            return;
        }
        string kind = Text(root, "task_type") ?? "unknown";
        string? origin = Text(root, "tool_use_id");
        string? parent = root.TryGetProperty("owned_by_subagent", out JsonElement owned) && owned.ValueKind == JsonValueKind.True
            && origin is not null && _subagentCalls.TryGetValue(origin, out string? agent) ? agent : null;
        string? file = origin is not null && _outputFiles.TryGetValue(origin, out string? named) ? named
            : kind == "local_bash" && _tasksDirectory is not null ? Path.Combine(_tasksDirectory, id + ".output") : null;
        bool known = _tasks.TryGetValue(id, out Entry? entry);
        entry ??= new Entry(new(id, kind, id, LlmAgentResourceState.Running) { Support = OtherSupport });
        _tasks[id] = entry;
        entry.OutputFile ??= file;
        Publish(known ? LlmAgentResourceChange.Updated : LlmAgentResourceChange.Added, entry, entry.Resource with
        {
            Kind = kind,
            Label = Text(root, "description") ?? entry.Resource.Label,
            State = LlmAgentResourceState.Running,
            Started = entry.Resource.Started ?? DateTimeOffset.UtcNow,
            OriginToolCallId = origin,
            ParentResourceId = parent,
            AgentType = Text(root, "subagent_type"),
            Backgrounded = root.TryGetProperty("is_backgrounded", out JsonElement background) && background.ValueKind == JsonValueKind.True,
            Support = SupportFor(kind, entry.OutputFile)
        });
    }

    private void Updated(JsonElement root)
    {
        if (Text(root, "task_id") is not { } id || !_tasks.TryGetValue(id, out Entry? entry)
            || !root.TryGetProperty("patch", out JsonElement patch) || patch.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        LlmAgentResource resource = entry.Resource;
        if (Text(patch, "description") is { } label) { resource = resource with { Label = label }; }
        if (Text(patch, "error") is { } error) { resource = resource with { Error = error }; }
        if (patch.TryGetProperty("is_backgrounded", out JsonElement background) && background.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            resource = resource with { Backgrounded = background.ValueKind == JsonValueKind.True };
        }
        LlmAgentResourceState? state = Text(patch, "status") switch
        {
            "pending" => LlmAgentResourceState.Pending,
            "running" => LlmAgentResourceState.Running,
            "paused" => LlmAgentResourceState.Paused,
            "completed" => LlmAgentResourceState.Completed,
            "failed" => LlmAgentResourceState.Failed,
            "killed" => LlmAgentResourceState.Stopped,
            _ => null
        };
        if (state is { } changed) { resource = resource with { State = changed }; }
        if (patch.TryGetProperty("end_time", out JsonElement endTime) && endTime.TryGetInt64(out long milliseconds))
        {
            resource = resource with { Ended = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) };
        }
        End(entry, resource);
    }

    private void Progress(JsonElement root)
    {
        if (Text(root, "task_id") is not { } id || !_tasks.TryGetValue(id, out Entry? entry))
        {
            return;
        }
        LlmAgentResourceUsage? usage = root.TryGetProperty("usage", out JsonElement value) && value.ValueKind == JsonValueKind.Object
            ? new(Number(value, "total_tokens"), (int)Number(value, "tool_uses"), Number(value, "duration_ms")) : entry.Resource.Usage;
        Publish(LlmAgentResourceChange.Updated, entry, entry.Resource with
        {
            Usage = usage,
            Summary = Text(root, "summary") ?? Text(root, "description") ?? entry.Resource.Summary
        });
    }

    private void Notified(JsonElement root)
    {
        if (Text(root, "task_id") is not { } id || !_tasks.TryGetValue(id, out Entry? entry))
        {
            return;
        }
        if (Text(root, "output_file") is { Length: > 0 } file && entry.Buffer is null)
        {
            entry.OutputFile = file;
        }
        LlmAgentResourceState state = Text(root, "status") switch
        {
            "completed" => LlmAgentResourceState.Completed,
            "failed" => LlmAgentResourceState.Failed,
            "stopped" => LlmAgentResourceState.Stopped,
            _ => LlmAgentResourceState.Unknown
        };
        End(entry, entry.Resource with
        {
            State = state,
            Summary = Text(root, "summary") ?? entry.Resource.Summary,
            Ended = entry.Resource.Ended ?? DateTimeOffset.UtcNow,
            Support = SupportFor(entry.Resource.Kind, entry.OutputFile, entry.Buffer is not null)
        });
    }

    private void SetChanged(JsonElement root)
    {
        if (!root.TryGetProperty("tasks", out JsonElement tasks) || tasks.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement task in tasks.EnumerateArray())
        {
            if (Text(task, "task_id") is not { Length: > 0 } id)
            {
                continue;
            }
            live.Add(id);
            if (!_tasks.ContainsKey(id))
            {
                string kind = Text(task, "task_type") ?? "unknown";
                var entry = new Entry(new(id, kind, Text(task, "description") ?? id, LlmAgentResourceState.Running)
                {
                    Started = DateTimeOffset.UtcNow,
                    Backgrounded = true,
                    Support = SupportFor(kind, null)
                });
                _tasks[id] = entry;
                _publish(new LlmAgentResourceEvent(_threadId, LlmAgentResourceChange.Added, entry.Resource));
            }
        }
        // The set arrives just before a task's own end messages. Work still missing at the next set has ended unreported.
        foreach (string id in _missing.Where(id => !live.Contains(id) && _tasks[id].Resource.Ended is null).ToArray())
        {
            Entry entry = _tasks[id];
            End(entry, entry.Resource with { State = LlmAgentResourceState.Unknown, Ended = DateTimeOffset.UtcNow });
        }
        _missing.Clear();
        foreach (Entry entry in _tasks.Values.Where(entry => entry.Resource is { Backgrounded: true, Ended: null }
            && !live.Contains(entry.Resource.ResourceId)))
        {
            _missing.Add(entry.Resource.ResourceId);
        }
    }

    private void End(Entry entry, LlmAgentResource resource)
    {
        bool ending = entry.Resource.Ended is null && resource.Ended is not null
            || resource.State is LlmAgentResourceState.Completed or LlmAgentResourceState.Failed or LlmAgentResourceState.Stopped
                && entry.Resource.Ended is null;
        if (ending)
        {
            resource = resource with { Ended = resource.Ended ?? DateTimeOffset.UtcNow };
            if (entry.OutputFile is { } path && entry.Buffer is null)
            {
                (int? exitCode, long? trailer) = ReadTrailer(path);
                entry.TrailerStart = trailer;
                resource = resource with { ExitCode = exitCode };
            }
            _lastReported = resource.ResourceId;
        }
        Publish(ending ? LlmAgentResourceChange.Ended : LlmAgentResourceChange.Updated, entry, resource);
    }

    private void Publish(LlmAgentResourceChange change, Entry entry, LlmAgentResource resource)
    {
        entry.Resource = resource;
        _publish(new LlmAgentResourceEvent(_threadId, change, resource));
    }

    private async Task PollAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (_sync)
            {
                foreach (Entry entry in _tasks.Values.Where(entry => entry is { OutputFile: not null, Buffer: null }
                    && entry.Resource.Ended is null))
                {
                    long length = File.Exists(entry.OutputFile) ? new FileInfo(entry.OutputFile!).Length : 0;
                    if (length > entry.PolledLength)
                    {
                        entry.PolledLength = length;
                        _publish(new LlmAgentResourceOutputEvent(_threadId, entry.Resource.ResourceId,
                            length.ToString(CultureInfo.InvariantCulture)));
                    }
                }
            }
        }
    }

    /// <summary>The CLI ends a command's output file with a trailer: <c>[exited with code N]</c> or <c>[killed]</c>.</summary>
    private static (int? ExitCode, long? TrailerStart) ReadTrailer(string path)
    {
        if (!File.Exists(path))
        {
            return (null, null);
        }
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, file.Length - 128);
        file.Position = start;
        byte[] tail = new byte[file.Length - start];
        file.ReadExactly(tail);
        Match match = TrailerPattern().Match(Encoding.UTF8.GetString(tail));
        if (!match.Success)
        {
            return (null, null);
        }
        long trailer = start + Encoding.UTF8.GetByteCount(Encoding.UTF8.GetString(tail)[..match.Index]);
        return (match.Groups["code"].Success ? int.Parse(match.Groups["code"].Value, CultureInfo.InvariantCulture) : null, trailer);
    }

    private static LlmAgentResourceSupport SupportFor(string kind, string? outputFile, bool buffered = false) => kind switch
    {
        "local_agent" => AgentSupport,
        _ when buffered => AgentSupport,
        "local_bash" when outputFile is not null => CommandSupport,
        "local_bash" => new(LlmOperationSupport.Unknown, LlmOperationSupport.Supported, LlmOperationSupport.Unsupported,
            "The runtime has not named this command's output file."),
        _ => OtherSupport
    };

    private static string ResultText(JsonElement block) =>
        block.TryGetProperty("content", out JsonElement content) switch
        {
            true when content.ValueKind == JsonValueKind.String => content.GetString() ?? string.Empty,
            true when content.ValueKind == JsonValueKind.Array => string.Join("\n", content.EnumerateArray()
                .Select(item => Text(item, "text")).Where(value => value is not null)),
            _ => string.Empty
        };

    private static LlmAgentResourceOutput Unavailable() =>
        new(LlmAgentResourceOutputStatus.Unavailable, ReadOnlyMemory<byte>.Empty, null, null, true);

    private static string Cursor(long offset) => offset.ToString(CultureInfo.InvariantCulture);

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long number) ? number : 0;

    [GeneratedRegex(@"Output is being written to: (?<path>\S+\.output)")]
    private static partial Regex OutputFilePattern();

    [GeneratedRegex(@"\n?\[(?:exited with code (?<code>-?\d+)|killed)\]\s*$")]
    private static partial Regex TrailerPattern();

    private sealed class Entry(LlmAgentResource resource)
    {
        public LlmAgentResource Resource { get; set; } = resource;
        public string? OutputFile { get; set; }
        public long PolledLength { get; set; }
        public long? TrailerStart { get; set; }
        public RetainedOutput? Buffer { get; set; }
    }

    /// <summary>Output Connect keeps itself, dropping the earliest bytes beyond the retention limit.</summary>
    private sealed class RetainedOutput
    {
        private byte[] _data = [];

        public long Start { get; private set; }

        public long End => Start + _data.Length;

        public void Append(byte[] bytes, long retention)
        {
            byte[] combined = [.. _data, .. bytes];
            long excess = combined.Length - retention;
            _data = excess > 0 ? combined[(int)excess..] : combined;
            Start += Math.Max(0, excess);
        }

        public LlmAgentResourceOutput Read(long offset, int chunk, bool closed)
        {
            string earliest = Start.ToString(CultureInfo.InvariantCulture);
            if (offset < Start)
            {
                return new(LlmAgentResourceOutputStatus.Gap, ReadOnlyMemory<byte>.Empty, earliest, earliest, false);
            }
            if (offset >= End)
            {
                return new(LlmAgentResourceOutputStatus.NoData, ReadOnlyMemory<byte>.Empty, Cursor(offset), earliest, closed);
            }
            int from = (int)(offset - Start);
            int count = (int)Math.Min(chunk, _data.Length - from);
            long next = offset + count;
            return new(LlmAgentResourceOutputStatus.Data, _data.AsMemory(from, count).ToArray(), Cursor(next), earliest, closed && next >= End);
        }
    }
}
