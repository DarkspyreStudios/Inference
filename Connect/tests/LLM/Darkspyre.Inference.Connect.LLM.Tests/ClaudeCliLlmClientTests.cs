using Darkspyre.Inference.Connect.LLM.Internal;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ClaudeCliLlmClientTests
{
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
            CapabilityOverrides = new() { SupportsStreaming = streaming }
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
    public async Task ToolBinaryPromptBlocksMatchTheSelectedCarriageAndDoNotProduceATextOnlyEstimate(bool restricted)
    {
        var runner = new RecordingRunner(new(0,
            """
            {"type":"result","subtype":"success","is_error":false,"result":"answer","session_id":"session-1","stop_reason":"end_turn"}
            """, string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses());
        LlmConnection connection = CliConnection() with
        { CapabilityOverrides = restricted ? new() { SupportsToolResultImages = false, SupportsToolResultDocuments = false } : null };
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
        if (!restricted) Assert.Null(client.EstimateInputTokens(connection, request));
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
        Assert.False(client.GetCapabilities(connection, request.ModelId).RichContentSupported.PreservesOrder);
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

    private sealed class UnexpectedHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("OAuth prompt unexpectedly used HTTP.");
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
            _reply = new JsonObject
            {
                ["type"] = "control_response",
                ["response"] = new JsonObject
                {
                    ["subtype"] = "success",
                    ["request_id"] = request.GetProperty("request_id").GetString(),
                    ["response"] = JsonNode.Parse("""
                        {"models":[{"value":"cli-returned-alias","displayName":"CLI model choice","unmappedOption":"preserved"}]}
                        """)
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
