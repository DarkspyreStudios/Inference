using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Common.Tests;

public sealed class SetupSessionTests
{
    [Theory]
    [InlineData(InferenceTask.TextGeneration, InferenceCapability.Text)]
    [InlineData(InferenceTask.ImageGeneration, InferenceCapability.Image)]
    [InlineData(InferenceTask.SpeechSynthesis, InferenceCapability.Audio)]
    [InlineData(InferenceTask.AudioGeneration, InferenceCapability.Audio)]
    [InlineData(InferenceTask.SpeechTranscription, InferenceCapability.Audio)]
    [InlineData(InferenceTask.Decision, InferenceCapability.Decision)]
    public void TasksHaveOneCanonicalGroup(InferenceTask task, InferenceCapability capability) =>
        Assert.Equal(capability, task.GetCapability());

    [Fact]
    public void DescriptorCopiesTaskDeclarations()
    {
        InferenceTask[] tasks = [InferenceTask.Decision];
        var descriptor = new InferenceImplementationDescriptor("id", "name", InferenceImplementationKind.Tensor, tasks);
        tasks[0] = InferenceTask.TextGeneration;
        Assert.Equal(InferenceTask.Decision, Assert.Single(descriptor.Tasks));
        Assert.Throws<ArgumentException>(() => new InferenceImplementationDescriptor("id", "name", InferenceImplementationKind.Tensor));
    }

    [Fact]
    public async Task FieldEditsAreTypedAndRejectStaleSnapshots()
    {
        await using var session = new TestSession(new Target());
        SetupTextField first = Field(session);
        Assert.Equal(SetupEditState.Applied, first.SetValue("new").State);
        Assert.Equal(SetupEditState.Stale, first.SetValue("stale").State);
        Assert.Equal("new", Field(session).Value);
        Assert.Equal(1, session.Snapshot.Revision);
        Assert.True(session.Snapshot.Status.Ready.IsStale);
    }

