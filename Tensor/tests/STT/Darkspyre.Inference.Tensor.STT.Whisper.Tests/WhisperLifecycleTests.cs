using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.STT.Whisper.Tests;

public sealed class WhisperLifecycleTests
{
    private static readonly SpeechTranscriptionRequest Request = new(new(new float[1600], 16000, 1));

    [Fact]
    public async Task ResidentModelServesRepeatedRequestsAndCanBeReplacedAfterUnload()
    {
        await using var host = new WhisperFixture.Host();
        await host.Engine.StartAsync(); await host.Engine.StartAsync();
        Assert.Equal(InferenceFailureCode.ModelUnavailable, (await Assert.ThrowsAsync<InferenceException>(() => host.Engine.TranscribeAsync(Request))).Code);
        Assert.Equal(0, host.Handler.Calls);
        await host.LoadAsync();
        for (int index = 0; index < 3; index++)
        {
            SpeechTranscriptionResult result = await host.Engine.TranscribeAsync(Request);
            Assert.Equal("hello", result.Text); Assert.Equal("en", result.DetectedLanguage);
            Assert.Equal(host.Pack.Key.ModelId, result.Provenance.ModelId);
            Assert.Equal(host.Pack.Key.Revision, result.Provenance.ModelRevision);
            Assert.Equal("Cpu", result.Provenance.Backend);
            Assert.Single(result.Segments);
        }
        Assert.Single(host.Loader.Models); Assert.Equal(1, host.Handler.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Engine.LoadAsync(host.Pack.Key));
        await Assert.ThrowsAsync<TensorModelPackException>(() => host.Packs.RemoveAsync(host.Pack.Key));
        string installed = host.Loader.Models[0].Path;
        await host.Engine.UnloadAsync();
        Assert.True(host.Loader.Models[0].Disposed); Assert.Null(host.Engine.Observation.Model);
        Assert.True(File.Exists(installed)); WhisperFixture.AssertNoOpenHandle(installed);
        TensorModelPackDefinition second = host.Pack with { Key = new("host/another-whisper", "two") };
        await host.Packs.RegisterAsync(second); await host.Packs.InstallAsync(second.Key); await host.Engine.LoadAsync(second.Key);
        Assert.Equal(second.Key.ModelId, (await host.Engine.TranscribeAsync(Request)).Provenance.ModelId);
        Assert.Equal(InferenceFailureCode.ModelUnavailable, (await Assert.ThrowsAsync<InferenceException>(() => host.Engine.TranscribeAsync(Request with { ModelId = host.Pack.Key.ModelId }))).Code);
        await host.Engine.StopAsync(); await host.Engine.StopAsync();
        Assert.True(host.Engine.Observation.IsStopped);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.Engine.LoadAsync(second.Key));
        await host.Engine.StartAsync(); // Start remains an allowed no-op, not a reset of terminal Stop.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.Engine.TranscribeAsync(Request));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TeardownProtectsNativeUseAndUrgencyCancelsOnlyTheRequest(bool urgent, bool stop)
    {
        await using var host = new WhisperFixture.Host(); await host.LoadAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Loader.Models[0].Processing = async _ => { entered.SetResult(); await finish.Task; };
        Task<SpeechTranscriptionResult> request = host.Engine.TranscribeAsync(Request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task unloading = stop ? host.Engine.StopAsync(new(urgent)) : host.Engine.UnloadAsync(new(urgent));
        Assert.False(unloading.IsCompleted); Assert.False(host.Loader.Models[0].Disposed);
        Assert.True(File.Exists(host.Loader.Models[0].Path));
        finish.SetResult();
        if (urgent) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request); else await request;
        await unloading;
        Assert.True(host.Loader.Models[0].Disposed);
        await host.Packs.RemoveAsync(host.Pack.Key);
    }

    [Fact]
    public async Task FailedLoadReleasesStorageAndNeverPublishesAResidentModel()
    {
        byte[] bytes = WhisperFixture.Header(); bytes[0] = 0;
        TensorModelPackDefinition pack = WhisperFixture.Pack(bytes);
        using var http = new HttpClient(new WhisperFixture.Handler(bytes));
        using var directory = new WhisperFixture.PackDirectory();
        using var packs = new TensorModelPackManager(new([pack]), directory.Path, http);
        var loader = new WhisperFixture.Loader();
        await using var engine = new WhisperSpeechToTextInference(packs, loader);
        await packs.InstallAsync(pack.Key);
        Assert.Equal(InferenceFailureCode.ModelCorrupt, (await Assert.ThrowsAsync<InferenceException>(() => engine.LoadAsync(pack.Key))).Code);
        Assert.Null(engine.Observation.Model); Assert.Empty(loader.Models);
        await using (TensorModelPackLease lease = await packs.AcquireAsync(pack.Key))
        {
            WhisperFixture.AssertNoOpenHandle(lease.GetRequiredArtifact("model"));
        }
        await packs.RemoveAsync(pack.Key);
    }

