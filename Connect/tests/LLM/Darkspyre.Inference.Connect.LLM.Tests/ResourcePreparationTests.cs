using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.Inference.Connect.LLM.Vertex;
using Darkspyre.Inference.Connect.Resources;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ResourcePreparationTests
{
    [Fact]
    public async Task ImportedDeferredReceiptConsumesOnceAndReturnsTheActualChatResult()
    {
        var handler = new ResourceResults();
        using var http = new HttpClient(handler);
        var source = new Source(ResourceResults.Connection());
        ILlmClient[] clients = [new XaiResponsesLlmClient(http)];
        await using var access = new LlmConnectionAccess(source, [], clients);
        await using var core = new LlmProviderService(source, clients, [], connectionAccess: access);
        await using var resources = new LlmResourceProviderService(access, core);
        var receipt = new LlmDeferredCompletion("request-1",
            new("https://api.x.ai/v1/chat/deferred-completion", "original-connection"));
        LlmDeferredCompletion imported = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(receipt, ConnectResourcesJson.LlmDeferredCompletion), ConnectResourcesJson.LlmDeferredCompletion)!;

        LlmDeferredResult result = await resources.GetDeferredAsync("selected-connection", imported);

        LlmChatResult chat = Assert.IsType<LlmDeferredCompleted>(result).Result;
        Assert.Equal("resource answer", chat.Text);
        Assert.Equal("observed-model", chat.ModelId);
        Assert.Equal(LlmCompletionStatus.Completed, chat.Status);
        Assert.Equal("stop", chat.FinishReason);
        Assert.Equal(5, chat.Usage!.TotalTokens);
        Assert.Empty(chat.GeneratedResources);
        Assert.Equal("original-connection", imported.Scope.Identity);
        Assert.Equal(new Uri("https://api.x.ai/v1/chat/deferred-completion/request-1"), Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task BatchChatResultsUseTheActualEnvelopeAndReturnOnlyTheRequestedPage()
    {
        var handler = new ResourceResults(batch: true);
        using var http = new HttpClient(handler);
        var source = new Source(ResourceResults.Connection());
        ILlmClient[] clients = [new XaiResponsesLlmClient(http)];
        await using var access = new LlmConnectionAccess(source, [], clients);
        await using var core = new LlmProviderService(source, clients, [], connectionAccess: access);
        await using var resources = new LlmResourceProviderService(access, core);
        var batch = new LlmBatch("batch-1", "batch", new("https://api.x.ai/v1/batches", "original-connection"),
            new(null, null, null, null, null));

        LlmBatchResultPage page = await resources.GetResultsAsync("selected-connection", batch,
            new(2, "input-cursor"), new(1024, 4096));

        Assert.Equal("next-cursor", page.PaginationToken);
        Assert.Equal(["row-1", "row-2"], page.Items.Select(item => item.CustomId));
        LlmChatResult chat = Assert.IsType<LlmBatchChatResult>(page.Items[0]).Result;
        Assert.Equal("resource answer", chat.Text);
        Assert.Equal("observed-model", chat.ModelId);
        Assert.Equal(LlmCompletionStatus.Completed, chat.Status);
        Assert.Equal(5, chat.Usage!.TotalTokens);
        Assert.Equal(InferenceFailureCode.ExecutionFailed, Assert.IsType<LlmBatchFailure>(page.Items[1]).Error.Code);
        Assert.Equal(new[]
        {
            new Uri("https://api.x.ai/v1/batches/batch-1"),
            new Uri("https://api.x.ai/v1/batches/batch-1/results?limit=2&pagination_token=input-cursor")
        }, handler.Requests);
    }

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

    [Theory]
    [InlineData("anthropic", false)]
    [InlineData("anthropic", true)]
    [InlineData("vertex-claude", false)]
    [InlineData("vertex-claude", true)]
    [InlineData("vertex-gemini", false)]
    [InlineData("vertex-gemini", true)]
    [InlineData("gemini-api", false)]
    [InlineData("gemini-api", true)]
    [InlineData("openai-responses", false)]
    [InlineData("openai-responses", true)]
    public async Task LogicalDocumentContextCanMoveFromGrokToAnInlineProviderWithoutChangingItsResources(string destination, bool generatedByTool)
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
            Entries = [new("prompt", "turn-1", new("user", "Read the reference")
                { Attachments = [new LlmResourceAttachment(reference), new LlmResourceAttachment(reference)] }),
                new("reply", "turn-1", new("assistant", "Created result.json"))]
        };
        if (generatedByTool)
        {
            context = context with
            {
                Entries = [context.Entries[0], context.Entries[1] with
                { Message = context.Entries[1].Message with { ToolCalls = [new LlmToolCall("create-1", "create_json", "{}")] } },
                    new("tool", "turn-1", new("user", string.Empty)
                    {
                        ToolResults = [new LlmToolResult("create-1", new() { Parts = [new LlmToolOutputSessionResource(generated)] })]
                    })]
            };
        }
        byte[] portable = JsonSerializer.SerializeToUtf8Bytes(context, ConnectLlmJson.AgentContext);
        var grok = new LlmConnection
        {
            Version = "grok",
            Transport = LlmTransport.XaiResponses,
            Xai = new(),
            Endpoint = "https://api.x.ai/v1",
            Credential = new LlmCredential { OAuth = new() { AccessToken = "token-grok" } }.Serialize()
        };
        using (var http = new HttpClient(new Uploads("grok")))
        await using (var files = new XaiFileClient(http))
        await using (LlmResourcePreparation prepared = await LlmResourcePreparation.PrepareAsync(grok,
            new() { ModelId = "grok-model", Resources = access, Messages = [.. context.Entries.Select(entry => entry.Message), new("user", "Compare the files")] },
            files, () => { }, CancellationToken.None))
        {
            Assert.IsType<LlmFileReference>(prepared.Request.Messages[0].Attachments[0]);
        }

        LlmConnection connection = InlineConnection(destination);
        bool streaming = destination != "openai-responses" || generatedByTool;
        if (!streaming) connection = connection with { CapabilityOverrides = new() { SupportsStreaming = false } };
        string model = InlineModel(destination);
        var handler = new Documents(destination: destination, streaming: streaming);
        using var targetHttp = new HttpClient(handler);
        LlmClientBase client = InlineClient(destination, targetHttp);
        LlmAgentContext restored = JsonSerializer.Deserialize(portable, ConnectLlmJson.AgentContext)!;
        var source = new Source(connection);
        await using var providers = new LlmProviderService(source, [client]);
        ILlmAgentClient agent = client switch
        {
            VertexLlmClient vertex => new ManagedVertexAgentClient(vertex, providers),
            GeminiApiLlmClient gemini => new ManagedGeminiApiAgentClient(gemini, providers),
            OpenAiResponsesLlmClient openai => new ManagedOpenAiResponsesAgentClient(openai, providers),
            _ => new ManagedAnthropicAgentClient((AnthropicMessagesLlmClient)client, providers)
        };
        await using var agents = new LlmAgentProviderService(source, [agent]);
        await using ILlmAgentSession session = await agents.StartSessionAsync("connection", new()
        {
            ModelId = model,
            SessionId = "session",
            Context = restored,
            UnboundContinuationState = LlmUnboundContinuationState.Omit,
            CallerServices = new() { Resources = access }
        });
        LlmAgentContext imported = await session.CaptureContextAsync();
        var events = new List<LlmAgentEvent>();
        await foreach (LlmAgentEvent item in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("Compare the files")] }))
            events.Add(item);
        Assert.Equal("session", session.SessionId);
        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        if (destination is "vertex-gemini" or "gemini-api" && generatedByTool)
        {
            Assert.Equal([("reference.txt", "reference text"), ("reference.txt", "reference text")], handler.Items);
            Assert.Contains("result.json", handler.ToolText, StringComparison.Ordinal);
            Assert.Contains("{\"answer\":42}", handler.ToolText, StringComparison.Ordinal);
        }
        else Assert.Equal([("reference.txt", "reference text"), ("reference.txt", "reference text"), ("result.json", "{\"answer\":42}")], handler.Items);
        Assert.Equal(4, access.Reads.Count);
        Assert.All(access.Reads, stream => Assert.False(stream.CanRead));
        Assert.Equal(9, imported.Revision);
        Assert.Equal(context.ResourceSelectionVersion, imported.ResourceSelectionVersion);
        Assert.Equal([generated, reference], imported.SelectedResources);
        Assert.IsType<LlmResourceAttachment>(imported.Entries[0].Message.Attachments[0]);
        LlmAgentContext captured = await session.CaptureContextAsync();
        Assert.Equal(context.ResourceSelectionVersion, captured.ResourceSelectionVersion);
        Assert.Equal([generated, reference], captured.SelectedResources);
        Assert.IsType<LlmResourceAttachment>(captured.Entries[0].Message.Attachments[0]);
        if (generatedByTool)
            Assert.IsType<LlmToolOutputSessionResource>(Assert.IsType<LlmToolResult>(Assert.Single(imported.Entries[2].Message.ToolResults)).Output.Parts[0]);
    }

    [Theory]
    [InlineData("result.json", false)]
    [InlineData("result.json", true)]
    [InlineData("result.pdf", true)]
    public async Task ClaudeCliChatCarriesSelectedLogicalDocumentsInItsActualInput(string fileName, bool generatedByTool)
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var generated = new InferenceResourceReference("workspace", "generated");
        byte[] bytes = fileName.EndsWith(".pdf", StringComparison.Ordinal)
            ? "%PDF-1.7\nactual generated content"u8.ToArray() : "{\"answer\":42}"u8.ToArray();
        var access = new Resources(new("workspace", "session", 4, [generated, reference]),
            [(reference, "reference.txt", "reference text"u8.ToArray()), (generated, fileName, bytes)]);
        LlmChatMessage[] messages =
        [
            new("user", "Read the reference")
            { Attachments = [new LlmResourceAttachment(reference), new LlmResourceAttachment(reference)] },
            new("assistant", "Created " + fileName)
        ];
        if (generatedByTool)
            messages = [messages[0], messages[1] with { ToolCalls = [new LlmToolCall("create-1", "create_file", "{}")] },
                new("user", string.Empty)
                { ToolResults = [new LlmToolResult("create-1", new() { Parts = [new LlmToolOutputSessionResource(generated)] })] }];
        var logical = new LlmChatRequest { ModelId = InlineModel("claude-cli"), Messages = [.. messages, new("user", "Compare the files")] };
        LlmChatRequest request = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(logical, ConnectLlmJson.ChatRequest),
            ConnectLlmJson.ChatRequest)!;
        var handler = new Documents();
        using var http = new HttpClient(handler);
        var runner = new CliDocuments();
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses());
        await using var providers = new LlmProviderService(new Source(InlineConnection("claude-cli")), [client]);
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in providers.StreamAsync("connection", request with { Resources = access })) events.Add(item);

        Assert.Equal("answer", Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Text);
        ClaudeCliInvocation invocation = Assert.Single(runner.Invocations);
        Assert.Contains("stream-json", invocation.Arguments);
        using JsonDocument input = JsonDocument.Parse(invocation.StandardInput!);
        JsonElement[] content = input.RootElement.GetProperty("message").GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal("user", input.RootElement.GetProperty("message").GetProperty("role").GetString());
        JsonElement[] documents = content.Where(part => part.GetProperty("type").GetString() == "document").ToArray();
        Assert.Equal(["reference.txt", "reference.txt", fileName], documents.Select(part => part.GetProperty("title").GetString()));
        Assert.All(documents.Take(2), part => Assert.Equal("reference text", part.GetProperty("source").GetProperty("data").GetString()));
        JsonElement source = documents[^1].GetProperty("source");
        Assert.Equal(fileName.EndsWith(".pdf", StringComparison.Ordinal) ? "base64" : "text", source.GetProperty("type").GetString());
        Assert.Equal(bytes, source.GetProperty("type").GetString() == "base64"
            ? Convert.FromBase64String(source.GetProperty("data").GetString()!)
            : System.Text.Encoding.UTF8.GetBytes(source.GetProperty("data").GetString()!));
        Assert.DoesNotContain("file_id", invocation.StandardInput!, StringComparison.Ordinal);
        if (generatedByTool)
            Assert.Contains("[TOOL RESULT id=create-1 status=ok]", content[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.IsType<LlmResourceAttachment>(request.Messages[0].Attachments[0]);
        if (generatedByTool)
            Assert.IsType<LlmToolOutputSessionResource>(Assert.IsType<LlmToolResult>(Assert.Single(request.Messages[2].ToolResults)).Output.Parts[0]);
        Assert.Equal([generated, reference], access.Selection.SelectedResources);
        Assert.Equal(4, access.Selection.Revision);
        Assert.Equal(2, access.Reads.Count);
        Assert.All(access.Reads, stream => Assert.False(stream.CanRead));
        Assert.Equal(2, access.ReleaseAttempts);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task ClaudeCliPlainTextLaneRefusesResourcesBeforeOpeningThemOrInvokingTheCli()
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var access = new Resources(new("workspace", "session", 1, [reference]),
            [(reference, "reference.txt", "reference text"u8.ToArray())]);
        LlmConnection connection = InlineConnection("claude-cli") with { CapabilityOverrides = new() { SupportsStreaming = false } };
        var handler = new Documents();
        using var http = new HttpClient(handler);
        var runner = new CliDocuments();
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses());
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (LlmStreamEvent _ in client.StreamAsync(connection,
                new() { ModelId = InlineModel("claude-cli"), Resources = access, Messages = [new("user", "Read the file")] })) { }
        });
        Assert.Empty(access.Reads);
        Assert.Empty(runner.Invocations);
        Assert.Equal(0, access.ReleaseAttempts);
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("anthropic", false)]
    [InlineData("anthropic", true)]
    [InlineData("openai-responses", false)]
    [InlineData("openai-responses", true)]
    [InlineData("claude-cli", false)]
    [InlineData("claude-cli", true)]
    public async Task InlineReaderCleanupRefusalPreventsSuccessAndPreservesAnIndependentOperationFailure(string destination, bool generationFails)
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var cleanup = new IOException("The reader could not be released.");
        var operation = new IOException("The provider request failed.");
        var access = new Resources(new("workspace", "session", 1, [reference]),
            [(reference, "reference.txt", "reference text"u8.ToArray())], cleanup);
        LlmConnection connection = InlineConnection(destination);
        using var http = new HttpClient(new Documents(generationFails ? operation : null, destination));
        LlmClientBase client = InlineClient(destination, http, generationFails ? operation : null);
        var events = new List<LlmStreamEvent>();
        Exception failure = (await Record.ExceptionAsync(async () =>
        {
            await foreach (LlmStreamEvent item in client.StreamAsync(connection,
                new() { ModelId = InlineModel(destination), MaxOutputTokens = destination == "claude-cli" ? null : 16, Resources = access, Messages = [new("user", "Read the file")] }))
                events.Add(item);
        }))!;
        Exception release;
        if (generationFails)
        {
            Exception[] causes = Assert.IsType<AggregateException>(failure).InnerExceptions.ToArray();
            Assert.Equal(2, causes.Length);
            Assert.Same(operation, Assert.IsType<InferenceResourceOperationException>(causes[0]).InnerException);
            release = causes[1];
        }
        else release = failure;
        InferenceResourceOperationException refused = Assert.IsType<InferenceResourceOperationException>(release);
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, refused.Failure.Phase);
        Assert.Equal(reference, refused.Failure.Reference);
        Assert.Same(cleanup, refused.InnerException);
        Assert.DoesNotContain(events, item => item is LlmChatCompletionEvent);
        Assert.Equal(1, access.ReleaseAttempts);
        using MemoryStream retained = Assert.Single(access.Reads);
        Assert.True(retained.CanRead);
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("vertex-claude")]
    [InlineData("vertex-gemini")]
    [InlineData("gemini-api")]
    [InlineData("openai-responses")]
    [InlineData("claude-cli")]
    public async Task ResourceBoundAccountingDoesNotIgnoreSelectedFilesOrOpenThem(string destination)
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var access = new Resources(new("workspace", "session", 1, [reference]),
            [(reference, "reference.txt", "reference text"u8.ToArray())]);
        LlmConnection connection = InlineConnection(destination);
        var handler = new Documents(destination: destination);
        using var http = new HttpClient(handler);
        LlmClientBase client = InlineClient(destination, http);
        var estimator = (ILlmContextEstimator)client;
        var request = new LlmChatRequest
        {
            ModelId = InlineModel(destination),
            MaxOutputTokens = destination == "claude-cli" ? null : 16,
            Resources = access,
            Messages = [new("user", "Read the file")]
        };
        Assert.Null(estimator.EstimateInputTokens(connection, request));
        Assert.Null(estimator.EstimateInputTokens(connection, request with
        { Messages = [new("user", "Read the file") { Attachments = [new LlmResourceAttachment(reference)] }] }));
        if (client is ILlmProviderTokenCounter counter)
            await Assert.ThrowsAsync<NotSupportedException>(() => counter.CountInputTokensAsync(connection, request));
        else
        {
            await using var providers = new LlmProviderService(new Source(connection), [client]);
            await Assert.ThrowsAsync<NotSupportedException>(() => providers.CountInputTokensAsync("connection", request));
        }
        Assert.Equal(0, handler.Requests);
        Assert.Empty(access.Reads);
        Assert.Equal(0, access.ReleaseAttempts);
    }

    private static string InlineModel(string destination) => destination switch
    {
        "vertex-gemini" => "google/gemini-3-flash-preview",
        "gemini-api" => "gemini-3-flash-preview",
        "openai-responses" => "gpt-4.1",
        "vertex-claude" => "anthropic/claude-sonnet-4-6",
        _ => "claude-sonnet-4-6"
    };

    private static LlmClientBase InlineClient(string destination, HttpClient http, Exception? failure = null) => destination switch
    {
        "anthropic" => new AnthropicMessagesLlmClient(http),
        "gemini-api" => new GeminiApiLlmClient(http),
        "openai-responses" => new OpenAiResponsesLlmClient(http),
        "claude-cli" => new ClaudeCliLlmClient(http, new CliDocuments(failure), new NoSessionProcesses()),
        _ => new VertexLlmClient(http, new VertexAuthenticationBoundary())
    };

    private static LlmConnection InlineConnection(string destination) => destination switch
    {
        "anthropic" => new()
        {
            Version = destination,
            Transport = LlmTransport.AnthropicMessages,
            Endpoint = "https://anthropic.example.test/v1",
            Credential = new LlmCredential { ApiKey = "test-key" }.Serialize()
        },
        "gemini-api" => new()
        {
            Version = destination,
            Transport = LlmTransport.GoogleGeminiApi,
            Endpoint = GeminiApiLlmClient.DefaultEndpoint,
            Credential = new LlmCredential { ApiKey = "test-key" }.Serialize()
        },
        "openai-responses" => new()
        {
            Version = destination,
            Transport = LlmTransport.OpenAiResponses,
            Endpoint = "https://api.openai.com/v1",
            Credential = new LlmCredential { ApiKey = "test-key" }.Serialize()
        },
        "claude-cli" => new()
        {
            Version = destination,
            Transport = LlmTransport.AnthropicClaudeCli,
            Endpoint = "claude"
        },
        _ => new()
        {
            Version = destination,
            Transport = LlmTransport.GoogleVertexAi,
            Endpoint = "https://aiplatform.googleapis.com",
            Vertex = new() { ProjectId = "project", Location = "global", AuthenticationMode = LlmGoogleAuthenticationMode.ApplicationDefault }
        }
    };

    private sealed class VertexAuthenticationBoundary : IVertexAuthentication
    {
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken) =>
            Task.FromResult(new VertexAccess("test-token", "test-scope"));
    }

    private sealed class CliDocuments(Exception? failure = null) : IClaudeCliRunner
    {
        public List<ClaudeCliInvocation> Invocations { get; } = [];

        public Task<ClaudeCliProcessResult> RunAsync(ClaudeCliInvocation invocation, CancellationToken cancellationToken = default)
        {
            Invocations.Add(invocation);
            if (failure is not null) throw failure;
            return Task.FromResult(new ClaudeCliProcessResult(0,
                """
                {"type":"result","subtype":"success","is_error":false,"result":"answer","session_id":"session-1","stop_reason":"end_turn","terminal_reason":"completed","usage":{"input_tokens":5,"output_tokens":2}}
                """, string.Empty));
        }
    }

    private sealed class NoSessionProcesses : IClaudeCliSessionProcessFactory
    {
        public Task<IClaudeCliSessionProcess> StartAsync(ClaudeCliSessionInvocation invocation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class Documents(Exception? failure = null, string destination = "anthropic", bool streaming = true) : HttpMessageHandler
    {
        public List<(string Name, string Text)> Items { get; } = [];
        public int Requests { get; private set; }
        public string ToolText { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.Method == HttpMethod.Get)
            {
                if (destination == "openai-responses")
                {
                    Assert.Equal(new Uri("https://api.openai.com/v1/models"), request.RequestUri);
                    Assert.Equal("test-key", request.Headers.Authorization!.Parameter);
                    return new(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"data\":[{\"id\":\"gpt-4.1\"}]}", System.Text.Encoding.UTF8, "application/json")
                    };
                }
                if (destination == "gemini-api")
                {
                    Assert.Equal(new Uri("https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000"), request.RequestUri);
                    Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
                    return new(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"models\":[{\"name\":\"models/gemini-3-flash-preview\",\"supportedGenerationMethods\":[\"generateContent\"]}]}",
                            System.Text.Encoding.UTF8, "application/json")
                    };
                }
                if (destination != "anthropic")
                {
                    string publisher = request.RequestUri!.AbsolutePath.Contains("/google/", StringComparison.Ordinal) ? "google" : "anthropic";
                    Assert.Equal(new Uri($"https://aiplatform.googleapis.com/v1beta1/publishers/{publisher}/models?pageSize=100"), request.RequestUri);
                    string model = publisher == "google" ? "gemini-3-flash-preview" : "claude-sonnet-4-6";
                    return new(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"{{\"publisherModels\":[{{\"name\":\"publishers/{publisher}/models/{model}\"}}]}}",
                            System.Text.Encoding.UTF8, "application/json")
                    };
                }
                Assert.Equal(new Uri("https://anthropic.example.test/v1/models?limit=1000"), request.RequestUri);
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[{\"id\":\"claude-sonnet-4-6\",\"display_name\":\"Claude Sonnet\"}],\"has_more\":false}",
                        System.Text.Encoding.UTF8, "application/json")
                };
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            if (destination == "anthropic")
            {
                Assert.Equal(new Uri("https://anthropic.example.test/v1/messages"), request.RequestUri);
                Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-api-key")));
            }
            else if (destination == "gemini-api")
                Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            else if (destination == "openai-responses")
                Assert.Equal("test-key", request.Headers.Authorization!.Parameter);
            else Assert.Equal("test-token", request.Headers.Authorization!.Parameter);
            using JsonDocument payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (destination == "openai-responses")
            {
                Assert.Equal(new Uri("https://api.openai.com/v1/responses"), request.RequestUri);
                Assert.Equal(streaming, payload.RootElement.GetProperty("stream").GetBoolean());
                foreach (JsonElement input in payload.RootElement.GetProperty("input").EnumerateArray())
                {
                    JsonElement parts;
                    if (input.TryGetProperty("type", out JsonElement type) && type.GetString() == "function_call_output")
                        parts = input.GetProperty("output");
                    else if (!input.TryGetProperty("content", out parts)) continue;
                    if (parts.ValueKind != JsonValueKind.Array) continue;
                    foreach (JsonElement part in parts.EnumerateArray())
                        if (part.GetProperty("type").GetString() == "input_file")
                        {
                            string data = part.GetProperty("file_data").GetString()!;
                            Assert.StartsWith("data:", data, StringComparison.Ordinal);
                            Assert.DoesNotContain("file_id", part.EnumerateObject().Select(property => property.Name));
                            Items.Add((part.GetProperty("filename").GetString()!, System.Text.Encoding.UTF8.GetString(
                                Convert.FromBase64String(data[(data.IndexOf(";base64,", StringComparison.Ordinal) + 8)..]))));
                        }
                }
                if (failure is not null) throw failure;
                const string response = "{\"id\":\"response-1\",\"model\":\"gpt-4.1\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"id\":\"message-1\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"done\"}]}],\"usage\":{\"input_tokens\":5,\"output_tokens\":1,\"total_tokens\":6}}";
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(streaming ? "data: {\"type\":\"response.completed\",\"response\":" + response + "}\n\n" : response,
                        System.Text.Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
                };
            }
            if (destination is "vertex-gemini" or "gemini-api")
            {
                Assert.Equal(new Uri(destination == "gemini-api"
                    ? "https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:streamGenerateContent?alt=sse"
                    : "https://aiplatform.googleapis.com/v1/projects/project/locations/global/publishers/google/models/gemini-3-flash-preview:streamGenerateContent?alt=sse"), request.RequestUri);
                foreach (JsonElement content in payload.RootElement.GetProperty("contents").EnumerateArray())
                    foreach (JsonElement part in content.GetProperty("parts").EnumerateArray())
                        if (part.TryGetProperty("inlineData", out JsonElement inline))
                        {
                            Assert.Equal("text/plain", inline.GetProperty("mimeType").GetString());
                            Items.Add((inline.GetProperty("displayName").GetString()!,
                                System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(inline.GetProperty("data").GetString()!))));
                        }
                        else if (part.TryGetProperty("functionResponse", out JsonElement function))
                            ToolText += function.GetProperty("response").GetProperty("output").GetString();
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent("data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"done\"}]},\"finishReason\":\"STOP\"}],\"modelVersion\":\"gemini-3-flash-preview\",\"usageMetadata\":{\"promptTokenCount\":5,\"candidatesTokenCount\":1,\"totalTokenCount\":6}}\n\n",
                        System.Text.Encoding.UTF8, "text/event-stream")
                };
            }
            if (destination == "vertex-claude")
                Assert.Equal(new Uri("https://aiplatform.googleapis.com/v1/projects/project/locations/global/publishers/anthropic/models/claude-sonnet-4-6:streamRawPredict"), request.RequestUri);
            foreach (JsonElement message in payload.RootElement.GetProperty("messages").EnumerateArray())
                foreach (JsonElement block in message.GetProperty("content").EnumerateArray())
                    foreach (JsonElement part in block.GetProperty("type").GetString() == "tool_result"
                        ? block.GetProperty("content").EnumerateArray() : new[] { block }.AsEnumerable())
                        if (part.GetProperty("type").GetString() == "document")
                        {
                            Assert.Equal("text", part.GetProperty("source").GetProperty("type").GetString());
                            Items.Add((part.GetProperty("title").GetString()!, part.GetProperty("source").GetProperty("data").GetString()!));
                        }
            if (failure is not null) throw failure;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"type\":\"message_start\",\"message\":{\"id\":\"message-1\",\"model\":\"claude-sonnet-4-6\",\"usage\":{\"input_tokens\":5,\"output_tokens\":0}}}\n\n"
                    + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":0}}\n\ndata: {\"type\":\"message_stop\"}\n\n",
                    System.Text.Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private sealed class ResourceResults(bool batch = false) : HttpMessageHandler
    {
        private const string Chat = """
            {"id":"completion-1","model":"observed-model","choices":[{"index":0,
            "message":{"role":"assistant","content":"resource answer"},"finish_reason":"stop"}],
            "usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}
            """;

        public List<Uri> Requests { get; } = [];

        public static LlmConnection Connection() => new()
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Xai = new(),
            Endpoint = "https://api.x.ai/v1",
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize(),
            Catalog = []
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("test-token", request.Headers.Authorization!.Parameter);
            Requests.Add(request.RequestUri!);
            if (!batch)
                return Task.FromResult(new HttpResponseMessage(Requests.Count == 1 ? HttpStatusCode.OK : HttpStatusCode.Gone)
                { Content = new StringContent(Requests.Count == 1 ? Chat : "{}") });
            Assert.InRange(Requests.Count, 1, 2);
            string body = Requests.Count == 1
                ? "{\"batch_id\":\"batch-1\",\"name\":\"batch\"}"
                : new JsonObject
                {
                    ["results"] = new JsonArray(
                        new JsonObject
                        {
                            ["batch_request_id"] = "row-1",
                            ["batch_result"] = new JsonObject { ["response"] = new JsonObject { ["chat_get_completion"] = JsonNode.Parse(Chat) } }
                        },
                        new JsonObject
                        {
                            ["batch_request_id"] = "row-2",
                            ["batch_result"] = new JsonObject { ["error"] = "The row failed." }
                        }),
                    ["pagination_token"] = "next-cursor"
                }.ToJsonString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    internal sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(connection);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

    internal sealed class Resources(InferenceResourceSelection selection,
        IReadOnlyList<(InferenceResourceReference Reference, string Name, byte[] Bytes)> content, Exception? releaseFailure = null,
        Task? readRelease = null) : IInferenceResourceAccess
    {
        public string WorkspaceId => Selection.WorkspaceId;
        public string SessionId => Selection.SessionId;
        public InferenceResourceSelection Selection { get; set; } = selection;
        public List<MemoryStream> Reads { get; } = [];
        public int ReleaseAttempts { get; private set; }

        public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default)
        {
            var file = content.Single(item => item.Reference == reference);
            return Task.FromResult<InferenceResourceDescriptor?>(new(reference, SessionId, file.Name, null, file.Bytes.Length));
        }

        public async Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default)
        {
            ReadEntered.TrySetResult();
            if (readRelease is not null) await readRelease;
            var stream = new MemoryStream(content.Single(item => item.Reference == reference).Bytes, writable: false);
            Reads.Add(stream);
            return new Read((await GetAsync(reference, cancellationToken))!, stream, this);
        }

        public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(Selection);
        public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private sealed class Read(InferenceResourceDescriptor resource, MemoryStream stream, Resources owner) : IInferenceResourceReadHandle
        {
            public InferenceResourceDescriptor Resource => resource;
            public Stream Content => stream;
            public ValueTask DisposeAsync()
            {
                owner.ReleaseAttempts++;
                return owner.ReleaseFailure is { } failure ? ValueTask.FromException(failure) : stream.DisposeAsync();
            }
        }
        private Exception? ReleaseFailure => releaseFailure;
        internal TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
