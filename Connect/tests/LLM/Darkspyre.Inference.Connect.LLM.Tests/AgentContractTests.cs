using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AgentContractTests
{
    [Fact]
    public void ContextRoundTripsStateToolGroupsAttachmentsAndOmissions()
    {
        var attachment = new LlmAttachment
        {
            Kind = LlmBinaryKind.Document,
            FileName = "report.txt",
            Data = "hello"u8.ToArray()
        };
        var context = new LlmAgentContext
        {
            Revision = 7,
            Entries =
            [
                new("user-1", "turn-1", new LlmChatMessage("user", "read") { Attachments = [attachment] }),
                new("assistant-1", "turn-1", new LlmChatMessage("assistant", "")
                {
                    ContinuationState = State(),
                    ToolCalls = [new LlmToolCall("call-1", "read", "{}")]
                }),
                new("result-1", "turn-1", new LlmChatMessage("user", "")
                {
                    ToolResults = [new LlmToolResult("call-1", new LlmToolOutput
                    {
                        Parts =
                        [
                            new LlmToolOutputText("done"),
                            new LlmToolOutputBinary(LlmBinaryKind.Document, "text/plain", "hello"u8.ToArray(), "hello.txt"),
                            new LlmToolOutputResource("file:///notes.md") { Name = "notes", MediaType = "text/markdown", Text = "# notes" }
                        ],
                        StructuredContent = JsonSerializer.SerializeToElement(new { rows = 2 }),
                        IsError = true
                    })],
                    OmittedAttachments = [new(LlmBinaryKind.Image, "report.png", 3, "call-1")]
                })
            ]
        };
        LlmAgentContext copy = RoundTrip(context);
        Assert.Equal(2, copy.FormatVersion);
        Assert.Equal(7, copy.Revision);
        Assert.Equal(context.Entries.Select(entry => entry.Id), copy.Entries.Select(entry => entry.Id));
        Assert.Equal(State(), copy.Entries[1].Message.ContinuationState);
        Assert.Equal("hello"u8.ToArray(), copy.Entries[0].Message.Attachments[0].Data.ToArray());
        LlmToolOutput output = copy.Entries[2].Message.ToolResults[0].Output;
        Assert.Equal("done", Assert.IsType<LlmToolOutputText>(output.Parts[0]).Text);
        var binary = Assert.IsType<LlmToolOutputBinary>(output.Parts[1]);
        Assert.Equal((LlmBinaryKind.Document, "text/plain", "hello.txt"), (binary.Kind, binary.MediaType, binary.FileName));
        Assert.Equal("hello"u8.ToArray(), binary.Data.ToArray());
        Assert.Equal("# notes", Assert.IsType<LlmToolOutputResource>(output.Parts[2]).Text);
        Assert.Equal(2, output.StructuredContent!.Value.GetProperty("rows").GetInt32());
        Assert.True(output.IsError);
        Assert.Equal(context.Entries[2].Message.OmittedAttachments, copy.Entries[2].Message.OmittedAttachments);
        Assert.Equal("call-1", copy.Entries[1].Message.ToolCalls[0].Id);
    }

    [Fact]
    public void UnknownStatusAndPlanningDefaultsRemainExplicit()
    {
        LlmAgentContextStatus status = RoundTrip(new LlmAgentContextStatus { ModelId = "model" });
        Assert.Null(status.NeedsCompaction);
        Assert.Null(status.MaxContextTokens);
        Assert.Null(status.UsedContextTokens);
        Assert.Null(status.AvailableInputTokens);
        Assert.Null(status.ReservedOutputTokens);
        Assert.Equal(LlmContextMeasurementSource.Unknown, status.Source);
        Assert.Null(new LlmModel("model", "Model").MaxContextTokens);
        Assert.Null(new LlmModel("model", "Model").MaxOutputTokens);
        var options = new LlmAgentExecutionOptions();
        Assert.Equal(32, options.MaxModelIterations);
        Assert.Equal(4, options.MaxConcurrentTools);
        Assert.Equal(LlmAgentToolExecutionMode.Sequential, options.ToolExecutionMode);
        Assert.Equal(0.85, options.ContextPolicy.CompactionThresholdRatio);
        Assert.Equal(4096, options.ContextPolicy.SafetyMarginTokens);
        Assert.Null(options.ContextPolicy.MaxContextTokens);
        Assert.Null(options.ContextPolicy.ReservedOutputTokens);
        Assert.False(new LlmAgentContextCaptureOptions().IncludeAttachmentData);
    }

    [Fact]
    public void PauseContinuationAndUsageRoundTripWithoutLosingRunIdentity()
    {
        var status = new LlmAgentContextStatus { ModelId = "model", ContextRevision = 3, NeedsCompaction = true };
        var pause = new LlmAgentCompactionRequiredEvent(
            "thread", "turn", "pause", status, LlmAgentCompactionReason.Threshold, true, ["user"]);
        var copy = RoundTrip(pause);
        Assert.Equal(pause.TurnId, copy.TurnId);
        Assert.Equal(pause.PauseId, copy.PauseId);
        Assert.Equal(pause.Status, copy.Status);
        Assert.Equal(pause.ProtectedEntryIds, copy.ProtectedEntryIds);
        var continuation = RoundTrip(new LlmAgentTurnContinuation("turn", "pause", 3, new()));
        Assert.Equal(3, continuation.ExpectedContextRevision);
        Assert.Equal("turn", continuation.TurnId);
        Assert.Null(RoundTrip(new LlmAgentUsageEvent("thread", "turn", "request", null, null)).RunTotals);
        Assert.Equal(
            [LlmAgentTurnStatus.Completed, LlmAgentTurnStatus.Interrupted, LlmAgentTurnStatus.Failed, LlmAgentTurnStatus.Failed],
            new[]
            {
                LlmAgentCompletionReason.Completed, LlmAgentCompletionReason.Interruption,
                LlmAgentCompletionReason.Refusal, LlmAgentCompletionReason.IterationLimit
            }.Select(reason => new LlmAgentTurnCompletedEvent("thread", "turn", reason, null).Status));
    }

    [Fact]
    public void AgentOverridesCannotManufactureBehaviorOrContradictOwnership()
    {
        var unsupported = new LlmProviderCapabilities();
        Assert.Throws<ArgumentException>(() => unsupported.Apply(new() { SupportsAgentTurnInstructions = true }));
        Assert.Throws<ArgumentException>(() => unsupported.Apply(new() { SupportsAgentSessions = true }));
        var native = new LlmProviderCapabilities
        {
            SupportsAgentSessions = true,
            SupportsAgentToolUse = true,
            Agent = NativeAgentContract.Claude
        };
        var disabled = native.Apply(new() { SupportsAgentSessions = false });
        Assert.False(disabled.SupportsAgentToolUse);
        Assert.Equal(new LlmAgentCapabilities(), disabled.Agent);
        Assert.Throws<ArgumentException>(() => native.Apply(new()
        {
            SupportsAgentSessions = false,
            SupportsAgentToolUse = true
        }));
        Assert.Throws<ArgumentException>(() => (native with
        {
            Agent = native.Agent with { SupportsCompactionHandoff = true }
        }).Apply(null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ContinuationStateRejectsWrongBinding(int change)
    {
        var state = State();
        state = change switch
        {
            0 => state with { FormatVersion = 1 },
            1 => state with { ImplementationId = ProviderContinuationStateCodec.ImplementationId(LlmTransport.AnthropicMessages) },
            2 => state with { ModelId = "another-model" },
            3 => state with { ScopeId = "another-account" },
            _ => state with { Payload = "not-json" }
        };
        Assert.Throws<ArgumentException>(() => ProviderContinuationStateCodec.Decode(state, Connection(), "model"));
    }

    [Fact]
    public void ContinuationStateScopeSurvivesVersionAndOAuthRefreshButNotAccountChanges()
    {
        LlmConnection connection = Connection() with
        {
            Credential = new LlmCredential
            {
                OAuth = new LlmOAuthTokens { AccessToken = "old-secret", AccountId = "account-1" }
            }.Serialize()
        };
        var state = ProviderContinuationStateCodec.Encode(connection, "model", Output());
        LlmConnection refreshed = connection with
        {
            Version = "v2",
            Credential = new LlmCredential
            {
                OAuth = new LlmOAuthTokens { AccessToken = "new-secret", AccountId = "account-1" }
            }.Serialize()
        };
        Assert.NotEmpty(ProviderContinuationStateCodec.Decode(state, refreshed, "model"));
        Assert.Throws<ArgumentException>(() => ProviderContinuationStateCodec.Decode(state, refreshed with
        {
            Credential = new LlmCredential
            {
                OAuth = new LlmOAuthTokens { AccessToken = "new-secret", AccountId = "account-2" }
            }.Serialize()
        }, "model"));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(state), StringComparison.Ordinal);
        Assert.DoesNotContain("account-1", JsonSerializer.Serialize(state), StringComparison.Ordinal);
    }

    [Fact]
    public void CountsOnlyStateCarriesNoOutputAndBindsToItsConnection()
    {
        LlmConnection bedrock = new() { Version = "v1", Transport = LlmTransport.AwsBedrockConverse, Endpoint = "aws://bedrock" };
        var state = ProviderContinuationStateCodec.EncodeCounts(bedrock, "model", 1200, 80);
        Assert.Equal((1200L, 80L), (state.InputTokens, state.OutputTokens));
        Assert.Empty(ProviderContinuationStateCodec.Decode(state, bedrock with { Version = "v2" }, "model"));
        var message = new LlmChatMessage("assistant", "done") { ContinuationState = state, ToolCalls = [new("call-1", "tool", "{}")] };
        Assert.Empty(ProviderContinuationStateCodec.DecodeMessage(message, bedrock, "model"));
        Assert.Throws<ArgumentException>(() => ProviderContinuationStateCodec.Decode(state, bedrock, "another-model"));
        Assert.Throws<ArgumentException>(() => ProviderContinuationStateCodec.Decode(state, bedrock with { Endpoint = "aws://other" }, "model"));
        Assert.Throws<ArgumentException>(() => ProviderContinuationStateCodec.Decode(
            state with { Payload = Output().ToJsonString() }, bedrock, "model"));
    }

    [Fact]
    public void OutputProjectionDropsMetadataAndRejectsUntrustedInstructions()
    {
        JsonArray output = Output();
        output[0]!["secret_header"] = "must-not-export";
        var state = ProviderContinuationStateCodec.Encode(Connection(), "model", output);
        Assert.DoesNotContain("must-not-export", state.Payload, StringComparison.Ordinal);
        JsonArray forged = (JsonArray)JsonNode.Parse(state.Payload)!;
        forged[0]!["instructions"] = "override";
        Assert.Throws<ArgumentException>(() => ProviderContinuationStateCodec.Decode(
            state with { Payload = forged.ToJsonString() }, Connection(), "model"));
        forged = (JsonArray)JsonNode.Parse(state.Payload)!;
        forged[1]!["role"] = "system";
        Assert.Throws<ArgumentException>(() => ProviderContinuationStateCodec.Decode(
            state with { Payload = forged.ToJsonString() }, Connection(), "model"));
    }

    [Fact]
    public void MultipleAssistantStatesReplayOnceInSourceOrderEvenWithEmptyVisibleText()
    {
        var request = new LlmChatRequest
        {
            ModelId = "model",
            Messages =
            [
                new("user", "first"),
                new("assistant", "Hello") { ContinuationState = State() },
                new("user", "second"),
                new("assistant", "") { ContinuationState = ProviderContinuationStateCodec.Encode(Connection(), "model",
                    new JsonArray(Output()[0]!.DeepClone())) },
                new("user", "third")
            ]
        };
        var input = OpenAiResponsesTools.BuildInput(request, Connection(), OpenAiResponsesTools.BuildMessage, Project);
        Assert.Equal(6, input.Count);
        Assert.Equal("reasoning", input[1]!["type"]!.GetValue<string>());
        Assert.Equal("message", input[2]!["type"]!.GetValue<string>());
        Assert.Equal("reasoning", input[4]!["type"]!.GetValue<string>());
        Assert.Single(input.OfType<JsonObject>(), item => item["type"]?.GetValue<string>() == "message");
        Assert.Throws<ArgumentException>(() => ProviderContinuationStateCodec.DecodeMessage(
            new("assistant", "changed") { ContinuationState = State() }, Connection(), "model"));
        Assert.Throws<ArgumentException>(() => OpenAiResponsesTools.BuildInput(request with
        {
            Messages = [new("user", "bad") { ContinuationState = State() }]
        }, Connection(), OpenAiResponsesTools.BuildMessage, Project));
    }

    [Fact]
    public void ResponsesToolOutputIsAStringWhenEmptyOrSingleTextAndAnItemListOtherwise()
    {
        LlmChatRequest request = new()
        {
            ModelId = "model",
            Messages =
            [
                new("assistant", "") { ToolCalls = [new LlmToolCall("a", "t", "{}"), new LlmToolCall("b", "t", "{}"), new LlmToolCall("c", "t", "{}")] },
                new("user", "")
                {
                    ToolResults =
                    [
                        new LlmToolResult("a", new LlmToolOutput()),
                        new LlmToolResult("b", LlmToolOutput.FromText("one")),
                        new LlmToolResult("c", new LlmToolOutput { Parts = [new LlmToolOutputText("x"), new LlmToolOutputText("y")] })
                    ]
                }
            ]
        };

        JsonArray input = OpenAiResponsesTools.BuildInput(request, Connection(), OpenAiResponsesTools.BuildMessage, Project);

        JsonNode?[] outputs = [.. input.Where(item => item!["type"]!.GetValue<string>() == "function_call_output").Select(item => item!["output"])];
        Assert.Equal("", outputs[0]!.GetValue<string>());
        Assert.Equal("one", outputs[1]!.GetValue<string>());
        Assert.Equal(["x", "y"], outputs[2]!.AsArray().Select(item => item!["text"]!.GetValue<string>()));
    }

    [Fact]
    public void OpaqueToolCallsCannotBypassVisibleToolHistoryValidation()
    {
        var output = (JsonArray)JsonNode.Parse("""
            [{"type":"function_call","call_id":"call-1","name":"read","arguments":"{}"}]
            """)!;
        var message = new LlmChatMessage("assistant", "")
        {
            ContinuationState = ProviderContinuationStateCodec.Encode(Connection(), "model", output)
        };
        Assert.Throws<ArgumentException>(() => ProviderContinuationStateCodec.DecodeMessage(message, Connection(), "model"));
        message = message with { ToolCalls = [new("call-1", "read", "{}")] };
        Assert.Single(ProviderContinuationStateCodec.DecodeMessage(message, Connection(), "model"));
        Assert.Throws<ArgumentException>(() => ProviderContinuationStateCodec.DecodeMessage(message with
        {
            ToolCalls = [new("call-1", "delete", "{}")]
        }, Connection(), "model"));
        Assert.Throws<ArgumentException>(() => ProviderContinuationStateCodec.DecodeMessage(message with
        {
            ToolCalls = [new("call-1", "read", "{\"changed\":true}")]
        }, Connection(), "model"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NativeStartRejectsHostIdentitiesAndManagedExecutionOptions(int field)
    {
        var options = new LlmAgentSessionOptions { ModelId = "model", Runtime = new() { WorkingDirectory = "." } };
        options = field switch
        {
            1 => options with { ThreadId = "thread" },
            2 => options with { SessionId = "session" },
            _ => options with { ExecutionOptions = new() }
        };
        Assert.Throws<NotSupportedException>(() => NativeAgentContract.ValidateStart(options, NativeAgentContract.Claude));
    }

    [Fact]
    public void NativeStartRequiresRuntimeOptionsAndResumeDefaultsToTheCurrentDirectory()
    {
        Assert.Throws<ArgumentException>(() => NativeAgentContract.ValidateStart(
            new LlmAgentSessionOptions { ModelId = "model" }, NativeAgentContract.Claude));

        LlmAgentNativeRuntimeOptions resumed = NativeAgentContract.ResumeRuntime(new LlmAgentResumeOptions(), NativeAgentContract.Claude);
        Assert.Equal(Environment.CurrentDirectory, resumed.WorkingDirectory);
        Assert.False(resumed.Ephemeral);
        var policy = Assert.IsType<LlmAgentHostRuntimePolicy>(resumed.Policy);
        Assert.Equal([Environment.CurrentDirectory], policy.WritableRoots);
        Assert.Equal((LlmAgentSandboxMode.WorkspaceWrite, true, LlmAgentApprovalPolicy.OnRequest, false),
            (policy.Sandbox, policy.NetworkAllowed, policy.ApprovalPolicy, policy.AllowRuntimeBackgroundWork));
        var supplied = new LlmAgentNativeRuntimeOptions
        {
            WorkingDirectory = "/work",
            Policy = new LlmAgentHostRuntimePolicy { Sandbox = LlmAgentSandboxMode.ReadOnly }
        };
        Assert.Same(supplied, NativeAgentContract.ResumeRuntime(new LlmAgentResumeOptions { Runtime = supplied }, NativeAgentContract.Claude));
    }

    [Fact]
    public void NativeRuntimeDefaultsToHostControlledPolicy()
    {
        var policy = Assert.IsType<LlmAgentHostRuntimePolicy>(new LlmAgentNativeRuntimeOptions { WorkingDirectory = "." }.Policy);
        Assert.Equal(LlmAgentRuntimePolicyKind.HostControlled, policy.Kind);
        Assert.False(policy.AllowRuntimeBackgroundWork);
        Assert.Equal(LlmAgentRuntimePolicyKind.ProviderConfigured, new LlmAgentProviderRuntimePolicy().Kind);
    }

    [Fact]
    public void HostControlledRuntimesRejectProviderConfiguredPolicyBeforeEffects()
    {
        var runtime = new LlmAgentNativeRuntimeOptions { WorkingDirectory = ".", Policy = new LlmAgentProviderRuntimePolicy() };
        foreach (LlmAgentCapabilities agent in new[] { NativeAgentContract.Claude, NativeAgentContract.AgentCore })
        {
            Assert.Equal([LlmAgentRuntimePolicyKind.HostControlled], agent.SupportedRuntimePolicies);
            Assert.Throws<NotSupportedException>(() => NativeAgentContract.ValidateStart(
                new LlmAgentSessionOptions { ModelId = "model", Runtime = runtime }, agent));
            Assert.Throws<NotSupportedException>(() => NativeAgentContract.ResumeRuntime(
                new LlmAgentResumeOptions { Runtime = runtime }, agent));
        }
        Assert.Throws<ArgumentException>(() => NativeAgentContract.ValidateStart(
            new LlmAgentSessionOptions { ModelId = "model", Runtime = runtime with { Policy = null! } }, NativeAgentContract.Claude));
    }

    [Fact]
    public void RuntimePolicySetsAreDeclaredOnlyForProviderOwnedContext()
    {
        Assert.Empty(new LlmAgentCapabilities().SupportedRuntimePolicies);
        var hostOwned = new LlmAgentCapabilities
        {
            ContextOwnership = LlmAgentOwnership.Host,
            SupportedRuntimePolicies = new HashSet<LlmAgentRuntimePolicyKind> { LlmAgentRuntimePolicyKind.HostControlled }
        };
        Assert.Throws<ArgumentException>(() => new LlmProviderCapabilities { SupportsAgentSessions = true, Agent = hostOwned }.Apply(null));
        var none = new LlmAgentCapabilities
        {
            SupportedRuntimePolicies = new HashSet<LlmAgentRuntimePolicyKind> { LlmAgentRuntimePolicyKind.ProviderConfigured }
        };
        Assert.Throws<ArgumentException>(() => new LlmProviderCapabilities { SupportsAgentSessions = true, Agent = none }.Apply(null));
    }

    private static ProjectedToolOutput Project(LlmToolOutput output) =>
        ToolOutputProjector.Project(output, RichContentRoutes.Responses, RichContentRoutes.Responses);
    private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    internal static async Task AssertNativeOperationsAsync(ILlmAgentSession session)
    {
        var caps = await session.GetCapabilitiesAsync();
        Assert.Equal(LlmAgentOwnership.Provider, caps.ContextOwnership);
        Assert.Equal(LlmAgentContextImport.OpeningContext, caps.ContextImport);
        Assert.False(caps.SupportsContextCapture);
        Assert.False(caps.SupportsContextReplacement);
        Assert.False(caps.SupportsContextStatus);
        Assert.False(caps.SupportsCompactionHandoff);
        await Assert.ThrowsAsync<NotSupportedException>(() => session.CaptureContextAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => session.GetContextStatusAsync());
        var accounting = await session.MeasureContextAsync(new());
        Assert.False(accounting.IsSupported);
        Assert.Null(accounting.UsedContextTokens);
        Assert.NotNull(accounting.Diagnostic);
        await Assert.ThrowsAsync<NotSupportedException>(() => session.ReplaceContextAsync(new(), 0));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in session.ContinueTurnAsync(new("turn", "pause", 0, new()))) { }
        });
        foreach (string instructions in new[] { "", "override" })
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
            {
                await foreach (var _ in session.RunTurnAsync(new()
                {
                    Input = [new LlmAgentTextContent("Hello")],
                    Instructions = instructions
                })) { }
            });
        }
    }

    private static LlmConnection Connection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.OpenAiResponses,
        Endpoint = "https://example.test/v1",
        Credential = new LlmCredential { ApiKey = "synthetic-test-key" }.Serialize()
    };

    private static JsonArray Output() => (JsonArray)JsonNode.Parse("""
        [{"type":"reasoning","id":"r1","summary":[],"encrypted_content":"opaque"},
         {"type":"message","id":"m1","role":"assistant","content":[{"type":"output_text","text":"Hello"}]}]
        """)!;

    private static LlmContinuationState State() => ProviderContinuationStateCodec.Encode(Connection(), "model", Output());
}
