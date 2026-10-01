using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Common.Tests;

public sealed class InferenceSettingsTests
{
    [Theory]
    [InlineData(InferenceTask.TextGeneration, InferenceCapability.Text)]
    [InlineData(InferenceTask.ImageGeneration, InferenceCapability.Image)]
    [InlineData(InferenceTask.SpeechSynthesis, InferenceCapability.Audio)]
    [InlineData(InferenceTask.AudioGeneration, InferenceCapability.Audio)]
    [InlineData(InferenceTask.SpeechTranscription, InferenceCapability.Audio)]
    [InlineData(InferenceTask.Decision, InferenceCapability.Decision)]
    [InlineData(InferenceTask.VideoGeneration, InferenceCapability.Video)]
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
    public void FieldEditsChangeOnlyTheSettingsAndMarkObservationsStale()
    {
        var target = new Target();
        var settings = new TestSettings { Status = new() { Ready = SetupObservation.Known(true) } };
        SetupTextField first = Name(settings);
        Assert.Equal(SetupEditState.Applied, first.SetValue("new").State);
        Assert.Equal("new", Name(settings).Value);
        Assert.True(settings.Status.Ready.IsStale);
        Assert.Equal(0, target.Calls);
    }

    [Fact]
    public void SecretsNeverAppearInSettingsText()
    {
        var settings = new TestSettings();
        SetupSecretField secret = Secret(settings);
        Assert.True(secret.HasValue);
        Assert.DoesNotContain("saved-secret", settings.ToString(), StringComparison.Ordinal);
        Assert.Equal(SetupEditState.Applied, secret.SetValue(SetupSecretEdit.Replace("new-secret")).State);
        Assert.DoesNotContain("new-secret", SetupSecretEdit.Replace("new-secret").ToString(), StringComparison.Ordinal);
        Assert.Equal(SetupSecretEditKind.Replace, Secret(settings).Change);
    }

    [Fact]
    public void CanSaveNeedsRequiredValuesAndSignIn()
    {
        var settings = new TestSettings();
        Assert.True(settings.CanSave);
        Name(settings).SetValue(string.Empty);
        Assert.False(settings.CanSave);
        Name(settings).SetValue("filled");
        Secret(settings).SetValue(SetupSecretEdit.Clear);
        Assert.False(settings.CanSave);
        Secret(settings).SetValue(SetupSecretEdit.Replace("key"));
        Assert.True(settings.CanSave);
        settings.SignedIn = false;
        Assert.False(settings.CanSave);
    }

    [Fact]
    public async Task SavePassesTypedChangesOnceAndRefusesASecondSave()
    {
        var target = new Target();
        var manager = new TestManager(target);
        var settings = new TestSettings();
        Name(settings).SetValue("changed");
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Equal("changed", target.Changes!.Name);
        Assert.True(settings.IsSaved);
        Assert.False(settings.HasUnsavedChanges);
        Assert.False(settings.CanSave);
        Assert.Equal(true, settings.Status.Configured.Value);
        Assert.Equal(SetupCommitState.Invalid, (await manager.SaveAsync(settings)).State);
        Assert.Equal(1, target.Calls);
    }

    [Fact]
    public async Task InvalidSaveReturnsIssuesWithoutCallingTheHost()
    {
        var target = new Target();
        var settings = new TestSettings();
        Name(settings).SetValue(string.Empty);
        SetupCommitResult result = await new TestManager(target).SaveAsync(settings);
        Assert.Equal(SetupCommitState.Invalid, result.State);
        Assert.Equal("name", Assert.Single(result.Issues!).FieldId);
        Assert.Equal(0, target.Calls);
    }

    [Fact]
    public async Task SaveWithoutSignInNeverCallsTheHost()
    {
        var target = new Target();
        var settings = new TestSettings { SignedIn = false };
        Assert.Equal(SetupCommitState.Invalid, (await new TestManager(target).SaveAsync(settings)).State);
        Assert.Equal(0, target.Calls);
    }

