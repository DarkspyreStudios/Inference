using Darkspyre.Inference.Common;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Diagnostics.CodeAnalysis;
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
    /// <summary>Twice the slowest startup handshake measured on AGY 1.3.2 (29 seconds; most starts take about 5).</summary>
    internal static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan ReopenExitTimeout = TimeSpan.FromSeconds(10);

    private readonly IAntigravityCliSessionProcessFactory _factory;
    private readonly string _executable;
    private readonly string _workingDirectory;
    private readonly AntigravityLaunch _defaults;
    private readonly LlmConnection _connection;
    private readonly HashSet<(int Generation, int Index)> _accountedSteps = [];
    private readonly NoAgentResources _resources = new(LlmAgentResourceStopStatus.Unknown);
    private readonly LlmOperationLifetime _retirement = new();
    private readonly Lock _sync = new();
    private readonly List<Exception> _cleanupFailures = [];

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
    // Sent once, ahead of the first turn's input; the notice reports what the opening history could not carry.
    private string? _introduction;
    private string? _openingNotice;

    // Result usage is cumulative for the whole conversation. A baseline is trusted only within the process
    // generation that reported it; a new conversation starts from zero.
    private AntigravityUsage? _baseline;
    private int _baselineGeneration;

    private AntigravityAgentSession(
        IAntigravityCliSessionProcessFactory factory,
        string executable,
        string workingDirectory,
        AntigravityLaunch defaults,
        LlmConnection connection,
        string? introduction,
        string? openingNotice)
    {
        _factory = factory;
        _executable = executable;
        _workingDirectory = workingDirectory;
        _defaults = defaults;
        _connection = connection;
        _launch = defaults;
        _introduction = introduction;
        _openingNotice = openingNotice;
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
    [SuppressMessage("Design", "CA1031", Justification = "Failed opening preserves both the original failure and a genuine retirement failure.")]
    public static async Task<ILlmAgentSession> OpenAsync(
        IAntigravityCliSessionProcessFactory factory,
        string executable,
        string workingDirectory,
        AntigravityLaunch launch,
        string? conversationId,
        LlmConnection connection,
        string? introduction,
        string? openingNotice,
        CancellationToken cancellationToken)
    {
        AntigravityCliLlmClient.ResolveCapabilities(connection, launch.ModelId);
        var session = new AntigravityAgentSession(factory, executable, workingDirectory, launch, connection,
            introduction, openingNotice);
        try
        {
            using LlmOperationLifetime.Lease operation = session._retirement.Enter(cancellationToken);
            await session.StartProcessAsync(launch, conversationId, operation.Token).ConfigureAwait(false);
            return session;
        }
        catch (Exception original)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            throw;
        }
    }

    public Task<LlmAgentCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = _retirement.Enter(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return Task.FromResult(NativeAgentContract.Antigravity);
    }

    public Task<LlmRequestAccounting> MeasureContextAsync(
        LlmAgentContext proposedContext,
        CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = _retirement.Enter(cancellationToken);
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
        CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = _retirement.Enter(cancellationToken);
        throw NativeAgentContract.UnsupportedContext(operation.Token);
    }

    public Task<LlmAgentContext> CaptureContextAsync(
        LlmAgentContextCaptureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = _retirement.Enter(cancellationToken);
        throw NativeAgentContract.UnsupportedContext(operation.Token);
    }

    /// <summary>AGY compacts its own conversation; Connect never supplies a replacement.</summary>
    public LlmCompactionResponsibility CompactionResponsibility => LlmCompactionResponsibility.Provider;

    public Task<LlmAgentCompactedEvent?> CompactNowAsync(CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = _retirement.Enter(cancellationToken);
        throw NativeAgentContract.UnsupportedContext(operation.Token);
    }

    public Task ReplaceContextAsync(
        LlmAgentContext context,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = _retirement.Enter(cancellationToken);
        throw NativeAgentContract.UnsupportedContext(operation.Token);
    }

    public async IAsyncEnumerable<LlmAgentEvent> ContinueTurnAsync(
        LlmAgentTurnContinuation continuation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = _retirement.Enter(cancellationToken);
        await Task.FromException(NativeAgentContract.UnsupportedContext(operation.Token)).ConfigureAwait(false);
        yield break;
    }

    public IAsyncEnumerable<LlmAgentEvent> RunTurnAsync(
        LlmAgentTurnRequest request, CancellationToken cancellationToken = default) =>
        _retirement.StreamAsync(token => ProduceTurnAsync(request, token), cancellationToken);

    private async IAsyncEnumerable<LlmAgentEvent> ProduceTurnAsync(
        LlmAgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        (AntigravityLaunch launch, string prompt, JsonElement? schema, bool introduces) = Admit(request);
        if (prompt.Length == 0)
        {
            // Empty input with nothing to introduce sends nothing: the turn completes at once.
            lock (_sync) { ThrowIfUnavailable(); }
            string emptyTurnId = Guid.NewGuid().ToString("N");
            yield return new LlmAgentTurnStartedEvent(ThreadId, emptyTurnId);
            yield return new LlmAgentTurnCompletedEvent(ThreadId, emptyTurnId, LlmAgentCompletionReason.Completed, null);
            yield break;
        }
        bool usageDeltas = (AntigravityCliLlmClient.ResolveCapabilities(_connection, launch.ModelId).SupportsUsageDeltas != InferenceSupport.Unsupported);
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

            var turn = new TurnMapper(this, turnId, schema, request.OutputSchema, usageDeltas);
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfUnavailable();
                _expectingOutput = true;
            }
            yield return new LlmAgentTurnStartedEvent(ThreadId, turnId);
            cancellationToken.ThrowIfCancellationRequested();

            string? writeFailure = await WritePromptAsync(prompt, cancellationToken).ConfigureAwait(false);
            string? openingNotice = null;
            if (introduces && writeFailure is null)
            {
                lock (_sync)
                {
                    _introduction = null;
                    openingNotice = _openingNotice;
                    _openingNotice = null;
                }
            }
            if (openingNotice is not null)
            {
                yield return new LlmAgentWarningEvent(ThreadId, turnId, openingNotice);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (writeFailure is not null)
            {
                lock (_sync)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfUnavailable();
                    FaultAndIdle(writeFailure);
                }
                settled = true;
                cancellationToken.ThrowIfCancellationRequested();
                yield return new LlmAgentTurnCompletedEvent(ThreadId, turnId, LlmAgentCompletionReason.ProviderFailure, writeFailure);
                yield break;
            }

            ChannelReader<ProcessLine> lines = _lines.Reader;
            while (!turn.IsComplete)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

                IReadOnlyList<LlmAgentEvent> mapped;
                lock (_sync)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfUnavailable();
                    mapped = turn.Map(line);
                }
                foreach (LlmAgentEvent agentEvent in mapped)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return agentEvent;
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfUnavailable();
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
        using LlmOperationLifetime.Lease operation = _retirement.Enter(cancellationToken);
        ThrowIfDisposed();
        throw new NotSupportedException("Antigravity CLI does not support steering an active turn.");
    }

    public Task InterruptAsync(string turnId, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = _retirement.Enter(cancellationToken);
        ThrowIfDisposed();
        throw new NotSupportedException(
            "Antigravity CLI has no in-process interrupt. Cancel RunTurnAsync; it closes the local session.");
    }

    public async IAsyncEnumerable<LlmAgentEvent> ObserveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _retirement.ThrowIfStopping();
        ThrowIfDisposed();
        await foreach (LlmAgentEvent item in _resources.ObserveAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public Task<IReadOnlyList<LlmAgentResource>> GetResourcesAsync(CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = _retirement.Enter(cancellationToken);
        ThrowIfDisposed();
        throw new NotSupportedException(
            "Antigravity CLI does not report its background work; ResourceObservation is Unsupported.");
    }

    public Task<LlmAgentResourceOutput> ReadResourceOutputAsync(
        string resourceId,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = _retirement.Enter(cancellationToken);
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        cancellationToken.ThrowIfCancellationRequested();
        return NoAgentResources.ReadAsync();
    }

    public Task<LlmAgentResourceStopStatus> StopResourceAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = _retirement.Enter(cancellationToken);
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        throw new NotSupportedException(
            "Antigravity CLI does not report its background work; ResourceObservation is Unsupported.");
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _state = SessionState.Disposed;
            _expectingOutput = false;
            _resources.Dispose();
            return new(_retirement.RetireAsync(RetireSessionAsync));
        }
    }

    private async Task RetireSessionAsync()
    {
        await CloseProcessAsync(graceful: true).ConfigureAwait(false);
        ThrowCleanupFailures();
    }

    /// <summary>
    /// The turn's launch flags and its prompt: the introduction on the session's first sent turn, the turn instructions,
    /// the input, and a notice for each attachment AGY cannot take. Settings AGY has no flag for are not sent.
    /// </summary>
    private (AntigravityLaunch Launch, string Prompt, JsonElement? Schema, bool Introduces) Admit(LlmAgentTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Input);
        ArgumentNullException.ThrowIfNull(request.Attachments);
        if (request.ModelId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId, nameof(request));
        }
        if (request.OutputSchema is { ValueKind: not JsonValueKind.Object })
        {
            throw new ArgumentException("An output schema must be a JSON object.", nameof(request));
        }

        // Overrides apply to this turn only; unspecified values resolve against the session defaults.
        var launch = new AntigravityLaunch(
            request.ModelId ?? _defaults.ModelId,
            request.ReasoningLevel ?? _defaults.Effort,
            request.OutputSchema?.GetRawText());
        LlmProviderCapabilities capabilities = AntigravityCliLlmClient.ResolveCapabilities(_connection, launch.ModelId);
        launch = launch with { Effort = AntigravityCliLlmClient.MapReasoning(_connection, launch.ModelId, launch.Effort) };

        // An attachment AGY cannot take is left out, with the shared notice in its place.
        string notices = LlmRequestNormalizer.Normalize(_connection, new LlmChatRequest
        {
            ModelId = launch.ModelId ?? string.Empty,
            Messages = [new("user", string.Empty) { Attachments = request.Attachments }]
        }, capabilities).Messages[0].Content;

        var input = new StringBuilder();
        foreach (LlmAgentContent content in request.Input)
        {
            if (input.Length > 0) input.Append('\n');
            input.Append(content switch
            {
                LlmAgentTextContent text => text.Text,
                LlmAgentImageContent image => $"[Image: {image.ImageUrl}]",
                _ => throw new NotSupportedException($"Antigravity CLI does not accept {content.GetType().Name} input.")
            });
        }
        if (notices.Length > 0)
        {
            if (input.Length > 0) input.Append('\n');
            input.Append(notices);
        }

        string? introduction;
        lock (_sync) { introduction = _introduction; }
        // Empty input appends no user message; with nothing to introduce the turn sends nothing.
        if (input.Length == 0)
        {
            return (launch, introduction ?? string.Empty, request.OutputSchema, introduction is not null);
        }
        var prompt = new StringBuilder();
        if (introduction is not null) prompt.Append(introduction).Append("\n\n");
        if (!string.IsNullOrEmpty(request.Instructions))
        {
            prompt.Append("Instructions for this turn:\n").Append(request.Instructions).Append("\n\n");
        }
        prompt.Append(input);
        return (launch, prompt.ToString(), request.OutputSchema, introduction is not null);
    }

    private async Task StartProcessAsync(AntigravityLaunch launch, string? conversationId, CancellationToken cancellationToken)
    {
        using LlmOperationLifetime.Lease readerOwner = _retirement.Enter(CancellationToken.None);
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
        var lines = Channel.CreateUnbounded<ProcessLine>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        int generation;
        try
        {
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfUnavailable();
                process = _factory.Start(_executable, arguments, _workingDirectory);
                _process = process;
                _lines = lines;
                generation = ++_generation;
                _accountedSteps.Clear();
                _expectingOutput = true;
                CancellationToken readerToken = readerOwner.Token;
                _reader = Task.Run(() => ReadAsync(process, lines.Writer, generation, readerToken), CancellationToken.None);
            }
        }
        catch (AntigravityCliUnavailableException exception)
        {
            throw new LlmProviderException(
                "Antigravity CLI was not found. Install agy, run it once, and complete Google sign-in.",
                LlmTransport.GoogleAntigravityCli,
                providerCode: "antigravity_cli_not_found",
                innerException: exception);
        }

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
        cancellationToken.ThrowIfCancellationRequested();

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
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfUnavailable();
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
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw Failure("Antigravity CLI did not exit while its launch options were changed.", "antigravity_cli_reopen_timeout");
            }
        }
        await CloseProcessAsync(graceful: false).ConfigureAwait(false);
        ThrowCleanupFailures();
        cancellationToken.ThrowIfCancellationRequested();
        string conversationId = ThreadId;
        await StartProcessAsync(launch, conversationId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> WritePromptAsync(string prompt, CancellationToken cancellationToken)
    {
        IAntigravityCliSessionProcess process = _process ?? throw new InvalidOperationException("The session has no process.");
        string line = AntigravityProtocol.WriteUserInput(prompt);
        try
        {
            await process.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        catch (IOException)
        {
            return "The Antigravity CLI closed its input before the turn was sent.";
        }
    }

    private async Task ReadAsync(IAntigravityCliSessionProcess process, ChannelWriter<ProcessLine> writer, int generation, CancellationToken cancellationToken)
    {
        try
        {
            while (await process.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                    Publish(new ProcessLine(generation, null, exception));
                    return;
                }
                if (parsed is AntigravityUnrecognizedEvent)
                {
                    continue;
                }
                lock (_sync)
                {
                    if (_state == SessionState.Disposed || generation != _generation) return;
                    if (!_expectingOutput && generation == _generation && _state is SessionState.Idle or SessionState.Running)
                    {
                        FaultLocked("The Antigravity CLI produced output while no turn was active.");
                    }
                    writer.TryWrite(new ProcessLine(generation, parsed, null));
                }
            }
            Publish(ProcessLine.End with { Generation = generation });
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            lock (_sync)
            {
                if (exception is not OperationCanceledException && _process is null && generation == _generation)
                    RecordCleanupFailure(exception);
            }
            Publish(new ProcessLine(generation, null, exception));
        }
        finally
        {
            writer.TryComplete();
        }

        void Publish(ProcessLine line)
        {
            lock (_sync)
            {
                if (!cancellationToken.IsCancellationRequested && _state != SessionState.Disposed && generation == _generation)
                    writer.TryWrite(line);
            }
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

    [SuppressMessage("Design", "CA1031", Justification = "Every cleanup stage runs and its failure is preserved for all retirement callers.")]
    private async Task CloseProcessAsync(bool graceful)
    {
        IAntigravityCliSessionProcess? process;
        string? schemaDirectory;
        Task reader;
        lock (_sync)
        {
            process = _process;
            _process = null;
            reader = _reader;
            _reader = Task.CompletedTask;
            schemaDirectory = _schemaDirectory;
            _schemaDirectory = null;
        }
        if (process is not null)
        {
            if (!graceful)
            {
                try { await process.KillAsync().ConfigureAwait(false); }
                catch (Exception error) { RecordCleanupFailure(error); }
            }
            try { await process.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { RecordCleanupFailure(error); }
            try { await reader.ConfigureAwait(false); }
            catch (Exception error) { RecordCleanupFailure(error); }
        }
        if (schemaDirectory is not null)
        {
            try { CliInfrastructure.DeleteWorkingDirectory(schemaDirectory); }
            catch (Exception error) { RecordCleanupFailure(error); }
        }
    }

    private void RecordCleanupFailure(Exception error)
    {
        lock (_sync) { _cleanupFailures.Add(error); }
        if (error is CliProcessTerminationException termination) FaultForTermination(termination);
    }

    private void ThrowCleanupFailures()
    {
        Exception[] failures;
        lock (_sync) { failures = [.. _cleanupFailures]; }
        if (failures.Length == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Length > 1) throw new AggregateException(failures);
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
    private (LlmUsage? Usage, string? Warning) SettleUsage(int generation, AntigravityUsage? cumulative, AntigravityStepLedger ledger)
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

    /// <summary>Maps one turn's protocol events to neutral agent events.</summary>
    private sealed class TurnMapper(
        AntigravityAgentSession session,
        string turnId,
        JsonElement? schema,
        JsonElement? requestedSchema,
        bool usageDeltas)
    {
        private readonly AntigravityStepLedger _ledger = new(session._generation, session._accountedSteps);
        private readonly UsageDeltas _deltas = new(usageDeltas);
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
            bool projectContent = _ledger.Record(step);
            var events = new List<LlmAgentEvent>();
            if (!projectContent)
            {
                if (_inputSeen) { ObserveUsage(events); }
                return events;
            }
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
            ObserveUsage(events);

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

        private void ObserveUsage(List<LlmAgentEvent> events)
        {
            if (_deltas.Next(_ledger.Usage) is { } observation)
            {
                events.Add(new LlmAgentUsageEvent(ThreadId, turnId, $"{_generation}:{turnId}",
                    observation.Cumulative, observation.Cumulative));
            }
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
            events.Add(new LlmAgentUsageEvent(ThreadId, turnId, $"{_generation}:{turnId}", usage, usage));

            string itemId = $"{_generation}:{turnId}:response";
            (LlmAgentCompletionReason reason, string? error) = result.Status switch
            {
                "SUCCESS" => (LlmAgentCompletionReason.Completed, (string?)null),
                "ERROR" or "INVALID" => (LlmAgentCompletionReason.ProviderFailure, "Antigravity CLI reported a provider error."),
                "CANCELED" or "INTERRUPTED" => (LlmAgentCompletionReason.Interruption, "Antigravity CLI reported the turn as interrupted."),
                _ => (LlmAgentCompletionReason.Unknown, $"Antigravity CLI returned terminal status '{result.Status}'.")
            };
            if (reason != LlmAgentCompletionReason.Completed && result.Error is not null)
            {
                error += $" CLI detail: {result.Error}";
            }

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
