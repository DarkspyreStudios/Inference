using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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

    [Fact]
    public async Task SelectedPackOwnsProvenanceAndLimitsOnlyAppearFromLoadedMetadata()
    {
        TensorModelPackDefinition first = Pack("host/first"), second = Pack("host/second");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, first, second);
        await packs.InstallAsync(first.Key);
        await packs.InstallAsync(second.Key);
        var runtime = new Runtime();
        await using var engine = new QwenLlmInference(packs, runtime, TensorBackendKind.Cpu);
        Assert.All(await engine.ListModelsAsync(), model => Assert.Null(model.MaxContextTokens));
        foreach (TensorModelPackDefinition pack in new[] { first, second })
        {
            await engine.LoadAndWaitAsync(pack.Key);
            Assert.Equal(pack.Key, (await engine.GetObservationAsync()).Model);
            Assert.Equal(8192, (await engine.ListModelsAsync()).Single(model => model.Id == pack.Key.ModelId).MaxContextTokens);
            LlmCompletionEvent completion = Assert.IsType<LlmCompletionEvent>((await Collect(engine, pack.Key.ModelId))[^1]);
            Assert.Equal(pack.Key.ModelId, completion.Result.Provenance!.ModelId);
            Assert.Equal(pack.Key.Revision, completion.Result.Provenance.ModelRevision);
            Assert.Equal("hello", completion.Result.Text);
            InferenceException wrong = await Assert.ThrowsAsync<InferenceException>(() => Collect(engine, "another/model"));
            Assert.Equal(InferenceFailureCode.ModelUnavailable, wrong.Code);
            await Assert.ThrowsAsync<TensorModelPackException>(() => packs.RemoveAsync(pack.Key));
            await engine.UnloadAndWaitAsync();
            Assert.Null((await engine.GetObservationAsync()).Model);
            Assert.Null(runtime.ContextLength);
            await packs.RemoveAsync(pack.Key);
        }
        Assert.Equal(new[] { first.Key, second.Key }, runtime.Keys);
    }

    [Fact]
    public async Task SetupSavesCustomSelectionWithoutInstalling()
    {
        TensorModelPackDefinition pack = Pack("host/custom");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, pack);
        var target = new Target();
        TensorSettingsManager manager = QwenSetup.CreateSettingsManager(target, packs, offeredModels: [pack]);
        TensorSettings setup = await manager.OpenAsync(new(SetupChangeIntent.Create, "custom", null));
        var field = Assert.IsType<SetupChoiceField>(setup.Steps[0].Sections[0].Fields[0]);
        Assert.Equal(SetupEditState.Applied, field.SetValue(pack.Key.ToString()).State);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(setup)).State);
        Assert.Equal(pack.Key, target.Changes!.Configuration.Model);
        var editTarget = new Target();
        TensorSettingsManager editManager = QwenSetup.CreateSettingsManager(editTarget, packs, offeredModels: [pack]);
        TensorSettings edit = await editManager.OpenAsync(new(SetupChangeIntent.Edit, "custom", "saved-v1"), target.Changes.Configuration);
        SetupChoiceField backend = Assert.IsType<SetupChoiceField>(edit.Steps.SelectMany(step => step.Sections)
            .SelectMany(section => section.Fields).Single(item => item.Description.Id == "backend"));
        Assert.Equal(SetupEditState.Applied, backend.SetValue(nameof(TensorBackendKind.Cpu)).State);
        Assert.Equal(SetupCommitState.Committed, (await editManager.SaveAsync(edit)).State);
        Assert.Equal("saved-v1", editTarget.Changes!.Identity.ExpectedVersion);
        Assert.Equal(pack.Key, editTarget.Changes.Configuration.Model);
        Assert.Equal(TensorBackendKind.Cpu, editTarget.Changes.Configuration.Backend);
        Assert.True(editTarget.Changes.RequiresUnload);
        Assert.Equal(TensorModelPackState.NotInstalled, (await packs.GetStatusAsync(pack.Key)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledLoadClearsRuntimeAndLease(bool cancel)
    {
        TensorModelPackDefinition pack = Pack("host/failure");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        using var cancellation = new CancellationTokenSource();
        var runtime = new Runtime { OnLoad = () => { if (cancel) { cancellation.Cancel(); } else { throw new InvalidDataException(); } } };
        await using var engine = new QwenLlmInference(packs, runtime, TensorBackendKind.Cpu);
        if (cancel) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.LoadAndWaitAsync(pack.Key, cancellation.Token)); }
        else { Assert.Equal(InferenceFailureCode.ModelCorrupt, (await Assert.ThrowsAsync<InferenceException>(() => engine.LoadAndWaitAsync(pack.Key))).Code); }
        Assert.Null(runtime.ContextLength);
        Assert.Null((await engine.GetObservationAsync()).Model);
        await packs.RemoveAsync(pack.Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingLeaseProtectsNativeLifetimeDuringUnload(bool urgent)
    {
        TensorModelPackDefinition pack = Pack("host/stream");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        var runtime = new Runtime();
        await using var engine = new QwenLlmInference(packs, runtime, TensorBackendKind.Cpu);
        await engine.LoadAndWaitAsync(pack.Key);
        await using IAsyncEnumerator<LlmStreamEvent> stream = engine.StreamAsync(Request(pack.Key.ModelId)).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Task unloading = engine.UnloadAndWaitAsync(new CancellationToken(urgent));
        Assert.False(unloading.IsCompleted);
        Assert.NotNull(runtime.ContextLength);
        if (urgent) { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await stream.MoveNextAsync(); }); }
        else { while (await stream.MoveNextAsync()) { } }
        await stream.DisposeAsync();
        await unloading;
        Assert.Null(runtime.ContextLength);
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

    private TensorModelPackManager Manager(HttpClient http, params TensorModelPackDefinition[] definitions) => new(new(definitions), _root, http);
    private static TensorModelPackDefinition Pack(string id) => TensorLlmModels.Qwen3FourBillionQ4Km with
    {
        Key = new(id, "custom-revision"),
        Artifacts = [new() { Role = "model", RelativePath = "model.gguf", Length = Weights.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(Weights)), Sources = [new("https://weights.example.test/model.gguf")] }]
    };
    private static LlmInferenceRequest Request(string id) => new() { ModelId = id, Messages = [new("user", "hello")] };
    private static async Task<List<LlmStreamEvent>> Collect(QwenLlmInference engine, string id)
    {
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in engine.StreamAsync(Request(id))) { events.Add(item); }
        return events;
    }
    private sealed class Runtime : IQwenModelRuntime
    {
        public List<TensorModelKey> Keys { get; } = [];
        public Action? OnLoad { get; init; }
        public int? ContextLength { get; private set; }
        public string? LoadedBackend { get; private set; }
        public CancellationToken LastStreamToken { get; private set; }
        public Task LoadAsync(TensorModelPackDefinition definition, TensorModelPackLease lease, string backend, CancellationToken cancellationToken)
        {
            Assert.Equal(definition.Key, lease.Key);
            Keys.Add(lease.Key); ContextLength = 8192; LoadedBackend = backend;
            OnLoad?.Invoke();
            return Task.CompletedTask;
        }
        public void Unload() { ContextLength = null; LoadedBackend = null; }
        public void ApplyGrammar(SamplingConfig sampling, StructuredOutputFormat format) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatSession session, List<ChatMessage> messages, int maxTokens,
            SamplingConfig sampling, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastStreamToken = cancellationToken;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return ChatStreamUpdate.Text("hello");
            cancellationToken.ThrowIfCancellationRequested();
            yield return new("", true, 1, 1, 0, 0, 0, 0, "stop");
        }
        public void Dispose() => Unload();
    }
    private sealed class Target : ISetupCommitTarget<TensorSetupChanges>
    {
        public TensorSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(TensorSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "version")); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Weights) });
    }
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
}
