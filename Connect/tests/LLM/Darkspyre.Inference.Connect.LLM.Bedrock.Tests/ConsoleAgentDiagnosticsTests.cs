using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.ConsoleTester;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class ConsoleAgentDiagnosticsTests
{
    [Theory]
    [InlineData(false, "Inference error [InvalidRequest]")]
    [InlineData(true, "Provider error [antigravity_cli_handshake_timeout]")]
    public async Task ProbeFailureRetainsPublicDiagnosticsWithoutItsPrivateCause(bool providerFailure, string label)
    {
        const string message = "The selected CLI did not report its conversation before the handshake deadline.";
        var cause = new IOException("private provider response");
        Exception failure = providerFailure
            ? new LlmProviderException(message, LlmTransport.GoogleAntigravityCli,
                providerCode: "antigravity_cli_handshake_timeout", innerException: cause)
            : new InferenceException(InferenceFailureCode.InvalidRequest, message, cause);
        using var report = new StringWriter();

        int exitCode = await ProbeRunner.RunAsync(
            [new("opening", _ => Task.FromException<string>(failure))],
            new("probe-model", "opening", 1, 5), true, report, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("FAIL 1/1 opening", report.ToString(), StringComparison.Ordinal);
        Assert.Contains($"{label}: {message}", report.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(cause.Message, report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void InferenceFailurePrintsItsCodeAndPublicMessageWithoutItsPrivateCause()
    {
        const string message = "The selected model does not accept reasoning level 'Medium'. It accepts: Low.";
        var failure = new InferenceException(InferenceFailureCode.InvalidRequest, message,
            new IOException("private provider response"));
        using var diagnostics = new StringWriter();

        diagnostics.WriteLine(Program.FormatInferenceFailure(failure));

        Assert.Equal($"Inference error [InvalidRequest]: {message}{Environment.NewLine}", diagnostics.ToString());
    }

    [Fact]
    public void TextDocumentToolDoesNotOfferAnUnsupportedImageArgument()
    {
        LlmAgentToolDefinition tool = Program.CreateAttachmentTool(supportsDocuments: true, supportsImages: false);
        Assert.Equal("document", Assert.Single(tool.InputSchema.GetProperty("properties").GetProperty("kind")
            .GetProperty("enum").EnumerateArray()).GetString());
        Assert.Contains("text document", tool.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LlmAgentCompletionReason.Completed, 0)]
    [InlineData(LlmAgentCompletionReason.ProviderFailure, 1)]
    public async Task ResumeProbeRequiresSuccessfulTerminalAfterMatchingText(
        LlmAgentCompletionReason reason, int expectedExitCode)
    {
        await using var agents = new LlmAgentProviderService(new ProbeConnections(), [new ResumeClient(reason)]);
        var probe = new AntigravityProbe(agents, "probe-connection", "probe-model", Environment.CurrentDirectory, new());
        using var report = new StringWriter();

        int exitCode = await ProbeRunner.RunAsync(probe.Cases,
            new("probe-model", "resume", 1, 5), true, report, CancellationToken.None);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(2, probe.Turns);
        Assert.Contains(expectedExitCode == 0 ? "PASS 1/1 resume" : "The resumed turn did not complete.",
            report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void UsageOutputPreservesUnknownQuotaFactsWithReflectionDisabled()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var retrieved = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var usage = new LlmAccountUsageSnapshot
        {
            RetrievedAt = retrieved,
            Quotas = [new() { Id = "5h", State = LlmQuotaState.Unknown }]
        };
        using var diagnostics = new StringWriter();

        Program.WriteUsage(usage, diagnostics);

        string output = diagnostics.ToString();
        Assert.Contains("\n  \"RetrievedAt\"", output, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(output);
        Assert.Equal(retrieved, json.RootElement.GetProperty("RetrievedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("Plan").ValueKind);
        JsonElement quota = Assert.Single(json.RootElement.GetProperty("Quotas").EnumerateArray());
        Assert.Equal("5h", quota.GetProperty("Id").GetString());
        Assert.Equal("Unknown", quota.GetProperty("State").GetString());
        Assert.Equal(JsonValueKind.Null, quota.GetProperty("Remaining").ValueKind);
        Assert.Equal(JsonValueKind.Null, quota.GetProperty("RemainingFraction").ValueKind);
    }

    [Fact]
    public void ManagedContextPersistenceRetainsDocumentBytesWithReflectionDisabled()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        byte[] document = "attachment smoke test document"u8.ToArray();
        var context = new LlmAgentContext
        {
            Revision = 7,
            SelectedResources = [new("workspace", "reference")],
            ResourceSelectionVersion = new("workspace", "session", 3),
            Entries =
            [
                new("prompt", "turn", new("user", "Read the reference.")),
                new("call", "turn", new("assistant", string.Empty)
                {
                    ToolCalls = [new LlmToolCall("attachment", "read", "{}")]
                }),
                new("result", "turn", new("user", string.Empty)
                {
                    ToolResults = [new LlmToolResult("attachment", new()
                    {
                        Parts = [new LlmToolOutputBinary(LlmBinaryKind.Document,
                            "text/plain", document, "reference.txt")]
                    })]
                })
            ]
        };

        LlmAgentContext restored = ManagedAgentProbe.RoundTripContext(context);

        Assert.Equal(3, restored.FormatVersion);
        Assert.Equal(context.Revision, restored.Revision);
        Assert.Equal(context.SelectedResources, restored.SelectedResources);
        Assert.Equal(context.ResourceSelectionVersion, restored.ResourceSelectionVersion);
        Assert.Equal(context.Entries.Select(entry => entry.Id), restored.Entries.Select(entry => entry.Id));
        Assert.Equal("attachment", Assert.Single(restored.Entries[1].Message.ToolCalls).Id);
        var result = Assert.IsType<LlmToolResult>(Assert.Single(restored.Entries[2].Message.ToolResults));
        Assert.Equal("attachment", result.ToolCallId);
        var binary = Assert.IsType<LlmToolOutputBinary>(Assert.Single(result.Output.Parts));
        Assert.Equal((LlmBinaryKind.Document, "text/plain", "reference.txt"),
            (binary.Kind, binary.MediaType, binary.FileName));
        Assert.Equal(document, binary.Data.ToArray());
    }

    [Fact]
    public void FailedAgentCompletionPrintsItsReasonAndExistingError()
    {
        using var diagnostics = new StringWriter();
        var completed = new LlmAgentTurnCompletedEvent("thread", "turn", LlmAgentCompletionReason.ProviderFailure,
            "The selected model requires an inference profile.");

        Program.WriteAgentCompletion(completed, diagnostics);

        string output = diagnostics.ToString();
        Assert.Contains("status=Failed", output, StringComparison.Ordinal);
        Assert.Contains("reason=ProviderFailure", output, StringComparison.Ordinal);
        Assert.Contains("[error] The selected model requires an inference profile.", output, StringComparison.Ordinal);
    }

    private sealed class ProbeConnections : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(new()
            {
                Version = "1",
                Transport = LlmTransport.GoogleAntigravityCli,
                Endpoint = "agy"
            });

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ResumeClient(LlmAgentCompletionReason resumedReason) : ILlmAgentResumeClient
    {
        private string? _remembered;
        public LlmTransport Transport => LlmTransport.GoogleAntigravityCli;

        public Task<ILlmAgentSession> StartSessionAsync(string connectionId, LlmConnection connection,
            LlmAgentSessionOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult<ILlmAgentSession>(new ProbeSession("probe-thread", LlmAgentCompletionReason.Completed, request =>
            {
                _remembered = Assert.IsType<LlmAgentTextContent>(Assert.Single(request.Input)).Text;
                return "OK";
            }));

        public Task<ILlmAgentSession> ResumeSessionAsync(string connectionId, LlmConnection connection, string threadId,
            LlmAgentResumeOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<ILlmAgentSession>(new ProbeSession(threadId, resumedReason,
                _ => _remembered ?? throw new InvalidOperationException("The first turn did not run.")));
    }

    private sealed class ProbeSession(string threadId, LlmAgentCompletionReason reason,
        Func<LlmAgentTurnRequest, string> answer) : ILlmAgentSession
    {
        public string ThreadId => threadId;
        public string SessionId => "probe-session";
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async IAsyncEnumerable<LlmAgentEvent> RunTurnAsync(LlmAgentTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new LlmAgentMessageCompletedEvent(threadId, "probe-turn", "message", answer(request));
            yield return new LlmAgentTurnCompletedEvent(threadId, "probe-turn", reason,
                reason == LlmAgentCompletionReason.Completed ? null : "Failure after matching text.");
        }

        public Task<LlmRequestAccounting> MeasureContextAsync(LlmAgentContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmAgentCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmAgentContextStatus> GetContextStatusAsync(LlmAgentTurnRequest? turn = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmAgentContext> CaptureContextAsync(LlmAgentContextCaptureOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReplaceContextAsync(LlmAgentContext context, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmAgentContext> CompactWithProviderAsync(LlmProviderCompactionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<LlmAgentEvent> ContinueTurnAsync(LlmAgentTurnContinuation continuation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SteerAsync(string turnId, IReadOnlyList<LlmAgentContent> input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task InterruptAsync(string turnId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<LlmAgentEvent> ObserveAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LlmAgentResource>> GetResourcesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmAgentResourceOutput> ReadResourceOutputAsync(string resourceId, string? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmAgentResourceStopStatus> StopResourceAsync(string resourceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
