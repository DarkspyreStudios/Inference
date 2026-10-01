using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Registry.Tests;

public sealed class TensorSettingsProbeFencingTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task LatestCheckOwnsPublication(bool olderAvailable, bool newerAvailable)
    {
        var fixture = new Fixture();
        TensorSettings settings = await fixture.OpenAsync();
        Task<SetupActionResult> older = fixture.Manager.RunAsync(settings, "backend-check");
        Task<SetupActionResult> newer = fixture.Manager.RunAsync(settings, "backend-check");
        fixture.Probe.Complete(1, newerAvailable, "newer evidence");
        Assert.Equal(newerAvailable ? SetupActionState.Completed : SetupActionState.Failed, (await newer).State);
        fixture.Probe.Complete(0, olderAvailable, "older evidence");
        Assert.Equal(SetupActionState.Unavailable, (await older).State);
        Assert.Equal("newer evidence", Observation(settings));
        fixture.AssertNoEffects();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedEditInvalidatesAnOutstandingCheck(bool changeBack)
    {
        var fixture = new Fixture();
        TensorSettings settings = await fixture.OpenAsync();
        Task<SetupActionResult> pending = fixture.Manager.RunAsync(settings, "backend-check");
        SelectBackend(settings, TensorBackendKind.Cpu);
        if (changeBack) SelectBackend(settings, TensorBackendKind.Auto);
        fixture.Probe.Complete(0, true, "old draft evidence");
        Assert.Equal(SetupActionState.Unavailable, (await pending).State);
        Assert.Contains("Unknown", Observation(settings), StringComparison.Ordinal);
        fixture.AssertNoEffects();
    }

    [Fact]
    public async Task SameValueEditAlsoInvalidatesAnOutstandingCheck()
    {
        var fixture = new Fixture();
        TensorSettings settings = await fixture.OpenAsync();
        Task<SetupActionResult> pending = fixture.Manager.RunAsync(settings, "backend-check");
        SelectBackend(settings, TensorBackendKind.Auto);
        fixture.Probe.Complete(0, false, "old unavailable evidence");
        Assert.Equal(SetupActionState.Unavailable, (await pending).State);
        Assert.Contains("Unknown", Observation(settings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndependentDraftsDoNotFenceEachOther()
    {
        var fixture = new Fixture();
        TensorSettings first = await fixture.OpenAsync("first");
        TensorSettings second = await fixture.OpenAsync("second");
        Task<SetupActionResult> firstCheck = fixture.Manager.RunAsync(first, "backend-check");
        Task<SetupActionResult> secondCheck = fixture.Manager.RunAsync(second, "backend-check");
        fixture.Probe.Complete(1, true, "second evidence");
        fixture.Probe.Complete(0, true, "first evidence");
        Assert.Equal(SetupActionState.Completed, (await firstCheck).State);
        Assert.Equal(SetupActionState.Completed, (await secondCheck).State);
        Assert.Equal("first evidence", Observation(first));
        Assert.Equal("second evidence", Observation(second));
        fixture.AssertNoEffects();
    }

    [Fact]
    public async Task CancellingLatestCheckDoesNotAllowOlderPublication()
    {
        var fixture = new Fixture();
        TensorSettings settings = await fixture.OpenAsync();
        Task<SetupActionResult> older = fixture.Manager.RunAsync(settings, "backend-check");
        using var cancellation = new CancellationTokenSource();
        Task<SetupActionResult> latest = fixture.Manager.RunAsync(settings, "backend-check", cancellationToken: cancellation.Token);
        cancellation.Cancel();
        fixture.Probe.Complete(1, true, "cancelled evidence");
        Assert.Equal(SetupActionState.Cancelled, (await latest).State);
        fixture.Probe.Complete(0, true, "older evidence");
        Assert.Equal(SetupActionState.Unavailable, (await older).State);
        Assert.Contains("Unknown", Observation(settings), StringComparison.Ordinal);
        fixture.AssertNoEffects();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceFailureKeepsSafeExistingResultAndNewerEvidence(bool latestFails)
    {
        var fixture = new Fixture();
        TensorSettings settings = await fixture.OpenAsync();
        Task<SetupActionResult> older = fixture.Manager.RunAsync(settings, "backend-check");
        Task<SetupActionResult> latest = fixture.Manager.RunAsync(settings, "backend-check");
        if (latestFails)
        {
            fixture.Probe.Fail(1);
            AssertSafeFailure(await latest);
            fixture.Probe.Complete(0, true, "older evidence");
            Assert.Equal(SetupActionState.Unavailable, (await older).State);
            Assert.Contains("Unknown", Observation(settings), StringComparison.Ordinal);
        }
        else
        {
            fixture.Probe.Complete(1, true, "newer evidence");
            Assert.Equal(SetupActionState.Completed, (await latest).State);
            fixture.Probe.Fail(0);
            AssertSafeFailure(await older);
            Assert.Equal("newer evidence", Observation(settings));
        }
        fixture.AssertNoEffects();
    }

    [Theory]
    [InlineData("")]
    [InlineData("another-rid")]
    public async Task WrongTargetRidNeverPublishes(string rid)
    {
        var fixture = new Fixture();
        TensorSettings settings = await fixture.OpenAsync();
        Task<SetupActionResult> check = fixture.Manager.RunAsync(settings, "backend-check");
        fixture.Probe.Complete(0, true, "wrong RID", rid);
        SetupActionResult result = await check;
        Assert.Equal(SetupActionState.Failed, result.State);
        Assert.Equal("backend-probe-mismatch", Assert.Single(result.Issues!).Code);
        Assert.False(result.IsRetryable);
        Assert.Contains("Unknown", Observation(settings), StringComparison.Ordinal);
        fixture.AssertNoEffects();
    }

    private static void AssertSafeFailure(SetupActionResult result)
    {
        Assert.Equal(SetupActionState.Failed, result.State);
        SetupIssue issue = Assert.Single(result.Issues!);
        Assert.Equal("operation-failed", issue.Code);
        Assert.DoesNotContain("secret", issue.Message, StringComparison.Ordinal);
    }

    private static void SelectBackend(TensorSettings settings, TensorBackendKind backend) => Assert.Equal(SetupEditState.Applied,
        settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields).OfType<SetupChoiceField>()
            .Single(field => field.Description.Id == "backend").SetValue(backend.ToString()).State);

    private static string Observation(TensorSettings settings) => settings.Steps.SelectMany(step => step.Sections)
        .SelectMany(section => section.Fields).OfType<SetupTextField>().Single(field => field.Description.Id == "backend-availability").Value;

    private sealed class Fixture
    {
        private readonly RecordingPacks _packs = new();
        private readonly RecordingTarget _target = new();
        public ControlledProbe Probe { get; } = new();
        public TensorSettingsManager Manager { get; }

        public Fixture() => Manager = new SyntheticLlmProvider().CreateSettingsManager(_target, _packs, [SyntheticLlmProvider.Pack], backendProbe: Probe);

        public Task<TensorSettings> OpenAsync(string id = "entry") => Manager.OpenAsync(new(SetupChangeIntent.Create, id, null));

        public void AssertNoEffects()
        {
            Assert.Empty(_packs.Calls);
            Assert.Null(_target.Changes);
        }
    }

    private sealed class ControlledProbe : ITensorBackendProbe
    {
        private readonly List<(TensorBackendProbeRequest Request, TaskCompletionSource<TensorBackendAvailability> Completion)> _calls = [];

        public Task<TensorBackendAvailability> CheckAsync(TensorBackendProbeRequest request, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<TensorBackendAvailability>(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls.Add((request, completion));
            return completion.Task;
        }

        public void Complete(int index, bool available, string reason, string? rid = null)
        {
            var call = _calls[index];
            call.Completion.SetResult(TensorBackendAvailability.Observe(new(call.Request.OperationId, call.Request.ImplementationId,
                call.Request.RequestedBackend, rid ?? call.Request.TargetRid),
                available ? TensorBackendAvailabilityState.Available : TensorBackendAvailabilityState.Unavailable, reason));
        }

        public void Fail(int index) => _calls[index].Completion.SetException(new IOException("secret endpoint"));
    }
}
