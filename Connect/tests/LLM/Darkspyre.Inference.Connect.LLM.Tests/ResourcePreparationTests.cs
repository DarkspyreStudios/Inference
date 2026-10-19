using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ResourcePreparationTests
{
    [Fact]
    public async Task ResourceBoundAccountingDoesNotIgnoreSelectedFilesOrOpenThem()
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var access = new Resources(new("workspace", "session", 1, [reference]),
            [(reference, "reference.txt", "reference text"u8.ToArray())]);
        var connection = new LlmConnection { Version = "claude-cli", Transport = LlmTransport.AnthropicClaudeCli, Endpoint = "claude" };
        using var http = new HttpClient(new NoHttp());
        var estimator = new ClaudeCliLlmClient(http, new NoRuns(), new NoSessionProcesses());
        var request = new LlmChatRequest
        {
            ModelId = "claude-sonnet-4-6",
            Resources = access,
            Messages = [new("user", "Read the file")]
        };
        var resources = await ResourceSizes.ResolveAsync(access, [reference], CancellationToken.None);
        long withoutResource = (await estimator.EstimateAsync(connection, request with { Resources = null }, ResourceSizes.None, CancellationToken.None))!.Value.Tokens;
        long resourceTokens = AttachmentTokens.Estimate(null, "reference text"u8.Length);
        // The selected file counts once, whether only the selection or also an attachment names it.
        Assert.Equal(withoutResource + resourceTokens, (await estimator.EstimateAsync(connection, request, resources, CancellationToken.None))!.Value.Tokens);
        Assert.Equal(withoutResource + resourceTokens, (await estimator.EstimateAsync(connection, request with
        { Messages = [new("user", "Read the file") { Attachments = [new LlmResourceAttachment(reference)] }] }, resources, CancellationToken.None))!.Value.Tokens);
        Assert.Empty(access.Reads);
        Assert.Equal(0, access.ReleaseAttempts);
    }

    private sealed class NoHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Accounting sends no request.");
    }

    private sealed class NoRuns : IClaudeCliRunner
    {
        public Task<ClaudeCliProcessResult> RunAsync(ClaudeCliInvocation invocation, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Accounting runs no CLI.");
    }

    private sealed class NoSessionProcesses : IClaudeCliSessionProcessFactory
    {
        public Task<IClaudeCliSessionProcess> StartAsync(ClaudeCliSessionInvocation invocation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    internal sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(connection);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    internal sealed class Resources(InferenceResourceSelection selection,
        IReadOnlyList<(InferenceResourceReference Reference, string Name, byte[] Bytes)> content) : IInferenceResourceAccess
    {
        public string WorkspaceId => Selection.WorkspaceId;
        public string SessionId => Selection.SessionId;
        public InferenceResourceSelection Selection { get; set; } = selection;
        public List<MemoryStream> Reads { get; } = [];
        public int ReleaseAttempts { get; private set; }
        public Func<InferenceResourceCreate, IInferenceResourceWriteHandle>? WriteFactory { get; init; }

        public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default)
        {
            var file = content.Single(item => item.Reference == reference);
            return Task.FromResult<InferenceResourceDescriptor?>(new(reference, SessionId, file.Name, null, file.Bytes.Length));
        }

        public async Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default)
        {
            var stream = new MemoryStream(content.Single(item => item.Reference == reference).Bytes, writable: false);
            Reads.Add(stream);
            return new Read((await GetAsync(reference, cancellationToken))!, stream, this);
        }

        public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(Selection);
        public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default) =>
            Task.FromResult(WriteFactory?.Invoke(resource) ?? throw new NotSupportedException());
        public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private sealed class Read(InferenceResourceDescriptor resource, MemoryStream stream, Resources owner) : IInferenceResourceReadHandle
        {
            public InferenceResourceDescriptor Resource => resource;
            public Stream Content => stream;
            public ValueTask DisposeAsync()
            {
                owner.ReleaseAttempts++;
                return stream.DisposeAsync();
            }
        }
    }

    internal sealed class GeneratedWrite(string workspace, InferenceResourceCreate create, Action afterCommit) : IInferenceResourceWriteHandle
    {
        private readonly MemoryStream _content = new();
        public InferenceResourceReference Reference { get; } = new(workspace, "generated");
        public Stream Content => _content;
        public byte[] CommittedBytes { get; private set; } = [];

        public Task<InferenceResourceCommitResult> CommitAsync(long bytesWritten, InferenceResourceCommitOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(options!.SelectForSession);
            Assert.Equal(_content.Length, bytesWritten);
            CommittedBytes = _content.ToArray();
            var receipt = new InferenceResourceCommitResult(
                new(Reference, create.SessionId, create.FileName, create.ContentType, bytesWritten),
                new(workspace, create.SessionId!, 4, [Reference]));
            afterCommit();
            return Task.FromResult(receipt);
        }

        public ValueTask DisposeAsync() => _content.DisposeAsync();
    }
}
