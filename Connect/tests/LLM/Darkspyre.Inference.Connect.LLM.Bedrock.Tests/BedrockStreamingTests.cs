using System.Reflection;
using System.Text.Json.Nodes;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.Documents;
using Amazon.Runtime.EventStreams;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockStreamingTests
{
    private const string Model = "us.anthropic.claude-sonnet-5-v1:0";
    private const string ToolInput = """{"path":"a.txt"}""";

    private static readonly LlmAwsCredentials Aws = new()
    {
        Region = "us-east-1",
        AccessKeyId = "test-access-key-id",
        SecretAccessKey = "test-secret-access-key"
    };

    [Fact]
    public async Task StreamsTextDeltasAndCompletesWithMetadataUsage()
    {
        var invoker = new StubConverseInvoker
        {
            Events =
            [
                new MessageStartEvent { Role = ConversationRole.Assistant },
                Text(0, "Hel"),
                Text(0, "lo"),
                new ContentBlockStopEvent { ContentBlockIndex = 0 },
                MessageStop("end_turn"),
                Metadata(12, 3)
            ]
        };

        List<LlmStreamEvent> events = await StreamAsync(invoker);

        Assert.Equal(["Hel", "lo"], events.OfType<LlmTextDeltaEvent>().Select(item => item.Delta));
        LlmChatResult result = Completion(events);
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("Hello", result.Text);
        Assert.Equal("stop", result.FinishReason);
        Assert.Equal((12, 3), (result.Usage!.InputTokens, result.Usage.OutputTokens));
        Assert.Equal("stream-request-1", result.Diagnostics.RequestId);
        Assert.Equal("[]", result.ContinuationState!.Payload);
        Assert.Empty(invoker.ConverseRequests);
        Assert.True(invoker.Disposed);
    }

    [Fact]
    public async Task ToolStartPrecedesInputDeltas()
    {
        var invoker = new StubConverseInvoker
        {
            Events =
            [
                ToolStart(0, "call-1", "read_file"),
                ToolDelta(0, """{"path":"""),
                ToolDelta(0, "\"a.txt\"}"),
                new ContentBlockStopEvent { ContentBlockIndex = 0 },
                MessageStop("tool_use"),
                Metadata(12, 3)
            ]
        };

        List<LlmStreamEvent> events = await StreamAsync(invoker);

        Assert.Equal(new LlmToolCallStartedEvent("call-1", "read_file", 0), events[0]);
        Assert.Equal(new LlmToolCallArgumentsDeltaEvent("call-1", """{"path":"""), events[1]);
        Assert.Equal(new LlmToolCallArgumentsDeltaEvent("call-1", "\"a.txt\"}"), events[2]);
        Assert.Equal(new LlmToolCallCompletedEvent(new LlmToolCall("call-1", "read_file", ToolInput)), events[3]);
        LlmChatResult result = Completion(events);
        Assert.Equal(LlmCompletionStatus.RequiresAction, result.Status);
        Assert.Equal("tool_calls", result.FinishReason);
        Assert.Equal([new LlmToolCall("call-1", "read_file", ToolInput)], result.ToolCalls);
        LlmProviderCapabilities capabilities = (await Client(invoker)).GetCapabilities(Connection(), Model);
        Assert.True(capabilities.Tools.SupportsArgumentStreaming);
        Assert.True(capabilities.SupportsStreamingUsage);
    }

    [Fact]
    public async Task ReasoningStreamsAndIsRetained()
    {
        var invoker = new StubConverseInvoker { Events = ReasoningTextAndTool() };

        List<LlmStreamEvent> events = await StreamAsync(invoker);

        Assert.Equal(new LlmReasoningDeltaEvent("Let me check.", false), events[0]);
        Assert.Equal(new LlmTextDeltaEvent("Checking."), events[1]);
        Assert.IsType<LlmToolCallStartedEvent>(events[2]);
        Assert.IsType<LlmToolCallArgumentsDeltaEvent>(events[3]);
        Assert.IsType<LlmToolCallCompletedEvent>(events[4]);
        LlmChatResult result = Completion(events);
        Assert.Equal(6, events.Count);
        Assert.Equal(
            JsonNode.Parse("""
                [{"type":"reasoning","text":"Let me check.","signature":"sig-1"},
                 {"type":"text","text":"Checking."},
                 {"type":"tool_use","id":"call-1","name":"read_file","input":{"path":"a.txt"}}]
                """)!.ToJsonString(),
            result.ContinuationState!.Payload);
        Assert.Equal((12L, 3L), (result.ContinuationState.InputTokens, result.ContinuationState.OutputTokens));

        List<ContentBlock> replayed = AssistantContent(BedrockLlmClient.BuildRequest(Connection(), FollowUp(result), Model));
        Assert.Equal(3, replayed.Count);
        Assert.Equal(("Let me check.", "sig-1"), (replayed[0].ReasoningContent.ReasoningText.Text, replayed[0].ReasoningContent.ReasoningText.Signature));
        Assert.Equal("Checking.", replayed[1].Text);
        Assert.Equal(("call-1", "read_file"), (replayed[2].ToolUse.ToolUseId, replayed[2].ToolUse.Name));
        Assert.Equal("a.txt", replayed[2].ToolUse.Input.AsDictionary()["path"].AsString());
    }

    [Fact]
    public async Task SignedReasoningWithEmptyTextIsRetainedWithoutAnEvent()
    {
        var invoker = new StubConverseInvoker
        {
            Events =
            [
                Reasoning(0, signature: "sig-1"),
                new ContentBlockStopEvent { ContentBlockIndex = 0 },
                Text(1, "Done."),
                MessageStop("end_turn"),
                Metadata(12, 3)
            ]
        };

        List<LlmStreamEvent> events = await StreamAsync(invoker);

        Assert.Empty(events.OfType<LlmReasoningDeltaEvent>());
        LlmChatResult result = Completion(events);
        Assert.Equal(
            """[{"type":"reasoning","text":"","signature":"sig-1"},{"type":"text","text":"Done."}]""",
            result.ContinuationState!.Payload);
        ContentBlock replayed = AssistantContent(BedrockLlmClient.BuildRequest(Connection(), FollowUp(result), Model))[0];
        Assert.Equal(("", "sig-1"), (replayed.ReasoningContent.ReasoningText.Text, replayed.ReasoningContent.ReasoningText.Signature));
    }

    [Fact]
    public async Task RedactedReasoningRoundTrips()
    {
        byte[] redacted = [1, 2, 3, 250];
        var invoker = new StubConverseInvoker
        {
            Events =
            [
                new ContentBlockDeltaEvent
                {
                    ContentBlockIndex = 0,
                    Delta = new ContentBlockDelta
                    {
                        ReasoningContent = new ReasoningContentBlockDelta { RedactedContent = new MemoryStream(redacted) }
                    }
                },
                Text(1, "Done."),
                MessageStop("end_turn"),
                Metadata(12, 3)
            ]
        };

        LlmChatResult result = Completion(await StreamAsync(invoker));

        Assert.True(JsonNode.DeepEquals(
            new JsonArray(
                new JsonObject { ["type"] = "redacted_reasoning", ["data"] = Convert.ToBase64String(redacted) },
                new JsonObject { ["type"] = "text", ["text"] = "Done." }),
            JsonNode.Parse(result.ContinuationState!.Payload)));
        ContentBlock replayed = AssistantContent(BedrockLlmClient.BuildRequest(Connection(), FollowUp(result), Model))[0];
        Assert.Equal(redacted, replayed.ReasoningContent.RedactedContent.ToArray());
    }

    [Theory]
    [InlineData("end_turn", LlmCompletionStatus.RequiresAction, "stop", null, true)]
    [InlineData("stop_sequence", LlmCompletionStatus.RequiresAction, "stop", null, true)]
    [InlineData("tool_use", LlmCompletionStatus.RequiresAction, "tool_calls", null, true)]
    [InlineData("max_tokens", LlmCompletionStatus.Incomplete, "length", "max_tokens", false)]
    [InlineData("model_context_window_exceeded", LlmCompletionStatus.Incomplete, "model_context_window_exceeded", "model_context_window_exceeded", false)]
    [InlineData("content_filtered", LlmCompletionStatus.Refused, "content_filter", null, false)]
    [InlineData("guardrail_intervened", LlmCompletionStatus.Refused, "content_filter", null, false)]
    [InlineData("malformed_model_output", LlmCompletionStatus.Failed, "malformed_model_output", null, false)]
    [InlineData("malformed_tool_use", LlmCompletionStatus.Failed, "malformed_tool_use", null, false)]
    [InlineData(null, LlmCompletionStatus.Unknown, null, null, false)]
    [InlineData("new_reason", LlmCompletionStatus.Unknown, "new_reason", null, false)]
    public async Task StopReasonMapping(
        string? stopReason,
        LlmCompletionStatus status,
        string? finishReason,
        string? incompleteReason,
        bool returnsCalls)
    {
        var invoker = new StubConverseInvoker
        {
            Events =
            [
                Text(0, "Reading."),
                ToolStart(1, "call-1", "read_file"),
                ToolDelta(1, ToolInput),
                new ContentBlockStopEvent { ContentBlockIndex = 1 },
                new MessageStopEvent { StopReason = stopReason is null ? null : new StopReason(stopReason) },
                Metadata(12, 3)
            ]
        };

        LlmChatResult result = Completion(await StreamAsync(invoker));

        Assert.Equal(status, result.Status);
        Assert.Equal(finishReason, result.FinishReason);
        Assert.Equal(incompleteReason, result.IncompleteReason);
        Assert.Equal(returnsCalls ? 1 : 0, result.ToolCalls.Count);
        Assert.Equal(status == LlmCompletionStatus.Refused ? "Reading." : null, result.Refusal);
    }

    [Fact]
    public async Task IncompleteReplyStateOmitsToolCallsItDoesNotReturn()
    {
        IReadOnlyList<IEventStreamEvent> events = [.. ReasoningTextAndTool().Select(item =>
            item is MessageStopEvent ? MessageStop("max_tokens") : item)];
        var invoker = new StubConverseInvoker { Events = events };

        LlmChatResult result = Completion(await StreamAsync(invoker));

        Assert.Equal(LlmCompletionStatus.Incomplete, result.Status);
        Assert.Empty(result.ToolCalls);
        Assert.DoesNotContain("tool_use", result.ContinuationState!.Payload, StringComparison.Ordinal);
        List<ContentBlock> replayed = AssistantContent(BedrockLlmClient.BuildRequest(Connection(), FollowUp(result), Model));
        Assert.Equal(2, replayed.Count);
    }

    [Fact]
    public async Task MalformedToolUseIsFailedWithoutCallsOrState()
    {
        var invoker = new StubConverseInvoker
        {
            Events =
            [
                ToolStart(0, "call-1", "read_file"),
                ToolDelta(0, "not json"),
                new ContentBlockStopEvent { ContentBlockIndex = 0 },
                MessageStop("malformed_tool_use"),
                Metadata(12, 3)
            ]
        };

        List<LlmStreamEvent> events = await StreamAsync(invoker);

        Assert.Empty(events.OfType<LlmToolCallCompletedEvent>());
        LlmChatResult result = Completion(events);
        Assert.Equal(LlmCompletionStatus.Failed, result.Status);
        Assert.Empty(result.ToolCalls);
        Assert.Null(result.ContinuationState);
    }

    [Fact]
    public async Task ToolUseStopWithInvalidInputThrows()
    {
        var invoker = new StubConverseInvoker
        {
            Events =
            [
                ToolStart(0, "call-1", "read_file"),
                ToolDelta(0, "[1]"),
                new ContentBlockStopEvent { ContentBlockIndex = 0 },
                MessageStop("tool_use")
            ]
        };

        LlmProviderException exception = await Assert.ThrowsAsync<LlmProviderException>(() => StreamAsync(invoker));

        Assert.Equal("Bedrock returned tool input that is not a JSON object.", exception.Message);
    }

    [Fact]
    public async Task StreamingOffCallsConverseAndUsesTheSharedOrder()
    {
        var invoker = new StubConverseInvoker
        {
            Response = StubConverseInvoker.Converse(
                [
                    new ContentBlock
                    {
                        ReasoningContent = new ReasoningContentBlock
                        {
                            ReasoningText = new ReasoningTextBlock { Text = "Let me check.", Signature = "sig-1" }
                        }
                    },
                    new ContentBlock { Text = "Checking." },
                    new ContentBlock
                    {
                        ToolUse = new ToolUseBlock
                        {
                            ToolUseId = "call-1",
                            Name = "read_file",
                            Input = new Document(new Dictionary<string, Document> { ["path"] = new Document("a.txt") })
                        }
                    }
                ],
                "tool_use",
                new TokenUsage { InputTokens = 12, OutputTokens = 3, TotalTokens = 15 })
        };
        LlmConnection connection = Connection() with { CapabilityOverrides = new() { SupportsStreaming = false } };

        List<LlmStreamEvent> events = await StreamAsync(invoker, connection);

        Assert.Single(invoker.ConverseRequests);
        Assert.Empty(invoker.StreamRequests);
        Assert.Equal(
            [typeof(LlmReasoningDeltaEvent), typeof(LlmTextDeltaEvent), typeof(LlmToolCallStartedEvent),
             typeof(LlmToolCallArgumentsDeltaEvent), typeof(LlmToolCallCompletedEvent), typeof(LlmChatCompletionEvent)],
            events.Select(item => item.GetType()));
        LlmChatResult converse = Completion(events);
        LlmChatResult streamed = Completion(await StreamAsync(new StubConverseInvoker { Events = ReasoningTextAndTool() }));
        Assert.Equal(
            (streamed.Text, streamed.Status, streamed.FinishReason, streamed.Usage, streamed.ContinuationState),
            (converse.Text, converse.Status, converse.FinishReason, converse.Usage, converse.ContinuationState));
        Assert.Equal(streamed.ToolCalls, converse.ToolCalls);
        Assert.Equal("converse-request-1", converse.Diagnostics.RequestId);
    }

    [Fact]
    public async Task StreamExceptionAfterDeltasPropagatesItsType()
    {
        var invoker = new StubConverseInvoker
        {
            Events = [Text(0, "Hel"), Text(0, "lo"), MessageStop("end_turn")],
            Failure = new ThrottlingException("Too many requests."),
            FailAfter = 1
        };
        var seen = new List<LlmStreamEvent>();

        await Assert.ThrowsAsync<ThrottlingException>(async () =>
        {
            await foreach (LlmStreamEvent item in (await Client(invoker)).StreamAsync(Connection(), Request()))
            {
                seen.Add(item);
            }
        });

        Assert.Equal([new LlmTextDeltaEvent("Hel")], seen);
        Assert.True(invoker.Disposed);
    }

    [Fact]
    public void UnwrapMapsCancellationAndTypedExceptions()
    {
        var validation = new ValidationException("Bad request.");
        var wrapped = new BedrockRuntimeEventStreamException("Stream failed.", validation);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Same(validation, BedrockConverseInvoker.Unwrap(wrapped, CancellationToken.None));
        OperationCanceledException canceled = Assert.IsType<OperationCanceledException>(
            BedrockConverseInvoker.Unwrap(wrapped, cancelled.Token));
        Assert.Equal(cancelled.Token, canceled.CancellationToken);
        var untyped = new BedrockRuntimeEventStreamException("Stream failed.", new IOException("reset"));
        Assert.Same(untyped, BedrockConverseInvoker.Unwrap(untyped, CancellationToken.None));
    }

    [Fact]
    public async Task MissingMessageStopThrows()
    {
        var invoker = new StubConverseInvoker { Events = [Text(0, "Hello"), Metadata(12, 3)] };

        LlmProviderException exception = await Assert.ThrowsAsync<LlmProviderException>(() => StreamAsync(invoker));

        Assert.Equal("Bedrock ended the stream without a message-stop event.", exception.Message);
    }

    [Fact]
    public async Task ToolInputWithoutStartThrows()
    {
        var invoker = new StubConverseInvoker { Events = [ToolDelta(0, ToolInput), MessageStop("tool_use")] };

        LlmProviderException exception = await Assert.ThrowsAsync<LlmProviderException>(() => StreamAsync(invoker));

        Assert.Equal("Bedrock streamed tool input before the tool's start event.", exception.Message);
    }

    [Fact]
    public async Task MixedBlockKindsThrow()
    {
        var invoker = new StubConverseInvoker { Events = [Text(0, "Hello"), Reasoning(0, text: "Hmm"), MessageStop("end_turn")] };

        LlmProviderException exception = await Assert.ThrowsAsync<LlmProviderException>(() => StreamAsync(invoker));

        Assert.Equal("Bedrock streamed mixed content in one block.", exception.Message);
    }

    [Fact]
    public void CountsOnlyStateStillDecodes()
    {
        var state = new LlmContinuationState
        {
            ImplementationId = ProviderContinuationStateCodec.ImplementationId(LlmTransport.AwsBedrockConverse),
            ModelId = Model,
            ScopeId = ProviderContinuationStateCodec.Scope(Connection()),
            Payload = "[]",
            InputTokens = 1200,
            OutputTokens = 80
        };
        LlmChatRequest request = Request() with
        {
            Messages =
            [
                new LlmChatMessage("user", "hello"),
                new LlmChatMessage("assistant", "done") { ContinuationState = state },
                new LlmChatMessage("user", "thanks")
            ]
        };

        ContentBlock sent = Assert.Single(AssistantContent(BedrockLlmClient.BuildRequest(Connection(), request, Model)));

        Assert.Equal("done", sent.Text);
    }

    [Fact]
    public async Task ReasoningStateMustMatchTheVisibleMessage()
    {
        LlmChatResult result = Completion(await StreamAsync(new StubConverseInvoker { Events = ReasoningTextAndTool() }));
        LlmChatRequest followUp = FollowUp(result);
        LlmChatMessage assistant = followUp.Messages[1];
        var invoker = new StubConverseInvoker { Events = ReasoningTextAndTool() };
        BedrockLlmClient client = await Client(invoker);

        await Assert.ThrowsAsync<ArgumentException>(() => CollectAsync(client.StreamAsync(Connection(), followUp with
        {
            Messages = [followUp.Messages[0], assistant with { Content = "Changed." }, followUp.Messages[2]]
        })));
        await Assert.ThrowsAsync<ArgumentException>(() => CollectAsync(client.StreamAsync(Connection(), followUp with
        {
            Messages =
            [
                followUp.Messages[0],
                assistant with { ToolCalls = [new LlmToolCall("call-1", "read_file", """{"path":"b.txt"}""")] },
                followUp.Messages[2]
            ]
        })));

        Assert.Empty(invoker.StreamRequests);
        Assert.Empty(invoker.ConverseRequests);
    }

    [Fact]
    public void StreamRequestCopiesEveryBuiltProperty()
    {
        LlmChatRequest request = Request() with
        {
            Instructions = "Answer briefly.",
            MaxOutputTokens = 64,
            Temperature = 0.5,
            Tools = [new LlmToolDefinition
            {
                Name = "read_file",
                Description = "Reads one file.",
                InputSchema = System.Text.Json.JsonElement.Parse("""{"type":"object"}""")
            }],
            ToolChoice = new LlmToolChoice { Mode = LlmToolChoiceMode.Auto }
        };
        ConverseRequest built = BedrockLlmClient.BuildRequest(Connection(), request, Model);

        ConverseStreamRequest stream = BedrockLlmClient.ToStreamRequest(built);

        int compared = 0;
        foreach (PropertyInfo property in typeof(ConverseRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            PropertyInfo? counterpart = typeof(ConverseStreamRequest).GetProperty(property.Name);
            object? value = property.GetValue(built);
            if (counterpart is null || counterpart.PropertyType != property.PropertyType
                || value is null || value is Document { } document && document.IsNull())
            {
                continue;
            }
            Assert.Equal(value, counterpart.GetValue(stream));
            compared++;
        }
        Assert.Equal(6, compared);
    }

    private static IReadOnlyList<IEventStreamEvent> ReasoningTextAndTool() =>
    [
        new MessageStartEvent { Role = ConversationRole.Assistant },
        Reasoning(0, text: "Let me check."),
        Reasoning(0, signature: "sig-1"),
        new ContentBlockStopEvent { ContentBlockIndex = 0 },
        Text(1, "Checking."),
        new ContentBlockStopEvent { ContentBlockIndex = 1 },
        ToolStart(2, "call-1", "read_file"),
        ToolDelta(2, ToolInput),
        new ContentBlockStopEvent { ContentBlockIndex = 2 },
        MessageStop("tool_use"),
        Metadata(12, 3)
    ];

    private static ContentBlockDeltaEvent Text(int index, string text) => new()
    {
        ContentBlockIndex = index,
        Delta = new ContentBlockDelta { Text = text }
    };

    private static ContentBlockDeltaEvent Reasoning(int index, string? text = null, string? signature = null) => new()
    {
        ContentBlockIndex = index,
        Delta = new ContentBlockDelta
        {
            ReasoningContent = new ReasoningContentBlockDelta { Text = text, Signature = signature }
        }
    };

    private static ContentBlockStartEvent ToolStart(int index, string id, string name) => new()
    {
        ContentBlockIndex = index,
        Start = new ContentBlockStart { ToolUse = new ToolUseBlockStart { ToolUseId = id, Name = name } }
    };

    private static ContentBlockDeltaEvent ToolDelta(int index, string input) => new()
    {
        ContentBlockIndex = index,
        Delta = new ContentBlockDelta { ToolUse = new ToolUseBlockDelta { Input = input } }
    };

    private static MessageStopEvent MessageStop(string reason) => new() { StopReason = new StopReason(reason) };

    private static ConverseStreamMetadataEvent Metadata(int input, int output) => new()
    {
        Usage = new TokenUsage { InputTokens = input, OutputTokens = output, TotalTokens = input + output }
    };

    private static LlmChatResult Completion(List<LlmStreamEvent> events) =>
        Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;

    private static List<ContentBlock> AssistantContent(ConverseRequest request) =>
        Assert.Single(request.Messages, message => message.Role == ConversationRole.Assistant).Content;

    /// <summary>The next request after a reply, carrying the reply as the host stores it and a tool result.</summary>
    private static LlmChatRequest FollowUp(LlmChatResult result) => Request() with
    {
        Messages =
        [
            new LlmChatMessage("user", "Read a.txt."),
            new LlmChatMessage("assistant", result.Text)
            {
                ToolCalls = result.ToolCalls,
                ContinuationState = result.ContinuationState
            },
            result.ToolCalls.Count > 0
                ? new LlmChatMessage("user", string.Empty)
                {
                    ToolResults = [new LlmToolResult("call-1", LlmToolOutput.FromText("contents"))]
                }
                : new LlmChatMessage("user", "Thanks.")
        ]
    };

    private static async Task<List<LlmStreamEvent>> StreamAsync(StubConverseInvoker invoker, LlmConnection? connection = null) =>
        await CollectAsync((await Client(invoker)).StreamAsync(connection ?? Connection(), Request()));

    private static async Task<List<LlmStreamEvent>> CollectAsync(IAsyncEnumerable<LlmStreamEvent> stream)
    {
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in stream)
        {
            events.Add(item);
        }
        return events;
    }

    /// <summary>A client whose logging guard has already verified the scope, so no control-plane call is made.</summary>
    private static async Task<BedrockLlmClient> Client(StubConverseInvoker invoker)
    {
        var guard = new BedrockLoggingGuard();
        await guard.EnsureLoggingDisabledAsync(
            BedrockClientFactory.ScopeKey(Aws),
            Aws.Region!,
            _ => Task.FromResult<Amazon.Bedrock.Model.LoggingConfig?>(null));
        return new BedrockLlmClient(new BedrockClientFactory(), guard, invoker);
    }

    private static LlmConnection Connection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.AwsBedrockConverse,
        Endpoint = "aws://bedrock",
        Credential = new LlmCredential { Aws = Aws }.Serialize()
    };

    private static LlmChatRequest Request() => new()
    {
        ModelId = Model,
        Messages = [new LlmChatMessage("user", "Read a.txt.")]
    };
}
