using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>The launch flags a process was started with. A turn whose tuple differs reopens the conversation.</summary>
internal sealed record AntigravityLaunch(string? ModelId, LlmReasoningLevel? Effort, string? SchemaJson);

/// <summary>
/// One native AGY conversation. It owns one process generation at a time and one reader per generation.
/// One turn runs at a time. Output that arrives while no turn or handshake is waiting faults the session,
/// because it cannot be attributed to a host turn.
/// </summary>
internal sealed class AntigravityAgentSession : ILlmAgentSession
{
    internal static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan ReopenExitTimeout = TimeSpan.FromSeconds(10);

    private static readonly IReadOnlySet<LlmReasoningLevel> Efforts = LlmReasoningEffort.Levels(
        LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High);

    private readonly IAntigravityCliSessionProcessFactory _factory;
    private readonly string _executable;
    private readonly string _workingDirectory;
    private readonly AntigravityLaunch _defaults;
    private readonly NoAgentResources _resources = new(LlmAgentResourceStopStatus.Unknown);
    private readonly Lock _sync = new();

    private IAntigravityCliSessionProcess? _process;
    private Channel<ProcessLine> _lines = Channel.CreateUnbounded<ProcessLine>();
    private Task _reader = Task.CompletedTask;
    private string? _schemaDirectory;
    private AntigravityLaunch _launch;
    private int _generation;
    private SessionState _state = SessionState.Idle;
    private bool _expectingOutput;
    private string? _fault;
    private string? _threadId;

    // Result usage is cumulative for the whole conversation. A baseline is trusted only within the process
    // generation that reported it; a new conversation starts from zero.
    private AntigravityUsage? _baseline;
    private int _baselineGeneration;

    private AntigravityAgentSession(
        IAntigravityCliSessionProcessFactory factory,
        string executable,
        string workingDirectory,
        AntigravityLaunch defaults)
    {
        _factory = factory;
        _executable = executable;
        _workingDirectory = workingDirectory;
        _defaults = defaults;
        _launch = defaults;
    }

    private enum SessionState
    {
        Idle,
        Running,
        Faulted,
        Disposed
    }

    public string ThreadId => _threadId ?? throw new InvalidOperationException("The session has no conversation yet.");

    public string SessionId => ThreadId;

