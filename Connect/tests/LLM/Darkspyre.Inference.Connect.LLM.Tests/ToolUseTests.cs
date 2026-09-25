using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ToolUseTests
{
    [Fact]
    public void CapabilitiesDistinguishHostExecutedToolsFromCliOwnedTools()
    {
        using var http = new HttpClient(new SequenceHttpMessageHandler([]));
        LlmProviderCapabilities responses =
            new OpenAiResponsesLlmClient(http).GetCapabilities(ResponsesConnection());
        Assert.True(responses.SupportsToolUse);
        Assert.True(responses.SupportsPromptImages);
        Assert.True(responses.SupportsPromptDocuments);
        Assert.Equal(RichContentRoutes.Responses, responses.RichContentSupported);
        Assert.True(new OpenAiCompatibleChatLlmClient(http).GetCapabilities(CompatibleConnection()).SupportsToolUse);
        LlmProviderCapabilities codex =
            new OpenAiCodexLlmClient(http).GetCapabilities(CodexConnection());
        Assert.True(codex.SupportsToolUse);
        Assert.True(codex.SupportsAgentToolUse);
        Assert.True(codex.SupportsPromptImages);
        Assert.True(codex.SupportsPromptDocuments);
        Assert.Equal(RichContentRoutes.Responses, codex.RichContentSupported);
        Assert.Equal(RichContentRoutes.Responses, codex.Agent.RichContentSupported);

        var anthropic = new AnthropicMessagesLlmClient(http);
        Assert.True(anthropic.GetCapabilities(AnthropicConnection()).SupportsToolUse);
        Assert.True(anthropic.GetCapabilities(AnthropicConnection()).SupportsPromptImages);
        Assert.True(anthropic.GetCapabilities(AnthropicConnection()).SupportsPromptDocuments);
        Assert.Equal(RichContentRoutes.Anthropic, anthropic.GetCapabilities(AnthropicConnection()).RichContentSupported);
        Assert.False(anthropic.GetCapabilities(AnthropicConnection()).SupportsAgentToolUse);
        Assert.Throws<NotSupportedException>(() => anthropic.GetCapabilities(
            AnthropicConnection() with
            {
                Credential = new LlmCredential
                {
                    OAuth = new LlmOAuthTokens { AccessToken = "wrong-lane" }
                }.Serialize()
            }));

        var claude = new ClaudeCliLlmClient(http, new ClaudeRunner());
        LlmProviderCapabilities cliOnly = claude.GetCapabilities(ClaudeConnection());
        LlmProviderCapabilities withCatalog = claude.GetCapabilities(ClaudeConnection() with
        {
            Credential = new LlmCredential
            {
                OAuth = new LlmOAuthTokens { AccessToken = "catalog-only" }
            }.Serialize()
        });
        Assert.False(cliOnly.SupportsToolUse);
        Assert.True(cliOnly.SupportsAgentSessions);
        Assert.True(cliOnly.SupportsAgentToolUse);
        Assert.True(cliOnly.SupportsPromptImages);
        Assert.True(cliOnly.SupportsPromptDocuments);
        Assert.Equal(RichContentRoutes.TextOnly, cliOnly.RichContentSupported);
        Assert.Equal(RichContentRoutes.ClaudeMcp, cliOnly.Agent.RichContentSupported);
        Assert.False(cliOnly.SupportsModelListing);
        Assert.True(withCatalog.SupportsAgentToolUse);
        Assert.True(withCatalog.SupportsModelListing);

        var antigravity = new AntigravityCliLlmClient(new AntigravityRunner());
        LlmConnection antigravityConnection = CliConnection(LlmTransport.GoogleAntigravityCli) with
        {
            CapabilityOverrides = new LlmCapabilityOverrides { SupportsToolUse = true }
        };
        LlmProviderCapabilities antigravityCapabilities =
            antigravity.GetCapabilities(antigravityConnection);
        Assert.False(antigravityCapabilities.SupportsToolUse);
        Assert.False(antigravityCapabilities.SupportsPromptImages);
        Assert.False(antigravityCapabilities.SupportsPromptDocuments);
    }

    [Fact]
    public async Task ResponsesStreamsToolCallsAndReplaysOpaqueContinuationState()
    {
        const string firstEvents = """
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"get_weather","arguments":""}}

            data: {"type":"response.function_call_arguments.delta","output_index":0,"delta":"{\"city\":\"Chi"}

            data: {"type":"response.function_call_arguments.delta","output_index":0,"delta":"cago\"}"}

            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"get_weather","arguments":"{\"city\":\"Chicago\"}"}}

            data: {"type":"response.completed","response":{"id":"resp_tools","status":"completed","output":[{"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"encrypted-state"},{"type":"function_call","id":"fc_1","call_id":"call_1","name":"get_weather","arguments":"{\"city\":\"Chicago\"}"}],"usage":{"input_tokens":8,"output_tokens":4,"total_tokens":12}}}

            """;
        const string secondEvents = """
            data: {"type":"response.completed","response":{"id":"resp_final","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"72 F"}]}]}}

            """;
        var handler = new SequenceHttpMessageHandler(
        [
            _ => SseResponse(firstEvents),
            _ => SseResponse(secondEvents)
        ]);
        using var http = new HttpClient(handler);
        var client = new OpenAiResponsesLlmClient(http);
        LlmToolDefinition tool = WeatherTool();
        var firstRequest = new LlmChatRequest
        {
            ModelId = "gpt-tool-test",
            Messages =
            [
                new LlmChatMessage("user", "Weather in Chicago?")
                {
                    Attachments = [ImageAttachment(), PdfAttachment()]
                }
            ],
            Tools = [tool],
            ToolChoice = new LlmToolChoice
            {
                Mode = LlmToolChoiceMode.Specific,
                Name = tool.Name
            },
            AllowParallelToolCalls = false
        };

        IReadOnlyList<LlmStreamEvent> firstStream = await ReadAllAsync(
            client.StreamAsync(ResponsesConnection(), firstRequest));

        LlmToolCallStartedEvent started = Assert.Single(firstStream.OfType<LlmToolCallStartedEvent>());
        Assert.Equal("call_1", started.ToolCallId);
        Assert.Equal("get_weather", started.Name);
        Assert.Equal(
            "{\"city\":\"Chicago\"}",
            string.Concat(firstStream.OfType<LlmToolCallArgumentsDeltaEvent>().Select(item => item.Delta)));
        Assert.Equal("call_1", Assert.Single(firstStream.OfType<LlmToolCallCompletedEvent>()).ToolCall.Id);

        LlmChatResult firstResult = Assert.Single(firstStream.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.RequiresAction, firstResult.Status);
        Assert.Equal("tool_calls", firstResult.FinishReason);
        LlmToolCall call = Assert.Single(firstResult.ToolCalls);
        Assert.NotNull(firstResult.ResponseState);

        using (JsonDocument sent = JsonDocument.Parse(handler.Bodies[0]))
        {
            JsonElement root = sent.RootElement;
            Assert.Equal("function", root.GetProperty("tools")[0].GetProperty("type").GetString());
            Assert.Equal("get_weather", root.GetProperty("tools")[0].GetProperty("name").GetString());
            Assert.Equal("get_weather", root.GetProperty("tool_choice").GetProperty("name").GetString());
            Assert.False(root.GetProperty("parallel_tool_calls").GetBoolean());
            Assert.Equal(
                "reasoning.encrypted_content",
                root.GetProperty("include")[0].GetString());
            JsonElement promptContent = root.GetProperty("input")[0].GetProperty("content");
            Assert.Equal("input_text", promptContent[0].GetProperty("type").GetString());
            Assert.Equal("input_image", promptContent[1].GetProperty("type").GetString());
            Assert.Equal("input_file", promptContent[2].GetProperty("type").GetString());
        }

        var continuationRequest = new LlmChatRequest
        {
            ModelId = "gpt-tool-test",
            Messages =
            [
                new LlmChatMessage("user", "Weather in Boston?"),
                new LlmChatMessage("assistant", string.Empty)
                {
                    ToolCalls = [new LlmToolCall("old_call", "get_weather", "{\"city\":\"Boston\"}")]
                },
                new LlmChatMessage("user", string.Empty)
                {
                    ToolResults = [new LlmToolResult("old_call", LlmToolOutput.FromText("55 F"))]
                },
                new LlmChatMessage("user", "Weather in Chicago?"),
                new LlmChatMessage("assistant", string.Empty)
                {
                    ToolCalls = firstResult.ToolCalls,
                    ResponseState = firstResult.ResponseState
                },
                new LlmChatMessage("user", string.Empty)
                {
                    ToolResults =
                    [
                        new LlmToolResult(call.Id, Output("{\"temperature_f\":72}", ImagePart(), PdfPart()))
                    ]
                }
            ],
            Tools = [tool]
        };

        IReadOnlyList<LlmStreamEvent> secondStream = await ReadAllAsync(
            client.StreamAsync(ResponsesConnection(), continuationRequest));

        Assert.Equal(
            "72 F",
            Assert.Single(secondStream.OfType<LlmChatCompletionEvent>()).Result.Text);
        using JsonDocument continued = JsonDocument.Parse(handler.Bodies[1]);
        JsonElement input = continued.RootElement.GetProperty("input");
        Assert.Equal(7, input.GetArrayLength());
        Assert.Equal("old_call", input[1].GetProperty("call_id").GetString());
        Assert.Equal("old_call", input[2].GetProperty("call_id").GetString());
        Assert.Equal("reasoning", input[4].GetProperty("type").GetString());
        Assert.Equal("encrypted-state", input[4].GetProperty("encrypted_content").GetString());
        Assert.Equal("function_call", input[5].GetProperty("type").GetString());
        Assert.Equal("function_call_output", input[6].GetProperty("type").GetString());
        Assert.Equal("call_1", input[6].GetProperty("call_id").GetString());
        JsonElement output = input[6].GetProperty("output");
        Assert.Equal("input_text", output[0].GetProperty("type").GetString());
        Assert.Equal(
            "data:image/png;base64,iVBORw0KGgo=",
            output[1].GetProperty("image_url").GetString());
        Assert.Equal("input_file", output[2].GetProperty("type").GetString());
        Assert.Equal("report.pdf", output[2].GetProperty("filename").GetString());
        Assert.Equal(
            "data:application/pdf;base64,JVBERi0xLjc=",
            output[2].GetProperty("file_data").GetString());
    }

    [Fact]
    public async Task CompatibleChatMapsToolHistoryAndStreamingDeltas()
    {
        const string events = """
            data: {"id":"chat_tools","choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_2","type":"function","function":{"name":"lookup","arguments":"{\"id\":"}}]},"finish_reason":null}]}

            data: {"id":"chat_tools","choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"7}"}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        var handler = new SequenceHttpMessageHandler([_ => SseResponse(events)]);
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        LlmToolDefinition tool = new()
        {
            Name = "lookup",
            InputSchema = Schema("""{"type":"object","properties":{"id":{"type":"integer"}},"required":["id"]}""")
        };
        var request = new LlmChatRequest
        {
            ModelId = "chat-tool-test",
            Messages =
            [
                new LlmChatMessage("user", "First"),
                new LlmChatMessage("assistant", string.Empty)
                {
                    ToolCalls = [new LlmToolCall("old_call", "lookup", "{\"id\":3}")]
                },
                new LlmChatMessage("user", string.Empty)
                {
                    ToolResults = [new LlmToolResult("old_call", LlmToolOutput.FromText("old result"))]
                },
                new LlmChatMessage("user", "Again")
            ],
            Tools = [tool with { Strict = true }, tool with { Name = "lookup_loose" }],
            ToolChoice = new LlmToolChoice { Mode = LlmToolChoiceMode.Required },
            AllowParallelToolCalls = true
        };

        IReadOnlyList<LlmStreamEvent> stream = await ReadAllAsync(
            client.StreamAsync(CompatibleConnection(), request));

        Assert.Equal("call_2", Assert.Single(stream.OfType<LlmToolCallStartedEvent>()).ToolCallId);
        Assert.Equal(
            "{\"id\":7}",
            Assert.Single(stream.OfType<LlmToolCallCompletedEvent>()).ToolCall.ArgumentsJson);
        LlmChatResult result = Assert.Single(stream.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.RequiresAction, result.Status);
        Assert.Equal("call_2", Assert.Single(result.ToolCalls).Id);

        using JsonDocument sent = JsonDocument.Parse(Assert.Single(handler.Bodies));
        JsonElement root = sent.RootElement;
        Assert.Equal("required", root.GetProperty("tool_choice").GetString());
        Assert.True(root.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Equal("function", root.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.True(root.GetProperty("tools")[0].GetProperty("function").GetProperty("strict").GetBoolean());
        Assert.False(root.GetProperty("tools")[1].GetProperty("function").TryGetProperty("strict", out _));
        JsonElement messages = root.GetProperty("messages");
        Assert.Equal("old_call", messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("old_call", messages[2].GetProperty("tool_call_id").GetString());
    }


    [Fact]
    public async Task AnthropicApiKeyMapsToolsAndContinuationState()
    {
        const string firstEvents = """
            data: {"type":"message_start","message":{"id":"msg_tool","usage":{"input_tokens":5}}}

            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"We need weather."}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"signed-state"}}

            data: {"type":"content_block_stop","index":0}

            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"get_weather","input":{}}}

            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"city\":\"Chicago\"}"}}

            data: {"type":"content_block_stop","index":1}

            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":3}}

            data: {"type":"message_stop"}

            """;
        const string secondEvents = """
            data: {"type":"message_start","message":{"id":"msg_final","usage":{"input_tokens":9}}}

            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"72 F"}}

            data: {"type":"content_block_stop","index":0}

            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}

            data: {"type":"message_stop"}

            """;
        var handler = new SequenceHttpMessageHandler(
        [
            _ => SseResponse(firstEvents),
            _ => SseResponse(secondEvents)
        ]);
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        LlmToolDefinition tool = WeatherTool();
        var request = new LlmChatRequest
        {
            ModelId = "claude-tool-test",
            Messages =
            [
                new LlmChatMessage("assistant", string.Empty)
                {
                    ToolCalls = [new LlmToolCall("old_tool", "get_weather", "{\"city\":\"Boston\"}")]
                },
                new LlmChatMessage("user", string.Empty)
                {
                    ToolResults = [new LlmToolResult("old_tool", LlmToolOutput.FromText("55 F"))]
                },
                new LlmChatMessage("user", "Now Chicago")
                {
                    Attachments = [ImageAttachment(), TextAttachment()]
                }
            ],
            Tools = [tool with { Strict = true }, tool with { Name = "get_weather_loose" }],
            ToolChoice = new LlmToolChoice { Mode = LlmToolChoiceMode.Required },
            AllowParallelToolCalls = false
        };

        IReadOnlyList<LlmStreamEvent> stream = await ReadAllAsync(
            client.StreamAsync(AnthropicConnection(), request));

        Assert.Equal("toolu_1", Assert.Single(stream.OfType<LlmToolCallStartedEvent>()).ToolCallId);
        LlmToolCall completed = Assert.Single(stream.OfType<LlmToolCallCompletedEvent>()).ToolCall;
        Assert.Equal("{\"city\":\"Chicago\"}", completed.ArgumentsJson);
        LlmChatResult result = Assert.Single(stream.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.RequiresAction, result.Status);
        Assert.Equal("toolu_1", Assert.Single(result.ToolCalls).Id);
        Assert.NotNull(result.ResponseState);

        using JsonDocument sent = JsonDocument.Parse(handler.Bodies[0]);
        JsonElement root = sent.RootElement;
        Assert.Equal("get_weather", root.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("object", root.GetProperty("tools")[0].GetProperty("input_schema").GetProperty("type").GetString());
        Assert.True(root.GetProperty("tools")[0].GetProperty("strict").GetBoolean());
        Assert.False(root.GetProperty("tools")[1].TryGetProperty("strict", out _));
        Assert.Equal("any", root.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.True(root.GetProperty("tool_choice").GetProperty("disable_parallel_tool_use").GetBoolean());
        Assert.Equal("tool_use", root.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("tool_result", root.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("type").GetString());
        JsonElement promptContent = root.GetProperty("messages")[2].GetProperty("content");
        Assert.Equal("text", promptContent[0].GetProperty("type").GetString());
        Assert.Equal("image", promptContent[1].GetProperty("type").GetString());
        Assert.Equal("document", promptContent[2].GetProperty("type").GetString());
        Assert.Equal("notes.txt", promptContent[2].GetProperty("title").GetString());

        var continuationRequest = request with
        {
            Messages =
            [
                .. request.Messages,
                new LlmChatMessage("assistant", string.Empty)
                {
                    ToolCalls = result.ToolCalls,
                    ResponseState = result.ResponseState
                },
                new LlmChatMessage("user", string.Empty)
                {
                    ToolResults =
                    [
                        new LlmToolResult("toolu_1", Output("72 F", ImagePart(), PdfPart(), TextPart()))
                    ]
                }
            ]
        };
        IReadOnlyList<LlmStreamEvent> continuedStream = await ReadAllAsync(
            client.StreamAsync(AnthropicConnection(), continuationRequest));

        Assert.Equal(
            "72 F",
            Assert.Single(continuedStream.OfType<LlmChatCompletionEvent>()).Result.Text);
        using JsonDocument continued = JsonDocument.Parse(handler.Bodies[1]);
        JsonElement continuedMessages = continued.RootElement.GetProperty("messages");
        Assert.Equal(5, continuedMessages.GetArrayLength());
        Assert.Equal("old_tool", continuedMessages[0].GetProperty("content")[0].GetProperty("id").GetString());
        JsonElement assistantContent = continuedMessages[3].GetProperty("content");
        Assert.Equal("thinking", assistantContent[0].GetProperty("type").GetString());
        Assert.Equal("signed-state", assistantContent[0].GetProperty("signature").GetString());
        Assert.Equal("toolu_1", assistantContent[1].GetProperty("id").GetString());
        Assert.Equal("toolu_1", continuedMessages[4].GetProperty("content")[0].GetProperty("tool_use_id").GetString());
        JsonElement resultContent = continuedMessages[4].GetProperty("content")[0].GetProperty("content");
        Assert.Equal("text", resultContent[0].GetProperty("type").GetString());
        Assert.Equal("image", resultContent[1].GetProperty("type").GetString());
        Assert.Equal(
            "iVBORw0KGgo=",
            resultContent[1].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("document", resultContent[2].GetProperty("type").GetString());
        Assert.Equal("report.pdf", resultContent[2].GetProperty("title").GetString());
        Assert.Equal(
            "JVBERi0xLjc=",
            resultContent[2].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("document", resultContent[3].GetProperty("type").GetString());
        Assert.Equal("notes.txt", resultContent[3].GetProperty("title").GetString());
        Assert.Equal(
            "text",
            resultContent[3].GetProperty("source").GetProperty("type").GetString());
        Assert.Equal(
            "plain text",
            resultContent[3].GetProperty("source").GetProperty("data").GetString());

        Assert.True(client.GetCapabilities(AnthropicConnection()).SupportsToolUse);
    }

    [Fact]
    public async Task ToolContractRejectsDuplicateDefinitionsBeforeSending()
    {
        var handler = new SequenceHttpMessageHandler([]);
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        LlmToolDefinition tool = WeatherTool();
        var request = new LlmChatRequest
        {
            ModelId = "chat-tool-test",
            Tools = [tool, tool]
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ReadAllAsync(client.StreamAsync(CompatibleConnection(), request)));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task UnsupportedToolOutputPartsAreOmittedAndReportedWithoutThrowing()
    {
        const string events = """
            data: {"id":"chat_ok","choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        var handler = new SequenceHttpMessageHandler([_ => SseResponse(events)]);
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        LlmToolOutput output = Output(
            "result",
            ImagePart(),
            TextPart(),
            new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", ReadOnlyMemory<byte>.Empty, "empty.png"));
        var request = new LlmChatRequest
        {
            ModelId = "chat-tool-test",
            Messages =
            [
                new LlmChatMessage("assistant", string.Empty) { ToolCalls = [new LlmToolCall("call-1", "lookup", "{}")] },
                new LlmChatMessage("user", string.Empty) { ToolResults = [new LlmToolResult("call-1", output)] }
            ]
        };

        await ReadAllAsync(client.StreamAsync(CompatibleConnection(), request));

        using JsonDocument sent = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(
            "result\n[File: notes.txt]\nplain text",
            sent.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        LlmRichContentSupported support = client.GetCapabilities(CompatibleConnection()).RichContentSupported;
        LlmToolOutputProjection report = ToolOutputProjector.Project(output, support, support).Report;
        Assert.Equal(
            [
                new LlmToolPartProjection(LlmToolPartDelivery.Delivered),
                new LlmToolPartProjection(LlmToolPartDelivery.Omitted, LlmToolPartOmission.UnsupportedByRoute),
                new LlmToolPartProjection(LlmToolPartDelivery.Converted),
                new LlmToolPartProjection(LlmToolPartDelivery.Omitted, LlmToolPartOmission.Empty)
            ],
            report.Parts);
    }

    [Fact]
    public void ImageMediaTypesOutsideTheRouteSetAreOmittedAsUnsupported()
    {
        LlmToolOutput output = Output("see", new LlmToolOutputBinary(LlmBinaryKind.Image, "image/bmp", new byte[] { 1 }, "x.bmp"));

        ProjectedToolOutput projected = ToolOutputProjector.Project(output, RichContentRoutes.Responses, RichContentRoutes.Responses);

        Assert.Equal([new SentText("see")], projected.Parts);
        Assert.Equal(LlmToolPartOmission.UnsupportedByRoute, projected.Report.Parts[1].Omission);
    }

    private static LlmToolDefinition WeatherTool() => new()
    {
        Name = "get_weather",
        Description = "Get current weather.",
        InputSchema = Schema("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"],"additionalProperties":false}""")
    };

    private static LlmAttachment ImageAttachment() => new()
    {
        Kind = LlmBinaryKind.Image,
        FileName = "chart.png",
        Data = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }
    };

    private static LlmAttachment PdfAttachment() => new()
    {
        Kind = LlmBinaryKind.Document,
        FileName = "report.pdf",
        Data = "%PDF-1.7"u8.ToArray()
    };

    private static LlmAttachment TextAttachment() => new()
    {
        Kind = LlmBinaryKind.Document,
        FileName = "notes.txt",
        Data = "plain text"u8.ToArray()
    };

    private static LlmToolOutput Output(string text, params LlmToolOutputPart[] parts) =>
        new() { Parts = [new LlmToolOutputText(text), .. parts] };

    private static LlmToolOutputBinary ImagePart() =>
        new(LlmBinaryKind.Image, "image/png", ImageAttachment().Data, "chart.png");

    private static LlmToolOutputBinary PdfPart() =>
        new(LlmBinaryKind.Document, "application/pdf", PdfAttachment().Data, "report.pdf");

    private static LlmToolOutputBinary TextPart() =>
        new(LlmBinaryKind.Document, "text/plain", TextAttachment().Data, "notes.txt");

    private static LlmConnection ResponsesConnection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.OpenAiResponses,
        Endpoint = "https://api.openai.test/v1",
        Credential = new LlmCredential { ApiKey = "openai-secret" }.Serialize()
    };

    private static LlmConnection CompatibleConnection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.OpenAiCompatibleChat,
        Endpoint = "https://router.test/v1",
        Credential = new LlmCredential { ApiKey = "router-secret" }.Serialize()
    };

    private static LlmConnection CodexConnection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.OpenAiCodex,
        Endpoint = OpenAiCodexLlmClient.DefaultEndpoint,
        Credential = new LlmCredential
        {
            OAuth = new LlmOAuthTokens { AccessToken = "codex-secret" }
        }.Serialize()
    };

    private static LlmConnection AnthropicConnection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.AnthropicMessages,
        Endpoint = "https://api.anthropic.test",
        Credential = new LlmCredential { ApiKey = "anthropic-secret" }.Serialize()
    };

    private static LlmConnection ClaudeConnection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.AnthropicClaudeCli,
        Endpoint = "claude"
    };

    private static LlmConnection CliConnection(LlmTransport transport) => new()
    {
        Version = "v1",
        Transport = transport,
        Endpoint = "unused"
    };

    private static JsonElement Schema(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage SseResponse(string events) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(events, Encoding.UTF8, "text/event-stream")
    };

    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        var results = new List<T>();
        await foreach (T item in source)
        {
            results.Add(item);
        }

        return results;
    }

    private sealed class SequenceHttpMessageHandler(
        IReadOnlyList<Func<HttpRequestMessage, HttpResponseMessage>> responses) : HttpMessageHandler
    {
        private int _index;

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            if (_index >= responses.Count)
            {
                throw new InvalidOperationException("No test response was configured.");
            }

            return responses[_index++](request);
        }
    }

    private sealed class AntigravityRunner : IAntigravityCliRunner
    {
        public Task<AntigravityCliProcessResult> RunAsync(
            AntigravityCliInvocation invocation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }


    private sealed class ClaudeRunner : IClaudeCliRunner
    {
        public Task<ClaudeCliProcessResult> RunAsync(
            ClaudeCliInvocation invocation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
