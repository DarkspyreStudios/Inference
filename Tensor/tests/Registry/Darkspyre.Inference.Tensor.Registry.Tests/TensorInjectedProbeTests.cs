using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Decision.Laya;
using Darkspyre.Inference.Tensor.Image.Supra;
using Darkspyre.Inference.Tensor.LLM.Qwen;
using Darkspyre.Inference.Tensor.STT.Whisper;

namespace Darkspyre.Inference.Tensor.Registry.Tests;

public sealed class TensorInjectedProbeTests
{
    public static TheoryData<string, bool, bool?> Observations => new()
    {
        { "qwen", false, true }, { "qwen", false, false }, { "qwen", false, null },
        { "laya", false, true }, { "laya", false, false }, { "laya", false, null },
        { "supra", false, true }, { "supra", false, false }, { "supra", false, null },
        { "whisper", false, true }, { "whisper", false, false }, { "whisper", false, null },
        { "qwen", true, true }, { "qwen", true, false }, { "qwen", true, null },
        { "laya", true, true }, { "laya", true, false }, { "laya", true, null },
        { "supra", true, true }, { "supra", true, false }, { "supra", true, null },
        { "whisper", true, true }, { "whisper", true, false }, { "whisper", true, null }
    };

    public static TheoryData<string, bool> Routes => new()
    {
        { "qwen", false }, { "laya", false }, { "supra", false }, { "whisper", false },
        { "qwen", true }, { "laya", true }, { "supra", true }, { "whisper", true }
    };

    [Theory]
    [MemberData(nameof(Observations))]
    public async Task InjectedProbeIsExclusiveLazyAndReadOnly(string engine, bool useProvider, bool? available)
    {
        var probe = new RecordingProbe((backend, _) => Task.FromResult(new TensorBackendAvailability(
            backend, available, "remote-observation", DateTimeOffset.UnixEpoch)));
        var packs = new RecordingPacks();
        var target = new RecordingTarget();
        TensorSettingsManager manager = Create(engine, useProvider, target, packs, probe);
        TensorSettings settings = await manager.OpenAsync(new(SetupChangeIntent.Create, "entry", null));
        await manager.QueryChoicesAsync(settings, "backend", new());
        SelectBackend(settings, TensorBackendKind.Auto);
        await manager.RefreshStatusAsync(settings);
        Assert.Empty(probe.Backends);
        Assert.Empty(packs.Calls);
        Assert.Null(target.Changes);

        SetupActionResult result = await manager.RunAsync(settings, "backend-check");
        Assert.Equal(available == true ? SetupActionState.Completed : SetupActionState.Failed, result.State);
        Assert.Equal(TensorBackendKind.Auto, Assert.Single(probe.Backends));
        Assert.Equal("remote-observation", Observation(settings));
        Assert.Empty(packs.Calls);
        Assert.Null(target.Changes);
        Assert.Null(settings.Status.Ready.Value);

        SelectBackend(settings, TensorBackendKind.Auto);
        Assert.Contains("Unknown", Observation(settings), StringComparison.Ordinal);
        await manager.RunAsync(settings, "backend-check");
        Assert.Equal([TensorBackendKind.Auto, TensorBackendKind.Auto], probe.Backends);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task InjectedProbeFailureDoesNotRunLocalProbeOrExposeException(string engine, bool useProvider)
    {
        var probe = new RecordingProbe((_, _) => throw new IOException("private remote endpoint"));
        TensorSettingsManager manager = Create(engine, useProvider, new RecordingTarget(), new RecordingPacks(), probe);
        TensorSettings settings = await manager.OpenAsync(new(SetupChangeIntent.Create, "entry", null));
        SetupActionResult result = await manager.RunAsync(settings, "backend-check");
        Assert.Equal(SetupActionState.Failed, result.State);
        Assert.Equal("operation-failed", Assert.Single(result.Issues!).Code);
        Assert.DoesNotContain("private", Assert.Single(result.Issues!).Message, StringComparison.Ordinal);
        Assert.Single(probe.Backends);
        Assert.Contains("Unknown", Observation(settings), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task CancelledInjectedProbeDoesNotPublishLateAvailableObservation(string engine, bool useProvider)
    {
        using var cancellation = new CancellationTokenSource();
        var probe = new RecordingProbe((backend, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
            return Task.FromResult(new TensorBackendAvailability(backend, true, "late success", DateTimeOffset.UnixEpoch));
        });
        TensorSettingsManager manager = Create(engine, useProvider, new RecordingTarget(), new RecordingPacks(), probe);
        TensorSettings settings = await manager.OpenAsync(new(SetupChangeIntent.Create, "entry", null));
        SetupActionResult result = await manager.RunAsync(settings, "backend-check", cancellationToken: cancellation.Token);
        Assert.Equal(SetupActionState.Cancelled, result.State);
        Assert.Single(probe.Backends);
        Assert.Contains("Unknown", Observation(settings), StringComparison.Ordinal);
    }

    private static TensorSettingsManager Create(string engine, bool useProvider, RecordingTarget target,
        RecordingPacks packs, ITensorBackendProbe probe)
    {
        if (!useProvider)
        {
            return engine switch
            {
                "qwen" => QwenSetup.CreateSettingsManager(target, packs, offeredModels: [], backendProbe: probe),
                "laya" => LayaSetup.CreateSettingsManager(target, packs, [], backendProbe: probe),
                "supra" => SupraSetup.CreateSettingsManager(target, packs, offeredModels: [], backendProbe: probe),
                "whisper" => WhisperSetup.CreateSettingsManager(target, packs, offeredModels: [], backendProbe: probe),
                _ => throw new ArgumentException("Unknown fixture engine.", nameof(engine))
            };
        }
        ITensorEngineProvider provider = engine switch
        {
            "qwen" => new QwenEngineProvider(),
            "laya" => new LayaEngineProvider(),
            "supra" => new SupraEngineProvider(),
            "whisper" => new WhisperEngineProvider(),
            _ => throw new ArgumentException("Unknown fixture engine.", nameof(engine))
        };
        return provider.CreateSettingsManager(target, packs, [], backendProbe: probe);
    }

    private static void SelectBackend(TensorSettings settings, TensorBackendKind backend) => Assert.Equal(SetupEditState.Applied,
        settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields).OfType<SetupChoiceField>()
            .Single(field => field.Description.Id == "backend").SetValue(backend.ToString()).State);

    private static string Observation(TensorSettings settings) => settings.Steps.SelectMany(step => step.Sections)
        .SelectMany(section => section.Fields).OfType<SetupTextField>().Single(field => field.Description.Id == "backend-availability").Value;

    private sealed class RecordingProbe(Func<TensorBackendKind, CancellationToken, Task<TensorBackendAvailability>> check) : ITensorBackendProbe
    {
        public List<TensorBackendKind> Backends { get; } = [];
        public Task<TensorBackendAvailability> CheckAsync(TensorBackendKind backend, CancellationToken cancellationToken = default)
        {
            Backends.Add(backend);
            return check(backend, cancellationToken);
        }
    }
}