    /// <summary>Starts a process, waits for its handshake and returns the session under its real conversation ID.</summary>
    public static async Task<ILlmAgentSession> OpenAsync(
        IAntigravityCliSessionProcessFactory factory,
        string executable,
        string workingDirectory,
        AntigravityLaunch launch,
        string? conversationId,
        CancellationToken cancellationToken)
    {
        var session = new AntigravityAgentSession(factory, executable, workingDirectory, launch);
        try
        {
            await session.StartProcessAsync(launch, conversationId, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<LlmAgentCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return Task.FromResult(NativeAgentContract.Antigravity);
    }

    public Task<LlmRequestAccounting> MeasureContextAsync(
        LlmAgentContext proposedContext,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(proposedContext);
        return Task.FromResult(new LlmRequestAccounting
        {
            ModelId = null,
            Diagnostic = "Provider-owned context does not support host replacement accounting."
        });
    }

    public Task<LlmAgentContextStatus> GetContextStatusAsync(
        LlmAgentTurnRequest? prospectiveTurn = null,
        CancellationToken cancellationToken = default) =>
        throw NativeAgentContract.UnsupportedContext(cancellationToken);

    public Task<LlmAgentContext> CaptureContextAsync(
        LlmAgentContextCaptureOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw NativeAgentContract.UnsupportedContext(cancellationToken);

    public Task ReplaceContextAsync(
        LlmAgentContext context,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        throw NativeAgentContract.UnsupportedContext(cancellationToken);

    public IAsyncEnumerable<LlmAgentEvent> ContinueTurnAsync(
        LlmAgentTurnContinuation continuation,
        CancellationToken cancellationToken = default) =>
        NativeAgentContract.UnsupportedContinuation(cancellationToken);

    public async IAsyncEnumerable<LlmAgentEvent> RunTurnAsync(
        LlmAgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        (AntigravityLaunch launch, string prompt, JsonElement? schema) = Admit(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfUnavailable();
            if (_state != SessionState.Idle)
            {
                throw new InvalidOperationException("An Antigravity session runs one turn at a time.");
            }
            if (_lines.Reader.TryPeek(out _))
            {
                FaultLocked("The Antigravity CLI produced output while no turn was active.");
                ThrowIfUnavailable();
            }
            _state = SessionState.Running;
        }

        string turnId = Guid.NewGuid().ToString("N");
        bool settled = false;
        try
        {
            if (launch != _launch)
            {
                // Applying new launch flags before a request is configuration, not a retry of a sent request.
                await ReopenAsync(launch, cancellationToken).ConfigureAwait(false);
            }

            var turn = new TurnMapper(this, turnId, schema, request.OutputSchema);
            lock (_sync)
            {
                _expectingOutput = true;
            }
            yield return new LlmAgentTurnStartedEvent(ThreadId, turnId);

            string? writeFailure = await WritePromptAsync(prompt, cancellationToken).ConfigureAwait(false);
            if (writeFailure is not null)
            {
                FaultAndIdle(writeFailure);
                settled = true;
                yield return new LlmAgentTurnCompletedEvent(ThreadId, turnId, LlmAgentCompletionReason.ProviderFailure, writeFailure);
                yield break;
            }

            ChannelReader<ProcessLine> lines = _lines.Reader;
            while (!turn.IsComplete)
            {
                if (!lines.TryRead(out ProcessLine line))
                {
                    if (!await lines.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        line = ProcessLine.End;
                    }
                    else
                    {
                        continue;
                    }
                }

                foreach (LlmAgentEvent agentEvent in turn.Map(line))
                {
                    yield return agentEvent;
                }
            }

            lock (_sync)
            {
                _expectingOutput = false;
                if (turn.FaultReason is { } reason)
                {
                    FaultLocked(reason);
                }
                else if (_state == SessionState.Running)
                {
                    _state = SessionState.Idle;
                }
            }
            settled = true;
        }
        finally
        {
            if (!settled)
            {
                // Cancellation or an abandoned enumeration: the turn's outcome is unknown, so the owned process
                // is terminated and this session object is not reused. Resume creates a fresh session.
                await AbandonAsync().ConfigureAwait(false);
            }
        }
    }

    public Task SteerAsync(
        string turnId,
        IReadOnlyList<LlmAgentContent> input,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        throw new NotSupportedException("Antigravity CLI does not support steering an active turn.");
    }

    public Task InterruptAsync(string turnId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        throw new NotSupportedException(
            "Antigravity CLI has no in-process interrupt. Cancel RunTurnAsync; it closes the local session.");
    }

    public async IAsyncEnumerable<LlmAgentEvent> ObserveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await foreach (LlmAgentEvent item in _resources.ObserveAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public Task<IReadOnlyList<LlmAgentResource>> GetResourcesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        throw new NotSupportedException(
            "Antigravity CLI does not report its background work; ResourceObservation is Unsupported.");
    }

    public Task<LlmAgentResourceOutput> ReadResourceOutputAsync(
        string resourceId,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        cancellationToken.ThrowIfCancellationRequested();
        return NoAgentResources.ReadAsync();
    }

    public Task<LlmAgentResourceStopStatus> StopResourceAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        throw new NotSupportedException(
            "Antigravity CLI does not report its background work; ResourceObservation is Unsupported.");
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_state == SessionState.Disposed)
            {
                return;
            }
            _state = SessionState.Disposed;
            _expectingOutput = false;
        }
        await CloseProcessAsync(graceful: true).ConfigureAwait(false);
        _resources.Dispose();
    }

    private (AntigravityLaunch Launch, string Prompt, JsonElement? Schema) Admit(LlmAgentTurnRequest request)
    {
        NativeAgentContract.ValidateTurn(request);
        ArgumentNullException.ThrowIfNull(request.Input);
        ArgumentNullException.ThrowIfNull(request.Attachments);
        if (request.Attachments.Count > 0)
        {
            throw new NotSupportedException("Antigravity CLI accepts text input only; attachments are unsupported.");
        }
        var prompt = new StringBuilder();
        foreach (LlmAgentContent content in request.Input)
        {
            if (content is not LlmAgentTextContent text)
            {
                throw new NotSupportedException("Antigravity CLI accepts text input only; image content is unsupported.");
            }
            if (prompt.Length > 0)
            {
                prompt.Append('\n');
            }
            prompt.Append(text.Text);
        }
        if (string.IsNullOrWhiteSpace(prompt.ToString()))
        {
            throw new ArgumentException("A turn requires nonempty text input.", nameof(request));
        }
        if (request.ModelId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId, nameof(request));
        }
        LlmReasoningEffort.Validate(request.ReasoningLevel, Efforts);
        if (request.OutputSchema is { ValueKind: not JsonValueKind.Object })
        {
            throw new ArgumentException("An output schema must be a JSON object.", nameof(request));
        }

        // Overrides apply to this turn only; unspecified values resolve against the session defaults.
        var launch = new AntigravityLaunch(
            request.ModelId ?? _defaults.ModelId,
            request.ReasoningLevel ?? _defaults.Effort,
            request.OutputSchema?.GetRawText());
        return (launch, prompt.ToString(), request.OutputSchema);
    }

