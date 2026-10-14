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
    [InlineData("000000206674797068656963000000006d69663168656963")]
    [InlineData("00000010667479706d703432000000006d69663168656963")]
    public void ImageMimeDetectionDoesNotReadCompatibleBrandsOutsideTheFileTypeBox(string hex)
    {
        var image = new LlmAttachment { Kind = LlmBinaryKind.Image, FileName = "photo.heic", Data = Convert.FromHexString(hex) };
        Assert.Throws<NotSupportedException>(() => LlmAttachments.ResolveMediaType(image));
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
        LlmChatRequest request = logical;
        var handler = new Documents();
        using var http = new HttpClient(handler);
        var runner = new CliDocuments();
        var catalog = new CatalogProcess();
        var client = new ClaudeCliLlmClient(http, runner, catalog);
        await using var providers = new LlmProviderService(new Source(InlineConnection("claude-cli")), [client]);
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in providers.StreamAsync("connection", request with { Resources = access })) events.Add(item);

        Assert.Equal("answer", Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Text);
        Assert.True(catalog.Disposed);
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
    public async Task ClaudeCliPlainTextLaneLeavesOutResourcesWithANotice()
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var access = new Resources(new("workspace", "session", 1, [reference]),
            [(reference, "reference.txt", "reference text"u8.ToArray())]);
        LlmConnection connection = InlineConnection("claude-cli") with { CapabilityOverrides = new() { SupportsStreaming = InferenceSupport.Unsupported } };
        var handler = new Documents();
        using var http = new HttpClient(handler);
        var runner = new CliDocuments();
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses());
        await foreach (LlmStreamEvent _ in client.StreamAsync(connection,
            new() { ModelId = InlineModel("claude-cli"), Resources = access, Messages = [new("user", "Read the file")] })) { }
        ClaudeCliInvocation invocation = Assert.Single(runner.Invocations);
        Assert.Contains("This content was not supplied to the model.", string.Join("\n", [.. invocation.Arguments, invocation.StandardInput ?? ""]), StringComparison.Ordinal);
        Assert.DoesNotContain("reference text", string.Join("\n", [.. invocation.Arguments, invocation.StandardInput ?? ""]), StringComparison.Ordinal);
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
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
        var resources = await ResourceSizes.ResolveAsync(access, [reference], CancellationToken.None);
        long withoutResource = estimator.EstimateInputTokens(connection, request with { Resources = null }, ResourceSizes.None)!.Value.Tokens;
        long resourceTokens = AttachmentTokens.Estimate(null, "reference text"u8.Length);
        // The selected file counts once, whether only the selection or also an attachment names it.
        Assert.Equal(withoutResource + resourceTokens, estimator.EstimateInputTokens(connection, request, resources)!.Value.Tokens);
        Assert.Equal(withoutResource + resourceTokens, estimator.EstimateInputTokens(connection, request with
        { Messages = [new("user", "Read the file") { Attachments = [new LlmResourceAttachment(reference)] }] }, resources)!.Value.Tokens);
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

    private static ClaudeCliLlmClient InlineClient(string destination, HttpClient http, Exception? failure = null) => destination switch
    {
        _ => new ClaudeCliLlmClient(http, new CliDocuments(failure), new NoSessionProcesses())
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

    private sealed class CatalogProcess : IClaudeCliSessionProcessFactory, IClaudeCliSessionProcess
    {
        private string? _reply;
        public bool Disposed { get; private set; }

        public Task<IClaudeCliSessionProcess> StartAsync(ClaudeCliSessionInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult<IClaudeCliSessionProcess>(this);

        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement request = document.RootElement;
            Assert.Equal("control_request", request.GetProperty("type").GetString());
            Assert.Equal("initialize", request.GetProperty("request").GetProperty("subtype").GetString());
            _reply = new JsonObject
            {
                ["type"] = "control_response",
                ["response"] = new JsonObject
                {
                    ["subtype"] = "success",
                    ["request_id"] = request.GetProperty("request_id").GetString(),
                    ["response"] = new JsonObject
                    {
                        ["models"] = new JsonArray(new JsonObject
                        { ["value"] = InlineModel("claude-cli"), ["displayName"] = "Claude Sonnet" })
                    }
                }
            }.ToJsonString();
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
        {
            string? reply = _reply;
            _reply = null;
            return ValueTask.FromResult(reply);
        }

        public Task<ClaudeCliProcessResult> WaitForExitAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Catalog discovery must consume the initialization reply.");

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
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
        public Func<InferenceResourceCreate, IInferenceResourceWriteHandle>? WriteFactory { get; init; }

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
                return owner.ReleaseFailure is { } failure ? ValueTask.FromException(failure) : stream.DisposeAsync();
            }
        }
        private Exception? ReleaseFailure => releaseFailure;
        internal TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class GeneratedWrite(string workspace, InferenceResourceCreate create, Action afterCommit) : IInferenceResourceWriteHandle
    {
        private readonly MemoryStream _content = new();
        public InferenceResourceReference Reference { get; } = new(workspace, "generated");
        public Stream Content => _content;
        public byte[] CommittedBytes { get; private set; } = [];
        public bool Disposed { get; private set; }

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

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return _content.DisposeAsync();
        }
    }
}
