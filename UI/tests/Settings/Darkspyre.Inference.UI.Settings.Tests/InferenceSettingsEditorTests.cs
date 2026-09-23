using Bunit;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.UI.Settings;
using Lumeo;
using Xunit;

namespace Darkspyre.Inference.UI.Settings.Tests;

public sealed class InferenceSettingsEditorTests : BunitContext
{
    public InferenceSettingsEditorTests()
    {
        Services.AddLumeo();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void RendersProviderStatusWithoutAHostConnectionFlag()
    {
        var session = new FakeSession();
        var host = new FakeHost();

        IRenderedComponent<InferenceSettingsEditor> editor = Render<InferenceSettingsEditor>(parameters => parameters
            .Add(value => value.Session, session)
            .Add(value => value.Host, host));

        Assert.Contains("Example provider", editor.Markup, StringComparison.Ordinal);
        Assert.Contains("Configured: Yes", editor.Markup, StringComparison.Ordinal);
        Assert.Contains("Ready: Unknown", editor.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"dialog\"", editor.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveUsesProviderSessionCommit()
    {
        var session = new FakeSession();
        var host = new FakeHost();
        IRenderedComponent<InferenceSettingsEditor> editor = Render<InferenceSettingsEditor>(parameters => parameters
            .Add(value => value.Session, session)
            .Add(value => value.Host, host));

        SetupCommitResult result = await editor.InvokeAsync(() => editor.Instance.SaveAsync());

        Assert.Equal(SetupCommitState.Committed, result.State);
        Assert.Equal(1, session.CommitCount);
    }

    [Fact]
    public void CheckConnectionUsesBoundActionAndProviderObservation()
    {
        var session = new FakeSession();
        var host = new FakeHost();
        IRenderedComponent<InferenceSettingsEditor> editor = Render<InferenceSettingsEditor>(parameters => parameters
            .Add(value => value.Session, session)
            .Add(value => value.Host, host));

        editor.FindAll("button").Single(button => button.TextContent.Contains("Check connection", StringComparison.Ordinal)).Click();

        editor.WaitForAssertion(() => Assert.Contains("Ready: Yes", editor.Markup, StringComparison.Ordinal));
        Assert.Equal(1, host.AuthorizationCount);
        Assert.Equal(1, host.ExecutionCount);
    }

    private sealed class FakeSession : IInferenceSetupSession
    {
        public int CommitCount { get; private set; }
        public FakeSession()
        {
            Snapshot = new(0,
                new(new("example", "Example provider", InferenceImplementationKind.Connect,
                    InferenceTask.TextGeneration), "Example description"),
                [new("connect", "Connect", "Enter settings", [])], [new FakeAction(this)], SetupValidation.Valid,
                new SetupStatus { Configured = SetupObservation.Known(true) }, false, false);
        }

        public SetupSnapshot Snapshot { get; private set; }

        public event EventHandler? Changed;
        public void MarkReady()
        {
            Snapshot = Snapshot with { Status = Snapshot.Status with { Ready = SetupObservation.Known(true) } };
            Changed?.Invoke(this, EventArgs.Empty);
        }
        public Task<SetupValidation> ValidateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(SetupValidation.Valid);
        public Task<SetupCommitResult> CommitAsync(CancellationToken cancellationToken = default)
        {
            CommitCount++;
            return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAction(FakeSession session) : ISetupAction
    {
        public SetupActionDescriptor Descriptor { get; } = new("check", "Check connection",
            SetupActionPurpose.CheckReadiness, SetupSideEffect.Network);
        public Task<SetupActionResult> ExecuteAsync(IProgress<SetupProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            session.MarkReady();
            return Task.FromResult(SetupActionResult.Completed);
        }
    }

    private sealed class FakeHost : IInferenceSettingsInteractionHost
    {
        public int AuthorizationCount { get; private set; }
        public int ExecutionCount { get; private set; }
        public ValueTask<bool> AuthorizeAsync(string operation, SetupSideEffect sideEffects,
            CancellationToken cancellationToken = default)
        {
            AuthorizationCount++;
            return ValueTask.FromResult(true);
        }
        public Task<SetupActionResult> ExecuteAsync(IInferenceSetupSession session, ISetupAction action,
            IProgress<SetupProgress> progress, CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return action.ExecuteAsync(progress, cancellationToken);
        }
        public ValueTask OpenBrowserAsync(Uri uri, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
