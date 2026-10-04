using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AttachmentReferenceTests
{
    [Fact]
    public void ByteExcludedCaptureRetainsMixedReferenceOrderAndConceptualOmissionSlots()
    {
        var resource = new InferenceResourceReference("workspace", "original");
        byte[] first = [1], last = [2];
        var message = new LlmChatMessage("user", "inspect")
        {
            Attachments = [Binary("first.txt", first), new LlmResourceAttachment(resource),
                new LlmFileReference(new("native-file")), Binary("last.txt", last)]
        };
        LlmChatMessage included = ManagedAgentContext.CopyMessage(message);
        first[0] = 9;
        last[0] = 9;
        Assert.Equal(1, Assert.IsType<LlmAttachment>(included.Attachments[0]).Data.Span[0]);
        Assert.Equal(2, Assert.IsType<LlmAttachment>(included.Attachments[3]).Data.Span[0]);

        LlmChatMessage excluded = ManagedAgentContext.CopyMessage(included, false);
        Assert.Equal(resource, Assert.IsType<LlmResourceAttachment>(excluded.Attachments[0]).Resource);
        Assert.Equal("native-file", Assert.IsType<LlmFileReference>(excluded.Attachments[1]).File.FileId);
        Assert.Equal([0, 3], excluded.OmittedAttachments.Select(item => item.Index));
        Assert.Equal(["first.txt", "last.txt"], excluded.OmittedAttachments.Select(item => item.FileName));
        Assert.All(excluded.OmittedAttachments, item => Assert.Null(item.ToolCallId));
        LlmChatMessage repeated = ManagedAgentContext.CopyMessage(excluded, false);
        Assert.Equal(excluded.Attachments, repeated.Attachments);
        Assert.Equal(excluded.OmittedAttachments, repeated.OmittedAttachments);
        Assert.Equal(4, included.Attachments.Count);
        Assert.Empty(included.OmittedAttachments);
    }

    [Fact]
    public void LogicalToolOutputSurvivesByteExclusionWithoutFetchingOrBecomingAUri()
    {
        var resource = new InferenceResourceReference("workspace", "generated");
        var message = new LlmChatMessage("user", "")
        {
            ToolResults = [new LlmToolResult("call", new()
            {
                Parts = [new LlmToolOutputBinary(LlmBinaryKind.Document, "text/plain", new byte[] { 1 }, "inline.txt"),
                    new LlmToolOutputSessionResource(resource), new LlmToolOutputText("done")]
            })]
        };
        LlmChatMessage copy = ManagedAgentContext.CopyMessage(message, false);
        Assert.Equal(resource, Assert.IsType<LlmToolOutputSessionResource>(Assert.IsType<LlmToolResult>(copy.ToolResults[0]).Output.Parts[0]).Resource);
        Assert.Equal("done", Assert.IsType<LlmToolOutputText>(Assert.IsType<LlmToolResult>(copy.ToolResults[0]).Output.Parts[1]).Text);
        Assert.Equal(new LlmOmittedAttachment(LlmBinaryKind.Document, "inline.txt", 0, "call"), Assert.Single(copy.OmittedAttachments));
        Assert.Equal(3, Assert.IsType<LlmToolResult>(message.ToolResults[0]).Output.Parts.Count);
    }

    [Fact]
    public void ExistingBinaryOnlyRoutesRefuseReferencesWithoutInventingBytes()
    {
        LlmMessageAttachment[] references = [new LlmFileReference(new("native-file")),
            new LlmResourceAttachment(new("workspace", "original"))];
        foreach (LlmMessageAttachment reference in references)
        {
            Assert.Throws<NotSupportedException>(() => LlmAttachments.Validate([reference], true, true, "request"));
            Assert.Throws<NotSupportedException>(() => LlmAttachments.RequireBinary(reference));
        }
        var output = new LlmToolOutput { Parts = [new LlmToolOutputSessionResource(new("workspace", "generated"))] };
        Assert.Throws<NotSupportedException>(() => ToolOutputProjector.Project(output, new(), new()));
    }

    [Theory]
    [InlineData("native-file")]
    [InlineData("resource")]
    [InlineData("tool-resource")]
    [InlineData("authority")]
    public async Task FacadeRefusesUnsupportedResourcesBeforeRefreshCatalogOrExecution(string variant)
    {
        var authority = new ForbiddenResources();
        var message = new LlmChatMessage("user", "inspect");
        message = variant switch
        {
            "native-file" => message with { Attachments = [new LlmFileReference(new("native"))] },
            "resource" => message with { Attachments = [new LlmResourceAttachment(new("workspace", "original"))] },
            "tool-resource" => message with { ToolResults = [new LlmToolResult("call", new() { Parts = [new LlmToolOutputSessionResource(new("workspace", "generated"))] })] },
            _ => message
        };
        var request = new LlmChatRequest { ModelId = "model", Messages = [message], Resources = variant == "authority" ? authority : null };
        var client = new GuardedClient();
        var auth = new GuardedAuthenticator();
        await using var service = new LlmProviderService(new Source(), [client], [auth]);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.CompleteAsync("selected", request));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.CountInputTokensAsync("selected", request));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.MeasureRequestAsync("selected", request));
        var portableError = await Assert.ThrowsAsync<InferenceException>(() => service.Bind("selected").CompleteAsync(new LlmInferenceRequest
        {
            ModelId = request.ModelId,
            Resources = request.Resources,
            Messages = [new(message.Role, message.Content)
                { Attachments = message.Attachments, ToolCalls = message.ToolCalls, ToolResults = message.ToolResults }]
        }));
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, portableError.Code);
        Assert.Equal(0, client.Effects);
        Assert.Equal(0, auth.Refreshes);
        Assert.Equal(0, authority.Touched);
    }

    [Fact]
    public void ByteExclusionPreservesScopedSelectionAndRuntimeImportRefusesUnboundAuthority()
    {
        var context = new LlmAgentContext
        {
            SelectedResources = [new("workspace", "original"), new("workspace", "generated")],
            ResourceSelectionVersion = new("workspace", "session", 7)
        };
        var copy = ManagedAgentContext.Copy(context, false);
        Assert.Equal(context.SelectedResources, copy.SelectedResources);
        Assert.NotSame(context.SelectedResources, copy.SelectedResources);
        Assert.Equal(context.ResourceSelectionVersion, copy.ResourceSelectionVersion);
        Assert.NotSame(context.ResourceSelectionVersion, copy.ResourceSelectionVersion);
        Assert.Throws<NotSupportedException>(() => ManagedAgentContext.Import(context, DirectCodexTests.Connection(), "model"));
        Assert.Throws<NotSupportedException>(() => ManagedAgentContext.WithoutContinuationState(context));
        Assert.Equal(7, context.ResourceSelectionVersion.Revision);
    }

    [Fact]
    public void RepeatedToolExclusionUsesOriginalSlotsWhenAnEarlierCaptureAlreadyOmittedBytes()
    {
        var message = new LlmChatMessage("user", "")
        {
            OmittedAttachments = [new(LlmBinaryKind.Document, "old.txt", 0, "call")],
            ToolResults = [new LlmToolResult("call", new()
            {
                Parts = [new LlmToolOutputText("keep"),
                    new LlmToolOutputBinary(LlmBinaryKind.Document, "text/plain", new byte[] { 1 }, "new.txt")]
            })]
        };
        var copy = ManagedAgentContext.CopyMessage(message, false);
        Assert.Equal([0, 2], copy.OmittedAttachments.Select(item => item.Index));
        Assert.IsType<LlmToolOutputText>(Assert.Single(Assert.IsType<LlmToolResult>(copy.ToolResults[0]).Output.Parts));
        Assert.Equal(copy.OmittedAttachments, ManagedAgentContext.CopyMessage(copy, false).OmittedAttachments);
    }

    private sealed class Source : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult<LlmConnection?>(DirectCodexTests.Connection() with
            {
                Credential = new LlmCredential { OAuth = new() { AccessToken = "expired", RefreshToken = "refresh", ExpiresAtUnixSeconds = 1 } }.Serialize()
            });
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Unexpected credential write.");
    }

    private sealed class GuardedAuthenticator : ILlmAuthenticator
    {
        public int Refreshes { get; private set; }
        public LlmTransport Transport => LlmTransport.OpenAiCodex;
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        {
            Refreshes++;
            throw new InvalidOperationException("Unexpected refresh.");
        }
    }

    private sealed class GuardedClient : ILlmClient, ILlmProviderTokenCounter
    {
        public int Effects { get; private set; }
        public LlmTransport Transport => LlmTransport.OpenAiCodex;
        public LlmProviderDefinition ProviderDefinition => ProviderDefinitions.ChatGptSubscription;
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new();
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request, CancellationToken cancellationToken = default)
            => Forbidden<IAsyncEnumerable<LlmStreamEvent>>();
        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
            => Forbidden<Task<IReadOnlyList<LlmModel>>>();
        public Task<LlmProviderInputTokenCount> CountInputTokensAsync(LlmConnection connection, LlmChatRequest request, CancellationToken cancellationToken = default)
            => Forbidden<Task<LlmProviderInputTokenCount>>();
        private T Forbidden<T>() { Effects++; throw new InvalidOperationException("Unexpected provider effect."); }
    }

    private sealed class ForbiddenResources : IInferenceResourceAccess
    {
        public int Touched { get; private set; }
        public string WorkspaceId => Forbidden<string>();
        public string SessionId => Forbidden<string>();
        public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default)
            => Forbidden<Task<InferenceResourceDescriptor?>>();
        public Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default)
            => Forbidden<Task<IInferenceResourceReadHandle>>();
        public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default)
            => Forbidden<Task<IInferenceResourceWriteHandle>>();
        public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default)
            => Forbidden<Task<InferenceResourceSelection>>();
        public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default)
            => Forbidden<Task<InferenceResourceSelection>>();
        public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default)
            => Forbidden<Task<InferenceResourceSelection>>();
        private T Forbidden<T>() { Touched++; throw new InvalidOperationException("Unexpected resource callback."); }
    }

    private static LlmAttachment Binary(string name, byte[] bytes) => new()
    { Kind = LlmBinaryKind.Document, FileName = name, MediaType = "text/plain", Data = bytes };

    [Theory]
    [InlineData(LlmTransport.OpenAiCodex)]
    [InlineData(LlmTransport.GoogleGeminiApi)]
    [InlineData(LlmTransport.AppleFoundationModels)]
    [InlineData(LlmTransport.SelfHost)]
    public async Task ManagedOpeningRejectsResourceContextsBeforeDiscoveryProbeOrEngineResolution(LlmTransport transport)
    {
        using var handler = new ForbiddenHttp();
        using var http = new HttpClient(handler);
        var host = new ForbiddenHost();
        var runner = new AppleFoundationModelsTests.Runner(_ => throw new InvalidOperationException("Unexpected helper generation."));
        ILlmClient client = transport switch
        {
            LlmTransport.OpenAiCodex => new OpenAiCodexLlmClient(http),
            LlmTransport.GoogleGeminiApi => new GeminiApiLlmClient(http),
            LlmTransport.AppleFoundationModels => new AppleFoundationModelsClient(http, runner),
            _ => new SelfHostLlmClient(host)
        };
        await using var providers = new LlmProviderService(new Source(), [client]);
        ILlmAgentClient agent = client switch
        {
            OpenAiCodexLlmClient codex => new ManagedCodexAgentClient(codex, providers),
            GeminiApiLlmClient gemini => new ManagedGeminiApiAgentClient(gemini, providers),
            AppleFoundationModelsClient apple => new ManagedAppleAgentClient(apple, providers),
            SelfHostLlmClient selfHost => new ManagedSelfHostAgentClient(selfHost, providers),
            _ => throw new InvalidOperationException()
        };
        var connection = DirectCodexTests.Connection() with { Transport = transport };
        LlmAgentContext[] contexts =
        [
            new() { SelectedResources = [new("workspace", "original")] },
            new() { Entries = [new("entry", "turn", new("user", "inspect")
                { Attachments = [new LlmResourceAttachment(new("workspace", "original"))] })] }
        ];
        foreach (var context in contexts)
            await Assert.ThrowsAsync<NotSupportedException>(() => agent.StartSessionAsync("selected", connection,
                new() { ModelId = "model", Context = context }));
        Assert.Equal(0, handler.Requests);
        Assert.Empty(runner.Requests);
        Assert.Equal(0, host.Calls);
    }

    private sealed class ForbiddenHttp : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; throw new InvalidOperationException("Unexpected HTTP request."); }
    }

    private sealed class ForbiddenHost : ILlmSelfHost
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<LlmSelfHostEngine>> ListEnginesAsync(CancellationToken cancellationToken = default)
            => Forbidden<Task<IReadOnlyList<LlmSelfHostEngine>>>();
        public Task<Darkspyre.Inference.Common.Embedding.IEmbeddingInference> GetEmbeddingInferenceAsync(string engineId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException("This host runs no embedding engine.");

        public Task<ILlmInference> GetInferenceAsync(string engineId, CancellationToken cancellationToken = default)
            => Forbidden<Task<ILlmInference>>();
        public LlmCapabilities? GetCapabilities(string engineId, string? modelId) => Forbidden<LlmCapabilities>();
        private T Forbidden<T>() { Calls++; throw new InvalidOperationException("Unexpected host engine call."); }
    }
}
