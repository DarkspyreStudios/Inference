namespace Darkspyre.Inference.Common.Setup;

/// <summary>
/// Owns a draft's revisions and operation lifetime, not application storage or a background service.
/// The host retains this session when a view closes and explicitly disposes it when work should end.
/// </summary>
public abstract class InferenceSetupSession<TChanges> : IInferenceSetupSession
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ISetupCommitTarget<TChanges> _target;
    private readonly HashSet<TaskCompletionSource> _queries = [];
    private TaskCompletionSource? _operation;
    private Task? _disposal;
    private long _revision;
    private bool _closed;
    private SetupValidation _validation = SetupValidation.Valid;

    protected InferenceSetupSession(InferenceSetupDefinition definition, ISetupCommitTarget<TChanges> target)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(target);
        Definition = definition;
        _target = target;
    }

    public event EventHandler? Changed;
    public InferenceSetupDefinition Definition { get; }
    protected SetupStatus Status { get; set; } = new();
    protected long Revision { get { lock (_sync) { return _revision; } } }

    public SetupSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return new(_revision, Definition, CreateSteps(_revision), CreateActions(_revision),
                    _validation, GetStatus(), _operation is not null, _closed)
                { Interaction = GetInteraction() };
            }
        }
    }

    protected abstract IReadOnlyList<SetupStep> CreateSteps(long revision);
    protected abstract IReadOnlyList<ISetupAction> CreateActions(long revision);
    protected abstract SetupValidation ValidateDraft();
    protected abstract TChanges CreateChanges();
    protected abstract void ClearSensitiveState();
    protected virtual SetupStatus GetStatus() => Status;
    protected virtual SetupInteraction? GetInteraction() => null;

    protected virtual void OnDraftEdited()
    {
        Status = Status with
        {
            Authenticated = Stale(Status.Authenticated),
            Verified = Stale(Status.Verified),
            Ready = Stale(Status.Ready)
        };
    }

    protected static SetupObservation Stale(SetupObservation observation) => observation.IsApplicable
        ? observation with { IsStale = true }
        : observation;

    protected SetupEditResult Edit(long expectedRevision, Action change)
    {
        lock (_sync)
        {
            if (_closed) { return new(SetupEditState.Closed, []); }
            if (_operation is not null) { return new(SetupEditState.Busy, []); }
            if (_revision != expectedRevision) { return new(SetupEditState.Stale, []); }
            change();
            _revision++;
            _validation = SetupValidation.Valid;
            OnDraftEdited();
        }
        NotifyChanged();
        return SetupEditResult.Applied;
    }

    /// <summary>Publish an operation's state change under the same owner lock as field edits.</summary>
    protected void Publish(Action change)
    {
        lock (_sync) { change(); _revision++; }
        NotifyChanged();
    }

    protected TResult Read<TResult>(Func<TResult> read)
    {
        lock (_sync) { return read(); }
    }

    protected bool PublishIfCurrent(long revision, Action change)
    {
        lock (_sync)
        {
            if (_closed || _revision != revision) { return false; }
            change();
            _revision++;
        }
        NotifyChanged();
        return true;
    }

    protected ISetupAction BindAction(long revision, SetupActionDescriptor descriptor,
        Func<IProgress<SetupProgress>?, CancellationToken, Task<SetupActionResult>> execute) =>
        new BoundAction(descriptor, (progress, token) => RunActionAsync(revision, descriptor, execute, progress, token));

    protected async Task<SetupChoicePage> QueryChoicesAsync(
        SetupChoiceQuery query, Func<CancellationToken, Task<SetupChoicePage>> queryChoices,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;
        TaskCompletionSource completion;
        lock (_sync)
        {
            if (_closed) { return new(_revision, [], State: SetupChoiceState.Unavailable); }
            if (query.ExpectedRevision != _revision) { return new(_revision, [], State: SetupChoiceState.Stale); }
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _queries.Add(completion);
        }
        using (linked)
        {
            try
            {
                SetupChoicePage result = await queryChoices(linked.Token).ConfigureAwait(false);
                lock (_sync)
                {
                    return query.ExpectedRevision != _revision || _closed
                        ? new(_revision, [], State: SetupChoiceState.Stale)
                        : result with { Revision = _revision };
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                return new(query.ExpectedRevision, [], State: SetupChoiceState.Cancelled);
            }
            catch (Exception)
            {
                return new(query.ExpectedRevision, [], State: SetupChoiceState.Failed,
                    Issues: [new("choices-unavailable", "The choices could not be retrieved.")]);
            }
            finally
            {
                lock (_sync) { _queries.Remove(completion); completion.TrySetResult(); }
            }
        }
    }

    public Task<SetupValidation> ValidateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetupValidation result;
        lock (_sync)
        {
            result = _closed ? new([new("closed", "This setup session is closed.")])
                : _operation is not null ? new([new("busy", "An operation is already running.")])
                : _validation = ValidateDraft();
        }
        NotifyChanged();
        return Task.FromResult(result);
    }

    public async Task<SetupCommitResult> CommitAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? completion = null;
        CancellationTokenSource? linked = null;
        TChanges changes = default!;
        SetupCommitResult? invalid = null;
        lock (_sync)
        {
            if (_closed) { return new(SetupCommitState.Closed); }
            if (_operation is not null) { return new(SetupCommitState.Busy); }
            if (cancellationToken.IsCancellationRequested) { return new(SetupCommitState.Cancelled); }
            _validation = ValidateDraft();
            if (!_validation.IsValid) { invalid = new(SetupCommitState.Invalid, Issues: _validation.Issues); }
            else
            {
                changes = CreateChanges();
                completion = _operation = new(TaskCreationOptions.RunContinuationsAsynchronously);
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            }
        }
        NotifyChanged();
        if (invalid is not null) { return invalid; }
        // Successful preparation initializes both operation resources while holding the owner lock.
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(linked);
        bool invoked = false;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            invoked = true;
            SetupCommitResult result = await _target.CommitAsync(changes, linked.Token).ConfigureAwait(false);
            if (result.State is SetupCommitState.Committed or SetupCommitState.Uncertain)
            {
                lock (_sync)
                {
                    // One draft commits once; a fresh edit must capture the new saved version.
                    _closed = true;
                    if (result.State == SetupCommitState.Committed)
                    {
                        Status = Status with { Configured = SetupObservation.Known(true) };
                    }
                }
            }
            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (invoked) { lock (_sync) { _closed = true; } }
            return new(invoked ? SetupCommitState.Uncertain : SetupCommitState.Cancelled);
        }
        catch (Exception)
        {
            // Host exceptions may contain secrets and do not prove that no write occurred.
            lock (_sync) { _closed = true; }
            return new(SetupCommitState.Uncertain, Issues: [new("commit-outcome-unknown", "Read the saved configuration before retrying.")]);
        }
        finally
        {
            linked.Dispose();
            Finish(completion);
        }
    }

    private async Task<SetupActionResult> RunActionAsync(long revision, SetupActionDescriptor descriptor,
        Func<IProgress<SetupProgress>?, CancellationToken, Task<SetupActionResult>> execute,
        IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        TaskCompletionSource completion;
        CancellationTokenSource linked;
        lock (_sync)
        {
            if (_closed) { return new(SetupActionState.Closed); }
            if (_operation is not null) { return new(SetupActionState.Busy); }
            if (revision != _revision) { return new(SetupActionState.Stale); }
            if (!descriptor.IsEnabled) { return new(SetupActionState.Unavailable); }
            completion = _operation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        }
        try
        {
            NotifyChanged();
            if (descriptor.Purpose is not (SetupActionPurpose.Unload or SetupActionPurpose.Stop))
            {
                linked.Token.ThrowIfCancellationRequested();
            }
            // Tensor teardown interprets a cancelled token as urgency, not "do nothing".
            return await execute(progress, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return new(SetupActionState.Cancelled);
        }
        catch (Exception)
        {
            // Protocol exceptions can contain credentials or raw responses. Never project them.
            return new(SetupActionState.Failed,
                [new("operation-failed", "The operation failed. Check configuration and provider availability.")]);
        }
        finally
        {
            linked.Dispose();
            Finish(completion);
        }
    }

    private void Finish(TaskCompletionSource completion)
    {
        lock (_sync)
        {
            _operation = null;
            completion.TrySetResult();
        }
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        if (Changed is not { } listeners) { return; }
        foreach (EventHandler listener in listeners.GetInvocationList().Cast<EventHandler>())
        {
            try { listener(this, EventArgs.Empty); }
            catch (Exception)
            {
                // Observers do not own operations. A rendering failure must not alter a host commit,
                // suppress another observer, or prevent completion/disposal. Snapshot remains authoritative.
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        lock (_sync)
        {
            _closed = true;
            _disposal ??= DisposeCoreAsync(Task.WhenAll(
                _queries.Select(query => query.Task).Append(_operation?.Task ?? Task.CompletedTask)));
            return new(_disposal);
        }
    }

    private async Task DisposeCoreAsync(Task pending)
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await pending.ConfigureAwait(false);
        lock (_sync) { ClearSensitiveState(); }
        _lifetime.Dispose();
        NotifyChanged();
    }

    private sealed class BoundAction(SetupActionDescriptor descriptor,
        Func<IProgress<SetupProgress>?, CancellationToken, Task<SetupActionResult>> execute) : ISetupAction
    {
        public SetupActionDescriptor Descriptor { get; } = descriptor;
        public Task<SetupActionResult> ExecuteAsync(IProgress<SetupProgress>? progress = null,
            CancellationToken cancellationToken = default) => execute(progress, cancellationToken);
    }
}
