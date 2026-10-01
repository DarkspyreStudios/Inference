using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Video.Tests;

public sealed class VideoSettingsTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static VideoSetupConfiguration Saved => new(new()
    {
        Transport = VideoTransport.XaiImagine,
        Endpoint = "https://api.x.ai/v1",
        Xai = new("connection"),
        AdditionalHeaders = new Dictionary<string, string> { ["existing"] = "protected-original" }
    }, "manual-model");

    [Fact]
    public async Task OpenRenderAndSaveNeverInvokeFactoryOrCredentialAcquisition()
    {
        var transport = new VideoOperationTests.FixtureTransport(); var target = new Target(); int factories = 0;
        await using VideoSettingsManager manager = new VideoProviderService([transport]).CreateSettingsManager(target, _ => { factories++; throw new InvalidOperationException(); });
        VideoSettings settings = await manager.OpenAsync(VideoTransport.XaiImagine, new(SetupChangeIntent.Edit, "entry", "saved-version"), Saved);
        Assert.NotEmpty(settings.Steps); Assert.NotEmpty(settings.Actions); Assert.True(settings.CanSave);
        SetupCommitResult result = await manager.SaveAsync(settings);
        Assert.Equal(SetupCommitState.Committed, result.State); Assert.Equal(0, factories);
        Assert.Empty(target.Changes!.Configuration.Connection.AdditionalHeaders); Assert.Null(target.Changes.Configuration.Connection.Authorization);
        Assert.Empty(target.Changes.Headers.Set); Assert.Empty(target.Changes.Headers.Remove);
        Assert.Equal("saved-version", target.Changes.Identity.ExpectedVersion);
        Assert.Equal(0, target.Disposes); Assert.Equal(0, transport.Disposes);
    }

    [Fact]
    public async Task CapabilityFactoryReceivesFrozenUnsavedSelectionAndStaleDraftCannotBeVerified()
    {
        var source = new VideoOperationTests.Source { Release = new() }; await using var access = new LlmConnectionAccess(source, []);
        var transport = new VideoOperationTests.FixtureTransport(); var target = new Target(); var seen = new List<string>();
        await using VideoSettingsManager manager = new VideoProviderService([transport]).CreateSettingsManager(target, selection =>
        { seen.Add(selection.ConnectionId); return new(selection, access); });
        VideoSettings settings = await manager.OpenAsync(VideoTransport.XaiImagine, new(SetupChangeIntent.Edit, "entry", "v"), Saved);
        Task<SetupActionResult> pending = manager.RunAsync(settings, "capabilities"); await source.Entered.Task.WaitAsync(Bound);
        Text(settings, "xai-connection").SetValue("edited-selection");
        source.Release.SetResult(); Assert.Equal(SetupActionState.Cancelled, (await pending.WaitAsync(Bound)).State);
        Assert.Equal(["connection"], seen); Assert.Null(settings.Status.Verified.Value); Assert.Equal(0, target.Calls);
    }

    [Fact]
    public async Task SynchronousFactoryMutationRefusesBeforeCredentialAcquisition()
    {
        var source = new VideoOperationTests.Source(); await using var access = new LlmConnectionAccess(source, []);
        VideoSettings? settings = null;
        await using VideoSettingsManager manager = new VideoProviderService([new VideoOperationTests.FixtureTransport()]).CreateSettingsManager(new Target(), selection =>
        { Text(settings!, "model").SetValue("edited-model"); return new(selection, access); });
        settings = await manager.OpenAsync(VideoTransport.XaiImagine, new(SetupChangeIntent.Edit, "entry", "v"), Saved);
        Assert.Equal(SetupActionState.Cancelled, (await manager.RunAsync(settings, "capabilities")).State);
        Assert.Equal(0, source.Reads);
    }

    [Theory]
    [InlineData(SetupCommitState.Committed)]
    [InlineData(SetupCommitState.Uncertain)]
    public async Task BaseAndInterfaceSaveRemainLeasedAndTruthfulDuringRetirement(SetupCommitState state)
    {
        var target = new Target { Release = new(), State = state };
        VideoSettingsManager manager = new VideoProviderService([new VideoOperationTests.FixtureTransport()]).CreateSettingsManager(target);
        VideoSettings settings = await manager.OpenAsync(VideoTransport.XaiImagine, new(SetupChangeIntent.Edit, "entry", "v"), Saved);
        InferenceSettingsManager<VideoSettings, VideoSetupChanges> baseOwner = manager;
        Task<SetupCommitResult> pending = baseOwner.SaveAsync(settings); await target.Entered.Task.WaitAsync(Bound);
        Task retire = manager.DisposeAsync().AsTask(); Assert.False(retire.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ((IInferenceSettingsManager)manager).QueryChoicesAsync(settings, "model", new()));
        target.Release.SetResult(); Assert.Equal(state, (await pending.WaitAsync(Bound)).State);
        await retire.WaitAsync(Bound); await manager.DisposeAsync();
        Assert.True(settings.IsSaved); Assert.Equal(state == SetupCommitState.Uncertain, settings.SaveOutcomeUncertain);
        Assert.Equal(0, target.Disposes); Assert.Equal(1, target.Calls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => baseOwner.SaveAsync(settings));
    }

    [Fact]
    public async Task RequiredSelectedTransportNeverBecomesAvailableFromDeclarations()
    {
        await using VideoSettingsManager manager = new VideoProviderService([]).CreateSettingsManager(new Target());
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => manager.OpenAsync(VideoTransport.XaiImagine, new(SetupChangeIntent.Create, "new", null)));
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, error.Code);
    }

    private static SetupTextField Text(VideoSettings settings, string name)
        => settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields).OfType<SetupTextField>().Single(field => field.Description.Id == name);
    private sealed class Target : ISetupCommitTarget<VideoSetupChanges>, IDisposable
    {
        internal TaskCompletionSource? Release { get; init; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal SetupCommitState State { get; init; } = SetupCommitState.Committed;
        internal VideoSetupChanges? Changes { get; private set; }
        internal int Calls { get; private set; }
        internal int Disposes { get; private set; }
        public async Task<SetupCommitResult> CommitAsync(VideoSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; Calls++; Entered.TrySetResult(); if (Release is not null) await Release.Task.ConfigureAwait(false); return new(State); }
        public void Dispose() => Disposes++;
    }
}
