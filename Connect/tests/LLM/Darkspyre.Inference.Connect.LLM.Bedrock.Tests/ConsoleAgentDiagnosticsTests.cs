using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.ConsoleTester;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using SecureStore;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class ConsoleAgentDiagnosticsTests
{
    [Fact]
    public void StrictProbeChecksTheOriginalRequiredEnumAndClosedObject()
    {
        LlmAgentToolDefinition tool = GrokProbe.CreateStrictTool();
        Assert.True(tool.Strict);
        Assert.Equal("marker", Assert.Single(tool.InputSchema.GetProperty("required").EnumerateArray()).GetString());
        Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("CONNECT_STRICT_ARGUMENT_OK", Assert.Single(tool.InputSchema.GetProperty("properties")
            .GetProperty("marker").GetProperty("enum").EnumerateArray()).GetString());
        GrokProbe.ValidateStrictArguments(JsonElement.Parse("""{"marker":"CONNECT_STRICT_ARGUMENT_OK"}"""));
        Assert.Throws<ProbeFailure>(() => GrokProbe.ValidateStrictArguments(
            JsonElement.Parse("""{"marker":"wrong"}""")));
        Assert.Throws<ProbeFailure>(() => GrokProbe.ValidateStrictArguments(
            JsonElement.Parse("""{"marker":"CONNECT_STRICT_ARGUMENT_OK","extra":true}""")));
    }

    [Fact]
    public void GrokProbeUsesTheSelectedRegionAndRefusesAnotherProvider()
    {
        using var http = new HttpClient();
        var providers = new ConsoleTesterProviders([new XaiResponsesLlmClient(http), new OpenAiResponsesLlmClient(http)]);
        ConsoleInvocation invocation = ConsoleInvocation.Parse(
            ["grok-probe", "--provider", "grok-subscription", "--case", "files", "--xai-region", "UnitedStates"], providers);

        Assert.Equal("https://us.api.x.ai/v1", Assert.IsType<XaiConnectionOptions>(invocation.Xai).GetEndpoint());
        Assert.Equal(["--case", "files"], invocation.CommandArguments);
        Assert.Throws<ArgumentException>(() => ConsoleInvocation.Parse(
            ["grok-probe", "--provider", "openai-api-key"], providers));
    }

    [Fact]
    public async Task FilesProbeComparesOriginalBytesAndDeletesOnlyItsOwnUpload()
    {
        var files = new SyntheticFiles();
        using var output = new StringWriter();

        string result = await GrokProbe.FilesAsync(files, "connection", 5, output, CancellationToken.None);

        Assert.Contains("original download verified", result, StringComparison.Ordinal);
        Assert.Equal("created-file", Assert.Single(files.DeletedIds));
        Assert.NotNull(files.Bytes);
        Assert.Contains("Synthetic file deletion confirmed.", output.ToString(), StringComparison.Ordinal);

        var corrupted = new SyntheticFiles { CorruptDownload = true };
        ProbeFailure mismatch = await Assert.ThrowsAsync<ProbeFailure>(() =>
            GrokProbe.FilesAsync(corrupted, "connection", 5, output, CancellationToken.None));
        Assert.Contains("bytes do not match", mismatch.Message, StringComparison.Ordinal);
        Assert.Equal("created-file", Assert.Single(corrupted.DeletedIds));
    }

    [Fact]
    public async Task FilesProbeRetainsOperationAndCleanupFailuresWithoutDeletingOtherFiles()
    {
        var operation = new IOException("Download failed.");
        var cleanup = new IOException("Deletion failed.");
        var files = new SyntheticFiles { DownloadFailure = operation, DeleteFailure = cleanup };
        using var output = new StringWriter();

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() =>
            GrokProbe.FilesAsync(files, "connection", 5, output, CancellationToken.None));

        Assert.Equal([operation, cleanup], failure.InnerExceptions);
        Assert.Equal("created-file", Assert.Single(files.DeletedIds));
        Assert.Contains("Synthetic file cleanup failed: IOException", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("deletion confirmed", output.ToString(), StringComparison.Ordinal);
    }

    private sealed class SyntheticFiles : ILlmFileClient
    {
        private LlmProviderFile? _created;
        public byte[]? Bytes { get; private set; }
        public List<string> DeletedIds { get; } = [];
        public Exception? DownloadFailure { get; init; }
        public Exception? DeleteFailure { get; init; }
        public bool CorruptDownload { get; init; }

        public async Task<LlmProviderFile> UploadAsync(string connectionId, Stream source, string fileName,
            LlmFileUploadOptions? options = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal(TimeSpan.FromHours(1), options?.ExpiresAfter);
            using var content = new MemoryStream();
            await source.CopyToAsync(content, cancellationToken);
            Bytes = content.ToArray();
            _created = new("created-file", fileName, Bytes.Length, DateTimeOffset.UtcNow, null);
            return _created;
        }

        public Task<LlmProviderFile> GetAsync(string connectionId, string fileId,
            CancellationToken cancellationToken = default) => Task.FromResult(_created!);

        public Task<LlmFilePage> ListAsync(string connectionId, LlmFileListRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new LlmFilePage(
                [new("unrelated-file", "existing.txt", 1, DateTimeOffset.UtcNow, null), _created!], null, true));

        public async Task<ResourceDownloadReceipt> DownloadAsync(string connectionId, string fileId,
            Stream destination, LlmFileDownloadOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (DownloadFailure is not null) throw DownloadFailure;
            Assert.Equal(LlmFileContentFormat.Original, options?.Format);
            byte[] content = Bytes!.ToArray();
            if (CorruptDownload) content[0] ^= 1;
            await destination.WriteAsync(content, cancellationToken);
            return new(Bytes!.Length, "text/plain", Bytes.Length);
        }

        public Task<LlmFileDeletion> DeleteAsync(string connectionId, string fileId,
            CancellationToken cancellationToken = default)
        {
            DeletedIds.Add(fileId);
            return DeleteFailure is not null ? Task.FromException<LlmFileDeletion>(DeleteFailure)
                : Task.FromResult(new LlmFileDeletion(fileId, true));
        }
    }

    [Theory]
    [InlineData(true, null, null, "unknown in / unknown out")]
    [InlineData(true, null, 0, "unknown in / 0 out")]
    [InlineData(true, 0, null, "0 in / unknown out")]
    [InlineData(true, 12, 34, "12 in / 34 out")]
    [InlineData(false, null, null, "unavailable")]
    public void CompletionUsageDistinguishesUnknownCountsFromObservedZero(
        bool hasUsage, int? inputTokens, int? outputTokens, string expectedUsage)
    {
        using var diagnostics = new StringWriter();
        var result = new LlmChatResult
        {
            Text = "answer",
            Status = LlmCompletionStatus.Completed,
            Usage = hasUsage ? new(inputTokens, outputTokens, null) : null,
            Diagnostics = new(200, null, null)
        };

        Program.WriteCompletion(result, diagnostics);

        using var lines = new StringReader(diagnostics.ToString());
        Assert.Equal($"[completed] status=Completed finish=unknown usage={expectedUsage}", lines.ReadLine());
    }

    [Theory]
    [InlineData("completion")]
    [InlineData("prompt")]
    [InlineData("agent")]
    public void ModelUsageDiagnosticsPreserveAllNormalizedFactsWithoutReflection(string source)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var usage = new LlmUsage(0, 19, null)
        {
            CachedInputTokens = 8,
            CacheWriteInputTokens = 0,
            ReasoningOutputTokens = null,
            ProviderReportedCostUsd = 0.0000000001m,
            HostedToolCallCount = 0,
            HostedSourcesUsed = null,
            HostedToolCalls = new Dictionary<LlmHostedToolKind, int> { [LlmHostedToolKind.CodeExecution] = 0 }
        };
        using var answer = new StringWriter();
        using var diagnostics = new StringWriter();
        switch (source)
        {
            case "completion":
                Program.WriteCompletion(new()
                {
                    Text = "answer",
                    Status = LlmCompletionStatus.Completed,
                    Usage = usage,
                    Diagnostics = new(200, null, null)
                }, diagnostics);
                break;
            case "prompt":
                Assert.False(Program.WritePromptEvent(new LlmUsageDeltaEvent(usage), answer, diagnostics));
                break;
            case "agent":
                Program.WriteAgentEvent(new LlmAgentUsageEvent("session", "turn", "request", usage, null), answer, diagnostics);
                Assert.Contains("[usage request] session=session turn=turn request=request", diagnostics.ToString(), StringComparison.Ordinal);
                Assert.Contains($"[run usage] null{Environment.NewLine}", diagnostics.ToString(), StringComparison.Ordinal);
                break;
        }

        Assert.Equal(string.Empty, answer.ToString());
        using var lines = new StringReader(diagnostics.ToString());
        string? line;
        do { line = lines.ReadLine(); } while (line is not null && !line.StartsWith("[usage] ", StringComparison.Ordinal));
        Assert.NotNull(line);
        using JsonDocument document = JsonDocument.Parse(line[8..]);
        JsonElement observed = document.RootElement;
        Assert.Equal(0, observed.GetProperty("InputTokens").GetInt32());
        Assert.Equal(19, observed.GetProperty("OutputTokens").GetInt32());
        Assert.Equal(JsonValueKind.Null, observed.GetProperty("TotalTokens").ValueKind);
        Assert.Equal(8, observed.GetProperty("CachedInputTokens").GetInt32());
        Assert.Equal(0, observed.GetProperty("CacheWriteInputTokens").GetInt32());
        Assert.Equal(JsonValueKind.Null, observed.GetProperty("ReasoningOutputTokens").ValueKind);
        Assert.Equal(0.0000000001m, observed.GetProperty("ProviderReportedCostUsd").GetDecimal());
        Assert.Equal(0, observed.GetProperty("HostedToolCallCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, observed.GetProperty("HostedSourcesUsed").ValueKind);
        Assert.Equal(0, observed.GetProperty("HostedToolCalls").GetProperty("CodeExecution").GetInt32());
    }

    [Fact]
    public async Task RequestFileRetainsTheOwnedTypedChatGraphWithReflectionDisabled()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        string directory = Directory.CreateTempSubdirectory("inference-console-request-").FullName;
        try
        {
            string path = Path.Combine(directory, "request.json");
            var expected = new LlmChatRequest
            {
                ModelId = "selected-model",
                Messages = [new("user", "Use the selected hosted tools.")],
                Temperature = 0.4,
                TopP = 0.8,
                MaxOutputTokens = 64,
                MaxHostedTurns = 2,
                HostedTools = [new LlmCodeExecutionTool(), new XaiXSearchTool(AllowedHandles: ["selected-handle"])]
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(expected, ConnectLlmJson.ChatRequest));

            LlmChatRequest actual = await Program.ReadChatRequestAsync(path, CancellationToken.None);

            Assert.Equal(expected.ModelId, actual.ModelId);
            Assert.Equal(expected.Messages.Select(message => (message.Role, message.Content)),
                actual.Messages.Select(message => (message.Role, message.Content)));
            Assert.Equal(expected.Temperature, actual.Temperature);
            Assert.Equal(expected.TopP, actual.TopP);
            Assert.Equal(expected.MaxOutputTokens, actual.MaxOutputTokens);
            Assert.Equal(expected.MaxHostedTurns, actual.MaxHostedTurns);
            Assert.Collection(actual.HostedTools,
                tool => Assert.IsType<LlmCodeExecutionTool>(tool),
                tool => Assert.Equal("selected-handle", Assert.Single(Assert.IsType<XaiXSearchTool>(tool).AllowedHandles!)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FullRequestModeDoesNotMergeInlineOptionsOrBecomeAnAgentOption()
    {
        Assert.Throws<ArgumentException>(() => Program.ParsePromptOptions(
            ["--request", "request.json", "--model", "another-model"], "default-model"));
        Assert.Throws<ArgumentException>(() => Program.ParsePromptOptions(
            ["--request", "request.json"], "default-model", allowModelIterationLimit: true));
    }

    [Fact]
    public void PromptShowsHostedLifecycleAndCitationsWithoutWritingThemIntoTheAnswer()
    {
        using var answer = new StringWriter();
        using var diagnostics = new StringWriter();
        LlmStreamEvent[] events =
        [
            new LlmHostedToolCallStartedEvent("observed-call", LlmHostedToolKind.DocumentSearch, "file_search"),
            new LlmHostedToolCallProgressEvent("observed-call", LlmHostedToolPhase.Searching),
            new LlmHostedToolCallCompletedEvent(new(null, LlmHostedToolKind.DocumentSearch, "file_search", "failed", null, null)),
            new LlmCitationEvent(new("https://example.org/reference"))
        ];

        foreach (LlmStreamEvent observation in events)
            Assert.False(Program.WritePromptEvent(observation, answer, diagnostics));

        Assert.Equal(string.Empty, answer.ToString());
        Assert.Equal(HostedDiagnostics, diagnostics.ToString());
    }

    [Fact]
    public void AgentShowsHostedLifecycleAndCitationsWithoutWritingThemIntoTheAnswer()
    {
        using var answer = new StringWriter();
        using var diagnostics = new StringWriter();
        LlmAgentEvent[] events =
        [
            new LlmAgentHostedToolCallStartedEvent("session", "turn", "request", "observed-call", LlmHostedToolKind.DocumentSearch, "file_search"),
            new LlmAgentHostedToolCallProgressEvent("session", "turn", "request", "observed-call", LlmHostedToolPhase.Searching),
            new LlmAgentHostedToolCallCompletedEvent("session", "turn", "request", new(null, LlmHostedToolKind.DocumentSearch, "file_search", "failed", null, null)),
            new LlmAgentCitationEvent("session", "turn", "request", new("https://example.org/reference"))
        ];

        foreach (LlmAgentEvent observation in events)
            Program.WriteAgentEvent(observation, answer, diagnostics);

        Assert.Equal(string.Empty, answer.ToString());
        Assert.Equal(HostedDiagnostics, diagnostics.ToString());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void HostedImageDiagnosticsReportObservedBytesWithoutPrintingThePayload(bool agent, bool hasOutput)
    {
        var call = new LlmHostedToolCall("image-call", LlmHostedToolKind.ImageGeneration,
            "image_generation", "completed", null,
            hasOutput ? new LlmHostedImageOutput(new byte[] { 0xff, 0xd8, 0xff }, "image/jpeg") : null);
        using var answer = new StringWriter();
        using var diagnostics = new StringWriter();

        if (agent)
            Program.WriteAgentEvent(new LlmAgentHostedToolCallCompletedEvent("session", "turn", "request", call), answer, diagnostics);
        else
            Assert.False(Program.WritePromptEvent(new LlmHostedToolCallCompletedEvent(call), answer, diagnostics));

        Assert.Equal(string.Empty, answer.ToString());
        Assert.Equal(string.Join(Environment.NewLine,
            "[hosted:completed] id=image-call kind=ImageGeneration name=image_generation status=completed",
            $"[hosted:image] bytes={(hasOutput ? "3" : "unknown")}", string.Empty), diagnostics.ToString());
    }

    private static string HostedDiagnostics => string.Join(Environment.NewLine,
        "[hosted:started] id=observed-call kind=DocumentSearch name=file_search",
        "[hosted:progress] id=observed-call phase=Searching",
        "[hosted:completed] id=unknown kind=DocumentSearch name=file_search status=failed",
        "[citation] https://example.org/reference", string.Empty);

    [Theory]
    [InlineData(XaiRegion.Global, "https://api.x.ai/v1")]
    [InlineData(XaiRegion.UnitedStates, "https://us.api.x.ai/v1")]
    public async Task TesterRegionAndNonstreamingControlsUseTheSelectedConnectionAndItsExistingCredentialKey(
        XaiRegion region, string endpoint)
    {
        using var http = new HttpClient();
        var providers = new ConsoleTesterProviders([new XaiResponsesLlmClient(http), new OpenAiResponsesLlmClient(http)]);
        ConsoleInvocation invocation = ConsoleInvocation.Parse(
            ["prompt", "--provider", "grok-subscription", "--xai-region", region.ToString(), "--non-streaming"], providers);
        var store = new TesterCredentials();
        var source = new SecureConnectionSource(store, providers, invocation);

        LlmConnection selected = Assert.IsType<LlmConnection>(await source.GetConnectionAsync(invocation.Provider.ConnectionId));

        Assert.Equal(endpoint, selected.Endpoint);
        XaiConnectionOptions xai = Assert.IsType<XaiConnectionOptions>(selected.Xai);
        Assert.Equal(region, xai.Region);
        Assert.Equal(XaiServiceTier.Default, xai.ServiceTier);
        Assert.False(selected.CapabilityOverrides!.SupportsStreaming);
        Assert.Equal(TesterCredentials.Value, selected.Credential);
        LlmConnection original = Assert.IsType<LlmConnection>(
            await new SecureConnectionSource(store, providers).GetConnectionAsync(invocation.Provider.ConnectionId));
        Assert.NotEqual(original.Version, selected.Version);
        Assert.Null(original.CapabilityOverrides);

        LlmConnection other = Assert.IsType<LlmConnection>(
            await source.GetConnectionAsync(providers.Resolve("openai-api-key").ConnectionId));
        Assert.Equal("https://api.openai.com/v1", other.Endpoint);
        Assert.Null(other.Xai);
        Assert.Null(other.CapabilityOverrides);
    }

    [Fact]
    public void TesterRouteControlsRefuseWrongProviderAndCommand()
    {
        using var http = new HttpClient();
        var providers = new ConsoleTesterProviders([new XaiResponsesLlmClient(http), new OpenAiResponsesLlmClient(http)]);
        Assert.Throws<ArgumentException>(() => ConsoleInvocation.Parse(
            ["prompt", "--provider", "openai-api-key", "--xai-region", "UnitedStates"], providers));
        Assert.Throws<ArgumentException>(() => ConsoleInvocation.Parse(
            ["agent", "--provider", "grok-subscription", "--non-streaming"], providers));
        Assert.Throws<ArgumentException>(() => ConsoleInvocation.Parse(
            ["setup", "--provider", "grok-subscription", "--xai-region", "UnitedStates"], providers));
    }

    [Fact]
    public void AgentIterationLimitBuildsTheExistingManagedPolicyAndPreservesOmittedDefaults()
    {
        Program.PromptOptions capped = Program.ParsePromptOptions(
            ["--max-model-iterations", "3"], "selected-model", allowModelIterationLimit: true);

        Assert.Equal(3, Assert.IsType<LlmAgentExecutionOptions>(capped.ExecutionOptions).MaxModelIterations);
        Assert.Equal("selected-model", capped.Model);
        Assert.Null(Program.ParsePromptOptions([], "selected-model", allowModelIterationLimit: true).ExecutionOptions);
    }

    [Fact]
    public void AgentIterationLimitRefusesNonpositiveValuesAndUnrelatedCommands()
    {
        Assert.Throws<ArgumentException>(() => Program.ParsePromptOptions(
            ["--max-model-iterations", "0"], "selected-model", allowModelIterationLimit: true));
        Assert.Throws<ArgumentException>(() => Program.ParsePromptOptions(
            ["--max-model-iterations", "3"], "selected-model"));
    }

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
    [InlineData(LlmTransport.GoogleAntigravityCli, LlmAgentCompletionReason.Completed, 0)]
    [InlineData(LlmTransport.GoogleAntigravityCli, LlmAgentCompletionReason.ProviderFailure, 1)]
    [InlineData(LlmTransport.AnthropicClaudeCli, LlmAgentCompletionReason.Completed, 0)]
    [InlineData(LlmTransport.AnthropicClaudeCli, LlmAgentCompletionReason.ProviderFailure, 1)]
    public async Task ResumeProbeRequiresSuccessfulTerminalAfterMatchingText(
        LlmTransport transport, LlmAgentCompletionReason reason, int expectedExitCode)
    {
        await using var agents = new LlmAgentProviderService(new ProbeConnections(transport), [new ResumeClient(reason, transport)]);
        var probe = new NativeCliProbe(agents, "probe-connection", "probe-model", Environment.CurrentDirectory, new(), transport);
        using var report = new StringWriter();

        int exitCode = await ProbeRunner.RunAsync(probe.Cases,
            new("probe-model", "resume", 1, 5), true, report, CancellationToken.None);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(2, probe.Turns);
        Assert.Contains(expectedExitCode == 0 ? "PASS 1/1 resume" : "status=Failed",
            report.ToString(), StringComparison.Ordinal);
        if (expectedExitCode != 0)
        {
            Assert.Contains("reason=ProviderFailure", report.ToString(), StringComparison.Ordinal);
            Assert.Contains("[error] Failure after matching text.", report.ToString(), StringComparison.Ordinal);
        }
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

    private sealed class TesterCredentials : ICredentialStore
    {
        public const string Value = "test-only-credential";
        public ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(name == "provider-credential-grok-subscription" ? Value : null);
        public ValueTask SetAsync(string name, string secret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ProbeConnections(LlmTransport transport) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(new()
            {
                Version = "1",
                Transport = transport,
                Endpoint = transport == LlmTransport.AnthropicClaudeCli ? "claude" : "agy"
            });

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ResumeClient(LlmAgentCompletionReason resumedReason, LlmTransport transport) : ILlmAgentResumeClient
    {
        private string? _remembered;
        public LlmTransport Transport => transport;

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