    [Fact]
    public async Task CancellationAfterTheHostWasCalledIsUncertainAndNotRetried()
    {
        using var cancellation = new CancellationTokenSource();
        var target = new Target { Commit = _ => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); } };
        var manager = new TestManager(target);
        var settings = new TestSettings();
        Assert.Equal(SetupCommitState.Uncertain, (await manager.SaveAsync(settings, cancellation.Token)).State);
        Assert.True(settings.SaveOutcomeUncertain);
        Assert.Equal(SetupCommitState.Invalid, (await manager.SaveAsync(settings)).State);
        Assert.Equal(1, target.Calls);
    }

    [Fact]
    public async Task HostFailureIsUncertainWithoutItsPayload()
    {
        var target = new Target { Commit = _ => throw new InvalidOperationException("host-secret") };
        var settings = new TestSettings();
        SetupCommitResult result = await new TestManager(target).SaveAsync(settings);
        Assert.Equal(SetupCommitState.Uncertain, result.State);
        Assert.DoesNotContain("host-secret", Assert.Single(result.Issues!).Message, StringComparison.Ordinal);
        Assert.True(settings.SaveOutcomeUncertain);
    }

    [Fact]
    public async Task UncertainHostResultIsExplicitEvenWhenNoFieldsChanged()
    {
        var target = new Target { Commit = _ => Task.FromResult(new SetupCommitResult(SetupCommitState.Uncertain)) };
        var settings = new TestSettings();
        Assert.False(settings.HasUnsavedChanges);
        await new TestManager(target).SaveAsync(settings);
        Assert.True(settings.IsSaved);
        Assert.True(settings.SaveOutcomeUncertain);
    }

    [Fact]
    public async Task ConflictLeavesTheSettingsEditable()
    {
        var target = new Target { Commit = _ => Task.FromResult(new SetupCommitResult(SetupCommitState.Conflict)) };
        var settings = new TestSettings();
        Name(settings).SetValue("changed");
        Assert.Equal(SetupCommitState.Conflict, (await new TestManager(target).SaveAsync(settings)).State);
        Assert.False(settings.IsSaved);
        Assert.True(settings.HasUnsavedChanges);
    }

    [Fact]
    public async Task PrecancelledSaveNeverCallsTheHost()
    {
        var target = new Target();
        Assert.Equal(SetupCommitState.Cancelled, (await new TestManager(target).SaveAsync(new TestSettings(), new(true))).State);
        Assert.Equal(0, target.Calls);
    }

    [Fact]
    public void UnsavedChangesTrackTheValuesAndRevert()
    {
        var settings = new TestSettings();
        Assert.False(settings.HasUnsavedChanges);
        Name(settings).SetValue("changed");
        Assert.True(settings.HasUnsavedChanges);
        Name(settings).SetValue("original");
        Assert.False(settings.HasUnsavedChanges);
    }

    [Fact]
    public async Task ActionsRunAgainstTheCurrentValues()
    {
        var manager = new TestManager(new Target());
        var settings = new TestSettings();
        Name(settings).SetValue("current");
        string? seen = null;
        manager.Execute = (value, _) => { seen = value.NameValue; return Task.FromResult(SetupActionResult.Completed); };
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, "test")).State);
        Assert.Equal("current", seen);
    }

    [Fact]
    public async Task UnknownOrDisabledActionsNeverRun()
    {
        var manager = new TestManager(new Target());
        var settings = new TestSettings();
        Assert.Equal(SetupActionState.Unavailable, (await manager.RunAsync(settings, "missing")).State);
        settings.TestEnabled = false;
        Assert.Equal(SetupActionState.Unavailable, (await manager.RunAsync(settings, "test")).State);
        Assert.Equal(0, manager.ActionCalls);
    }

    [Fact]
    public async Task ActionFailureDoesNotLeakExceptionPayload()
    {
        var manager = new TestManager(new Target()) { Execute = (_, _) => throw new InvalidOperationException("provider-secret") };
        SetupActionResult result = await manager.RunAsync(new TestSettings(), "test");
        Assert.Equal(SetupActionState.Failed, result.State);
        Assert.DoesNotContain("provider-secret", Assert.Single(result.Issues!).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledActionReportsCancelled()
    {
        var manager = new TestManager(new Target())
        {
            Execute = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return SetupActionResult.Completed; }
        };
        using var cancellation = new CancellationTokenSource();
        Task<SetupActionResult> running = manager.RunAsync(new TestSettings(), "test", cancellationToken: cancellation.Token);
        await cancellation.CancelAsync();
        Assert.Equal(SetupActionState.Cancelled, (await running).State);
    }

    [Fact]
    public async Task LocalChoicesArePagedWithoutAskingTheProvider()
    {
        var manager = new TestManager(new Target());
        SetupChoicePage first = await manager.QueryChoicesAsync(new TestSettings(), "size", new("", PageSize: 2));
        Assert.Equal(["small", "medium"], first.Choices.Select(choice => choice.Value));
        SetupChoicePage second = await manager.QueryChoicesAsync(new TestSettings(), "size", new("", first.Continuation, 2));
        Assert.Equal("large", Assert.Single(second.Choices).Value);
        Assert.Null(second.Continuation);
        SetupChoicePage search = await manager.QueryChoicesAsync(new TestSettings(), "size", new("LAR"));
        Assert.Equal("large", Assert.Single(search.Choices).Value);
        Assert.Equal(0, manager.QueryCalls);
    }

    [Fact]
    public async Task ProviderChoiceFailureDoesNotLeakItsPayload()
    {
        var manager = new TestManager(new Target()) { Query = _ => throw new InvalidOperationException("provider-secret") };
        SetupChoicePage page = await manager.QueryChoicesAsync(new TestSettings(), "model", new());
        Assert.Equal(SetupChoiceState.Failed, page.State);
        Assert.DoesNotContain("provider-secret", Assert.Single(page.Issues!).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownChoiceFieldIsUnavailable()
    {
        var manager = new TestManager(new Target());
        Assert.Equal(SetupChoiceState.Unavailable, (await manager.QueryChoicesAsync(new TestSettings(), "name", new())).State);
        Assert.Equal(0, manager.QueryCalls);
    }

    [Fact]
    public async Task ChoiceQueryCancellationReportsCancelled()
    {
        var manager = new TestManager(new Target()) { Query = async token => { await Task.Delay(Timeout.Infinite, token); return new([]); } };
        using var cancellation = new CancellationTokenSource();
        Task<SetupChoicePage> query = manager.QueryChoicesAsync(new TestSettings(), "model", new(), cancellation.Token);
        await cancellation.CancelAsync();
        Assert.Equal(SetupChoiceState.Cancelled, (await query).State);
    }

    [Fact]
    public async Task SettingsOfAnotherProviderAreRejected()
    {
        var manager = new TestManager(new Target());
        await Assert.ThrowsAsync<ArgumentException>(() => manager.SaveAsync(new OtherSettings()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingOperationsDispatchThroughDerivedOwnershipForBaseAndInterfaceReferences(bool throughInterface)
    {
        var target = new Target();
        var owner = new OwnedManager(target);
        InferenceSettingsManager<TestSettings, Changes> manager = owner;
        var settings = new TestSettings();
        if (throughInterface)
        {
            IInferenceSettingsManager contract = manager;
            Assert.Equal(SetupActionState.Completed, (await contract.RunAsync(settings, "test")).State);
            Assert.Equal(3, (await contract.QueryChoicesAsync(settings, "size", new())).Choices.Count);
            Assert.Equal(SetupCommitState.Committed, (await contract.SaveAsync(settings)).State);
        }
        else
        {
            Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, "test")).State);
            Assert.Equal(3, (await manager.QueryChoicesAsync(settings, "size", new())).Choices.Count);
            Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        }
        Assert.Equal((1, 1, 1), (owner.RunEntries, owner.QueryEntries, owner.SaveEntries));
        Assert.Equal(1, target.Calls);
    }

    [Fact]
    public async Task DetachedCommitTargetRefusesSaveWithoutFabricatingAnUncertainCommit()
    {
        var target = new Target();
        var manager = new OwnedManager(target);
        manager.ReleaseTarget();
        manager.ReleaseTarget();
        var settings = new TestSettings();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.SaveAsync(settings));
        Assert.Equal(0, target.Calls);
        Assert.Equal(0, target.Disposals);
        Assert.False(settings.IsSaved);
        Assert.False(settings.SaveOutcomeUncertain);
    }

    [Fact]
    public void RetainedManagerDoesNotRetainItsDetachedBorrowedCommitTarget()
    {
        (OwnedManager manager, WeakReference target) = CreateDetachedManager();
        for (int attempt = 0; target.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10);
        }
        Assert.False(target.IsAlive);
        GC.KeepAlive(manager);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (OwnedManager, WeakReference) CreateDetachedManager()
    {
        var target = new Target();
        var manager = new OwnedManager(target);
        manager.ReleaseTarget();
        return (manager, new WeakReference(target));
    }

    private static SetupTextField Name(TestSettings settings) =>
        Assert.IsType<SetupTextField>(settings.Steps[0].Sections[0].Fields[0]);

    private static SetupSecretField Secret(TestSettings settings) =>
        Assert.IsType<SetupSecretField>(settings.Steps[0].Sections[0].Fields[1]);

    private sealed record Changes(string Name, string? Secret);

    private sealed class Target : ISetupCommitTarget<Changes>, IDisposable
    {
        public Changes? Changes { get; private set; }
        public int Calls { get; private set; }
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
        public Func<CancellationToken, Task<SetupCommitResult>> Commit { get; init; } =
            _ => Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "v2"));
        public Task<SetupCommitResult> CommitAsync(Changes changes, CancellationToken cancellationToken = default)
        {
            Calls++;
            Changes = changes;
            return Commit(cancellationToken);
        }
    }

    private static InferenceSetupDefinition TestDefinition { get; } =
        new(new("test", "Test", InferenceImplementationKind.Connect, InferenceTask.TextGeneration), "Test");

    private static SetupConfigurationIdentity NewEntry() => new(SetupChangeIntent.Create, "entry", null);

    private sealed class TestSettings() : InferenceSettings<Changes>(TestDefinition, NewEntry())
    {
        private static readonly SetupChoice[] Sizes = [new("small", "Small"), new("medium", "Medium"), new("large", "Large")];
        private string _name = "original";
        private string? _secret = "saved-secret";
        private SetupSecretEditKind _secretChange;
        private string _size = "small";
        private string _model = string.Empty;
        public string NameValue => _name;
        public bool SignedIn { get; set; } = true;
        public bool TestEnabled { get; set; } = true;
        protected override IReadOnlyList<SetupStep> CreateSteps() =>
        [new("step", "Step", "Instructions", [new("section", "Section",
        [
            new SetupTextField(new("name", "Name") { IsRequired = true }, _name, "original", value => Edit(() => _name = value)),
            new SetupSecretField(new("key", "Key") { IsRequired = true }, _secret is not null, _secretChange,
                value => Edit(() =>
                {
                    _secretChange = value.Kind;
                    _secret = value.Kind switch { SetupSecretEditKind.Replace => value.Value, SetupSecretEditKind.Clear => null, _ => "saved-secret" };
                })),
            new SetupChoiceField(new("size", "Size"), _size, "small", value => Edit(() => _size = value), false),
            new SetupChoiceField(new("model", "Model"), _model, string.Empty, value => Edit(() => _model = value), true,
                SetupSideEffect.Network, SetupChoiceCapabilities.Searchable)
        ])])];
        protected override IReadOnlyList<SetupActionDescriptor> CreateActions() =>
            [new("test", "Test", SetupActionPurpose.Test, SetupSideEffect.Network, TestEnabled)];
        protected override SetupValidation ValidateDraft() => string.IsNullOrWhiteSpace(_name)
            ? new([new("required", "Name is required.", "name")]) : SetupValidation.Valid;
        protected override Changes CreateChanges() => new(_name, _secret);
        protected override bool IsDraftDirty => _name != "original" || _secretChange != SetupSecretEditKind.Keep;
        protected override bool IsSignInSatisfied => SignedIn;
        protected override SetupChoicePage? GetLocalChoices(string fieldId, SetupChoiceQuery query) =>
            fieldId == "size" ? Page(query, Sizes) : null;
    }

    private sealed class OtherSettings() : InferenceSettings<Changes>(TestDefinition, NewEntry())
    {
        protected override IReadOnlyList<SetupStep> CreateSteps() => [];
        protected override IReadOnlyList<SetupActionDescriptor> CreateActions() => [];
        protected override SetupValidation ValidateDraft() => SetupValidation.Valid;
        protected override Changes CreateChanges() => new("other", null);
        protected override bool IsDraftDirty => false;
    }

    private sealed class TestManager(Target target) : InferenceSettingsManager<TestSettings, Changes>(target)
    {
        public int ActionCalls { get; private set; }
        public int QueryCalls { get; private set; }
        public Func<TestSettings, CancellationToken, Task<SetupActionResult>> Execute { get; set; } =
            (_, _) => Task.FromResult(SetupActionResult.Completed);
        public Func<CancellationToken, Task<SetupChoicePage>> Query { get; set; } = _ => Task.FromResult(new SetupChoicePage([]));

        protected override Task<SetupActionResult> RunCoreAsync(TestSettings settings, SetupActionDescriptor action,
            IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
        {
            ActionCalls++;
            return Execute(settings, cancellationToken);
        }

        protected override Task<SetupChoicePage> QueryProviderChoicesAsync(TestSettings settings, string fieldId,
            SetupChoiceQuery query, CancellationToken cancellationToken)
        {
            QueryCalls++;
            return Query(cancellationToken);
        }
    }

    private sealed class OwnedManager(Target target) : InferenceSettingsManager<TestSettings, Changes>(target)
    {
        public int RunEntries { get; private set; }
        public int QueryEntries { get; private set; }
        public int SaveEntries { get; private set; }
        public void ReleaseTarget() => DetachCommitTarget();

        public override Task<SetupActionResult> RunAsync(InferenceSettings settings, string actionId,
            IProgress<SetupProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            RunEntries++;
            return base.RunAsync(settings, actionId, progress, cancellationToken);
        }

        public override Task<SetupChoicePage> QueryChoicesAsync(InferenceSettings settings, string fieldId,
            SetupChoiceQuery query, CancellationToken cancellationToken = default)
        {
            QueryEntries++;
            return base.QueryChoicesAsync(settings, fieldId, query, cancellationToken);
        }

        public override Task<SetupCommitResult> SaveAsync(InferenceSettings settings, CancellationToken cancellationToken = default)
        {
            SaveEntries++;
            return base.SaveAsync(settings, cancellationToken);
        }

        protected override Task<SetupActionResult> RunCoreAsync(TestSettings settings, SetupActionDescriptor action,
            IProgress<SetupProgress>? progress, CancellationToken cancellationToken) => Task.FromResult(SetupActionResult.Completed);
    }
}
