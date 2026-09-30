using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AppleFoundationModelsTests
{
    internal static JsonElement Json(string value) => JsonElement.Parse(value);
    internal static LlmConnection Connection { get; } = new() { Version = "v1", Transport = LlmTransport.AppleFoundationModels, Endpoint = "darkspyre-apple-fm" };
    internal static LlmChatRequest Request { get; } = new() { ModelId = "apple-system-default", Messages = [new("user", "hello")] };
    internal const string ObjectSchema = """{"type":"object","properties":{"key":{"type":"string"}},"required":["key"],"additionalProperties":false}""";
    internal static LlmToolDefinition Tool { get; } = new() { Name = "lookup", Description = "Read synthetic data", InputSchema = Json(ObjectSchema) };
    internal const string Hello = """{"type":"hello","protocol":"darkspyre.apple-fm","major":1,"helperVersion":"test","osVersion":"26.0","availability":"available","contextSize":4096}""";

    [Fact]
    public async Task SamplingRequiresHelperAdvertisementAndPreservesEveryWireValue()
    {
        var runner = new Runner(_ => [Frame("text", "hi"), Terminal("hi")]) { SupportsSampling = true };
        using var http = new HttpClient();
        var client = new AppleFoundationModelsClient(http, runner);
        await Read(client.StreamAsync(Connection, Request with { TopP = .8, Seed = 42, Temperature = .5 }));
        AppleGeneration generation = Assert.Single(runner.Requests).Request!;
        Assert.Equal(.8, generation.TopP);
        Assert.Equal(42, generation.Seed);
        JsonElement serialized = JsonSerializer.SerializeToElement(generation, AppleProtocol.Json);
        Assert.Equal(.8, serialized.GetProperty("topP").GetDouble());
        Assert.Equal(42, serialized.GetProperty("seed").GetInt32());
        await Read(client.StreamAsync(Connection, Request with { TopK = 17 }));
        Assert.Equal(17, runner.Requests[^1].Request!.TopK);
        await Assert.ThrowsAsync<NotSupportedException>(() => Read(client.StreamAsync(Connection, Request with { TopP = .8, TopK = 17 })));
        Assert.Equal(2, runner.Requests.Count);
        runner.SupportsSampling = false;
        await Assert.ThrowsAsync<NotSupportedException>(() => Read(client.StreamAsync(Connection, Request with { Seed = 42 })));
        var oldHelper = Assert.Single(await client.ListModelsAsync(Connection));
        Assert.False(oldHelper.Capabilities!.SupportsSeed);
        Assert.False(client.GetCapabilities(Connection with { Catalog = [oldHelper] }, AppleProtocol.ModelId).SupportsSeed);
        Assert.Throws<NotSupportedException>(() => AppleProtocol.ValidateSampling(Json(Hello), generation));
    }

    [Fact]
    public async Task StreamingOffAsksForOneCompleteResponse()
    {
        var runner = new Runner(_ => [Frame("text", "Complete reply."), Terminal("Complete reply.")]);
        var client = new AppleFoundationModelsClient(new HttpClient(), runner);

        List<LlmStreamEvent> events = await Read(client.StreamAsync(
            Connection with { CapabilityOverrides = new() { SupportsStreaming = false } }, Request));

        Assert.False(Assert.Single(runner.Requests).Request!.Stream);
        Assert.Collection(events,
            item => Assert.Equal(new LlmTextDeltaEvent("Complete reply."), item),
            item => Assert.Equal(LlmCompletionStatus.Unknown, Assert.IsType<LlmChatCompletionEvent>(item).Result.Status));
        await Read(client.StreamAsync(Connection, Request));
        Assert.True(runner.Requests[^1].Request!.Stream);
    }

    [Fact]
    public void SchemaPreservesNestedRequiredOptionalReferencesAndBounds()
    {
        AppleSchemaNode node = AppleSchema.Convert(Json("""
            {"type":"object","additionalProperties":false,"required":["values","ok"],"properties":{
              "values":{"type":"array","minItems":2,"maxItems":2,"items":{"$ref":"#/$defs/item"}},
              "ok":{"type":"boolean"},"choice":{"type":"string","enum":["a","b"]},
              "constant":{"type":"string","const":"fixed"},
              "count":{"type":"integer","minimum":-9223372036854775808,"maximum":9223372036854775807}},
             "$defs":{"item":{"type":"number","minimum":-0.5,"maximum":1.5}}}
            """));
        Assert.Equal(5, node.Properties!.Count);
        Assert.True(node.Properties[0].Required);
        Assert.False(node.Properties[2].Required);
        Assert.Equal(2, node.Properties[0].Schema.MinItems);
        Assert.Equal(2, node.Properties[0].Schema.MaxItems);
        Assert.Equal("number", node.Properties[0].Schema.Items!.Kind);
        Assert.Equal("-0.5", node.Properties[0].Schema.Items!.Minimum);
        Assert.Equal("1.5", node.Properties[0].Schema.Items!.Maximum);
        Assert.Equal("boolean", node.Properties[1].Schema.Kind);
        Assert.Equal(["a", "b"], node.Properties[2].Schema.Choices);
        Assert.Equal(["fixed"], node.Properties[3].Schema.Choices);
        Assert.Equal("-9223372036854775808", node.Properties[4].Schema.Minimum);
        Assert.Equal("9223372036854775807", node.Properties[4].Schema.Maximum);
    }

    [Fact]
    public void SchemaGuidanceAndEscapedLocalReferencesSurviveProjection()
    {
        var node = AppleSchema.Convert(Json("""
            {"type":"object","title":"Root","description":"Guidance","additionalProperties":false,
             "properties":{"value":{"$ref":"#/$defs/a~1b~0c","title":"Field","description":"Hint"}},
             "$defs":{"a/b~c":{"type":"string"}}}
            """));
        Assert.Equal("Root: Guidance", node.Description);
        Assert.Equal("string", node.Properties![0].Schema.Kind);
        Assert.Contains("$[\"value\"]: Field: Hint", AppleSchema.Guidance(node), StringComparison.Ordinal);
    }

    [Fact]
    public void AcyclicReferenceExpansionIsBounded()
    {
        var definitions = new Dictionary<string, JsonElement> { ["leaf"] = Json("""{"type":"string"}""") };
        string previous = "leaf";
        for (int index = 0; index < 20; index++)
        {
            string current = "node" + index;
            definitions[current] = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new Dictionary<string, object>
                {
                    ["a"] = new Dictionary<string, string> { ["$ref"] = "#/$defs/" + previous },
                    ["b"] = new Dictionary<string, string> { ["$ref"] = "#/$defs/" + previous }
                }
            });
            previous = current;
        }
        var schema = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["$ref"] = "#/$defs/" + previous, ["$defs"] = definitions });
        Assert.Contains("16,384", Assert.Throws<NotSupportedException>(() => AppleSchema.Convert(schema)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"type":"object"}""")]
    [InlineData("""{"type":["string","null"]}""")]
    [InlineData("""{"type":"string","pattern":"a"}""")]
    [InlineData("""{"type":"string","format":"email"}""")]
    [InlineData("""{"type":"number","exclusiveMinimum":0}""")]
    [InlineData("""{"type":"integer","minimum":9223372036854775808}""")]
    [InlineData("""{"type":"number","minimum":1e999}""")]
    [InlineData("""{"type":"number","minimum":2,"maximum":1}""")]
    [InlineData("""{"type":"array","items":{"type":"boolean"},"uniqueItems":true}""")]
    [InlineData("""{"type":"array","items":[{"type":"boolean"}]}""")]
    [InlineData("""{"type":"array","items":{"type":"boolean"},"minItems":-1}""")]
    [InlineData("""{"type":"array","items":{"type":"boolean"},"minItems":2,"maxItems":1}""")]
    [InlineData("""{"type":"string","enum":[]}""")]
    [InlineData("""{"type":"string","enum":["a","a"]}""")]
    [InlineData("""{"type":"string","const":1}""")]
    [InlineData("""{"type":"string","const":"a","enum":["a"]}""")]
    [InlineData("""{"type":"boolean","type":"boolean"}""")]
    [InlineData("""{"type":"object","additionalProperties":false,"required":["missing"]}""")]
    [InlineData("""{"$ref":"https://example.test/schema"}""")]
    [InlineData("""{"$ref":"#/$defs/x","$defs":{"x":{"$ref":"#/$defs/x"}}}""")]
    [InlineData("""{"type":"string","anyOf":[]}""")]
    public void UnsupportedSchemasFailExplicitly(string schema) => Assert.Throws<NotSupportedException>(() => AppleSchema.Convert(Json(schema)));

    [Fact]
    public async Task TextDeltasPreserveUnicodeAndUnknownTerminationThroughBind()
    {
        var runner = new Runner(_ => [Frame("text", "🙂"), Frame("text", "🙂"), Frame("text", "🙂 done"), Terminal("🙂 done")]);
        using var http = new HttpClient();
        var client = new AppleFoundationModelsClient(http, runner);
        var service = new LlmProviderService(new Source(), [client]);
        var events = await Read(service.Bind("apple").StreamAsync(new() { ModelId = Request.ModelId, Messages = [new("user", "hello")] }));
        Assert.Equal(["🙂", " done"], events.OfType<LlmTextDeltaEvent>().Select(e => e.Delta));
        var result = Assert.Single(events.OfType<LlmCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.Unknown, result.Status);
        Assert.Equal("🙂 done", result.Text);
        Assert.Null(result.Usage);
        Assert.Null(result.FinishReason);
        Assert.Null(result.IncompleteReason);
        Assert.Collection(runner.Requests,
            command => Assert.Equal("probe", command.Command),
            command => Assert.Equal("generate", command.Command));
    }

    [Fact]
    public async Task StructuredSnapshotsReplaceAndFinalSchemaIsValidated()
    {
        var runner = new Runner(_ => [Frame("structured", "{}"), Frame("structured", "{\"key\":\"value\"}"), Terminal("{\"key\":\"value\"}")]);
        using var http = new HttpClient();
        var client = new AppleFoundationModelsClient(http, runner);
        var events = await Read(client.StreamAsync(Connection, Request with { StructuredOutput = new() { Name = "output", Schema = Json(ObjectSchema) } }));
        Assert.Empty(events.OfType<LlmTextDeltaEvent>());
        Assert.Equal(2, events.OfType<LlmStructuredOutputSnapshotEvent>().Count());
        Assert.True(Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.StructuredOutput?.IsValid);
    }

    [Fact]
    public async Task UnsupportedOutputRequiresExplicitLocalValidationFallback()
    {
        var runner = new Runner(_ => [Frame("text", "\"a\""), Terminal("\"a\"")]);
        using var http = new HttpClient();
        var client = new AppleFoundationModelsClient(http, runner);
        var request = Request with { StructuredOutput = new() { Name = "value", Schema = Json("""{"type":"string","pattern":"^a$"}""") } };
        await Assert.ThrowsAsync<NotSupportedException>(() => Read(client.StreamAsync(Connection, request)));
        Assert.Empty(runner.Requests);
        var events = await Read(client.StreamAsync(Connection, request with { StructuredOutput = request.StructuredOutput with { Fallback = LlmStructuredOutputFallback.LocalValidation } }));
        Assert.Null(Assert.Single(runner.Requests).Request!.Schema);
        var validation = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.StructuredOutput;
        Assert.True(validation?.IsValid);
        Assert.Equal(LlmStructuredOutputEnforcement.LocalValidationOnly, validation?.Enforcement);
    }

    [Fact]
    public async Task BindReconstructsIdenticalCallsAndReverseResultsWithoutHiddenState()
    {
        var runner = new Runner(_ => [Frame("text", "done"), Terminal("done")]);
        using var http = new HttpClient();
        var service = new LlmProviderService(new Source(), [new AppleFoundationModelsClient(http, runner)]);
        await service.Bind("apple").CompleteAsync(new()
        {
            ModelId = Request.ModelId,
            ToolChoice = new() { Mode = LlmToolChoiceMode.None },
            Tools = [Tool],
            Messages = [new("user", ""), new("assistant", "looking") { ToolCalls = [new("a", "lookup", "{}"), new("b", "lookup", "{}")] },
                new("user", "continue") { ToolResults = [new("b", LlmToolOutput.FromText("second", isError: true)), new("a", LlmToolOutput.FromText("first"))] }]
        });
        Assert.Equal("probe", runner.Requests[0].Command);
        var request = Assert.Single(runner.Requests, command => command.Command == "generate").Request!;
        Assert.Empty(request.Tools);
        Assert.Equal("", request.Messages[0].Content);
        Assert.Equal(["a", "b"], request.Messages[1].Calls.Select(c => c.Id));
        Assert.Equal(["b", "a"], request.Messages[2].Results.Select(r => r.Id));
        Assert.Contains("[Tool error]", request.Messages[2].Results[0].Text, StringComparison.Ordinal);
        Assert.Equal("continue", request.Messages[2].Content);
    }

    [Fact]
    public async Task WholeBatchIsValidatedBeforeAnyToolIsExposed()
    {
        var runner = new Runner(_ => [Batch(new("a", "lookup", "{\"key\":\"x\"}"), new("b", "lookup", "{}")), Terminal("", "requiresAction")]);
        using var http = new HttpClient();
        var emitted = new List<LlmStreamEvent>();
        var client = new AppleFoundationModelsClient(http, runner);
        var error = await Assert.ThrowsAsync<InferenceException>(async () =>
        { await foreach (var item in client.StreamAsync(Connection, Request with { Tools = [Tool] })) emitted.Add(item); });
        Assert.Equal(InferenceFailureCode.ProtocolFailure, error.Code);
        Assert.Empty(emitted.OfType<LlmToolCallCompletedEvent>());
    }

    [Fact]
    public async Task ManagedAgentSettlesSuccessiveBatchesAndFinishesUnknownOnce()
    {
        int step = 0;
        var runner = new Runner(_ => ++step switch
        {
            1 => [Batch(new("a", "lookup", "{\"key\":\"x\"}"), new("b", "lookup", "{\"key\":\"x\"}")), Terminal("", "requiresAction")],
            2 => [Batch(new AppleCall("c", "lookup", "{\"key\":\"y\"}")), Terminal("", "requiresAction")],
            _ => [Frame("text", "done"), Terminal("done")]
        });
        using var http = new HttpClient();
        var client = new AppleFoundationModelsClient(http, runner);
        var service = new LlmProviderService(new Source(), [client]);
        var executed = new List<string>();
        await using var session = await new ManagedAppleAgentClient(client, service).StartSessionAsync("apple", Connection, new()
        {
            ModelId = Request.ModelId,
            Tools = [new() { Name = Tool.Name, Description = Tool.Description, InputSchema = Tool.InputSchema }],
            ToolHandler = (call, _) => { executed.Add(call.CallId); return Task.FromResult(LlmToolOutput.FromText(call.CallId, isError: call.CallId == "b")); }
        });
        var events = await Read(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("lookup")] }));
        Assert.Equal(["a", "b", "c"], executed.Order(StringComparer.Ordinal));
        var done = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
        Assert.Equal(LlmAgentCompletionReason.Unknown, done.Reason);
        Assert.Equal(LlmAgentTurnStatus.Unknown, done.Status);
        Assert.Equal(3, runner.Requests.Count(c => c.Command == "generate"));
        var context = await session.CaptureContextAsync();
        Assert.Equal("done", context.Entries[^1].Message.Content);
        Assert.Equal(3, context.Entries.Sum(e => e.Message.ToolResults.Count));
        Assert.Null((await session.MeasureContextAsync(context)).UsedContextTokens);
    }

    [Fact]
    public async Task NativeFailureAfterToolExecutionKeepsResultsAndDoesNotRetry()
    {
        int step = 0;
        var runner = new Runner(_ => ++step == 1
            ? [Batch(new AppleCall("a", "lookup", "{\"key\":\"x\"}")), Terminal("", "requiresAction")]
            : [Json("""{"type":"terminal","status":"error","code":"execution_failed"}""")]);
        using var http = new HttpClient();
        var client = new AppleFoundationModelsClient(http, runner);
        var service = new LlmProviderService(new Source(), [client]);
        int executed = 0;
        await using var session = await new ManagedAppleAgentClient(client, service).StartSessionAsync("apple", Connection, new()
        {
            ModelId = Request.ModelId,
            Tools = [new() { Name = Tool.Name, Description = Tool.Description, InputSchema = Tool.InputSchema }],
            ToolHandler = (_, _) => { executed++; return Task.FromResult(LlmToolOutput.FromText("result")); }
        });
        var events = await Read(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("lookup")] }));
        Assert.Equal(LlmAgentCompletionReason.ProviderFailure, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        Assert.Equal(1, executed);
        Assert.Equal(2, step);
        Assert.Equal("result", (await session.CaptureContextAsync()).Entries[^1].Message.ToolResults[0].Output.Parts.OfType<LlmToolOutputText>().Single().Text);
    }

    [Theory]
    [InlineData("context_length_exceeded", InferenceFailureCode.InvalidRequest)]
    [InlineData("capacity_exhausted", InferenceFailureCode.CapacityExhausted)]
    [InlineData("model_unavailable", InferenceFailureCode.ModelUnavailable)]
    [InlineData("unsupported_feature", InferenceFailureCode.UnsupportedFeature)]
    [InlineData("execution_failed", InferenceFailureCode.ExecutionFailed)]
    public async Task BindPreservesFailureCategory(string code, InferenceFailureCode expected)
    {
        var runner = new Runner(_ => [JsonSerializer.SerializeToElement(new { type = "terminal", status = "error", code })]);
        using var http = new HttpClient();
        var service = new LlmProviderService(new Source(), [new AppleFoundationModelsClient(http, runner)]);
        var error = await Assert.ThrowsAsync<InferenceException>(() => service.Bind("apple").CompleteAsync(new() { ModelId = Request.ModelId }));
        Assert.Equal(expected, error.Code);
    }

    [Theory]
    [InlineData("revised")]
    [InlineData("terminal-mismatch")]
    public async Task InconsistentTextFailsWithoutCompletion(string scenario)
    {
        var runner = new Runner(_ => scenario == "revised" ? [Frame("text", "abc"), Frame("text", "abX")] : [Frame("text", "abc"), Terminal("xyz")]);
        using var http = new HttpClient();
        var error = await Assert.ThrowsAsync<InferenceException>(() => Read(new AppleFoundationModelsClient(http, runner).StreamAsync(Connection, Request)));
        Assert.Equal(InferenceFailureCode.ProtocolFailure, error.Code);
    }

    [Fact]
    public void InvalidHistoryControlsAndOverridesFailBeforeStartingHelper()
    {
        var runner = new Runner(_ => []);
        using var http = new HttpClient();
        var client = new AppleFoundationModelsClient(http, runner);
        Assert.Throws<ArgumentException>(() => client.EstimateInputTokens(Connection, Request with { Messages = [new("assistant", "") { ToolCalls = [new("a", "lookup", "{}")] }] }));
        Assert.Throws<ArgumentException>(() => client.EstimateInputTokens(Connection, Request with { Messages = [new("user", "") { ToolResults = [new("missing", LlmToolOutput.FromText("x"))] }] }));
        Assert.Throws<NotSupportedException>(() => client.EstimateInputTokens(Connection, Request with { Messages = [new("system", "instructions")] }));
        Assert.Throws<NotSupportedException>(() => client.EstimateInputTokens(Connection, Request with { Tools = [Tool], ToolChoice = new() { Mode = LlmToolChoiceMode.Required } }));
        Assert.Throws<NotSupportedException>(() => client.EstimateInputTokens(Connection, Request with { Tools = [Tool], ToolChoice = new() { Mode = LlmToolChoiceMode.Specific, Name = "lookup" } }));
        Assert.Throws<NotSupportedException>(() => client.EstimateInputTokens(Connection, Request with { Tools = [Tool], AllowParallelToolCalls = false }));
        Assert.Throws<NotSupportedException>(() => client.GetCapabilities(Connection with { CapabilityOverrides = new() { SupportsPromptImages = true } }));
        Assert.Throws<ArgumentException>(() => client.GetCapabilities(Connection with { Credential = "secret" }));
        Assert.Throws<ArgumentException>(() => client.GetCapabilities(Connection with { ProviderOptions = new Dictionary<string, JsonElement> { ["requestTimeoutSeconds"] = Json("0") } }));
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task SetupUsesOnlyExplicitLocalChecksAndNoAuthentication()
    {
        var runner = new Runner(_ => [Frame("text", "OK"), Terminal("OK")]);
        using var http = new HttpClient();
        var service = new LlmProviderService(new Source(), [new AppleFoundationModelsClient(http, runner)]);
        var manager = service.CreateSettingsManager(new Target());
        var settings = await manager.OpenAsync(LlmTransport.AppleFoundationModels, new(SetupChangeIntent.Create, "apple", null));
        var fields = settings.Steps.SelectMany(s => s.Sections).SelectMany(s => s.Fields).ToArray();
        Assert.Single(fields.OfType<SetupExecutableField>());
        Assert.Empty(fields.OfType<SetupSecretField>());
        Assert.All(settings.Actions, a => Assert.Equal(SetupSideEffect.LocalVerification | SetupSideEffect.ExternalCli, a.SideEffects));
        Assert.Empty(runner.Requests);
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, "check")).State);
        Assert.Null(settings.Status.Authenticated.Value);
        Assert.True(settings.Status.Verified.Value);
        Assert.Null(settings.Status.Ready.Value);
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, "test")).State);
        Assert.True(settings.Status.Ready.Value);
        runner.Availability = "appleIntelligenceNotEnabled";
        Assert.Equal(SetupActionState.NeedsInteraction, (await manager.RunAsync(settings, "check")).State);
        Assert.IsType<SetupExternalPrerequisite>(settings.Interaction);
        Assert.False(settings.Status.Verified.Value);
    }

    [Theory]
    [InlineData("wrong-id")]
    [InlineData("duplicate-sequence")]
    [InlineData("duplicate-terminal")]
    [InlineData("duplicate-field")]
    [InlineData("unknown-type")]
    public void ProtocolRejectsBadSequence(string scenario)
    {
        var frames = new AppleFrameSequence("r");
        frames.Hello(Hello);
        frames.Next("""{"type":"text","id":"r","sequence":1,"content":""}""");
        if (scenario == "duplicate-terminal") frames.Next("""{"type":"terminal","id":"r","sequence":2}""");
        string line = scenario switch
        {
            "wrong-id" => """{"type":"text","id":"other","sequence":2}""",
            "duplicate-sequence" => """{"type":"text","id":"r","sequence":1}""",
            "duplicate-field" => """{"type":"text","id":"r","id":"r","sequence":2}""",
            "unknown-type" => """{"type":"unexpected","id":"r","sequence":2}""",
            _ => """{"type":"terminal","id":"r","sequence":3}"""
        };
        Assert.Equal(InferenceFailureCode.ProtocolFailure, Assert.Throws<InferenceException>(() => frames.Next(line)).Code);
        Assert.Throws<InferenceException>(() => new AppleFrameSequence("r").Hello(Hello.Replace("\"major\":1", "\"major\":2", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("truncated")]
    [InlineData("utf8")]
    public async Task ProtocolRejectsUnboundedOrMalformedLines(string scenario)
    {
        byte[] bytes = scenario switch { "oversize" => Encoding.UTF8.GetBytes("12345\n"), "truncated" => [65], _ => [0xff, 10] };
        using var stream = new MemoryStream(bytes);
        var reader = new AppleFrameReader(stream, 4);
        Assert.Equal(InferenceFailureCode.ProtocolFailure, (await Assert.ThrowsAsync<InferenceException>(() => reader.ReadAsync(default))).Code);
    }

    internal static JsonElement Frame(string type, string content) => JsonSerializer.SerializeToElement(new { type, content });
    internal static JsonElement Terminal(string content, string status = "unknown") => JsonSerializer.SerializeToElement(new { type = "terminal", content, status });
    internal static JsonElement Batch(params AppleCall[] calls) => JsonSerializer.SerializeToElement(new { type = "batch", calls }, AppleProtocol.Json);
    internal static async Task<List<T>> Read<T>(IAsyncEnumerable<T> source)
    { var result = new List<T>(); await foreach (var item in source) result.Add(item); return result; }

    internal sealed class Source : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<LlmConnection?>(Connection);
        public Task SaveCredentialAsync(string id, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Target : ISetupCommitTarget<LlmSetupChanges>
    { public Task<SetupCommitResult> CommitAsync(LlmSetupChanges changes, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No implicit save"); }
    internal sealed class Runner(Func<AppleCommand, JsonElement[]> response) : IAppleProcessRunner
    {
        internal List<AppleCommand> Requests { get; } = [];
        internal string Availability { get; set; } = "available";
        internal bool SupportsSampling { get; set; }
        public async IAsyncEnumerable<JsonElement> RunAsync(string executable, AppleCommand command, AppleProcessLimits limits,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(command);
            yield return Json(Hello.Replace("available", Availability, StringComparison.Ordinal).Replace("\"major\":1", "\"major\":1,\"supportsSamplingControls\":" + (SupportsSampling ? "true" : "false"), StringComparison.Ordinal));
            foreach (var frame in command.Command == "probe" ? [Terminal("", "ready")] : response(command)) yield return frame;
            await Task.CompletedTask;
        }
    }
}