    [Fact]
    public async Task UnsupportedBackendAndUninstalledPackFailWithoutNativeWork()
    {
        await using var host = new WhisperFixture.Host();
        Assert.Equal(InferenceFailureCode.ModelUnavailable, (await Assert.ThrowsAsync<InferenceException>(() => host.Engine.LoadAsync(host.Pack.Key))).Code);
        await using var gpu = new WhisperSpeechToTextInference(host.Packs, host.Loader, TensorBackendKind.Cuda);
        Assert.Equal(InferenceFailureCode.BackendUnavailable, (await Assert.ThrowsAsync<InferenceException>(() => gpu.LoadAsync(host.Pack.Key))).Code);
        Assert.Equal(0, host.Handler.Calls); Assert.Empty(host.Loader.Models);
    }

    [Fact]
    public async Task EmptyAudioReturnsAnExplicitEmptyResultAndMalformedAudioDoesNotReachNativeWork()
    {
        await using var host = new WhisperFixture.Host(); await host.LoadAsync();
        SpeechTranscriptionResult empty = await host.Engine.TranscribeAsync(new(new(Array.Empty<float>(), 16000, 1)));
        Assert.Empty(empty.Text); Assert.Empty(empty.Segments); Assert.Null(empty.DetectedLanguage);
        await Assert.ThrowsAsync<InferenceException>(() => host.Engine.TranscribeAsync(new(new(new[] { float.NaN }, 16000, 1))));
        Assert.Empty(host.Loader.Models[0].Calls);
    }

    [Fact]
    public async Task RequestsSerializeAgainstOneResidentFactory()
    {
        await using var host = new WhisperFixture.Host(); await host.LoadAsync();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Loader.Models[0].Processing = _ => finish.Task;
        Task first = host.Engine.TranscribeAsync(Request), second = host.Engine.TranscribeAsync(Request);
        Assert.Single(host.Loader.Models[0].Calls);
        finish.SetResult(); await Task.WhenAll(first, second);
        Assert.Equal(2, host.Loader.Models[0].Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowStreamHoldsLifetimeUntilDisposedOrCancelled(bool urgent)
    {
        await using var host = new WhisperFixture.Host(); await host.LoadAsync();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<SpeechAudioWindow> Windows([EnumeratorCancellation] CancellationToken token = default)
        {
            yield return new("window-one", Request.Audio);
            yield return new("window-two", Request.Audio);
            waiting.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }
        await using var enumerator = host.Engine.TranscribeStreamAsync(Windows(), new()).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync()); Assert.Equal("window-one", enumerator.Current.WindowId);
        Assert.True(await enumerator.MoveNextAsync()); Assert.Equal("window-two", enumerator.Current.WindowId);
        Assert.Equal(TimeSpan.Zero, enumerator.Current.Transcription.Segments[0].Start);
        Task<bool>? next = urgent ? enumerator.MoveNextAsync().AsTask() : null;
        if (urgent) await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task unloading = host.Engine.UnloadAsync(new(urgent));
        Assert.False(unloading.IsCompleted);
        if (next is not null) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        await enumerator.DisposeAsync(); await unloading;
        Assert.True(host.Loader.Models[0].Disposed);
    }

    [Fact]
    public async Task CancellationAfterNativeLoadReleasesTheModelFileAndLeaseWithoutPublishingModel()
    {
        await using var host = new WhisperFixture.Host();
        await host.Packs.InstallAsync(host.Pack.Key);
        using var cancellation = new CancellationTokenSource();
        host.Loader.Loaded = _ => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Engine.LoadAsync(host.Pack.Key, cancellation.Token));
        Assert.Null(host.Engine.Observation.Model);
        Assert.True(host.Loader.Models.Single().Disposed); WhisperFixture.AssertNoOpenHandle(host.Loader.Models[0].Path);
        await host.Packs.RemoveAsync(host.Pack.Key);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UnsupportedTranslationAndLanguageFailBeforeProcessing(bool english, bool turbo)
    {
        await using var host = new WhisperFixture.Host(english, turbo); await host.LoadAsync();
        SpeechToTextCapabilities capabilities = await host.Engine.GetCapabilitiesAsync();
        Assert.False(capabilities.SupportsTranslationToEnglish);
        await Assert.ThrowsAsync<InferenceException>(() => host.Engine.TranscribeAsync(Request with { Options = new(TranslateToEnglish: true) }));
        await Assert.ThrowsAsync<InferenceException>(() => host.Engine.TranscribeAsync(Request with { Options = new("invalid-language") }));
        if (english) await Assert.ThrowsAsync<InferenceException>(() => host.Engine.TranscribeAsync(Request with { Options = new("fr") }));
        Assert.Empty(host.Loader.Models[0].Calls);
        await host.Engine.TranscribeAsync(Request);
        Assert.Equal(english ? "en" : "auto", host.Loader.Models[0].Calls[0].Language);
    }
}
