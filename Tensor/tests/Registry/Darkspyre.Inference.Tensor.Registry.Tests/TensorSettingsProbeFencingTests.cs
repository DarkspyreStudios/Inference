using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.LLM.Qwen;

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
    public async Task LatestCancellationOrFailurePreservesEstablishedEvidence(bool cancel)
    {
        var fixture = new Fixture();
        TensorSettings settings = await fixture.OpenAsync();
        Task<SetupActionResult> initial = fixture.Manager.RunAsync(settings, "backend-check");
        fixture.Probe.Complete(0, true, "established evidence");
        Assert.Equal(SetupActionState.Completed, (await initial).State);
        Task<SetupActionResult> older = fixture.Manager.RunAsync(settings, "backend-check");
        using var cancellation = new CancellationTokenSource();
        Task<SetupActionResult> latest = fixture.Manager.RunAsync(settings, "backend-check", cancellationToken: cancellation.Token);
        if (cancel)
        {
            cancellation.Cancel();
            fixture.Probe.Complete(2, true, "cancelled evidence");
            Assert.Equal(SetupActionState.Cancelled, (await latest).State);
        }
        else
        {
            fixture.Probe.Fail(2);
            AssertSafeFailure(await latest);
        }
        fixture.Probe.Complete(1, false, "older evidence");
        Assert.Equal(SetupActionState.Unavailable, (await older).State);
        Assert.Equal("established evidence", Observation(settings));
        fixture.AssertNoEffects();
    }

    [Fact]
    public async Task RejectedEditDoesNotInvalidateAnOutstandingCheck()
    {
        var fixture = new Fixture();
        TensorSettings settings = await fixture.OpenAsync();
        Task<SetupActionResult> pending = fixture.Manager.RunAsync(settings, "backend-check");
        SetupChoiceField field = settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields)
            .OfType<SetupChoiceField>().Single(field => field.Description.Id == "backend");
        Assert.Equal(SetupEditState.Invalid, field.SetValue(TensorBackendKind.Cuda.ToString()).State);
        fixture.Probe.Complete(0, true, "valid evidence");
        Assert.Equal(SetupActionState.Completed, (await pending).State);
        Assert.Equal("valid evidence", Observation(settings));
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

    [Fact]
    public void ActualForeignRegistrySettingsProbeSaveAndMetadataGraphRetires()
    {
        WeakReference weak = ExerciseForeignGeneration();
        for (int attempt = 0; weak.IsAlive && attempt < 20; ++attempt)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(weak.IsAlive, "The settled foreign registry/provider/settings/owned metadata graph remains rooted.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeignGeneration()
    {
        string path = typeof(TensorSettingsProbeFencingTests).Assembly.Location;
        var generation = new SettingsGeneration(Path.GetDirectoryName(path)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(path);
            Type caller = fixture.GetType(typeof(TensorSettingsProbeFencingTests).FullName!, throwOnError: true)!;
            caller.GetMethod(nameof(RunForeignSettingsPath), BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
            Assert.Equal(0, generation.NativeRequests);
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static void RunForeignSettingsPath()
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(TensorSettingsProbeFencingTests).Assembly)!;
        Assert.True(generation.IsCollectible);
        Assert.NotSame(AssemblyLoadContext.Default, generation);
        var registry = new TensorEngineRegistry([new QwenEngineProvider()]);
        Assert.True(registry.TryGetProvider("darkspyre.tensor.qwen", out ITensorEngineProvider? provider));
        var probe = new ControlledProbe();
        var packs = new RecordingPacks();
        var target = new RecordingTarget();
        TensorModelPackDefinition pack = TensorLlmModels.Qwen3FourBillionQ4Km;
        TensorSettingsManager manager = provider.CreateSettingsManager(target, packs, [pack], backendProbe: probe);
        TensorSettings settings = manager.OpenAsync(new(SetupChangeIntent.Create, "foreign-entry", null)).GetAwaiter().GetResult();
        foreach (Assembly owned in new[] { registry.GetType().Assembly, provider.GetType().Assembly, manager.GetType().Assembly,
            settings.GetType().Assembly, settings.Identity.GetType().Assembly, packs.GetType().Assembly,
            TensorJson.SetupChanges.Type.Assembly, TensorJson.SetupChanges.Options.TypeInfoResolver!.GetType().Assembly })
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(owned));

        Assert.Empty(packs.Calls);
        Task<SetupActionResult> oldCheck = manager.RunAsync(settings, "backend-check");
        Task<SetupActionResult> latestCheck = manager.RunAsync(settings, "backend-check");
        probe.Complete(1, true, "foreign latest evidence");
        Assert.Equal(SetupActionState.Completed, latestCheck.GetAwaiter().GetResult().State);
        probe.Complete(0, false, "foreign stale evidence");
        Assert.Equal(SetupActionState.Unavailable, oldCheck.GetAwaiter().GetResult().State);
        Assert.Equal("foreign latest evidence", Observation(settings));

        SetupChoiceField model = settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields)
            .OfType<SetupChoiceField>().Single(field => field.Description.Id == "model");
        Assert.Equal(SetupEditState.Applied, model.SetValue(pack.Key.ToString()).State);
        Assert.Contains("Unknown", Observation(settings), StringComparison.Ordinal);
        SelectBackend(settings, TensorBackendKind.Cpu);
        Assert.Equal(SetupCommitState.Committed, manager.SaveAsync(settings).GetAwaiter().GetResult().State);
        TensorSetupChanges changes = target.Changes!;
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(changes.Identity.GetType().Assembly));
        string json = JsonSerializer.Serialize(changes, TensorJson.SetupChanges);
        TensorSetupChanges restored = JsonSerializer.Deserialize(json, TensorJson.SetupChanges)!;
        Assert.Equal(json, JsonSerializer.Serialize(restored, TensorJson.SetupChanges));
        Assert.Equal("foreign-entry", restored.Identity.Id);
        Assert.Equal(pack.Key, restored.Configuration.Model);
        Assert.Equal(TensorBackendKind.Cpu, restored.Configuration.Backend);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(restored.Identity.GetType().Assembly));
        Assert.Empty(packs.Calls);
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
            call.Completion.SetResult(new(call.Request.OperationId, call.Request.ImplementationId, call.Request.RequestedBackend,
                available ? TensorBackendAvailabilityState.Available : TensorBackendAvailabilityState.Unavailable,
                reason, DateTimeOffset.UnixEpoch, rid ?? call.Request.TargetRid));
        }

        public void Fail(int index) => _calls[index].Completion.SetException(new IOException("secret endpoint"));
    }

    private sealed class SettingsGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        public int NativeRequests { get; private set; }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            ++NativeRequests;
            throw new DllNotFoundException("This controlled settings generation cannot execute native libraries.");
        }
    }
}
