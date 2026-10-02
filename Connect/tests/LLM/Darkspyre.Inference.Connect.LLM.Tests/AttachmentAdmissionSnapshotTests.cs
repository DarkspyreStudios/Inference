using System.Runtime.CompilerServices;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AttachmentAdmissionSnapshotTests
{
    [Fact]
    public void ForeignOwnedSnapshotCallerAndMetadataRetireAfterTheOperation()
    {
        WeakReference[] weak = ExerciseForeign();
        for (int attempt = 0; weak.Any(reference => reference.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.All(weak, reference => Assert.False(reference.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeign()
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(AttachmentAdmissionSnapshotTests).Assembly.Location)!);
        var weak = new List<WeakReference> { new(generation) };
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(AttachmentAdmissionSnapshotTests).Assembly.Location);
            foreach (Assembly original in new[] { typeof(InferenceJson).Assembly, typeof(ConnectLlmJson).Assembly,
                typeof(AttachmentAdmissionSnapshotTests).Assembly })
            {
                Assembly actual = generation.LoadFromAssemblyPath(original.Location);
                Assert.NotSame(original, actual);
                Assert.Equal(original.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
                weak.Add(new(actual));
            }
            Type type = fixture.GetType(typeof(AttachmentAdmissionSnapshotTests).FullName!, true)!;
            object caller = Activator.CreateInstance(type)!;
            type.GetMethod(nameof(SnapshotPreservesTheWholeAttachmentUnionWithoutEffects))!.Invoke(caller, null);
            ((Task)type.GetMethod(nameof(FacadeFreezesAttachmentOrderBytesAndOmissionsAtAdmission))!
                .Invoke(caller, [0, 0])!).GetAwaiter().GetResult();
        }
        finally { generation.Unload(); }
        return weak.ToArray();
    }

    [Fact]
    public void SnapshotPreservesTheWholeAttachmentUnionWithoutEffects()
    {
        byte[] first = [1], last = [2];
        List<LlmMessageAttachment> attachments = [new LlmAttachment { Kind = LlmBinaryKind.Document,
            FileName = "first.txt", Data = first }, new LlmResourceAttachment(new("workspace", "original")),
            new LlmFileReference(new("native")), new LlmAttachment { Kind = LlmBinaryKind.Document,
                FileName = "last.txt", Data = last }];
        var request = new LlmChatRequest { ModelId = "model", Messages = [new("user", "") { Attachments = attachments }] };
        LlmChatRequest copy = ClientTools.Snapshot(request);
        attachments.Clear(); first[0] = 9; last[0] = 9;
        LlmChatMessage message = Assert.Single(copy.Messages);
        Assert.Collection(message.Attachments,
            attachment => Assert.Equal(1, Assert.IsType<LlmAttachment>(attachment).Data.Span[0]),
            attachment => Assert.Equal("original", Assert.IsType<LlmResourceAttachment>(attachment).Resource.Id),
            attachment => Assert.Equal("native", Assert.IsType<LlmFileReference>(attachment).File.FileId),
            attachment => Assert.Equal(2, Assert.IsType<LlmAttachment>(attachment).Data.Span[0]));
        string written = JsonSerializer.Serialize(copy, ConnectLlmJson.ChatRequest);
        LlmChatRequest read = JsonSerializer.Deserialize(written, ConnectLlmJson.ChatRequest)!;
        Assert.Equal(written, JsonSerializer.Serialize(read, ConnectLlmJson.ChatRequest));
        Assert.Same(typeof(ConnectLlmJson).Assembly, ConnectLlmJson.ChatRequest.Type.Assembly);
        foreach (LlmMessageAttachment attachment in message.Attachments)
        {
            string json = JsonSerializer.Serialize(attachment, InferenceJson.LlmMessageAttachment);
            LlmMessageAttachment parsed = JsonSerializer.Deserialize(json, InferenceJson.LlmMessageAttachment)!;
            Assert.Equal(json, JsonSerializer.Serialize(parsed, InferenceJson.LlmMessageAttachment));
        }
        Assert.Same(typeof(InferenceJson).Assembly, InferenceJson.LlmMessageAttachment.Type.Assembly);
        Assert.Throws<NotSupportedException>(() => LlmAttachments.ValidateReferences(copy));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectSelfHostFreezesAttachmentsBeforeEngineAndCapabilityAwaits(bool holdCapabilities)
    {
        var host = new Host(holdCapabilities);
        var client = new SelfHostLlmClient(host);
        byte[] bytes = [1, 2, 3];
        List<LlmMessageAttachment> attachments = [new LlmAttachment
        { Kind = LlmBinaryKind.Document, FileName = "original.txt", MediaType = "text/plain", Data = bytes }];
        Task run = ReadAsync(client.StreamAsync(new() { Transport = LlmTransport.SelfHost, Version = "1", Endpoint = "engine" },
            new() { ModelId = "model", Messages = [new("user", "hello") { Attachments = attachments }] }));
        try
        {
            await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bytes[0] = 9;
            attachments.Add(new LlmFileReference(new ProviderFileReference("unsupported-late")));
            host.Release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<LlmAttachment>(Assert.Single(
                Assert.Single(Assert.IsType<LlmInferenceRequest>(host.Sent).Messages).Attachments)).Data.ToArray());
        }
        finally
        {
            host.Release.TrySetResult();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) when (run.IsCompleted) { }
        }
    }

    private static async Task ReadAsync(IAsyncEnumerable<LlmStreamEvent> events)
    { await foreach (var unused in events) { } }

    public static IEnumerable<object[]> Cases()
    {
        for (int boundary = 0; boundary < 3; boundary++)
            for (int operation = 0; operation < 4; operation++)
                if (boundary != 1 || operation != 3) yield return [boundary, operation];
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task FacadeFreezesAttachmentOrderBytesAndOmissionsAtAdmission(int boundary, int operation)
    {
        var gate = new Gate(boundary);
        var source = new Source(gate);
        var client = new Client(gate);
        var authenticator = new Authenticator(gate);
        byte[] bytes = [1, 2, 3];
        List<LlmMessageAttachment> attachments = [new LlmAttachment
        { Kind = LlmBinaryKind.Document, FileName = "original.txt", MediaType = "text/plain", Data = bytes },
            new LlmFileReference(new ProviderFileReference("native-original"))];
        List<LlmOmittedAttachment> omissions = [new(LlmBinaryKind.Document, "omitted.txt", 2)];
        var request = new LlmChatRequest
        {
            ModelId = "model",
            Messages = [new("user", "original")
        { Attachments = attachments, OmittedAttachments = omissions }]
        };
        var facade = new LlmProviderService(source, [client], [authenticator]);
        Task run = ExecuteAsync();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bytes[0] = 9;
            attachments.Reverse();
            attachments.Add(new LlmFileReference(new ProviderFileReference("late-native")));
            omissions.Clear();
            gate.Release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            LlmChatMessage sent = Assert.Single(Assert.IsType<LlmChatRequest>(client.Sent).Messages);
            Assert.Collection(sent.Attachments,
                attachment => Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<LlmAttachment>(attachment).Data.ToArray()),
                attachment => Assert.Equal("native-original", Assert.IsType<LlmFileReference>(attachment).File.FileId));
            Assert.Equal("omitted.txt", Assert.Single(sent.OmittedAttachments).FileName);
            Assert.Equal(3, attachments.Count);
            Assert.Equal(9, bytes[0]);
        }
        finally
        {
            Task retirement = facade.DisposeAsync().AsTask();
            gate.Release.TrySetResult();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) when (run.IsCompleted) { }
            finally { await retirement.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        Assert.False(source.Disposed);
        Assert.False(authenticator.Disposed);

        async Task ExecuteAsync()
        {
            if (operation == 1) await facade.CompleteAsync("connection", request);
            else if (operation == 2) await facade.CountInputTokensAsync("connection", request);
            else if (operation == 3) await facade.MeasureRequestAsync("connection", request);
            else await foreach (var unused in facade.StreamAsync("connection", request)) { }
        }
    }

    private sealed class Gate(int boundary)
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task WaitAsync(int stage, CancellationToken token)
        {
            if (stage != boundary) return;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
        }
    }

    private sealed class Source(Gate gate) : ILlmConnectionSource, IDisposable
    {
        internal bool Disposed { get; private set; }
        public async Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(0, cancellationToken);
            return new()
            {
                Transport = LlmTransport.OpenAiResponses,
                Version = "1",
                Endpoint = "https://example.test",
                Credential = new LlmCredential { OAuth = new() { AccessToken = "controlled" } }.Serialize()
            };
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The unchanged credential cannot be persisted.");
        public void Dispose() => Disposed = true;
    }

    private sealed class Authenticator(Gate gate) : ILlmAuthenticator, IDisposable
    {
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        internal bool Disposed { get; private set; }
        public async Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { await gate.WaitAsync(1, cancellationToken); return credential; }
        public void Dispose() => Disposed = true;
    }

    private sealed class Client(Gate gate) : ILlmClient, ILlmCatalogCapabilities, ILlmProviderTokenCounter, ILlmContextEstimator
    {
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        public LlmProviderDefinition ProviderDefinition => new()
        {
            Id = "controlled",
            DisplayName = "Controlled",
            Description = "Controlled",
            Transport = Transport,
            DefaultEndpoint = "https://example.test",
            SetupSteps = []
        };
        internal LlmChatRequest? Sent { get; private set; }
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new()
        { SupportsStreaming = true, SupportsModelListing = true, SupportsFileReferences = true };
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Sent = request; await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            yield return new LlmChatCompletionEvent(new() { Text = "actual", Status = LlmCompletionStatus.Completed, Diagnostics = new(200, null, null) });
        }
        public async Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
        { await gate.WaitAsync(2, cancellationToken); return [new("model", "Model")]; }
        public Task<LlmProviderInputTokenCount> CountInputTokensAsync(LlmConnection connection, LlmChatRequest request, CancellationToken cancellationToken = default)
        { Sent = request; return Task.FromResult(new LlmProviderInputTokenCount { ModelId = request.ModelId, InputTokens = 1 }); }
        public string? EstimateDiagnostic => null;
        public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request) { Sent = request; return new(1); }
    }

    private sealed class Host(bool holdCapabilities) : ILlmSelfHost, ILlmInference
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LlmInferenceRequest? Sent { get; private set; }
        public Darkspyre.Inference.Common.InferenceImplementationDescriptor Descriptor =>
            new("controlled", "Controlled", Darkspyre.Inference.Common.InferenceImplementationKind.Tensor,
                Darkspyre.Inference.Common.InferenceTask.TextGeneration);
        public LlmCapabilities? GetCapabilities(string engineId, string? modelId) => new() { SupportsStreaming = true };
        public Task<IReadOnlyList<LlmSelfHostEngine>> ListEnginesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmSelfHostEngine>>([]);
        public async Task<ILlmInference> GetInferenceAsync(string engineId, CancellationToken cancellationToken = default)
        { if (!holdCapabilities) await WaitAsync(cancellationToken); return this; }
        public async Task<LlmCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default)
        { if (holdCapabilities) await WaitAsync(cancellationToken); return new() { SupportsStreaming = true }; }
        public Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmModelInfo>>([]);
        public Task<IReadOnlyList<LlmModelInfo>> RefreshModelsAsync(CancellationToken cancellationToken = default) => ListModelsAsync(cancellationToken);
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmInferenceRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Sent = request; await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            yield return new LlmCompletionEvent(new()
            {
                Text = "actual",
                Status = LlmCompletionStatus.Completed,
                Provenance = new() { ImplementationId = "controlled", ModelId = "model" }
            });
        }
        private async Task WaitAsync(CancellationToken token)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
    }

    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
