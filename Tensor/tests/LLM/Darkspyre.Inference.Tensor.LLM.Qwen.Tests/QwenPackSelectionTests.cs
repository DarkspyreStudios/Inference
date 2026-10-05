using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;
using TensorSharp.Runtime;
using TensorSharp.Server;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class QwenPackSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "darkspyre-qwen-selection-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Weights = "controlled checkpoint bytes"u8.ToArray();

    [Theory]
    [InlineData("eos", LlmCompletionStatus.Completed, null)]
    [InlineData("max_tokens", LlmCompletionStatus.Incomplete, "max_output_tokens")]
    [InlineData("thinking_budget", LlmCompletionStatus.Incomplete, "thinking_budget")]
    [InlineData("repetition", LlmCompletionStatus.Incomplete, "repetition")]
    [InlineData("aborted", LlmCompletionStatus.Incomplete, "aborted")]
    [InlineData("cancelled", LlmCompletionStatus.Incomplete, "cancelled")]
    [InlineData("unrecognized-stop", LlmCompletionStatus.Unknown, null)]
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
    public async Task AStreamWithoutTerminalRuntimeEvidenceCannotReportSuccessfulCompletion()
    {
        TensorModelPackDefinition pack = Pack("host/truncated");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        var runtime = new Runtime { EmitCompletion = false };
        await using var engine = new QwenLlmInference(packs, runtime, TensorBackendKind.Cpu);
        await engine.LoadAndWaitAsync(pack.Key);
        var events = new List<LlmStreamEvent>();

        InferenceException failure = await Assert.ThrowsAsync<InferenceException>(async () =>
        {
            await foreach (LlmStreamEvent item in engine.StreamAsync(Request(pack.Key.ModelId))) events.Add(item);
        });

        Assert.Equal(InferenceFailureCode.ExecutionFailed, failure.Code);
        Assert.Contains(events, item => item is LlmTextDeltaEvent { Delta: "hello" });
        Assert.DoesNotContain(events, item => item is LlmCompletionEvent);
        Assert.True(runtime.LastStreamToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ACancelledTerminalRuntimeUpdateCannotReportSuccessfulCompletion()
    {
        TensorModelPackDefinition pack = Pack("host/cancelled-terminal");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        using var cancellation = new CancellationTokenSource();
        var runtime = new Runtime { BeforeCompletion = cancellation.Cancel };
        await using var engine = new QwenLlmInference(packs, runtime, TensorBackendKind.Cpu);
        await engine.LoadAndWaitAsync(pack.Key);
        var events = new List<LlmStreamEvent>();

        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (LlmStreamEvent item in engine.StreamAsync(Request(pack.Key.ModelId), cancellation.Token)) events.Add(item);
        });

        Assert.True(failure.CancellationToken.IsCancellationRequested);
        Assert.Contains(events, item => item is LlmTextDeltaEvent { Delta: "hello" });
        Assert.DoesNotContain(events, item => item is LlmCompletionEvent);
    }

    [Fact]
    public async Task DisposingAnUnfinishedStreamAbortsItsEngineRequest()
    {
        TensorModelPackDefinition pack = Pack("host/abandon");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        var runtime = new Runtime();
        await using var engine = new QwenLlmInference(packs, runtime, TensorBackendKind.Cpu);
        await engine.LoadAndWaitAsync(pack.Key);

        await using (IAsyncEnumerator<LlmStreamEvent> stream = engine.StreamAsync(Request(pack.Key.ModelId)).GetAsyncEnumerator())
        {
            Assert.True(await stream.MoveNextAsync());
        }
        Assert.True(runtime.LastStreamToken.IsCancellationRequested);

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

    private async Task<(QwenLlmInference Engine, TensorModelPackManager Packs, string Model)> LoadedAsync(Runtime runtime, string id)
    {
        TensorModelPackDefinition pack = Pack(id);
        var http = new HttpClient(new Handler());
        TensorModelPackManager packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        var engine = new QwenLlmInference(packs, runtime, TensorBackendKind.Cpu);
        await engine.LoadAndWaitAsync(pack.Key);
        return (engine, packs, pack.Key.ModelId);
    }

    private TensorModelPackManager Manager(HttpClient http, params TensorModelPackDefinition[] definitions) => new(new(definitions), _root, http);
    private static TensorModelPackDefinition Pack(string id) => TensorLlmModels.Qwen3FourBillionQ4Km with
    {
        Key = new(id, "custom-revision"),
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
        public string? Architecture { get; init; } = "qwen3";
        public IReadOnlyList<string> Pieces { get; init; } = ["hello"];
        public bool EmitCompletion { get; init; } = true;
        public Action? BeforeCompletion { get; init; }
        public string FinishReason { get; init; } = "stop";
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
        public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatSession session, List<ChatMessage> messages, int maxTokens,
            SamplingConfig sampling, List<ToolFunction>? tools, bool enableThinking, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastStreamToken = cancellationToken;
            LastTools = tools;
            LastThinking = enableThinking;
            LastSampling = sampling;
            LastMessages = messages;
            await Task.Yield();
            foreach (string piece in Pieces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ChatStreamUpdate.Text(piece);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!EmitCompletion) yield break;
            BeforeCompletion?.Invoke();
            yield return new("", true, 1, 1, 0, 0, 0, 0, cancellationToken.IsCancellationRequested ? "cancelled" : FinishReason);
        }
        public void Dispose() { Unload(); Disposals++; }
    }
    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Weights) });
    }
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
}
