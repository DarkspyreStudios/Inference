using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AttachmentAdmissionSnapshotTests
{
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
        var request = new LlmChatRequest { ModelId = "model", Messages = [new("user", "original")
        { Attachments = attachments, OmittedAttachments = omissions }] };
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
            return new() { Transport = LlmTransport.OpenAiResponses, Version = "1", Endpoint = "https://example.test",
                Credential = new LlmCredential { OAuth = new() { AccessToken = "controlled" } }.Serialize() };
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
        { Id = "controlled", DisplayName = "Controlled", Description = "Controlled", Transport = Transport,
            DefaultEndpoint = "https://example.test", SetupSteps = [] };
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
}