    private async Task StartProcessAsync(AntigravityLaunch launch, string? conversationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var arguments = new List<string>
        {
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--sandbox",
            "--disable-slash-commands"
        };
        if (launch.ModelId is { } model && !string.Equals(model, "auto", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(["--model", model]);
        }
        if (launch.Effort is { } effort)
        {
            arguments.AddRange(["--effort", LlmReasoningEffort.Word(effort)]);
        }
        if (launch.SchemaJson is { } schemaJson)
        {
            _schemaDirectory = CliInfrastructure.CreateWorkingDirectory("antigravity");
            string schemaPath = Path.Combine(_schemaDirectory, "output-schema.json");
            await File.WriteAllTextAsync(schemaPath, schemaJson, cancellationToken).ConfigureAwait(false);
            arguments.AddRange(["--json-schema", schemaPath]);
        }
        if (conversationId is not null)
        {
            // Never --continue: it selects the most recent conversation, which may belong to someone else.
            arguments.AddRange(["--conversation", conversationId]);
        }

        IAntigravityCliSessionProcess process;
        try
        {
            process = _factory.Start(_executable, arguments, _workingDirectory);
        }
        catch (AntigravityCliUnavailableException exception)
        {
            throw new LlmProviderException(
                "Antigravity CLI was not found. Install agy, run it once, and complete Google sign-in.",
                LlmTransport.GoogleAntigravityCli,
                providerCode: "antigravity_cli_not_found",
                innerException: exception);
        }

        var lines = Channel.CreateUnbounded<ProcessLine>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        int generation;
        lock (_sync)
        {
            _process = process;
            _lines = lines;
            generation = ++_generation;
            _expectingOutput = true;
        }
        _reader = Task.Run(() => ReadAsync(process, lines.Writer, generation), CancellationToken.None);

        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(HandshakeTimeout);
        ProcessLine first;
        try
        {
            first = await lines.Reader.ReadAsync(handshake.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("Antigravity CLI did not report its conversation before the handshake deadline.", "antigravity_cli_handshake_timeout");
        }
        catch (ChannelClosedException)
        {
            first = ProcessLine.End;
        }

        if (first.Event is not AntigravityInit init)
        {
            throw first.Error as LlmProviderException
                ?? Failure("Antigravity CLI ended its session before reporting a conversation.", "antigravity_cli_invalid_response");
        }
        string expected = conversationId ?? _threadId ?? init.ConversationId;
        if (!string.Equals(init.ConversationId, expected, StringComparison.Ordinal))
        {
            throw Failure("Antigravity CLI opened a different conversation than the one requested.", "antigravity_cli_identity_mismatch");
        }

        lock (_sync)
        {
            _expectingOutput = false;
            _threadId = init.ConversationId;
            _launch = launch;
            if (conversationId is null && _generation == 1)
            {
                // A new conversation has done no work, so its cumulative counters start from zero.
                _baseline = new AntigravityUsage(0, 0, 0, 0, 0, false);
                _baselineGeneration = generation;
            }
        }
    }

    private async Task ReopenAsync(AntigravityLaunch launch, CancellationToken cancellationToken)
    {
        IAntigravityCliSessionProcess? process;
        lock (_sync)
        {
            process = _process;
        }
        if (process is not null)
        {
            process.CloseInput();
            using var exit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            exit.CancelAfter(ReopenExitTimeout);
            try
            {
                await process.WaitForExitAsync(exit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw Failure("Antigravity CLI did not exit while its launch options were changed.", "antigravity_cli_reopen_timeout");
            }
        }
        await CloseProcessAsync(graceful: false).ConfigureAwait(false);
        string conversationId = ThreadId;
        await StartProcessAsync(launch, conversationId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> WritePromptAsync(string prompt, CancellationToken cancellationToken)
    {
        IAntigravityCliSessionProcess process = _process ?? throw new InvalidOperationException("The session has no process.");
        string line = JsonSerializer.Serialize(new
        {
            @event = "user",
            message = new { content = prompt }
        }) + "\n";
        try
        {
            await process.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (IOException)
        {
            return "The Antigravity CLI closed its input before the turn was sent.";
        }
    }

    private async Task ReadAsync(IAntigravityCliSessionProcess process, ChannelWriter<ProcessLine> writer, int generation)
    {
        try
        {
            while (await process.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                AntigravityEvent parsed;
                try
                {
                    parsed = AntigravityProtocol.Parse(line);
                }
                catch (LlmProviderException exception)
                {
                    writer.TryWrite(new ProcessLine(generation, null, exception));
                    return;
                }
                if (parsed is AntigravityUnrecognizedEvent)
                {
                    continue;
                }
                lock (_sync)
                {
                    if (!_expectingOutput && generation == _generation && _state is SessionState.Idle or SessionState.Running)
                    {
                        FaultLocked("The Antigravity CLI produced output while no turn was active.");
                    }
                }
                writer.TryWrite(new ProcessLine(generation, parsed, null));
            }
            writer.TryWrite(ProcessLine.End with { Generation = generation });
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            writer.TryWrite(new ProcessLine(generation, null, exception));
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task AbandonAsync()
    {
        lock (_sync)
        {
            _expectingOutput = false;
            FaultLocked("The previous turn was cancelled or abandoned; its outcome is unknown. Resume the conversation.");
        }
        await CloseProcessAsync(graceful: false).ConfigureAwait(false);
    }

    private void FaultAndIdle(string reason)
    {
        lock (_sync)
        {
            _expectingOutput = false;
            FaultLocked(reason);
        }
    }

    private async Task CloseProcessAsync(bool graceful)
    {
        IAntigravityCliSessionProcess? process;
        string? schemaDirectory;
        lock (_sync)
        {
            process = _process;
            _process = null;
            schemaDirectory = _schemaDirectory;
            _schemaDirectory = null;
        }
        if (process is not null)
        {
            try
            {
                if (!graceful)
                {
                    await process.KillAsync().ConfigureAwait(false);
                }
            }
            catch (CliProcessTerminationException exception)
            {
                FaultForTermination(exception);
            }
            try
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }
            catch (CliProcessTerminationException exception)
            {
                FaultForTermination(exception);
            }
            await _reader.ConfigureAwait(false);
        }
        if (schemaDirectory is not null)
        {
            CliInfrastructure.DeleteWorkingDirectory(schemaDirectory);
        }
    }

    /// <summary>
    /// Records a process that did not exit after it was killed. The operation that closed the process keeps its own
    /// outcome, and later calls fail with a reason that names the process.
    /// </summary>
    private void FaultForTermination(CliProcessTerminationException exception)
    {
        lock (_sync)
        {
            if (_state == SessionState.Faulted)
            {
                // A cancelled turn has already faulted the session. The reason gains the process detail once.
                if (_fault?.Contains(exception.Message, StringComparison.Ordinal) != true)
                {
                    _fault = $"{_fault} {exception.Message}";
                }
            }
            else
            {
                FaultLocked(exception.Message);
            }
        }
    }

    private void FaultLocked(string reason)
    {
        if (_state is SessionState.Idle or SessionState.Running)
        {
            _state = SessionState.Faulted;
            _fault = reason;
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_state == SessionState.Disposed, this);
        if (_state == SessionState.Faulted)
        {
            throw new InvalidOperationException($"The Antigravity session is no longer usable. {_fault}");
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_state == SessionState.Disposed, this);
        }
    }

    private static LlmProviderException Failure(string message, string code) =>
        new(message, LlmTransport.GoogleAntigravityCli, providerCode: code);

    /// <summary>Turn usage from the per-step ledger, reconciled with a trusted cumulative difference.</summary>
    private (LlmUsage? Usage, string? Warning) SettleUsage(int generation, AntigravityUsage? cumulative, StepLedger ledger)
    {
        LlmUsage? fromSteps = ledger.Usage;
        lock (_sync)
        {
            AntigravityUsage? baseline = _baselineGeneration == generation ? _baseline : null;
            if (cumulative is null || cumulative.Invalid)
            {
                _baseline = null;
                return (fromSteps, cumulative is { Invalid: true }
                    ? "Antigravity CLI reported invalid usage counters; the turn total is unavailable."
                    : null);
            }

            _baseline = cumulative;
            _baselineGeneration = generation;
            if (baseline is null)
            {
                return (fromSteps, null);
            }

            int? input = Difference(cumulative.InputTokens, baseline.InputTokens, out bool inputDecreased);
            int? output = Difference(cumulative.OutputTokens, baseline.OutputTokens, out bool outputDecreased);
            int? total = Difference(cumulative.TotalTokens, baseline.TotalTokens, out bool totalDecreased);
            if (inputDecreased || outputDecreased || totalDecreased)
            {
                _baseline = null;
                return (null, "Antigravity CLI usage counters decreased; the turn total is unavailable.");
            }
            return input is null && output is null && total is null
                ? (fromSteps, null)
                : (new LlmUsage(input, output, total), null);
        }
    }

    private static int? Difference(long? current, long? baseline, out bool decreased)
    {
        decreased = current is not null && baseline is not null && current < baseline;
        if (current is null || baseline is null || decreased)
        {
            return null;
        }
        long difference = current.Value - baseline.Value;
        return difference <= int.MaxValue ? (int)difference : null;
    }

    private readonly record struct ProcessLine(int Generation, AntigravityEvent? Event, Exception? Error)
    {
        public static ProcessLine End { get; } = new(0, null, null);

        public bool IsEnd => Event is null && Error is null;
    }

    /// <summary>Final usage of each completed step, counted once per process generation and step index.</summary>
    private sealed class StepLedger
    {
        private readonly Dictionary<int, AntigravityStep> _done = [];

        /// <summary>False for an identical repeated DONE; throws for conflicting final data.</summary>
        public bool Record(AntigravityStep step)
        {
            if (!step.IsDone)
            {
                return true;
            }
            if (_done.TryGetValue(step.StepIndex, out AntigravityStep? existing))
            {
                return !Same(existing, step)
                    ? throw new InvalidDataException("Antigravity CLI reported conflicting final data for one step.")
                    : false;
            }
            _done.Add(step.StepIndex, step);
            return true;
        }

        public LlmUsage? Usage
        {
            get
            {
                AntigravityUsage[] reported = [.. _done.Values.Select(step => step.Usage).OfType<AntigravityUsage>()];
                if (reported.Length == 0 || reported.Any(usage => usage.Invalid))
                {
                    return null;
                }
                return new LlmUsage(Sum(reported, usage => usage.InputTokens), Sum(reported, usage => usage.OutputTokens),
                    Sum(reported, usage => usage.TotalTokens));
            }
        }

        private static int? Sum(AntigravityUsage[] reported, Func<AntigravityUsage, long?> field)
        {
            long total = 0;
            foreach (AntigravityUsage usage in reported)
            {
                if (field(usage) is not { } value)
                {
                    return null;
                }
                total += value;
                if (total > int.MaxValue)
                {
                    return null;
                }
            }
            return (int)total;
        }

        private static bool Same(AntigravityStep left, AntigravityStep right) =>
            left.StepType == right.StepType
            && left.TextDelta == right.TextDelta
            && left.Usage == right.Usage
            && left.Tool?.Name == right.Tool?.Name
            && left.Tool?.Output == right.Tool?.Output
            && left.Tool?.ErrorMessage == right.Tool?.ErrorMessage;
    }

    /// <summary>Maps one turn's protocol events to neutral agent events.</summary>
    private sealed class TurnMapper(
        AntigravityAgentSession session,
        string turnId,
        JsonElement? schema,
        JsonElement? requestedSchema)
    {
        private readonly StepLedger _ledger = new();
        private readonly HashSet<int> _announced = [];
        private readonly HashSet<int> _toolsStarted = [];
        private readonly int _generation = session._generation;
        private bool _inputSeen;

        public bool IsComplete { get; private set; }

        /// <summary>Set when the session cannot continue after this turn.</summary>
        public string? FaultReason { get; private set; }

        private string ThreadId => session.ThreadId;

        public List<LlmAgentEvent> Map(ProcessLine line)
        {
            if (line.Error is not null)
            {
                return Fail("Antigravity CLI returned malformed or unreadable output.", fault: true);
            }
            if (line.IsEnd)
            {
                return Fail("Antigravity CLI exited before reporting the turn result.", fault: true);
            }
            try
            {
                return line.Event switch
                {
                    AntigravityStep step => MapStep(step),
                    AntigravityResult result => MapResult(result),
                    _ => Fail("Antigravity CLI reported a second handshake during a turn.", fault: true)
                };
            }
            catch (InvalidDataException exception)
            {
                return Fail(exception.Message, fault: true);
            }
        }

        private List<LlmAgentEvent> MapStep(AntigravityStep step)
        {
            if (step.ConversationId is not null && !string.Equals(step.ConversationId, ThreadId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Antigravity CLI reported a step for a different conversation.");
            }
            if (!_ledger.Record(step))
            {
                return [];
            }
            var events = new List<LlmAgentEvent>();
            if (string.Equals(step.StepType, "user_input", StringComparison.Ordinal))
            {
                _inputSeen = true;
                return events;
            }
            if (!_inputSeen)
            {
                // Work reported before this turn's input boundary cannot be attributed to the host's request.
                throw new InvalidDataException("Antigravity CLI reported work before this turn's input.");
            }

            string itemId = $"{_generation}:{step.StepIndex}";
            if (_announced.Add(step.StepIndex))
            {
                events.Add(new LlmAgentActivityEvent(ThreadId, turnId, itemId, $"Model activity: {step.StepType.Replace('_', ' ')}"));
            }
            if (string.Equals(step.StepType, "agent_response", StringComparison.Ordinal))
            {
                // A schema turn publishes only the validated canonical result.
                if (schema is null && !string.IsNullOrEmpty(step.TextDelta))
                {
                    events.Add(new LlmAgentTextDeltaEvent(ThreadId, turnId, itemId, step.TextDelta));
                }
            }
            else if (step.Tool is { } tool)
            {
                if (_toolsStarted.Add(step.StepIndex) && !step.IsDone)
                {
                    events.Add(new LlmAgentToolEvent(ThreadId, turnId, itemId, null, tool.Name, tool.Parameters, null, "inProgress"));
                }
                if (step.IsDone)
                {
                    bool failed = tool.ErrorMessage is not null;
                    LlmToolOutput? output = tool.Output is null && tool.ErrorMessage is null
                        ? null
                        : LlmToolOutput.FromText(tool.ErrorMessage ?? tool.Output!, isError: failed);
                    events.Add(new LlmAgentToolEvent(ThreadId, turnId, itemId, null, tool.Name, tool.Parameters,
                        !failed, failed ? "failed" : "completed", output));
                }
            }
            return events;
        }

        private List<LlmAgentEvent> MapResult(AntigravityResult result)
        {
            if (result.ConversationId is not null && !string.Equals(result.ConversationId, ThreadId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Antigravity CLI reported a result for a different conversation.");
            }
            IsComplete = true;
            var events = new List<LlmAgentEvent>();
            (LlmUsage? usage, string? warning) = session.SettleUsage(_generation, result.Usage, _ledger);
            if (warning is not null)
            {
                events.Add(new LlmAgentWarningEvent(ThreadId, turnId, warning));
            }
            if (usage is not null)
            {
                events.Add(new LlmAgentUsageEvent(ThreadId, turnId, $"{_generation}:{turnId}", usage, usage));
            }

            string itemId = $"{_generation}:{turnId}:response";
            (LlmAgentCompletionReason reason, string? error) = result.Status switch
            {
                "SUCCESS" => (LlmAgentCompletionReason.Completed, (string?)null),
                "ERROR" or "INVALID" => (LlmAgentCompletionReason.ProviderFailure, "Antigravity CLI reported a provider error."),
                "CANCELED" or "INTERRUPTED" => (LlmAgentCompletionReason.Interruption, "Antigravity CLI reported the turn as interrupted."),
                _ => (LlmAgentCompletionReason.Unknown, "Antigravity CLI ended the turn without a terminal classification.")
            };

            if (reason == LlmAgentCompletionReason.Completed && schema is not null)
            {
                string? canonical = result.StructuredOutput?.GetRawText();
                if (canonical is null || !StructuredOutput.Validate(
                        canonical,
                        new LlmStructuredOutputRequest { Name = "output", Schema = requestedSchema!.Value },
                        LlmStructuredOutputEnforcement.ProviderJsonSchema).IsValid)
                {
                    events.Add(new LlmAgentTurnCompletedEvent(ThreadId, turnId, LlmAgentCompletionReason.ProviderFailure,
                        "Antigravity CLI returned no valid structured output for the requested schema."));
                    return events;
                }
                events.Add(new LlmAgentStructuredOutputSnapshotEvent(ThreadId, turnId, itemId, result.StructuredOutput!.Value));
                events.Add(new LlmAgentMessageCompletedEvent(ThreadId, turnId, itemId, canonical));
            }
            else if (reason == LlmAgentCompletionReason.Completed && result.Response.Length > 0)
            {
                events.Add(new LlmAgentMessageCompletedEvent(ThreadId, turnId, itemId, result.Response));
            }
            events.Add(new LlmAgentTurnCompletedEvent(ThreadId, turnId, reason, error));
            return events;
        }

        private List<LlmAgentEvent> Fail(string reason, bool fault)
        {
            IsComplete = true;
            if (fault)
            {
                FaultReason = reason;
            }
            return [new LlmAgentTurnCompletedEvent(ThreadId, turnId, LlmAgentCompletionReason.ProviderFailure, reason)];
        }
    }
}
