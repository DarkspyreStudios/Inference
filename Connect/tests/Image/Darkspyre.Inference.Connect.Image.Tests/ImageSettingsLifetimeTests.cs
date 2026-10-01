using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Image.Tests;

public sealed partial class ImageSetupTests
{
    private static readonly TimeSpan LifetimeBound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task EditedDraftCannotBeVerifiedByEarlierIgnoringHealthResult()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource<ImageProviderHealth>();
        var transport = new Transport { OnHealth = connection => { entered.SetResult(); return release.Task; } };
        var (manager, settings) = await NewAsync(transport, new Target());
        await using (manager)
        {
            Task<SetupActionResult> pending = manager.RunAsync(settings, "health");
            await entered.Task.WaitAsync(LifetimeBound);
            Field<SetupUriField>(settings, "endpoint").SetValue(new("http://changed.invalid"));
            release.SetResult(new(true, "old endpoint healthy"));
            Assert.Equal(SetupActionState.Cancelled, (await pending.WaitAsync(LifetimeBound)).State);
            Assert.Null(settings.Status.Verified.Value);
        }
    }

    [Fact]
    public async Task RetirementDrainsIgnoredHealthAndFencesBaseAndInterfaceAdmission()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource<ImageProviderHealth>();
        var transport = new Transport { OnHealth = connection => { entered.SetResult(); return release.Task; } };
        var (manager, settings) = await NewAsync(transport, new Target());
        InferenceSettingsManager<ImageSettings, ImageSetupChanges> baseReference = manager;
#pragma warning disable CA1859 // This test specifically exercises virtual dispatch through the public interface.
        IInferenceSettingsManager interfaceReference = manager;
#pragma warning restore CA1859
        Task<SetupActionResult> pending = baseReference.RunAsync(settings, "health");
        await entered.Task.WaitAsync(LifetimeBound);
        Task first = manager.DisposeAsync().AsTask();
        Task second = manager.DisposeAsync().AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => interfaceReference.RunAsync(settings, "missing"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => baseReference.QueryChoicesAsync(settings, "workflow-origin", new()));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => interfaceReference.SaveAsync(settings));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.OpenAsync(ImageTransport.ComfyUi, new(SetupChangeIntent.Create, "other", null)));
        release.SetResult(new(true, "late result"));
        Assert.Equal(SetupActionState.Cancelled, (await pending.WaitAsync(LifetimeBound)).State);
        await Task.WhenAll(first, second).WaitAsync(LifetimeBound);
        Assert.Null(settings.Status.Verified.Value);
    }

    [Theory]
    [InlineData(SetupCommitState.Committed)]
    [InlineData(SetupCommitState.Uncertain)]
    public async Task AdmittedSaveDrainsIgnoredCommitAndPreservesObservedOutcome(SetupCommitState state)
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource<SetupCommitResult>();
        var target = new Target { OnCommit = changes => { entered.SetResult(); return release.Task; } };
        var (manager, settings) = await NewAsync(new Transport(), target);
        Field<SetupChoiceField>(settings, "workflow").SetValue("workflow");
        await manager.RunAsync(settings, "validate-workflow");
        Task<SetupCommitResult> pending = ((IInferenceSettingsManager)manager).SaveAsync(settings);
        await entered.Task.WaitAsync(LifetimeBound);
        Task retiring = manager.DisposeAsync().AsTask();
        Assert.False(retiring.IsCompleted);
        release.SetResult(new(state, "v2"));
        Assert.Equal(state, (await pending.WaitAsync(LifetimeBound)).State);
        await retiring.WaitAsync(LifetimeBound);
        Assert.Equal(state == SetupCommitState.Uncertain, settings.SaveOutcomeUncertain);
        Assert.True(settings.IsSaved);
        Assert.NotNull(target.Changes);
    }

    [Fact]
    public async Task RetirementDrainsIgnoredChoiceAndReleasesBorrowedSourceWithoutDisposal()
    {
        var source = new HeldWorkflows();
        ImageSettingsManager manager = new ImageProviderService([new Transport()]).CreateSettingsManager(new Target(), source);
        ImageSettings settings = await manager.OpenAsync(ImageTransport.ComfyUi, new(SetupChangeIntent.Create, "id", null));
        Task<SetupChoicePage> pending = ((IInferenceSettingsManager)manager).QueryChoicesAsync(settings, "workflow", new());
        await source.Entered.Task.WaitAsync(LifetimeBound);
        Task retiring = manager.DisposeAsync().AsTask();
        Assert.False(retiring.IsCompleted);
        source.Release.SetResult(new([new("workflow", "Old choices")]));
        Assert.Equal(SetupChoiceState.Cancelled, (await pending.WaitAsync(LifetimeBound)).State);
        await retiring.WaitAsync(LifetimeBound);
        Assert.Equal(0, source.Disposals);
    }

    [Fact]
    public async Task XaiFactoryUsesFrozenDraftAndCannotCauseStaleHealthEffects()
    {
        var transport = new Transport { Kind = ImageTransport.XaiImagine };
        await using var access = new LlmConnectionAccess(new XaiSource(), []);
        ImageSettings? draft = null;
        string? selected = null;
        int calls = 0;
        ImageSettingsManager manager = new ImageProviderService([transport]).CreateSettingsManager(new Target(), new Workflows(), selection =>
        {
            calls++;
            selected = selection.ConnectionId;
            Field<SetupTextField>(draft!, "xai-connection").SetValue("new-selection");
            Field<SetupUriField>(draft!, "endpoint").SetValue(new("https://new.invalid/v1"));
            return new(selection, access);
        });
        await using (manager)
        {
            draft = await manager.OpenAsync(ImageTransport.XaiImagine, new(SetupChangeIntent.Edit, "id", "v1"),
                new() { Transport = ImageTransport.XaiImagine, Endpoint = "https://api.x.ai/v1", Xai = new("old-selection") },
                new(ImageWorkflowOrigin.Host, "workflow"));
            _ = draft.Steps;
            _ = draft.Actions;
            Assert.Equal(0, calls);
            Assert.Equal(SetupActionState.Cancelled, (await manager.RunAsync(draft, "health")).State);
            Assert.Equal("old-selection", selected);
            Assert.Equal(1, calls);
            Assert.Equal(0, transport.HealthCalls);
            Assert.Null(draft.Status.Verified.Value);
        }
    }

    private static T Field<T>(ImageSettings settings, string id) where T : SetupField
        => Assert.IsType<T>(Assert.Single(Fields(settings), field => field.Description.Id == id));

    private sealed class HeldWorkflows : IImageWorkflowChoiceSource, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new();
        public TaskCompletionSource<SetupChoicePage> Release { get; } = new();
        public int Disposals { get; private set; }
        public Task<SetupChoicePage> GetChoicesAsync(ImageTransport transport, SetupChoiceQuery query, CancellationToken cancellationToken = default)
        { Entered.SetResult(); return Release.Task; }
        public Task<ImageWorkflow?> GetWorkflowAsync(string workflowId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() => Disposals++;
    }
}