    [Fact]
    public async Task CancelledEditNeverCallsCommitOrExposesSavedSecret()
    {
        var target = new Target();
        var session = new TestSession(target);
        var secret = Assert.IsType<SetupSecretField>(session.Snapshot.Steps[0].Sections[0].Fields[1]);
        Assert.True(secret.HasValue);
        Assert.DoesNotContain("saved-secret", session.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Equal(SetupEditState.Applied, secret.SetValue(SetupSecretEdit.Replace("new-secret")).State);
        Assert.DoesNotContain("new-secret", SetupSecretEdit.Replace("new-secret").ToString(), StringComparison.Ordinal);
        await session.DisposeAsync();
        Assert.Null(target.Changes);
        Assert.True(session.Cleared);
        Assert.Equal(SetupCommitState.Closed, (await session.CommitAsync()).State);
    }

    [Fact]
    public async Task RunningActionBlocksEditsAndCommitWithoutQueuingThem()
    {
        await using var session = new TestSession(new Target());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Execute = async token => { entered.SetResult(); await release.Task.WaitAsync(token); return SetupActionResult.Completed; };
        SetupTextField field = Field(session);
        Task<SetupActionResult> running = session.Snapshot.Actions[0].ExecuteAsync();
        await entered.Task;
        Assert.Equal(SetupEditState.Busy, field.SetValue("blocked").State);
        Assert.Equal(SetupCommitState.Busy, (await session.CommitAsync()).State);
        release.SetResult();
        Assert.Equal(SetupActionState.Completed, (await running).State);
        Assert.False(session.Snapshot.IsBusy);
    }

    [Fact]
    public async Task StaleActionNeverRuns()
    {
        await using var session = new TestSession(new Target());
        ISetupAction action = session.Snapshot.Actions[0];
        Field(session).SetValue("changed");
        Assert.Equal(SetupActionState.Stale, (await action.ExecuteAsync()).State);
        Assert.Equal(0, session.ActionCalls);
    }

    [Fact]
    public async Task AsyncChoicesCannotPublishAnOlderRevision()
    {
        await using var session = new TestSession(new Target());
        var response = new TaskCompletionSource<SetupChoicePage>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Query = _ => response.Task;
        Task<SetupChoicePage> query = session.Choices(new(session.Snapshot.Revision));
        Field(session).SetValue("changed");
        response.SetResult(new(0, [new("old", "old")]));
        SetupChoicePage result = await query;
        Assert.Equal(SetupChoiceState.Stale, result.State);
        Assert.Empty(result.Choices);
    }

    [Fact]
    public async Task DisposeCancelsButWaitsForQueriesToReleaseResources()
    {
        var session = new TestSession(new Target());
        var response = new TaskCompletionSource<SetupChoicePage>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken received = default;
        session.Query = token => { received = token; return response.Task; };
        Task<SetupChoicePage> query = session.Choices(new(0));
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = received.Register(() => cancelled.TrySetResult());
        Task disposal = session.DisposeAsync().AsTask();
        await cancelled.Task;
        Assert.True(received.IsCancellationRequested);
        Assert.False(disposal.IsCompleted);
        response.SetResult(new(0, []));
        await query;
        await disposal;
        Assert.True(session.Cleared);
    }

    [Fact]
    public async Task CommitPassesTypedDraftOnceAndClosesAfterSuccess()
    {
        var target = new Target();
        await using var session = new TestSession(target);
        Field(session).SetValue("changed");
        Assert.Equal(SetupCommitState.Committed, (await session.CommitAsync()).State);
        Assert.Equal("changed", target.Changes!.Name);
        Assert.Equal(SetupCommitState.Closed, (await session.CommitAsync()).State);
        Assert.Equal(1, target.Calls);
    }

    [Fact]
    public async Task CancellationAfterCommitWasInvokedIsUncertainNotSafeToRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var target = new Target { Commit = _ => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); } };
        await using var session = new TestSession(target);
        Assert.Equal(SetupCommitState.Uncertain, (await session.CommitAsync(cancellation.Token)).State);
        Assert.Equal(SetupCommitState.Closed, (await session.CommitAsync()).State);
        Assert.Equal(1, target.Calls);
    }

    [Fact]
    public async Task PrecancelledCommitNeverInvokesHost()
    {
        var target = new Target();
        await using var session = new TestSession(target);
        Assert.Equal(SetupCommitState.Cancelled, (await session.CommitAsync(new(true))).State);
        Assert.Equal(0, target.Calls);
    }

    [Fact]
    public async Task ActionFailureDoesNotLeakExceptionPayload()
    {
        await using var session = new TestSession(new Target())
        {
            Execute = _ => throw new InvalidOperationException("provider-secret")
        };
        SetupActionResult result = await session.Snapshot.Actions[0].ExecuteAsync();
        Assert.Equal(SetupActionState.Failed, result.State);
        Assert.DoesNotContain("provider-secret", Assert.Single(result.Issues!).Message, StringComparison.Ordinal);
    }

    private static SetupTextField Field(TestSession session) =>
        Assert.IsType<SetupTextField>(session.Snapshot.Steps[0].Sections[0].Fields[0]);

    [Fact]
    public async Task InvalidCommitPublishesValidationWithoutInvokingTheHost()
    {
        var target = new Target();
        await using var session = new TestSession(target);
        Field(session).SetValue(string.Empty);
        bool observed = false;
        session.Changed += (_, _) => observed = !session.Snapshot.Validation.IsValid;
        Assert.Equal(SetupCommitState.Invalid, (await session.CommitAsync()).State);
        Assert.True(observed);
        Assert.Equal(0, target.Calls);
    }

    [Fact]
    public async Task ObserverFailureCannotChangeCommitOutcomeOrSuppressOtherObservers()
    {
        var target = new Target();
        await using var session = new TestSession(target);
        int observed = 0;
        session.Changed += (_, _) => throw new InvalidOperationException("view failure");
        session.Changed += (_, _) => observed++;
        Assert.Equal(SetupCommitState.Committed, (await session.CommitAsync()).State);
        Assert.Equal(1, target.Calls);
        Assert.True(observed >= 2);
        Assert.False(session.Snapshot.IsBusy);
    }

    private sealed record Changes(string Name, string? Secret);

    [Fact]
    public async Task DirtyTracksDraftAndRevertsButNotValidationOrActions()
    {
        await using var session = new TestSession(new Target());
        Assert.False(session.Snapshot.IsDirty);
        await session.ValidateAsync();
        await session.Snapshot.Actions[0].ExecuteAsync();
        Assert.False(session.Snapshot.IsDirty);
        SetupTextField Name() => (SetupTextField)session.Snapshot.Steps[0].Sections[0].Fields[0];
        Name().SetValue("changed");
        Assert.True(session.Snapshot.IsDirty);
        Name().SetValue("original");
        Assert.False(session.Snapshot.IsDirty);
        Name().SetValue("saved");
        Assert.Equal(SetupCommitState.Committed, (await session.CommitAsync()).State);
        Assert.False(session.Snapshot.IsDirty);
        Assert.False(session.Snapshot.CommitOutcomeUncertain);
    }

    [Fact]
    public async Task UncertainCommitIsExplicitEvenWhenNoFieldsChanged()
    {
        await using var session = new TestSession(new Target { Commit = _ => Task.FromResult(new SetupCommitResult(SetupCommitState.Uncertain)) });
        Assert.False(session.Snapshot.IsDirty);
        await session.CommitAsync();
        Assert.True(session.Snapshot.IsClosed);
        Assert.True(session.Snapshot.CommitOutcomeUncertain);
        Assert.Equal(SetupCommitState.Closed, (await session.CommitAsync()).State);
    }

    private sealed class Target : ISetupCommitTarget<Changes>
    {
        public Changes? Changes { get; private set; }
        public int Calls { get; private set; }
        public Func<CancellationToken, Task<SetupCommitResult>> Commit { get; init; } =
            _ => Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "v2"));
        public Task<SetupCommitResult> CommitAsync(Changes changes, CancellationToken cancellationToken = default)
        {
            Calls++;
            Changes = changes;
            return Commit(cancellationToken);
        }
    }

    private sealed class TestSession(Target target) : InferenceSetupSession<Changes>(
        new(new("test", "Test", InferenceImplementationKind.Connect, InferenceTask.TextGeneration), "Test"), target)
    {
        private string _name = "original";
        private string? _secret = "saved-secret";
        private SetupSecretEditKind _secretChange;
        public bool Cleared { get; private set; }
        public int ActionCalls { get; private set; }
        public Func<CancellationToken, Task<SetupActionResult>> Execute { get; set; } = _ => Task.FromResult(SetupActionResult.Completed);
        public Func<CancellationToken, Task<SetupChoicePage>> Query { get; set; } = _ => Task.FromResult(new SetupChoicePage(0, []));
        public Task<SetupChoicePage> Choices(SetupChoiceQuery query) => QueryChoicesAsync(query, Query, default);
        protected override IReadOnlyList<SetupStep> CreateSteps(long revision) =>
        [new("step", "Step", "Instructions", [new("section", "Section",
        [
            new SetupTextField(new("name", "Name"), _name, "original", value => Edit(revision, () => _name = value)),
            new SetupSecretField(new("key", "Key"), _secret is not null, _secretChange,
                value => Edit(revision, () =>
                {
                    _secretChange = value.Kind;
                    _secret = value.Kind switch { SetupSecretEditKind.Replace => value.Value, SetupSecretEditKind.Clear => null, _ => "saved-secret" };
                }))
        ])])];
        protected override IReadOnlyList<ISetupAction> CreateActions(long revision) =>
        [BindAction(revision, new("test", "Test", SetupActionPurpose.Test, SetupSideEffect.Network), (_, token) =>
        {
            ActionCalls++;
            return Execute(token);
        })];
        protected override SetupValidation ValidateDraft() => string.IsNullOrWhiteSpace(_name)
            ? new([new("required", "Name is required.", "name")]) : SetupValidation.Valid;
        protected override Changes CreateChanges() => new(_name, _secret);
        protected override bool IsDraftDirty => _name != "original" || _secretChange != SetupSecretEditKind.Keep;
        protected override void ClearSensitiveState() { _secret = null; Cleared = true; }
    }
}
