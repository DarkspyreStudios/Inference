using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ResourcePreparationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterFailureRetainsOrderedGeneratedReceiptsWithoutDuplicatingThem(bool canceled)
    {
        var first = new InferenceResourceReference("workspace", "first-output");
        var second = new InferenceResourceReference("workspace", "second-output");
        InferenceResourceCommitResult[] receipts =
        [
            new(new(first, "session", "first.json", "application/json", 10),
                new("workspace", "session", 5, [first])),
            new(new(second, "session", "second.json", "application/json", 20),
                new("workspace", "session", 6, [second, first]))
        ];
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception original = canceled ? new OperationCanceledException(cancellation.Token)
            : new IOException("The next output could not be downloaded.");
        Exception current = LlmResourcePreparation.WithGeneratedResources(original, [receipts[1]]);
        var cleanup = new IOException("The output reader could not be released.");

        Exception failure = LlmResourcePreparation.WithGeneratedResources(new AggregateException(current, cleanup), receipts);
        Exception[] causes = Assert.IsType<AggregateException>(failure).InnerExceptions.ToArray();
        Assert.Equal(2, causes.Length);
        Assert.Same(cleanup, causes[1]);
        InferenceResourceOperationFailure observed;
        if (canceled)
        {
            var operation = Assert.IsType<InferenceResourceOperationCanceledException>(causes[0]);
            Assert.Equal(cancellation.Token, operation.CancellationToken);
            Assert.Same(current, operation.InnerException);
            Assert.Same(original, operation.InnerException!.InnerException);
            observed = operation.Failure;
        }
        else
        {
            var operation = Assert.IsType<InferenceResourceOperationException>(causes[0]);
            Assert.Same(current, operation.InnerException);
            Assert.Same(original, operation.InnerException!.InnerException);
            observed = operation.Failure;
        }
        Assert.Equal(InferenceResourceOperationPhase.Materialize, observed.Phase);
        Assert.Equal(InferenceResourceCommitOutcome.NotAttempted, observed.CommitOutcome);
        Assert.Null(observed.Reference);

        InferenceResourceOperationFailure restored = JsonSerializer.Deserialize(
            JsonSerializer.SerializeToUtf8Bytes(observed, InferenceJson.InferenceResourceOperationFailure),
            InferenceJson.InferenceResourceOperationFailure)!;
        Assert.Equal(receipts.Select(receipt => receipt.Resource), restored.GeneratedResources.Select(receipt => receipt.Resource));
        Assert.Equal([5L, 6L], restored.GeneratedResources.Select(receipt => receipt.Selection!.Revision));
        Assert.Equal([first], restored.GeneratedResources[0].Selection!.SelectedResources);
        Assert.Equal([second, first], restored.GeneratedResources[1].Selection!.SelectedResources);
        Assert.All(restored.GeneratedResources, receipt =>
            Assert.Equal(("workspace", "session"), (receipt.Selection!.WorkspaceId, receipt.Selection.SessionId)));

        Exception[] repeated = Assert.IsType<AggregateException>(
            LlmResourcePreparation.WithGeneratedResources(failure, receipts)).InnerExceptions.ToArray();
        Assert.Same(causes[0], repeated[0]);
        Assert.Same(cleanup, repeated[1]);
    }

    [Fact]
    public async Task PortableSessionResourcesUploadAgainForTheSelectedDestination()
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var generated = new InferenceResourceReference("workspace", "generated-json");
        var access = new Resources(new("workspace", "session", 4, [generated, reference]),
            [(reference, "reference.txt", "reference text"u8.ToArray()), (generated, "result.json", "{\"answer\":42}"u8.ToArray())]);
        var context = new LlmAgentContext
        {
            Revision = 9,
            ResourceSelectionVersion = new(access.WorkspaceId, access.SessionId, 4),
            SelectedResources = [generated, reference],
            Entries =
            [
                new("prompt", "turn-1", new("user", "Read the reference")
                { Attachments = [new LlmResourceAttachment(reference), new LlmResourceAttachment(reference)] }),
                new("reply", "turn-1", new("assistant", "Created result.json"))
            ]
        };
        byte[] portable = JsonSerializer.SerializeToUtf8Bytes(context, ConnectLlmJson.AgentContext);

        foreach (string destination in new[] { "a", "b" })
        {
            LlmAgentContext restored = JsonSerializer.Deserialize(portable, ConnectLlmJson.AgentContext)!;
            var connection = new LlmConnection
            {
                Version = destination,
                Transport = LlmTransport.XaiResponses,
                Xai = new(),
                Endpoint = "https://api.x.ai/v1",
                Credential = new LlmCredential { OAuth = new() { AccessToken = "token-" + destination } }.Serialize()
            };
            LlmAgentContext imported = ManagedAgentContext.Import(restored, connection, "model-" + destination, supportsResources: true);
            await LlmResourcePreparation.ValidateContextAsync(imported, access.Selection, access, () => { }, CancellationToken.None);
            var request = new LlmChatRequest
            {
                ModelId = "model-" + destination,
                Messages = [.. imported.Entries.Select(entry => entry.Message),
                    new("user", "Compare the two files")]
            };
            request = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(request, ConnectLlmJson.ChatRequest),
                ConnectLlmJson.ChatRequest)!;
            request = request with { Resources = access };
            var handler = new Uploads(destination);
            using var http = new HttpClient(handler);
            await using var files = new XaiFileClient(http);
            await using (LlmResourcePreparation prepared = await LlmResourcePreparation.PrepareAsync(
                connection, request, files, () => { }, CancellationToken.None))
            {
                Assert.Equal(9, imported.Revision);
                Assert.Equal(context.ResourceSelectionVersion, prepared.SelectionVersion);
                Assert.Equal([generated, reference], imported.SelectedResources);
                Assert.Null(prepared.Request.Resources);
                Assert.Equal(["file-" + destination + "-1", "file-" + destination + "-1"],
                    prepared.Request.Messages[0].Attachments.Cast<LlmFileReference>().Select(part => part.File.FileId));
                Assert.Equal("file-" + destination + "-2",
                    Assert.IsType<LlmFileReference>(Assert.Single(prepared.Request.Messages[^1].Attachments)).File.FileId);
                Assert.Equal("reference.txt", handler.Items[0].Name);
                Assert.Equal("reference text"u8.ToArray(), handler.Items[0].Bytes);
                Assert.Equal("result.json", handler.Items[1].Name);
                Assert.Equal("{\"answer\":42}"u8.ToArray(), handler.Items[1].Bytes);
                Assert.Equal(2, handler.Items.Count);
            }
            Assert.All(access.Reads, stream => Assert.False(stream.CanRead));
        }

        InferenceResourceSelection selection = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(access.Selection, InferenceJson.InferenceResourceSelection), InferenceJson.InferenceResourceSelection)!;
        Assert.Equal(("workspace", "session", 4), (selection.WorkspaceId, selection.SessionId, selection.Revision));
        Assert.Equal([generated, reference], selection.SelectedResources);
        Assert.IsType<LlmResourceAttachment>(context.Entries[0].Message.Attachments[0]);
    }

    private sealed class Uploads(string destination) : HttpMessageHandler
    {
        public List<(string Name, byte[] Bytes)> Items { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(new Uri("https://api.x.ai/v1/files"), request.RequestUri);
            Assert.Equal("token-" + destination, request.Headers.Authorization!.Parameter);
            HttpContent file = Assert.Single(Assert.IsType<MultipartFormDataContent>(request.Content));
            string name = file.Headers.ContentDisposition!.FileName!.Trim('"');
            byte[] bytes = await file.ReadAsByteArrayAsync(cancellationToken);
            Items.Add((name, bytes));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject
                {
                    ["object"] = "file",
                    ["id"] = "file-" + destination + "-" + Items.Count,
                    ["filename"] = name,
                    ["bytes"] = bytes.Length,
                    ["created_at"] = 1
                }.ToJsonString())
            };
        }
    }

    private sealed class Resources(InferenceResourceSelection selection,
        IReadOnlyList<(InferenceResourceReference Reference, string Name, byte[] Bytes)> content) : IInferenceResourceAccess
    {
        public string WorkspaceId => selection.WorkspaceId;
        public string SessionId => selection.SessionId;
        public InferenceResourceSelection Selection => selection;
        public List<MemoryStream> Reads { get; } = [];

        public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default)
        {
            var file = content.Single(item => item.Reference == reference);
            return Task.FromResult<InferenceResourceDescriptor?>(new(reference, SessionId, file.Name, null, file.Bytes.Length));
        }

        public async Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default)
        {
            var stream = new MemoryStream(content.Single(item => item.Reference == reference).Bytes, writable: false);
            Reads.Add(stream);
            return new Read((await GetAsync(reference, cancellationToken))!, stream);
        }

        public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(selection);
        public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private sealed class Read(InferenceResourceDescriptor resource, MemoryStream stream) : IInferenceResourceReadHandle
        {
            public InferenceResourceDescriptor Resource => resource;
            public Stream Content => stream;
            public ValueTask DisposeAsync() => stream.DisposeAsync();
        }
    }
}
