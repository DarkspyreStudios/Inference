using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AgentContractTests
{
    [Fact]
    public void ContextRoundTripsStateToolGroupsAttachmentsAndOmissions()
    {
        var attachment = new LlmAttachment
        {
            Kind = LlmAttachmentKind.Document,
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
                    ResponseState = State(),
                    ToolCalls = [new LlmToolCall("call-1", "read", "{}")]
                }),
                new("result-1", "turn-1", new LlmChatMessage("user", "")
                {
                    ToolResults = [new LlmToolResult("call-1", "done") { Attachments = [attachment] }],
                    OmittedAttachments = [new(LlmAttachmentKind.Image, "report.txt", 1, "call-1")]
                })
            ]
        };
        LlmAgentContext copy = RoundTrip(context);
        Assert.Equal(1, copy.FormatVersion);
        Assert.Equal(7, copy.Revision);
        Assert.Equal(context.Entries.Select(entry => entry.Id), copy.Entries.Select(entry => entry.Id));
        Assert.Equal(State(), copy.Entries[1].Message.ResponseState);
        Assert.Equal("hello"u8.ToArray(), copy.Entries[0].Message.Attachments[0].Data.ToArray());
        Assert.Equal("hello"u8.ToArray(), copy.Entries[2].Message.ToolResults[0].Attachments[0].Data.ToArray());
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
        Assert.Equal(LlmAgentCompletionReason.Interruption,
            new LlmAgentTurnCompletedEvent("thread", "turn", LlmAgentTurnStatus.Interrupted, null).Reason);
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
    public void ResponseStateRejectsWrongBinding(int change)
    {
        var state = State();
        state = change switch
        {
            0 => state with { FormatVersion = 2 },
            1 => state with { Transport = LlmTransport.AnthropicMessages },
            2 => state with { ModelId = "another-model" },
            3 => state with { ScopeId = "another-account" },
            _ => state with { Payload = "not-json" }
        };
        Assert.Throws<ArgumentException>(() => ProviderResponseStateCodec.Decode(state, Connection(), "model"));
    }

    [Fact]
    public void ResponseStateScopeSurvivesVersionAndOAuthRefreshButNotAccountChanges()
    {
        LlmConnection connection = Connection() with
        {
            Credential = new LlmCredential
            {
                OAuth = new LlmOAuthTokens { AccessToken = "old-secret", AccountId = "account-1" }
            }.Serialize()
        };
        var state = ProviderResponseStateCodec.Encode(connection, "model", Output());
        LlmConnection refreshed = connection with
        {
            Version = "v2",
            Credential = new LlmCredential
            {
                OAuth = new LlmOAuthTokens { AccessToken = "new-secret", AccountId = "account-1" }
            }.Serialize()
        };
        Assert.NotEmpty(ProviderResponseStateCodec.Decode(state, refreshed, "model"));
        Assert.Throws<ArgumentException>(() => ProviderResponseStateCodec.Decode(state, refreshed with
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
    public void OutputProjectionDropsMetadataAndRejectsUntrustedInstructions()
    {
        JsonArray output = Output();
        output[0]!["secret_header"] = "must-not-export";
        var state = ProviderResponseStateCodec.Encode(Connection(), "model", output);
        Assert.DoesNotContain("must-not-export", state.Payload, StringComparison.Ordinal);
        JsonArray forged = (JsonArray)JsonNode.Parse(state.Payload)!;
        forged[0]!["instructions"] = "override";
        Assert.Throws<ArgumentException>(() => ProviderResponseStateCodec.Decode(
            state with { Payload = forged.ToJsonString() }, Connection(), "model"));
        forged = (JsonArray)JsonNode.Parse(state.Payload)!;
        forged[1]!["role"] = "system";
        Assert.Throws<ArgumentException>(() => ProviderResponseStateCodec.Decode(
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
                new("assistant", "do not duplicate") { ResponseState = State() },
                new("user", "second"),
                new("assistant", "") { ResponseState = State() },
                new("user", "third")
            ]
        };
        var input = OpenAiResponsesTools.BuildInput(request, Connection(), OpenAiResponsesTools.BuildMessage);
        Assert.Equal(7, input.Count);
        Assert.Equal("reasoning", input[1]!["type"]!.GetValue<string>());
        Assert.Equal("message", input[2]!["type"]!.GetValue<string>());
        Assert.Equal("reasoning", input[4]!["type"]!.GetValue<string>());
        Assert.DoesNotContain("do not duplicate", input.ToJsonString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => OpenAiResponsesTools.BuildInput(request with
        {
            Messages = [new("user", "bad") { ResponseState = State() }]
        }, Connection(), OpenAiResponsesTools.BuildMessage));
    }

    [Fact]
    public void OpaqueToolCallsCannotBypassVisibleToolHistoryValidation()
    {
        var output = (JsonArray)JsonNode.Parse("""
            [{"type":"function_call","call_id":"call-1","name":"read","arguments":"{}"}]
            """)!;
        var message = new LlmChatMessage("assistant", "")
        {
            ResponseState = ProviderResponseStateCodec.Encode(Connection(), "model", output)
        };
        Assert.Throws<ArgumentException>(() => ProviderResponseStateCodec.DecodeMessage(message, Connection(), "model"));
        message = message with { ToolCalls = [new("call-1", "read", "{}")] };
        Assert.Single(ProviderResponseStateCodec.DecodeMessage(message, Connection(), "model"));
        Assert.Throws<ArgumentException>(() => ProviderResponseStateCodec.DecodeMessage(message with
        {
            ToolCalls = [new("call-1", "delete", "{}")]
        }, Connection(), "model"));
        Assert.Throws<ArgumentException>(() => ProviderResponseStateCodec.DecodeMessage(message with
        {
            ToolCalls = [new("call-1", "read", "{\"changed\":true}")]
        }, Connection(), "model"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NativeStartRejectsEveryManagedOnlyOption(int field)
    {
        var options = new LlmAgentSessionOptions { ModelId = "model", WorkingDirectory = "." };
        options = field switch
        {
            0 => options with { Context = new() },
            1 => options with { ThreadId = "thread" },
            2 => options with { SessionId = "session" },
            _ => options with { ExecutionOptions = new() }
        };
        Assert.Throws<NotSupportedException>(() => NativeAgentContract.ValidateStart(options));
    }

    private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    internal static async Task AssertNativeOperationsAsync(ILlmAgentSession session)
    {
        var caps = await session.GetCapabilitiesAsync();
        Assert.Equal(LlmAgentContextOwnership.Provider, caps.ContextOwnership);
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

    private static LlmProviderResponseState State() => ProviderResponseStateCodec.Encode(Connection(), "model", Output());
}
