using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.BedrockAgentCore;
using Amazon.BedrockAgentCore.Model;
using Amazon.Runtime.EventStreams;
using Darkspyre.Inference.Connect.LLM.Bedrock;
using Darkspyre.Inference.Connect.LLM.Tests;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockAgentCoreTests
{
    [Fact]
    public async Task NativeContextAndInstructionRejectionsDoNotInvokeHarness()
    {
        var invoker = new StubInvoker();
        var client = new BedrockAgentCoreLlmClient(invoker);
        var options = Options(LlmAgentSandboxMode.WorkspaceWrite, LlmAgentApprovalPolicy.OnRequest);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.StartSessionAsync("connection", Connection(), options with { ThreadId = "host-id" }));
        await using var session = await client.StartSessionAsync("connection", Connection(), options);
        await AgentContractTests.AssertNativeOperationsAsync(session);
        Assert.Empty(invoker.Requests);
        Assert.False((await session.GetCapabilitiesAsync()).SupportsInterrupt);
    }

    [Fact]
    public void CapabilitiesDescribeHarnessAgentLaneWithoutAttachments()
    {
        var client = new BedrockAgentCoreLlmClient(new StubInvoker());

        LlmProviderCapabilities capabilities = client.GetCapabilities(Connection());

        Assert.True(capabilities.SupportsStreaming);
        Assert.True(capabilities.SupportsStreamingUsage);
        Assert.True(capabilities.SupportsAgentSessions);
        Assert.True(capabilities.SupportsAgentToolUse);
        Assert.False(capabilities.SupportsToolUse);
        Assert.False(capabilities.SupportsModelListing);
        Assert.False(capabilities.SupportsPromptImages);
        Assert.False(capabilities.SupportsPromptDocuments);
        Assert.Equal(capabilities.RichContentSupported, capabilities.Agent.RichContentSupported);
        Assert.Equal(LlmRichContentCarriage.Unsupported, capabilities.RichContentSupported.Images);
        Assert.Equal(LlmRichContentCarriage.Unsupported, capabilities.RichContentSupported.PdfDocuments);
        Assert.Equal(LlmRichContentCarriage.Text, capabilities.RichContentSupported.TextDocuments);
        Assert.Equal(LlmRichContentCarriage.Native, capabilities.RichContentSupported.ErrorFlag);
        Assert.False(capabilities.RichContentSupported.AllowsEmpty);
    }

    [Fact]
    public void ConnectionRequiresHarnessArnAndMatchingAwsRegion()
    {
        var client = new BedrockAgentCoreLlmClient(new StubInvoker());

        Assert.Throws<ArgumentException>(() => client.GetCapabilities(
            Connection() with { Endpoint = "https://bedrock-agentcore.us-east-1.amazonaws.com" }));
        string credential = new LlmCredential
        {
            Aws = new LlmAwsCredentials
            {
                Region = "us-west-2",
                Profile = "test-profile"
            }
        }.Serialize();
        Assert.Throws<ArgumentException>(() => client.GetCapabilities(
            Connection() with { Credential = credential }));
    }

    [Fact]
    public void ChatRequestMapsHarnessMessagesAndDisablesTools()
    {
        InvokeHarnessRequest request = BedrockAgentCoreLlmClient.BuildChatRequest(
            Connection(),
            new LlmChatRequest
            {
                ModelId = "anthropic.claude-test",
                Instructions = "top-level",
                MaxOutputTokens = 300,
                Messages =
                [
                    new LlmChatMessage("system", "inline system"),
                    new LlmChatMessage("user", "hello"),
                    new LlmChatMessage("assistant", "hi")
                ]
            });

        Assert.Equal(HarnessArn, request.HarnessArn);
        Assert.StartsWith("darkspyre-inference-connect-", request.RuntimeSessionId, StringComparison.Ordinal);
        Assert.InRange(request.RuntimeSessionId.Length, 33, 100);
        Assert.Empty(request.AllowedTools);
        Assert.Equal(300, request.MaxTokens);
        Assert.Equal(
            ["top-level", "inline system"],
            request.SystemPrompt.Select(block => block.Text));
        Assert.Equal([HarnessConversationRole.User, HarnessConversationRole.Assistant],
            request.Messages.Select(message => message.Role));
        Assert.Equal("hello", request.Messages[0].Content[0].Text);
        Assert.Equal("anthropic.claude-test", request.Model.BedrockModelConfig.ModelId);
    }

    [Fact]
    public async Task ChatLaneStreamsTextUsageAndCompletion()
    {
        var invoker = new StubInvoker(
        [
            new HarnessContentBlockDeltaEvent
            {
                ContentBlockIndex = 0,
                Delta = new HarnessContentBlockDelta { Text = "Hello" }
            },
            new HarnessMessageStopEvent { StopReason = HarnessStopReason.End_turn },
            new HarnessMetadataEvent
            {
                Usage = new HarnessTokenUsage
                {
                    InputTokens = 4,
                    OutputTokens = 1,
                    TotalTokens = 5
                }
            }
        ]);
        var client = new BedrockAgentCoreLlmClient(invoker);

        LlmStreamEvent[] events = await ToArrayAsync(client.StreamAsync(
            Connection(),
            new LlmChatRequest
            {
                ModelId = BedrockAgentCoreLlmClient.DefaultModelId,
                Messages = [new LlmChatMessage("user", "Say hello")]
            }));

        Assert.Equal("Hello", Assert.IsType<LlmTextDeltaEvent>(events[0]).Delta);
        LlmChatResult result = Assert.IsType<LlmCompletionEvent>(events[^1]).Result;
        Assert.Equal("Hello", result.Text);
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal(new LlmChatUsage(4, 1, 5), result.Usage);
        Assert.Null(invoker.Requests[0].Model);
    }

    [Fact]
    public async Task ChatLaneRejectsStreamWithoutTerminalMessageStop()
    {
        var client = new BedrockAgentCoreLlmClient(new StubInvoker([]));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ToArrayAsync(client.StreamAsync(
                Connection(),
                new LlmChatRequest
                {
                    ModelId = BedrockAgentCoreLlmClient.DefaultModelId,
                    Messages = [new LlmChatMessage("user", "Hello")]
                })));
    }

    [Fact]
    public async Task AgentSessionExecutesInlineToolAndContinuesSameRuntimeSession()
    {
        var invoker = new StubInvoker(
        [
            new HarnessContentBlockStartEvent
            {
                ContentBlockIndex = 0,
                Start = new HarnessContentBlockStart
                {
                    ToolUse = new HarnessToolUseBlockStart
                    {
                        ToolUseId = "call-1",
                        Name = "get_weather",
                        Type = HarnessToolUseType.Tool_use
                    }
                }
            },
            new HarnessContentBlockDeltaEvent
            {
                ContentBlockIndex = 0,
                Delta = new HarnessContentBlockDelta
                {
                    ToolUse = new HarnessToolUseBlockDelta
                    {
                        Input = "{\"city\":\"Chicago\"}"
                    }
                }
            },
            new HarnessMessageStopEvent { StopReason = HarnessStopReason.Tool_use }
        ],
        [
            new HarnessContentBlockDeltaEvent
            {
                ContentBlockIndex = 0,
                Delta = new HarnessContentBlockDelta { Text = "It is 72 F." }
            },
            new HarnessMessageStopEvent { StopReason = HarnessStopReason.End_turn }
        ]);
        using JsonDocument schema = JsonDocument.Parse(
            """{"type":"object","properties":{"city":{"type":"string"}}}""");
        var calls = new List<LlmAgentToolCall>();
        var client = new BedrockAgentCoreLlmClient(invoker);
        await using ILlmAgentSession session = await client.StartSessionAsync(
            "connection", Connection(),
            new LlmAgentSessionOptions
            {
                ModelId = BedrockAgentCoreLlmClient.DefaultModelId,
                WorkingDirectory = "/not-sent-to-agentcore",
                Instructions = "Be concise.",
                Sandbox = LlmAgentSandboxMode.ReadOnly,
                ApprovalPolicy = LlmAgentApprovalPolicy.Never,
                Tools =
                [
                    new LlmAgentToolDefinition
                    {
                        Name = "get_weather",
                        Description = "Get current weather.",
                        InputSchema = schema.RootElement.Clone()
                    }
                ],
                ToolHandler = (call, _) =>
                {
                    calls.Add(call);
                    // The route carries no images; the image is omitted and the tool still succeeds.
                    return ValueTask.FromResult(new LlmToolOutput
                    {
                        Parts =
                        [
                            new LlmToolOutputText("72 F"),
                            new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", new byte[] { 0x89 }, "radar.png")
                        ]
                    });
                }
            });

        LlmAgentEvent[] events = await ToArrayAsync(session.RunTurnAsync(
            new LlmAgentTurnRequest
            {
                Input = [new LlmAgentTextContent("Weather in Chicago?")]
            }));

        Assert.Equal(2, invoker.Requests.Count);
        Assert.Equal(session.SessionId, invoker.Requests[0].RuntimeSessionId);
        Assert.Equal(session.SessionId, invoker.Requests[1].RuntimeSessionId);
        Assert.Equal(["get_weather"], invoker.Requests[0].AllowedTools);
        HarnessTool tool = Assert.Single(invoker.Requests[0].Tools);
        Assert.Equal(HarnessToolType.Inline_function, tool.Type);
        Assert.Equal("get_weather", tool.Name);
        Assert.Equal("Be concise.", Assert.Single(invoker.Requests[1].SystemPrompt).Text);

        Assert.Equal(2, invoker.Requests[1].Messages.Count);
        HarnessToolUseBlock sentCall = Assert.Single(
            invoker.Requests[1].Messages[0].Content).ToolUse;
        Assert.Equal("call-1", sentCall.ToolUseId);
        Assert.Equal("Chicago", sentCall.Input.AsDictionary()["city"].AsString());
        HarnessToolResultBlock sentResult = Assert.Single(
            invoker.Requests[1].Messages[1].Content).ToolResult;
        Assert.Equal("call-1", sentResult.ToolUseId);
        Assert.Equal(HarnessToolUseStatus.Success, sentResult.Status);
        Assert.Equal("72 F", Assert.Single(sentResult.Content).Text);

        LlmAgentToolCall hostCall = Assert.Single(calls);
        Assert.Equal("get_weather", hostCall.Name);
        Assert.Equal("Chicago", hostCall.Arguments.GetProperty("city").GetString());
        Assert.Contains(events, item => item is LlmAgentToolEvent
        {
            Tool: "get_weather",
            Status: "completed",
            Success: true
        });
        Assert.Equal("It is 72 F.", Assert.Single(
            events.OfType<LlmAgentMessageCompletedEvent>()).Text);
        Assert.Equal(LlmAgentTurnStatus.Completed, Assert.Single(
            events.OfType<LlmAgentTurnCompletedEvent>()).Status);
    }

    [Fact]
    public async Task EveryNonFullSandboxRestrictsHarnessManagedTools()
    {
        var client = new BedrockAgentCoreLlmClient(new StubInvoker());
        await using ILlmAgentSession restricted = await client.StartSessionAsync(
            "connection", Connection(),
            Options(
                LlmAgentSandboxMode.ReadOnly,
                LlmAgentApprovalPolicy.Never));
        await using ILlmAgentSession workspace = await client.StartSessionAsync(
            "connection", Connection(),
            Options(
                LlmAgentSandboxMode.WorkspaceWrite,
                LlmAgentApprovalPolicy.Never));
        await using ILlmAgentSession unrestricted = await client.StartSessionAsync(
            "connection", Connection(),
            Options(
                LlmAgentSandboxMode.DangerFullAccess,
                LlmAgentApprovalPolicy.Never));

        var restrictedSession = Assert.IsType<BedrockAgentCoreSession>(restricted);
        var workspaceSession = Assert.IsType<BedrockAgentCoreSession>(workspace);
        var unrestrictedSession = Assert.IsType<BedrockAgentCoreSession>(unrestricted);
        Assert.Empty(restrictedSession.BuildRequest(
            UserMessages(),
            BedrockAgentCoreLlmClient.DefaultModelId).AllowedTools);
        Assert.Empty(workspaceSession.BuildRequest(
            UserMessages(),
            BedrockAgentCoreLlmClient.DefaultModelId).AllowedTools);
        Assert.Null(unrestrictedSession.BuildRequest(
            UserMessages(),
            BedrockAgentCoreLlmClient.DefaultModelId).AllowedTools);
    }

    [Fact]
    public async Task FullAccessAgentRejectsDisabledNetwork()
    {
        var client = new BedrockAgentCoreLlmClient(new StubInvoker());

        await Assert.ThrowsAsync<NotSupportedException>(() => client.StartSessionAsync(
            "connection", Connection(),
            Options(
                LlmAgentSandboxMode.DangerFullAccess,
                LlmAgentApprovalPolicy.Never) with
            {
                NetworkAllowed = false
            }));
    }

    [Fact]
    public async Task AgentLaneRejectsUnsupportedAttachmentBeforeInvocation()
    {
        var invoker = new StubInvoker();
        var client = new BedrockAgentCoreLlmClient(invoker);
        await using ILlmAgentSession session = await client.StartSessionAsync(
            "connection", Connection(),
            Options(LlmAgentSandboxMode.ReadOnly, LlmAgentApprovalPolicy.Never));

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await ToArrayAsync(session.RunTurnAsync(new LlmAgentTurnRequest
            {
                Input = [new LlmAgentTextContent("Inspect this")],
                Attachments =
                [
                    new LlmAttachment
                    {
                        Kind = LlmAttachmentKind.Document,
                        FileName = "notes.txt",
                        Data = "notes"u8.ToArray()
                    }
                ]
            })));
        Assert.Empty(invoker.Requests);
    }

    [Fact]
    public async Task ResumeUsesExistingRuntimeSessionAndUnsupportedOperationsFailExplicitly()
    {
        const string sessionId = "session_123456789012345678901234567890";
        var client = new BedrockAgentCoreLlmClient(new StubInvoker());

        await using ILlmAgentSession session = await client.ResumeSessionAsync(
            "connection", Connection(),
            sessionId);

        Assert.Equal(sessionId, session.ThreadId);
        Assert.Equal(sessionId, session.SessionId);
        Assert.IsNotAssignableFrom<ILlmAgentForkClient>(client);
    }

    private const string HarnessArn =
        "arn:aws:bedrock-agentcore:us-east-1:123456789012:harness/test-harness";

    private static LlmConnection Connection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.AwsBedrockAgentCore,
        Endpoint = HarnessArn,
        Credential = new LlmCredential
        {
            Aws = new LlmAwsCredentials
            {
                Region = "us-east-1",
                Profile = "test-profile"
            }
        }.Serialize()
    };

    private static LlmAgentSessionOptions Options(
        LlmAgentSandboxMode sandbox,
        LlmAgentApprovalPolicy approvalPolicy) => new()
        {
            ModelId = BedrockAgentCoreLlmClient.DefaultModelId,
            WorkingDirectory = "/not-sent-to-agentcore",
            Sandbox = sandbox,
            ApprovalPolicy = approvalPolicy
        };

    private static List<HarnessMessage> UserMessages() =>
    [
        new HarnessMessage
        {
            Role = HarnessConversationRole.User,
            Content = [new HarnessContentBlock { Text = "Hello" }]
        }
    ];

    private static async Task<T[]> ToArrayAsync<T>(IAsyncEnumerable<T> source)
    {
        var results = new List<T>();
        await foreach (T item in source)
        {
            results.Add(item);
        }
        return [.. results];
    }

    [Fact]
    public async Task AStartContextOpensTheFirstTurnAsTextAndReportsUncarriedAttachments()
    {
        var invoker = new StubInvoker(
        [
            new HarnessContentBlockDeltaEvent { ContentBlockIndex = 0, Delta = new HarnessContentBlockDelta { Text = "396" } },
            new HarnessMessageStopEvent { StopReason = HarnessStopReason.End_turn }
        ]);
        var client = new BedrockAgentCoreLlmClient(invoker);
        await using ILlmAgentSession session = await client.StartSessionAsync("connection", Connection(), new LlmAgentSessionOptions
        {
            ModelId = BedrockAgentCoreLlmClient.DefaultModelId,
            WorkingDirectory = "/not-sent-to-agentcore",
            UnboundResponseState = LlmUnboundResponseState.Omit,
            Context = new LlmAgentContext
            {
                Entries =
                [
                    new("e1", "t1", new LlmChatMessage("user", "What is 17 times 23?")
                    {
                        Attachments = [new LlmAttachment { Kind = LlmAttachmentKind.Document, FileName = "note.txt", Data = "hi"u8.ToArray() }]
                    }),
                    new("e2", "t1", new LlmChatMessage("assistant", "391"))
                ]
            }
        });

        LlmAgentEvent[] events = await ToArrayAsync(session.RunTurnAsync(
            new LlmAgentTurnRequest { Input = [new LlmAgentTextContent("Add 5.")] }));

        string sent = string.Join("\n", invoker.Requests[0].Messages[0].Content.Select(block => block.Text));
        Assert.StartsWith("CONVERSATION SO FAR", sent, StringComparison.Ordinal);
        Assert.Contains("391", sent, StringComparison.Ordinal);
        Assert.EndsWith("Add 5.", sent, StringComparison.Ordinal);
        LlmAgentWarningEvent warning = Assert.Single(events.OfType<LlmAgentWarningEvent>());
        Assert.EndsWith("1 history attachments could not be carried.", warning.Message, StringComparison.Ordinal);
    }

    private sealed class StubInvoker : IBedrockAgentCoreInvoker
    {
        private readonly Queue<IReadOnlyList<IEventStreamEvent>> _responses;

        public StubInvoker(params IReadOnlyList<IEventStreamEvent>[] responses) =>
            _responses = new Queue<IReadOnlyList<IEventStreamEvent>>(responses);

        public List<InvokeHarnessRequest> Requests { get; } = [];

        public async IAsyncEnumerable<IEventStreamEvent> InvokeAsync(
            LlmAwsCredentials aws,
            InvokeHarnessRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = aws;
            Requests.Add(request);
            if (_responses.Count == 0)
            {
                yield break;
            }
            foreach (IEventStreamEvent streamEvent in _responses.Dequeue())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return streamEvent;
                await Task.Yield();
            }
        }
    }
}
