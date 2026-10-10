using Darkspyre.Inference.Connect.LLM.Internal;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ClaudeCliLlmClientTests
{
    [Fact]
    public async Task ChatArgumentsAreIsolated()
    {
        var runner = new RecordingRunner(new(0,
            """{"type":"result","subtype":"success","is_error":false,"result":"OK","stop_reason":"end_turn"}""",
            string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses());

        await ReadAllAsync(client.StreamAsync(CliConnection(),
            new() { ModelId = "claude-test", Messages = [new("user", "test")] }));

        IReadOnlyList<string> arguments = Assert.Single(runner.Invocations).Arguments;
        int start = arguments.ToList().IndexOf(ClaudeCliConfiguration.IsolationArguments[0]);
        Assert.Equal(ClaudeCliConfiguration.IsolationArguments, arguments.Skip(start).Take(ClaudeCliConfiguration.IsolationArguments.Count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputLimitReachesTheChildEnvironmentForBothChatPaths(bool streaming)
    {
        var runner = new RecordingRunner(new(0,
            """{"type":"result","subtype":"success","is_error":false,"result":"OK","stop_reason":"end_turn"}""",
            string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses());
        LlmConnection connection = CliConnection() with
        { CapabilityOverrides = new() { SupportsStreaming = streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported } };
        Assert.Equal(LlmOutputLimitEnforcement.Unknown, client.GetCapabilities(connection, "claude-test").OutputLimitEnforcement);
        string? inherited = Environment.GetEnvironmentVariable("CLAUDE_CODE_MAX_OUTPUT_TOKENS");

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(connection,
            new() { ModelId = "claude-test", Messages = [new("user", "test")], MaxOutputTokens = 123 }));

        Assert.Equal("OK", Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Text);
        ClaudeCliInvocation invocation = Assert.Single(runner.Invocations);
        Assert.Equal(123, invocation.MaxOutputTokens);
        Assert.Equal("123", ClaudeCliProcessRunner.CreateStartInfo(invocation).Environment["CLAUDE_CODE_MAX_OUTPUT_TOKENS"]);
        Assert.Equal(inherited, Environment.GetEnvironmentVariable("CLAUDE_CODE_MAX_OUTPUT_TOKENS"));
        ClaudeCliProcessRunner.CreateStartInfo(invocation with { MaxOutputTokens = null })
            .Environment.TryGetValue("CLAUDE_CODE_MAX_OUTPUT_TOKENS", out string? unchanged);
        Assert.Equal(inherited, unchanged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogEffortFactsControlAliasAdmissionThroughTheFacade(bool supportsEffort)
    {
        var process = new CatalogProcess
        {
            ModelInfo = JsonElement.Parse(supportsEffort
                ? """{"value":"cli-returned-alias","displayName":"CLI model choice","supportsEffort":true,"supportedEffortLevels":["low"]}"""
                : """{"value":"cli-returned-alias","displayName":"CLI model choice","supportsEffort":false}"""
            )
        };
        var runner = new RecordingRunner(new(0,
            """{"type":"result","subtype":"success","is_error":false,"result":"OK","stop_reason":"end_turn"}""",
            string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, process);
        LlmConnection connection = CliConnection() with
        { CapabilityOverrides = new() { ReasoningLevels = ClaudeCliLlmClient.ReasoningLevels } };
        await using var service = new LlmProviderService(new ResourcePreparationTests.Source(connection), [client]);

        LlmProviderCapabilities capabilities = await service.GetModelCapabilitiesAsync("connection", "cli-returned-alias");
        Assert.True(capabilities.ReasoningLevels.SetEquals(supportsEffort ? [LlmReasoningLevel.Low] : []));
        var request = new LlmChatRequest
        {
            ModelId = "cli-returned-alias",
            Messages = [new("user", "Reply with OK.")],
            ReasoningLevel = LlmReasoningLevel.ExtraHigh
        };
        await ReadAllAsync(service.StreamAsync("connection", request));
        IReadOnlyList<string> mapped = Assert.Single(runner.Invocations).Arguments;
        // Without an effort control the effort is not sent.
        if (supportsEffort) Assert.Equal("low", mapped[mapped.ToList().IndexOf("--effort") + 1]);
        else Assert.DoesNotContain("--effort", mapped);

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(service.StreamAsync("connection",
            request with { ReasoningLevel = supportsEffort ? LlmReasoningLevel.Low : null }));
        Assert.Equal(LlmCompletionStatus.Completed, Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Status);
        Assert.Equal(1, process.Initializations);
        IReadOnlyList<string> arguments = runner.Invocations.Last().Arguments;
        Assert.Equal("cli-returned-alias", arguments[arguments.ToList().IndexOf("--model") + 1]);
    }

    [Theory]
    [InlineData("claude-sonnet-4-6")]
    [InlineData("claude-opus-4-6")]
    [InlineData("claude-sonnet-4-6[1m]")]
    [InlineData("claude-opus-4-6[1m]")]
    public async Task ExplicitClaude46ProfilesMapExtraHighDownBeforeInvocation(string model)
    {
        var runner = new RecordingRunner(new(0,
            """{"type":"result","subtype":"success","is_error":false,"result":"OK","stop_reason":"end_turn"}""",
            string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses());
        LlmConnection connection = CliConnection() with
        {
            ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides>
            {
                [model] = new() { ReasoningLevels = ClaudeCliLlmClient.ReasoningLevels }
            }
        };
        IReadOnlySet<LlmReasoningLevel> offered = client.GetCapabilities(connection, model).ReasoningLevels;
        Assert.True(offered.SetEquals([LlmReasoningLevel.Low, LlmReasoningLevel.Medium,
            LlmReasoningLevel.High, LlmReasoningLevel.Maximum]));
        var request = new LlmChatRequest
        {
            ModelId = model,
            Messages = [new("user", "Reply with OK.")],
            ReasoningLevel = LlmReasoningLevel.ExtraHigh
        };

        await ReadAllAsync(client.StreamAsync(connection, request));
        IReadOnlyList<string> mapped = Assert.Single(runner.Invocations).Arguments;
        Assert.Equal("high", mapped[mapped.ToList().IndexOf("--effort") + 1]);

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(connection,
            request with { ReasoningLevel = LlmReasoningLevel.Maximum }));
        Assert.Equal(LlmCompletionStatus.Completed, Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Status);
        IReadOnlyList<string> arguments = runner.Invocations.Last().Arguments;
        Assert.Equal("max", arguments[arguments.ToList().IndexOf("--effort") + 1]);
        LlmConnection narrowed = CliConnection() with
        { CapabilityOverrides = new() { ReasoningLevels = LlmReasoningEffort.Levels(LlmReasoningLevel.Low) } };
        Assert.True(client.GetCapabilities(narrowed, model).ReasoningLevels.SetEquals([LlmReasoningLevel.Low]));
        Assert.Contains(LlmReasoningLevel.ExtraHigh, client.GetCapabilities(CliConnection(), "sonnet").ReasoningLevels);
        Assert.Contains(LlmReasoningLevel.ExtraHigh, client.GetCapabilities(CliConnection(), "unrecognized-model").ReasoningLevels);
    }

    [Theory]
    [InlineData(false, LlmReasoningLevel.None)]
    [InlineData(true, LlmReasoningLevel.Minimal)]
    public async Task BelowMinimumEffortUsesTheLowestCliSettingForBothChatPaths(bool streaming, LlmReasoningLevel effort)
    {
        var runner = new RecordingRunner(new(0,
            """{"type":"result","subtype":"success","is_error":false,"result":"OK","stop_reason":"end_turn"}""", string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses());
        LlmConnection connection = CliConnection() with
        { CapabilityOverrides = new() { SupportsStreaming = streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported } };
        var request = new LlmChatRequest
        { ModelId = "unrecognized-model", Messages = [new("user", "test")], ReasoningLevel = effort };

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(connection, request));

        Assert.Equal("OK", Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Text);
        IReadOnlyList<string> arguments = Assert.Single(runner.Invocations).Arguments;
        Assert.Equal("low", arguments[arguments.ToList().IndexOf("--effort") + 1]);
        Assert.Equal(effort, request.ReasoningLevel);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    public async Task FailedTerminalPreservesSanitizedErrorsAndActualExitCode(bool streaming, int exitCode)
    {
        const string detail = """{"access_token":"diagnostic-secret","detail":"The selected model is unavailable."}""";
        string output = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "error_during_execution",
            ["is_error"] = true,
            ["errors"] = new JsonArray(JsonValue.Create(detail), JsonValue.Create("Quota exceeded. catalog-secret"))
        }.ToJsonString();
        var runner = new RecordingRunner(new(exitCode, output, string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses());
        LlmConnection connection = CliConnection() with
        {
            Credential = new LlmCredential { OAuth = new() { AccessToken = "catalog-secret" } }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = (streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported) }
        };

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() => ReadAllAsync(
            client.StreamAsync(connection, new() { ModelId = "claude-test", Messages = [new("user", "test")] })));

        Assert.Equal(exitCode == 0 ? "claude_cli_error" : "claude_cli_exit_1", failure.ProviderCode);
        Assert.Contains("The selected model is unavailable.", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Quota exceeded.", failure.Message, StringComparison.Ordinal);
        Assert.True(failure.Message.IndexOf("The selected model", StringComparison.Ordinal)
            < failure.Message.IndexOf("Quota exceeded", StringComparison.Ordinal));
        Assert.Contains("[REDACTED]", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("catalog-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.Single(runner.Invocations);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task SuccessfulSchemaToolStopRequiresTheNativeStructuredPayload(bool streaming, bool nativePayload)
    {
        const string answer = """{"marker":"fixture"}""";
        string output = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = false,
            ["stop_reason"] = "tool_use",
            ["result"] = answer,
            ["structured_output"] = nativePayload ? JsonNode.Parse(answer) : null
        }.ToJsonString();
        var runner = new RecordingRunner(new(0, output, string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses());
        LlmConnection connection = CliConnection() with { CapabilityOverrides = new() { SupportsStreaming = (streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported) } };
        var request = new LlmChatRequest
        {
            ModelId = "claude-test",
            Messages = [new("user", "Return the marker")],
            StructuredOutput = new()
            {
                Name = "marker",
                Schema = JsonElement.Parse("""
                    {"type":"object","properties":{"marker":{"type":"string"}},"required":["marker"],"additionalProperties":false}
                    """)
            }
        };

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(connection, request));
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;

        Assert.Equal(nativePayload ? LlmCompletionStatus.Completed : LlmCompletionStatus.Unknown, result.Status);
        Assert.Equal("tool_use", result.FinishReason);
        Assert.Equal(answer, result.Text);
        Assert.True(result.StructuredOutput!.IsValid);
        Assert.Equal(LlmStructuredOutputEnforcement.ProviderJsonSchema, result.StructuredOutput.Enforcement);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task AccountUsageFailurePreservesSanitizedResultDetail()
    {
        const string detail = """{"refresh_token":"usage-secret","detail":"Account usage is unavailable for this organization."}""";
        var runner = new RecordingRunner(new(0,
            new JsonObject { ["type"] = "result", ["is_error"] = true, ["result"] = detail }.ToJsonString(), string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses());

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() => client.GetAccountUsageAsync(CliConnection()));

        Assert.Equal("claude_cli_usage_error", failure.ProviderCode);
        Assert.Contains("Account usage is unavailable for this organization.", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("usage-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task CatalogOAuthProtocolFailureRedactsTheActualHttpHeadersWithoutLaunchingTheCli()
    {
        using var handler = new UnexpectedHttpHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(new Uri("https://api.anthropic.com/v1/models?limit=1000"), request.RequestUri);
            Assert.Equal("test-token", request.Headers.Authorization!.Parameter);
            Assert.Equal("header-value", Assert.Single(request.Headers.GetValues("X-Trace")));
            Assert.Equal("default-value", Assert.Single(request.Headers.GetValues("X-Default")));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("not-json test-token header-value default-value") };
            response.Headers.Add("x-request-id", "request-test-token-header-value-default-value-1");
            return response;
        });
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("X-Trace", "header-value");
        http.DefaultRequestHeaders.Add("X-Default", "default-value");
        var runner = new RecordingRunner(new(0, string.Empty, string.Empty));
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses());
        LlmConnection connection = CliConnection() with
        { Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize() };

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() => client.ListModelsAsync(connection));

        Assert.Equal(HttpStatusCode.OK, failure.StatusCode);
        Assert.Equal("request-[REDACTED]-[REDACTED]-[REDACTED]-1", failure.RequestId);
        Assert.Equal("Anthropic model discovery returned invalid JSON.", failure.Message);
        Assert.IsAssignableFrom<JsonException>(failure.InnerException);
        foreach (string secret in new[] { "test-token", "header-value", "default-value" })
            Assert.DoesNotContain(secret, failure.ToString() + failure.SanitizedResponseBody, StringComparison.Ordinal);
        Assert.Empty(runner.Invocations);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ModelDiscoveryWithoutCatalogOAuthPreservesCliChoicesAndRetiresItsProcess()
    {
        var process = new CatalogProcess();
        var runner = new RecordingRunner(new(0, string.Empty, string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, process);

        IReadOnlyList<LlmModel> models = await client.ListModelsAsync(CliConnection());

        LlmModel model = Assert.Single(models);
        Assert.Equal("cli-returned-alias", model.Id);
        Assert.Equal("CLI model choice", model.DisplayName);
        Assert.Equal("preserved", model.CatalogEntry!.Value.GetProperty("unmappedOption").GetString());
        Assert.Empty(runner.Invocations);
        Assert.True(process.Disposed);
        Assert.False(Directory.Exists(process.Invocation!.WorkingDirectory));
    }

    [Fact]
    public async Task CancellationDuringCatalogRetirementWaitsForProcessCleanup()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new CatalogProcess { DisposalRelease = release.Task };
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, new RecordingRunner(new(0, string.Empty, string.Empty)), process);
        using var cancellation = new CancellationTokenSource();
        Task<IReadOnlyList<LlmModel>> listing = client.ListModelsAsync(CliConnection(), cancellation.Token);
        try
        {
            await process.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(Directory.Exists(process.Invocation!.WorkingDirectory));
            cancellation.Cancel();
            Assert.False(listing.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.True(process.Disposed);
        Assert.False(Directory.Exists(process.Invocation!.WorkingDirectory));
    }

    [Fact]
    public async Task ToolHistoryReachesThePromptAsLabelledBlocks()
    {
        var runner = new RecordingRunner(new ClaudeCliProcessResult(
            0,
            """
            {"type":"system","subtype":"init","session_id":"session-1"}
            {"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"522,000"}}}
            {"type":"result","subtype":"success","is_error":false,"result":"522,000","session_id":"session-1","stop_reason":"end_turn","terminal_reason":"completed","usage":{"input_tokens":7,"output_tokens":2}}
            """,
            string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses());

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(
            CliConnection(), new LlmChatRequest { ModelId = "claude-test", Messages = ToolHistory() }));

        Assert.Equal("522,000", Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Text);
        string input = Assert.Single(runner.Invocations).StandardInput!;
        Assert.Contains("[TOOL CALL id=c1 name=lookup]", input, StringComparison.Ordinal);
        Assert.Contains("[TOOL RESULT id=c1 status=ok]", input, StringComparison.Ordinal);
        Assert.Equal(LlmToolHistory.Text, client.GetCapabilities(CliConnection()).ToolHistory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolBinaryPromptBlocksMatchTheSelectedCarriageAndCountInTheEstimate(bool restricted)
    {
        var runner = new RecordingRunner(new(0,
            """
            {"type":"result","subtype":"success","is_error":false,"result":"answer","session_id":"session-1","stop_reason":"end_turn"}
            """, string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses());
        LlmConnection connection = CliConnection() with
        { CapabilityOverrides = restricted ? new() { SupportsToolResultImages = InferenceSupport.Unsupported, SupportsToolResultDocuments = InferenceSupport.Unsupported } : null };
        byte[] image = [0xff, 0xd8, 0xff];
        byte[] pdf = "%PDF-1.7\nactual tool content"u8.ToArray();
        var output = new LlmToolOutput
        {
            Parts = [new LlmToolOutputText("tool text"),
                new LlmToolOutputBinary(LlmBinaryKind.Image, "image/jpeg", image, "tool.jpg"),
                new LlmToolOutputBinary(LlmBinaryKind.Document, "application/pdf", pdf, "tool.pdf")]
        };
        var request = new LlmChatRequest
        {
            ModelId = "claude-test",
            Messages = [new("assistant", string.Empty) { ToolCalls = [new LlmToolCall("c1", "lookup", "{}")] },
                new("user", string.Empty) { ToolResults = [new LlmToolResult("c1", output)] }, new("user", "Compare the files")]
        };
        ProjectedToolOutput projected = client.ProjectToolOutput(connection, request.ModelId, output);
        Assert.Equal(restricted ? LlmToolPartDelivery.Omitted : LlmToolPartDelivery.Delivered, projected.Report.Parts[1].Delivery);
        Assert.Equal(restricted ? LlmToolPartDelivery.Omitted : LlmToolPartDelivery.Delivered, projected.Report.Parts[2].Delivery);
        // A delivered image costs about 1,600 tokens and each PDF page its text plus its page image.
        if (!restricted) Assert.True(client.EstimateInputTokens(connection, request, ResourceSizes.None)!.Value.Tokens >= 1_600 + 4_600);
        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(connection, request));
        Assert.Equal("answer", Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Text);
        using JsonDocument input = JsonDocument.Parse(Assert.Single(runner.Invocations).StandardInput!);
        JsonElement[] content = input.RootElement.GetProperty("message").GetProperty("content").EnumerateArray().ToArray();
        Assert.Contains("tool text", content[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal(restricted ? ["text"] : ["text", "image", "document"], content.Select(part => part.GetProperty("type").GetString()));
        if (!restricted)
        {
            Assert.Equal(image, Convert.FromBase64String(content[1].GetProperty("source").GetProperty("data").GetString()!));
            Assert.Equal("image/jpeg", content[1].GetProperty("source").GetProperty("media_type").GetString());
            Assert.Equal(pdf, Convert.FromBase64String(content[2].GetProperty("source").GetProperty("data").GetString()!));
            Assert.Equal("tool.pdf", content[2].GetProperty("title").GetString());
        }
        Assert.Equal(InferenceSupport.Unsupported, client.GetCapabilities(connection, request.ModelId).RichContentSupported.PreservesOrder);
    }

    private static LlmChatMessage[] ToolHistory() =>
    [
        new LlmChatMessage("user", "Population of Lyon?"),
        new LlmChatMessage("assistant", string.Empty) { ToolCalls = [new LlmToolCall("c1", "lookup", """{"city":"Lyon"}""")] },
        new LlmChatMessage("user", string.Empty) { ToolResults = [new LlmToolResult("c1", LlmToolOutput.FromText("522,250"))] },
        new LlmChatMessage("user", "Round it."),
    ];

    private static LlmConnection CliConnection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.AnthropicClaudeCli,
        Endpoint = "claude",
        Credential = null
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

    private sealed class RecordingRunner(ClaudeCliProcessResult result) : IClaudeCliRunner
    {
        public List<ClaudeCliInvocation> Invocations { get; } = [];
        public Action<ClaudeCliInvocation>? OnInvocation { get; init; }

        public Task<ClaudeCliProcessResult> RunAsync(
            ClaudeCliInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(invocation);
            OnInvocation?.Invoke(invocation);
            return Task.FromResult(result);
        }
    }

    private sealed class UnexpectedHttpHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return respond is null ? throw new InvalidOperationException("OAuth prompt unexpectedly used HTTP.")
                : Task.FromResult(respond(request));
        }
    }

    private sealed class NoSessionProcesses : IClaudeCliSessionProcessFactory
    {
        public Task<IClaudeCliSessionProcess> StartAsync(
            ClaudeCliSessionInvocation invocation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CatalogProcess : IClaudeCliSessionProcessFactory, IClaudeCliSessionProcess
    {
        private string? _reply;
        internal JsonElement ModelInfo { get; init; } = JsonElement.Parse(
            """{"value":"cli-returned-alias","displayName":"CLI model choice","unmappedOption":"preserved"}""");
        internal int Initializations { get; private set; }
        public ClaudeCliSessionInvocation? Invocation { get; private set; }
        public Task? DisposalRelease { get; init; }
        public TaskCompletionSource DisposalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public Task<IClaudeCliSessionProcess> StartAsync(ClaudeCliSessionInvocation invocation, CancellationToken cancellationToken = default)
        {
            Invocation = invocation;
            return Task.FromResult<IClaudeCliSessionProcess>(this);
        }

        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement request = document.RootElement;
            Assert.Equal("control_request", request.GetProperty("type").GetString());
            Assert.Equal("initialize", request.GetProperty("request").GetProperty("subtype").GetString());
            Initializations++;
            _reply = new JsonObject
            {
                ["type"] = "control_response",
                ["response"] = new JsonObject
                {
                    ["subtype"] = "success",
                    ["request_id"] = request.GetProperty("request_id").GetString(),
                    ["response"] = new JsonObject
                    {
                        ["models"] = new JsonArray(JsonNode.Parse(ModelInfo.GetRawText()))
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
            throw new InvalidOperationException("Model discovery must consume the initialize reply before process retirement.");

        public async ValueTask DisposeAsync()
        {
            DisposalEntered.TrySetResult();
            if (DisposalRelease is not null) await DisposalRelease;
            Disposed = true;
        }
    }
}
