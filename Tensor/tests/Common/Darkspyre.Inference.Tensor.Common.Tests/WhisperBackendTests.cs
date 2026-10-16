using System.Runtime.InteropServices;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Tensor.STT;
using Darkspyre.Inference.Tensor.STT.Whisper;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class WhisperBackendTests
{
    [Theory]
    [InlineData(TensorBackendKind.Auto, TensorBackendKind.GgmlCuda)]
    [InlineData(TensorBackendKind.Auto, TensorBackendKind.Cpu)]
    [InlineData(TensorBackendKind.Cpu, TensorBackendKind.Cpu)]
    public async Task WarmModelReportsObservedBackendAndRetainsGpuDeviceSelection(TensorBackendKind requested, TensorBackendKind observed)
    {
        var packs = new InstalledPack();
        var loader = new Loader(observed);
        await using var engine = new WhisperSpeechToTextInference(packs, loader, requested, gpuDevice: 1);
        var loaded = new TaskCompletionSource<TensorEngineStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.EngineStatusChanged += (_, status) => { if (status.State == TensorEngineState.Loaded) loaded.TrySetResult(status); else if (status.State == TensorEngineState.Failed) loaded.TrySetException(status.Error!); };
        await engine.StartAsync();
        await engine.LoadAsync(packs.Definition.Key);
        Assert.Equal(observed, (await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5))).Backend);
        Assert.Equal(observed, (await engine.GetObservationAsync()).Backend);
        var request = new SpeechTranscriptionRequest(new SpeechAudio(new float[160], 16000, 1), ModelId: packs.Definition.Key.ModelId);
        var first = await engine.TranscribeAsync(request);
        var second = await engine.TranscribeAsync(request);
        Assert.Equal("transcript", first.Text); Assert.Equal("transcript", second.Text);
        Assert.Equal(observed.ToString(), first.Provenance.Backend);
        Assert.Equal(observed.ToString(), second.Provenance.Backend);
        Assert.Equal((requested, 1), loader.Selection);
        Assert.Equal(1, loader.Loads); Assert.Equal(2, loader.Requests);
    }

    [Fact]
    public void FailedAutomaticGpuAllocationGetsOneCpuAttempt()
    {
        var attempts = new List<bool>();
        object resident = new();
        object result = WhisperRuntime.Allocate(allowCpuFallback: true, useGpu: true, useGpu =>
        {
            attempts.Add(useGpu);
            if (useGpu) throw new WhisperModelLoadException("GPU allocation failed.");
            return resident;
        });
        Assert.Same(resident, result); Assert.Equal([true, false], attempts);
        attempts.Clear();
        Assert.Throws<WhisperModelLoadException>(() => WhisperRuntime.Allocate<object>(true, true, useGpu =>
        { attempts.Add(useGpu); throw new WhisperModelLoadException("Allocation failed."); }));
        Assert.Equal([true, false], attempts);
    }

    [Fact]
    public void NativeGpuInitializationFailureReportsCpuFallback()
    {
        var observed = new WhisperBackendObservation(1);
        observed.Observe("whisper_backend_init_gpu: using CUDA1 backend\n");
        Assert.Equal(TensorBackendKind.GgmlCuda, observed.Backend);
        observed.Observe("whisper_backend_init_gpu: failed to initialize CUDA1 backend\n");
        Assert.Equal(TensorBackendKind.Cpu, observed.Backend);
        var absent = new WhisperBackendObservation(1);
        absent.Observe("whisper_backend_init_gpu: no GPU found\n");
        Assert.Equal(TensorBackendKind.Cpu, absent.Backend);
    }

    [Theory]
    [InlineData(TensorBackendKind.GgmlCuda, RuntimeLibrary.Cuda12, TensorBackendAvailabilityState.Available)]
    [InlineData(TensorBackendKind.GgmlCuda, RuntimeLibrary.Cpu, TensorBackendAvailabilityState.Unavailable)]
    [InlineData(TensorBackendKind.Auto, RuntimeLibrary.Cpu, TensorBackendAvailabilityState.Available)]
    [InlineData(TensorBackendKind.Cpu, RuntimeLibrary.Cuda12, TensorBackendAvailabilityState.Available)]
    public async Task ProbeSeparatesAvailableRuntimeFromModelAllocation(TensorBackendKind backend, RuntimeLibrary library, TensorBackendAvailabilityState expected)
    {
        var probe = new WhisperBackendProbe(() => new("native features", library));
        var result = await probe.CheckAsync(new(Guid.NewGuid(), WhisperSetup.Definition.Implementation.Id, backend, RuntimeInformation.RuntimeIdentifier));
        Assert.Equal(expected, result.State); Assert.Null(result.ActualBackend);
    }

    private sealed class Loader(TensorBackendKind backend) : IWhisperLoader
    {
        internal int Requests;
        internal int Loads;
        internal (TensorBackendKind Backend, int GpuDevice) Selection;
        public Task<IWhisperModel> LoadAsync(TensorModelPackDefinition definition, TensorModelPackLease lease, TensorBackendKind selected, int gpuDevice, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Loads++; Selection = (selected, gpuDevice); return Task.FromResult<IWhisperModel>(new Model(backend, () => Requests++)); }
    }
    private sealed class Model(TensorBackendKind backend, Action onRequest) : IWhisperModel
    {
        public TensorBackendKind Backend => backend;
        public WhisperModelMetadata Metadata => new(false, InferenceSupport.Supported, 99);
        public Task<WhisperTranscript> TranscribeAsync(float[] samples, string language, bool translate, CancellationToken token)
        { token.ThrowIfCancellationRequested(); onRequest(); return Task.FromResult(new WhisperTranscript([new("transcript", TimeSpan.Zero, TimeSpan.FromMilliseconds(10))], "en")); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    // The native loader is the expensive seam. This installed-pack stand-in supplies no filesystem or native work.
    private sealed class InstalledPack : ITensorModelPackManager
    {
        internal readonly TensorModelPackDefinition Definition = TensorSpeechModels.Whisper.Single(pack => pack.Key.ModelId == "whisper/tiny");
        public Task<IReadOnlyList<TensorModelPackDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TensorModelPackDefinition>>([Definition]);
        public Task<TensorModelPackDefinition> GetRequiredDefinitionAsync(TensorModelKey key, CancellationToken cancellationToken = default) => Task.FromResult(Definition);
        public Task<TensorModelPackStatus> GetStatusAsync(TensorModelKey key, CancellationToken cancellationToken = default) => Task.FromResult(new TensorModelPackStatus(key, TensorModelPackState.Installed));
        public Task<TensorModelPackLease> AcquireAsync(TensorModelKey key, CancellationToken cancellationToken = default) => Task.FromResult(new TensorModelPackLease(key, new Dictionary<string, string>(), () => ValueTask.CompletedTask));
        public Task<TensorModelPackDefinition> RegisterAsync(TensorModelPackDefinition definition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task InstallAsync(TensorModelKey key, IProgress<TensorModelInstallProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TensorModelImportResult> ImportAsync(TensorModelImportRequest request, IProgress<TensorModelInstallProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TensorModelPackStatus> VerifyAsync(TensorModelKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(TensorModelKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
