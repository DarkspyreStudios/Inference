using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Tensor.Image;
using Darkspyre.Inference.Tensor.STT;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.LLM;
using GpuArbiter.Backends;
using GpuArbiter.Gpu;
using GpuArbiter.Tensor;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace GpuArbiter.Tensor.Tests;

internal sealed class FakeEngine : ITensorLlmInference, ITensorImageInference, ITensorSpeechToTextInference
{
    public event EventHandler<TensorEngineStatus>? EngineStatusChanged;
    public TensorEngineState State { get; private set; } = TensorEngineState.Started;
    public TensorBackendKind RequestedBackend => TensorBackendKind.Cpu;
    public InferenceImplementationDescriptor Descriptor { get; } = new("fake", "Fake", InferenceImplementationKind.Tensor, InferenceTask.TextGeneration);
    public LlmCapabilities Capabilities { get; set; } = new()
    {
        SupportsStreaming = true, SupportsTemperature = true, SupportsJsonObject = true, SupportsJsonSchema = true,
        SupportsToolUse = true, OutputLimitEnforcement = LlmOutputLimitEnforcement.EngineEnforced,
        ReasoningLevels = new HashSet<LlmReasoningLevel> { LlmReasoningLevel.None, LlmReasoningLevel.High },
        Tools = new() { StrictInputSchema = LlmStrictInputSchemaSupport.Unsupported }
    };
    public List<LlmInferenceRequest> Requests { get; } = [];
    public bool EmitTools { get; set; }
    public bool NoUsage { get; set; }
    public bool FailExecution { get; set; }
    public bool HoldExecution { get; set; }
    public bool EnumeratorDisposed { get; private set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<LlmCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default) => Task.FromResult(Capabilities);
    public Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LlmModelInfo>>([]);
    public Task<IReadOnlyList<LlmModelInfo>> RefreshModelsAsync(CancellationToken cancellationToken = default) => ListModelsAsync(cancellationToken);
    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmInferenceRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        Entered.TrySetResult();
        try
        {
            if (HoldExecution) await Released.Task.WaitAsync(cancellationToken);
            if (FailExecution) throw new InferenceException(InferenceFailureCode.ExecutionFailed, "secret-prompt must not leak");
            if (request.ReasoningLevel == LlmReasoningLevel.High) yield return new LlmReasoningDeltaEvent("Reasoning", false);
            if (EmitTools)
            {
                yield return new LlmToolCallStartedEvent("call-1", "lookup", 0);
                yield return new LlmToolCallArgumentsDeltaEvent("call-1", "{\"id\":1}");
                yield return new LlmToolCallCompletedEvent(new("call-1", "lookup", "{\"id\":1}"));
            }
            else { yield return new LlmTextDeltaEvent("Hello"); yield return new LlmTextDeltaEvent(" world"); }
            yield return new LlmCompletionEvent(new()
            {
                Text = EmitTools ? "" : "Hello world", Status = EmitTools ? LlmCompletionStatus.RequiresAction : LlmCompletionStatus.Completed,
                ToolCalls = EmitTools ? [new("call-1", "lookup", "{\"id\":1}")] : [],
                Usage = NoUsage ? null : new(10, 2, 12) { CachedInputTokens = 3 },
                Provenance = new() { ImplementationId = "fake", ModelId = "actual-model" }
            });
        }
        finally { EnumeratorDisposed = true; }
    }
    public ImageGenerationRequest? ImageRequest { get; private set; }
    public SpeechTranscriptionRequest? SpeechRequest { get; private set; }
    Task<ImageCapabilities> IImageInference.GetCapabilitiesAsync(string? modelId, CancellationToken token) => Task.FromResult(new ImageCapabilities { SupportedSizes = [(512, 512)], SupportsSeed = true });
    public Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, IProgress<ImageGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ImageRequest = request;
        return Task.FromResult(new ImageGenerationResult { Images = [new() { ContentType = "image/png", Data = new byte[] { 1, 2, 3 } }], Provenance = new() { ImplementationId = "fake", ModelId = request.ModelId } });
    }
    Task<SpeechToTextCapabilities> ITensorSpeechToTextInference.GetCapabilitiesAsync(string? modelId, CancellationToken token) => Task.FromResult(new SpeechToTextCapabilities(false, true, true, ["en"], 16000, 16000, 1));
    Task<IReadOnlyList<SpeechModelInfo>> ITensorSpeechToTextInference.ListModelsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<SpeechModelInfo>>([]);
    public Task<SpeechTranscriptionResult> TranscribeAsync(SpeechTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        SpeechRequest = request;
        return Task.FromResult(new SpeechTranscriptionResult("Hello", [new("Hello", TimeSpan.Zero, TimeSpan.FromSeconds(1))], "en", new() { ImplementationId = "fake", ModelId = "actual-model" }));
    }
    public async IAsyncEnumerable<SpeechWindowTranscription> TranscribeStreamAsync(IAsyncEnumerable<SpeechAudioWindow> windows, SpeechToTextOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var window in windows.WithCancellation(cancellationToken)) yield return new(window.Id, await TranscribeAsync(new(window.Audio, options), cancellationToken));
    }
    public void Transition(TensorEngineState state, Exception? error = null)
    {
        State = state;
        EngineStatusChanged?.Invoke(this, new(null, RequestedBackend, state, DateTimeOffset.UtcNow, error));
    }
    public Task<TensorModelObservation> GetObservationAsync(CancellationToken cancellationToken = default) => Task.FromResult(new TensorModelObservation(null, RequestedBackend, State, DateTimeOffset.UtcNow));
    public Task StartAsync(CancellationToken cancellationToken = default) { Transition(TensorEngineState.Started); return Task.CompletedTask; }
    public Task LoadAsync(TensorModelKey model, CancellationToken cancellationToken = default) { Transition(TensorEngineState.Loaded); return Task.CompletedTask; }
    public Task UnloadAsync(CancellationToken cancellationToken = default) { Transition(TensorEngineState.Unloaded); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken = default) { Transition(TensorEngineState.Stopped); return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeModels : ITensorModelHost
{
    public FakeEngine Engine { get; } = new();
    public IReadOnlyList<HostedModel> Models => [new("public-model", new() { Key = new("actual-model", "revision"), Capability = TensorCapability.Llm, RuntimeArchitecture = "fake", License = "test" }, Engine)];
    public HostedModel Get(string alias) => alias == "public-model" ? Models[0] : throw new ApiException(404, "Unknown model", "model");
    public Task<IReadOnlyList<ModelState>> ModelsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ModelState>>([new("public-model", Engine.State == TensorEngineState.Loaded ? "loaded" : "unloaded", false, null)]);
    public Task LoadAsync(string model, CancellationToken token) => Engine.LoadAsync(Get(model).Pack.Key, token);
    public Task UnloadAsync(string model, CancellationToken token) => Get(model).Instance.UnloadAsync(token);
}

internal sealed class FakeComfy : IComfyClient
{
    public Task<ComfyState> QueueAsync(CancellationToken token) => Task.FromResult(new ComfyState(false, 0, 0));
    public Task FreeAsync(CancellationToken token) => Task.CompletedTask;
}

internal sealed class FakeVram : IVramProbe
{
    public Task<int> ReadAsync(CancellationToken token) => Task.FromResult(0);
}

internal sealed class ApiRig : IAsyncDisposable
{
    public FakeModels Models { get; } = new();
    public GpuCoordinator Coordinator { get; }
    public TextApi Api { get; }
    public ApiRig()
    {
        var options = new TensorOptions();
        Coordinator = new(Models, new FakeComfy(), new FakeVram(), options, TimeProvider.System, NullLogger<GpuCoordinator>.Instance);
        Coordinator.ModelHostReady();
        Api = new(Models, new(Coordinator, options, TimeProvider.System), options, TimeProvider.System);
    }
    public async Task StartAsync() => await Coordinator.StartAsync(CancellationToken.None);
    public static DefaultHttpContext Context(string route = "/v1/chat/completions", CancellationToken token = default)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = route;
        context.RequestAborted = token;
        context.Response.Body = new MemoryStream();
        return context;
    }
    public async Task<string> SendAsync(string json, string route = "/v1/chat/completions")
    {
        var context = Context(route);
        using var input = System.Text.Json.JsonDocument.Parse(json);
        try { await Api.HandleAsync(context, input.RootElement); }
        catch (Exception error) { await ApiException.WriteAsync(context, error); }
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }
    public async ValueTask DisposeAsync() { await Coordinator.StopAsync(CancellationToken.None); Coordinator.Dispose(); }
}
