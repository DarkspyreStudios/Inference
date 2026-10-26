using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Ggml;
using TensorSharp.Runtime;
using TensorSharp.Server;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class QwenPackSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "darkspyre-qwen-selection-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Weights = "controlled checkpoint bytes"u8.ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausedEventReaderDoesNotBlockModelRetirement(bool stop)
    {
        var runtime = new Runtime { Pieces = Enumerable.Repeat("hello", 128).ToArray() };
        (QwenLlmInference engine, TensorModelPackManager packs, string model) = await LoadedAsync(runtime, "host/paused-retirement");
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.EngineStatusChanged += (_, status) =>
        {
            if (status.State == (stop ? TensorEngineState.Stopping : TensorEngineState.Unloading)) stopping.TrySetResult();
        };
        using (packs)
        {
            IAsyncEnumerator<LlmStreamEvent> events = engine.StreamAsync(Request(model)).GetAsyncEnumerator();
            try
            {
                Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.IsType<LlmTextDeltaEvent>(events.Current);
                Task retirement = stop ? engine.DisposeAsync().AsTask() : engine.UnloadAndWaitAsync();
                await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));

                await retirement.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(1, runtime.StreamDisposals);
                Assert.Equal(stop ? 1 : 0, runtime.Disposals);
                Assert.Equal(128, runtime.PiecesDelivered);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => events.MoveNextAsync().AsTask());
                await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                await events.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public async Task UrgentRetirementWaitsForAnIgnoringRuntimeButNotItsPausedReader()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime
        {
            BeforeCompletionWait = release.Task,
            IgnoreCompletionCancellation = true,
            BeforeCompletionWaiting = () => entered.TrySetResult(),
            OnStreamCancelled = () => canceled.TrySetResult()
        };
        (QwenLlmInference engine, TensorModelPackManager packs, string model) = await LoadedAsync(runtime, "host/ignoring-retirement");
        using (packs)
        using (var urgency = new CancellationTokenSource())
        {
            IAsyncEnumerator<LlmStreamEvent> events = engine.StreamAsync(Request(model)).GetAsyncEnumerator();
            try
            {
                Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await engine.StopAsync(urgency.Token);
                Task retirement = engine.DisposeAsync().AsTask();
                urgency.Cancel();
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.False(retirement.IsCompleted);
                Assert.Equal(0, runtime.Disposals);
                release.SetResult();
                await retirement.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, runtime.StreamDisposals);
                Assert.Equal(1, runtime.Disposals);
            }
            finally
            {
                release.TrySetResult();
                await events.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Theory]
    [InlineData("eos", LlmCompletionStatus.Completed, null)]
    [InlineData("max_tokens", LlmCompletionStatus.Incomplete, "max_output_tokens")]
    [InlineData("repetition", LlmCompletionStatus.Incomplete, "repetition")]
    public async Task TerminalRuntimeReasonsRetainTheirActualCompletionMeaning(string reason, LlmCompletionStatus status,
        string? incompleteReason)
    {
        var runtime = new Runtime { FinishReason = reason };
        (QwenLlmInference engine, TensorModelPackManager packs, string model) = await LoadedAsync(runtime, "host/terminal-reason");
        await using (engine)
        using (packs)
        {
            List<LlmStreamEvent> events = await Collect(engine, model);
            LlmCompletionEvent completion = Assert.IsType<LlmCompletionEvent>(events[^1]);
            Assert.Equal(status, completion.Result.Status);
            Assert.Equal(reason, completion.Result.FinishReason);
            Assert.Equal(incompleteReason, completion.Result.IncompleteReason);
            Assert.Equal(new LlmUsage(1, 1, 2), completion.Result.Usage);
        }
    }

    [Fact]
    public async Task DisposingAnUnfinishedStreamAbortsItsEngineRequest()
    {
        TensorModelPackDefinition pack = Pack("host/abandon");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime { BeforeCompletionWait = completion.Task };
        await using var engine = new QwenLlmInference(packs, runtime, TensorBackendKind.Cpu, _root);
        await engine.LoadAndWaitAsync(pack.Key);

        await using (IAsyncEnumerator<LlmStreamEvent> stream = engine.StreamAsync(Request(pack.Key.ModelId)).GetAsyncEnumerator())
        {
            Assert.True(await stream.MoveNextAsync());
        }
        Assert.True(runtime.LastStreamToken.IsCancellationRequested);

        completion.SetResult();
        await Collect(engine, pack.Key.ModelId);
        Assert.False(runtime.LastStreamToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ARequestLargerThanTheFittedContextFailsBeforeTheEngineRuns()
    {
        var runtime = new Runtime { LoadedContext = 4096, PromptEstimate = 3500 };
        (QwenLlmInference engine, TensorModelPackManager packs, string model) = await LoadedAsync(runtime, "host/fit");
        await using (engine)
        using (packs)
        {
            Assert.Equal(4096, (await engine.ListModelsAsync()).Single().MaxContextTokens);
            InferenceException full = await Assert.ThrowsAsync<InferenceException>(() =>
                Collect(engine, Request(model) with { MaxOutputTokens = 1000 }));
            Assert.Equal(InferenceFailureCode.CapacityExhausted, full.Code);
            Assert.Empty(runtime.LastMessages);
            List<LlmStreamEvent> events = await Collect(engine, Request(model) with { MaxOutputTokens = 500 });
            LlmCompletionEvent completion = Assert.IsType<LlmCompletionEvent>(events[^1]);
            Assert.Equal(new LlmUsage(1, 1, 2), completion.Result.Usage);
        }
    }

    [Fact]
    public async Task LoadedVisionTowerAcceptsPngUnderTheHostScratchDirectoryUntilUnload()
    {
        var runtime = new Runtime { Architecture = "qwen4exp", VisionEncoderLoaded = true };
        (QwenLlmInference engine, TensorModelPackManager packs, string model) = await LoadedAsync(runtime, "host/vision", projector: true);
        await using (engine)
        using (packs)
        {
            Assert.Equal(InferenceSupport.Supported, (await engine.GetCapabilitiesAsync(model)).SupportsPromptImages);
            Assert.Equal(InferenceSupport.Supported, Assert.IsType<LlmCapabilities>((await engine.ListModelsAsync()).Single().Capabilities).SupportsPromptImages);
            byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aH1sAAAAASUVORK5CYII=");
            await Collect(engine, ImageRequest(model, png));
            string path = Assert.Single(Assert.Single(runtime.LastMessages).ImagePaths!);
            Assert.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
            Assert.Equal(png, await File.ReadAllBytesAsync(path));
            string folder = Path.GetDirectoryName(path)!;
            runtime.VisionEncoderLoaded = false;
            Assert.Equal(InferenceSupport.Unsupported, (await engine.GetCapabilitiesAsync(model)).SupportsPromptImages);
            await engine.UnloadAndWaitAsync();
            Assert.False(Directory.Exists(folder));
        }
    }

    [Fact]
    public async Task GifIsRefusedBeforeTheRuntimeStreams()
    {
        var runtime = new Runtime { Architecture = "qwen4exp", VisionEncoderLoaded = true };
        (QwenLlmInference engine, TensorModelPackManager packs, string model) = await LoadedAsync(runtime, "host/gif", projector: true);
        await using (engine)
        using (packs)
        {
            InferenceException refused = await Assert.ThrowsAsync<InferenceException>(() => Collect(engine, ImageRequest(model, "GIF89a"u8.ToArray())));
            Assert.Equal(InferenceFailureCode.UnsupportedFeature, refused.Code);
            Assert.Equal("Qwen reads PNG and JPEG images only.", refused.Message);
            Assert.Empty(runtime.LastMessages);
        }
    }

    [Fact]
    public async Task LoadedStatusAndObservationCarryPlacementAndUnloadClearsIt()
    {
        var placement = new TensorExpertPlacement(11, 48);
        var runtime = new Runtime { ExpertPlacement = placement };
        TensorModelPackDefinition pack = Pack("host/placement");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        await using var engine = new QwenLlmInference(packs, runtime, TensorBackendKind.Cpu, _root);
        var statuses = new List<TensorEngineStatus>();
        engine.EngineStatusChanged += (_, status) => statuses.Add(status);
        await engine.LoadAndWaitAsync(pack.Key);
        Assert.Equal(placement, Assert.Single(statuses, status => status.State == TensorEngineState.Loaded).ExpertPlacement);
        Assert.Equal(placement, (await engine.GetObservationAsync()).ExpertPlacement);
        await engine.UnloadAndWaitAsync();
        Assert.Null((await engine.GetObservationAsync()).ExpertPlacement);
        Assert.Null(Assert.Single(statuses, status => status.State == TensorEngineState.Unloaded).ExpertPlacement);
    }

    private static LlmInferenceRequest ImageRequest(string model, byte[] data) => new()
    {
        ModelId = model,
        Messages = [new("user", "Describe this.") { Attachments = [new LlmAttachment
            { Kind = LlmBinaryKind.Image, FileName = "../../untrusted.gif", MediaType = "image/gif", Data = data }] }]
    };

    private async Task<(QwenLlmInference Engine, TensorModelPackManager Packs, string Model)> LoadedAsync(Runtime runtime, string id, bool projector = false)
    {
        TensorModelPackDefinition pack = Pack(id);
        if (projector) pack = pack with
        {
            SupportedBackends = QwenModelCompatibility.BackendsFor([TensorLlmModels.VisionProjectorRole]),
            Artifacts = [.. pack.Artifacts, pack.Artifacts[0] with { Role = TensorLlmModels.VisionProjectorRole, RelativePath = "mmproj.gguf" }]
        };
        var http = new HttpClient(new Handler());
        TensorModelPackManager packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        var engine = new QwenLlmInference(packs, runtime, TensorBackendKind.Cpu, _root);
        await engine.LoadAndWaitAsync(pack.Key);
        return (engine, packs, pack.Key.ModelId);
    }

    private TensorModelPackManager Manager(HttpClient http, params TensorModelPackDefinition[] definitions) => new(new(definitions), _root, http);
    private static TensorModelPackDefinition Pack(string id) => new()
    {
        Key = new(id, "test"),
        Capability = TensorCapability.Llm,
        RuntimeArchitecture = TensorLlmModels.QwenRuntimeArchitecture,
        License = "Apache-2.0",
        SupportedBackends = TensorLlmModels.Backends,
        Artifacts = [new() { Role = "model", RelativePath = "model.gguf", Length = Weights.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(Weights)), Sources = [new("https://weights.example.test/model.gguf")] }]
    };
    private static LlmInferenceRequest Request(string id) => new() { ModelId = id, Messages = [new("user", "hello")] };
    private static Task<List<LlmStreamEvent>> Collect(QwenLlmInference engine, string id) => Collect(engine, Request(id));
    private static async Task<List<LlmStreamEvent>> Collect(QwenLlmInference engine, LlmInferenceRequest request)
    {
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in engine.StreamAsync(request)) { events.Add(item); }
        return events;
    }
    private sealed class Runtime : IQwenModelRuntime
    {
        public List<TensorModelKey> Keys { get; } = [];
        public string? Architecture { get; init; } = "qwen35";
        public bool VisionEncoderLoaded { get; set; }
        public TensorExpertPlacement? ExpertPlacement { get; init; }
        public IReadOnlyList<string> Pieces { get; init; } = ["hello"];
        public Task? BeforeCompletionWait { get; init; }
        public bool IgnoreCompletionCancellation { get; init; }
        public Action? BeforeCompletionWaiting { get; init; }
        public string FinishReason { get; init; } = "stop";
        public Action? OnStreamCancelled { get; init; }
        public int StreamDisposals { get; private set; }
        public int PiecesDelivered { get; private set; }
        private CancellationTokenRegistration _streamCancellation;
        public List<ToolFunction>? LastTools { get; private set; }
        public bool LastThinking { get; private set; }
        public SamplingConfig? LastSampling { get; private set; }
        public List<ChatMessage> LastMessages { get; private set; } = [];
        public int LoadedContext { get; init; } = 8192;
        public int? PromptEstimate { get; init; }
        public int? EstimatePromptTokens(List<ChatMessage> messages, List<ToolFunction>? tools, bool enableThinking) => PromptEstimate;
        public Action? OnLoad { get; init; }
        public Action? OnUnload { get; init; }
        public int Disposals { get; private set; }
        public int UnloadAttempts { get; private set; }
        public int? ContextLength { get; private set; }
        public string? LoadedBackend { get; private set; }
        public CancellationToken LastStreamToken { get; private set; }
        public Task LoadAsync(TensorModelPackDefinition definition, TensorModelPackLease lease, string backend, CancellationToken cancellationToken)
        {
            Assert.Equal(definition.Key, lease.Key);
            Keys.Add(lease.Key); ContextLength = LoadedContext; LoadedBackend = backend;
            OnLoad?.Invoke();
            return Task.CompletedTask;
        }
        public void Unload() { UnloadAttempts++; OnUnload?.Invoke(); ContextLength = null; LoadedBackend = null; }
        public void ApplyGrammar(SamplingConfig sampling, StructuredOutputFormat format, bool enableThinking) => throw new NotSupportedException();
        public IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatSession session, List<ChatMessage> messages, int maxTokens,
            SamplingConfig sampling, List<ToolFunction>? tools, bool enableThinking, CancellationToken cancellationToken) =>
            new RuntimeStream(StreamCoreAsync(session, messages, maxTokens, sampling, tools, enableThinking, cancellationToken), this);

        private async IAsyncEnumerable<ChatStreamUpdate> StreamCoreAsync(ChatSession session, List<ChatMessage> messages, int maxTokens,
            SamplingConfig sampling, List<ToolFunction>? tools, bool enableThinking, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastStreamToken = cancellationToken;
            _streamCancellation = cancellationToken.Register(() => OnStreamCancelled?.Invoke());
            LastTools = tools;
            LastThinking = enableThinking;
            LastSampling = sampling;
            LastMessages = messages;
            await Task.Yield();
            foreach (string piece in Pieces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PiecesDelivered++;
                yield return ChatStreamUpdate.Text(piece);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforeCompletionWait is not null)
            {
                BeforeCompletionWaiting?.Invoke();
                if (IgnoreCompletionCancellation) await BeforeCompletionWait;
                else await BeforeCompletionWait.WaitAsync(cancellationToken);
            }
            yield return new("", true, 1, 1, 0, 0, 0, 0, cancellationToken.IsCancellationRequested ? "cancelled" : FinishReason);
        }
        public void Dispose() { Unload(); Disposals++; }

        private sealed class RuntimeStream(IAsyncEnumerable<ChatStreamUpdate> source, Runtime owner) : IAsyncEnumerable<ChatStreamUpdate>
        {
            public IAsyncEnumerator<ChatStreamUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
                new Enumerator(source.GetAsyncEnumerator(cancellationToken), owner);

            private sealed class Enumerator(IAsyncEnumerator<ChatStreamUpdate> source, Runtime owner) : IAsyncEnumerator<ChatStreamUpdate>
            {
                public ChatStreamUpdate Current => source.Current;
                public ValueTask<bool> MoveNextAsync() => source.MoveNextAsync();
                public async ValueTask DisposeAsync()
                {
                    owner.StreamDisposals++;
                    try { await source.DisposeAsync(); }
                    finally { owner._streamCancellation.Dispose(); }
                }
            }
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Weights) });
    }
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
}
