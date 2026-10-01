using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.Decision;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Common.Tests;

public sealed class InferenceJsonTests
{
    private const string ProvenanceJson = "{\"ImplementationId\":\"fixture\",\"ModelId\":\"model\"}";
    private const string ResultJson = "{\"Text\":\"done\",\"Status\":0,\"Provenance\":" + ProvenanceJson + "}";
    private const string ContinuationJson = "{\"FormatVersion\":2,\"ImplementationId\":\"fixture\",\"ModelId\":\"model\",\"ScopeId\":\"scope\",\"Payload\":\"opaque\"}";
    private const string HostedUsageJson = "{\"InputTokens\":null,\"OutputTokens\":0,\"TotalTokens\":null,\"ProviderReportedCostUsd\":0.0000000001,\"HostedToolCalls\":{\"WebSearch\":2},\"HostedToolCallCount\":5,\"HostedSourcesUsed\":0}";
    private static readonly string[] HostedToolNames = ["WebSearch", "SocialSearch", "CodeExecution", "ImageGeneration", "DocumentSearch", "RemoteMcp", "ImageUnderstanding", "VideoUnderstanding"];

    [Fact]
    public void HostedUsageRootsPreserveExactIdentitiesAndUnknownCounts()
    {
        Assert.Equal(HostedToolNames, Enum.GetNames<LlmHostedToolKind>());
        Assert.Equal(Enumerable.Range(0, 8), Enum.GetValues<LlmHostedToolKind>().Select(value => (int)value));
        foreach (LlmHostedToolKind kind in Enum.GetValues<LlmHostedToolKind>())
            Assert.Equal(kind, JsonSerializer.Deserialize(JsonSerializer.Serialize(kind, InferenceJson.LlmHostedToolKind), InferenceJson.LlmHostedToolKind));

        var usage = JsonSerializer.Deserialize(HostedUsageJson, InferenceJson.LlmUsage)!;
        Assert.Equal(0.0000000001m, usage.ProviderReportedCostUsd);
        Assert.Equal(5, usage.HostedToolCallCount);
        Assert.Equal(0, usage.HostedSourcesUsed);
        Assert.Equal(2, usage.HostedToolCalls![LlmHostedToolKind.WebSearch]);
        var invalid = JsonSerializer.Deserialize("{\"HostedToolCalls\":{\"WebSearch\":-1,\"RemoteMcp\":0},\"HostedToolCallCount\":-1,\"HostedSourcesUsed\":-1}", InferenceJson.LlmUsage)!;
        Assert.Null(invalid.HostedToolCallCount);
        Assert.Null(invalid.HostedSourcesUsed);
        Assert.Equal(new KeyValuePair<LlmHostedToolKind, int>(LlmHostedToolKind.RemoteMcp, 0), Assert.Single(invalid.HostedToolCalls!));
        var copy = usage with { HostedToolCallCount = -1, HostedSourcesUsed = -1 };
        Assert.Null(copy.HostedToolCallCount);
        Assert.Null(copy.HostedSourcesUsed);
        Assert.Null(JsonSerializer.Deserialize("{}", InferenceJson.LlmUsage)!.HostedToolCalls);
    }

    [Fact]
    public void LlmRequestPreservesSchemaSamplingContinuationAndTypedToolOutput()
    {
        var original = new LlmInferenceRequest
        {
            ModelId = "model",
            Temperature = 0.5,
            TopP = 0.9,
            TopK = 4,
            Seed = 42,
            PresencePenalty = 0.1,
            FrequencyPenalty = 0.2,
            StopSequences = ["stop"],
            ReasoningLevel = LlmReasoningLevel.High,
            PromptCache = new() { Scope = LlmPromptCacheScope.Prefix, Retention = LlmPromptCacheRetention.Extended },
            StructuredOutput = new() { Name = "answer", Schema = Json("{\"type\":\"object\"}") },
            Tools = [new() { Name = "tool", InputSchema = Json("{\"type\":\"object\"}"), Strict = true }],
            Messages = [new("assistant", "text")
            {
                ContinuationState = JsonSerializer.Deserialize(ContinuationJson, InferenceJson.ContinuationState),
                Attachments = [new() { Kind = LlmBinaryKind.Image, FileName = "image.png", Data = new byte[] { 1, 2, 3 } }],
                ToolCalls = [new("call", "tool", "{}")],
                ToolResults = [new("call", new()
                {
                    Parts = [new LlmToolOutputText("text"), new LlmToolOutputBinary(LlmBinaryKind.Audio, "audio/wav", new byte[] { 4 }),
                        new LlmToolOutputResource("urn:test") { Text = "resource", Data = ReadOnlyMemory<byte>.Empty }],
                    StructuredContent = Json("{\"ok\":true}"),
                    IsError = true
                })]
            }]
        };
        string json = JsonSerializer.Serialize(original, InferenceJson.LlmRequest);
        LlmInferenceRequest restored = JsonSerializer.Deserialize(json, InferenceJson.LlmRequest)!;
        Assert.Equal(json, JsonSerializer.Serialize(restored, InferenceJson.LlmRequest));
        Assert.Equal(original.StopSequences, restored.StopSequences);
        Assert.Equal(original.PromptCache, restored.PromptCache);
        Assert.Equal("opaque", restored.Messages[0].ContinuationState!.Payload);
        Assert.Equal(new byte[] { 1, 2, 3 }, restored.Messages[0].Attachments[0].Data.ToArray());
        Assert.True(restored.Tools[0].Strict);
        Assert.Equal(original.Tools[0].InputSchema.GetRawText(), restored.Tools[0].InputSchema.GetRawText());
        LlmToolOutput output = restored.Messages[0].ToolResults[0].Output;
        Assert.IsType<LlmToolOutputText>(output.Parts[0]);
        Assert.IsType<LlmToolOutputBinary>(output.Parts[1]);
        Assert.True(Assert.IsType<LlmToolOutputResource>(output.Parts[2]).Data.HasValue);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("AQID", document.RootElement.GetProperty("Messages")[0].GetProperty("Attachments")[0].GetProperty("Data").GetString());
        Assert.Equal("binary", document.RootElement.GetProperty("Messages")[0].GetProperty("ToolResults")[0].GetProperty("Output").GetProperty("Parts")[1].GetProperty("type").GetString());
        Assert.Equal(4, document.RootElement.GetProperty("ReasoningLevel").GetInt32());
    }

    [Fact]
    public void ToolResourcePreservesNullVersusEmptyBinaryBodies()
    {
        var original = new LlmToolOutput
        {
            Parts = [new LlmToolOutputResource("urn:null"), new LlmToolOutputResource("urn:empty") { Data = ReadOnlyMemory<byte>.Empty }]
        };
        string json = JsonSerializer.Serialize(original, InferenceJson.ToolOutput);
        Assert.Contains("\"Data\":null", json);
        Assert.Contains("\"Data\":\"\"", json);
        LlmToolOutput restored = JsonSerializer.Deserialize(json, InferenceJson.ToolOutput)!;
        Assert.Null(Assert.IsType<LlmToolOutputResource>(restored.Parts[0]).Data);
        Assert.Equal(0, Assert.IsType<LlmToolOutputResource>(restored.Parts[1]).Data!.Value.Length);
    }

    [Theory]
    [InlineData("{\"FormatVersion\":2,\"ImplementationId\":\"fixture\",\"ModelId\":\"model\",\"ScopeId\":\"scope\",\"Payload\":\"opaque\",\"future\":true}")]
    [InlineData("{\"ImplementationId\":\"fixture\",\"ModelId\":\"model\",\"ScopeId\":\"scope\",\"Payload\":\"opaque\"}")]
    [InlineData("{\"FormatVersion\":2,\"ModelId\":\"model\",\"ScopeId\":\"scope\",\"Payload\":\"opaque\"}")]
    public void ContinuationMetadataRetainsRequiredAndUnmappedMemberRules(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, InferenceJson.ContinuationState));

    [Fact]
    public void MessageMetadataStillRejectsUnknownMembers() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Role\":\"user\",\"Content\":\"text\",\"future\":true}", InferenceJson.LlmMessage));

    [Fact]
    public void OrdinaryContractMetadataPreservesUnknownNumericEnumsAndIgnoresUnknownProperties()
    {
        LlmInferenceResult value = JsonSerializer.Deserialize(ResultJson.Replace("\"Status\":0", "\"Status\":999,\"future\":true", StringComparison.Ordinal), InferenceJson.LlmResult)!;
        Assert.Equal((LlmCompletionStatus)999, value.Status);
        Assert.Contains("\"Status\":999", JsonSerializer.Serialize(value, InferenceJson.LlmResult));
        SetupStatus status = JsonSerializer.Deserialize("{}", InferenceJson.SetupStatus)!;
        Assert.Null(status.Ready.Value);
        Assert.False(status.Installed.IsApplicable);
    }

    [Fact]
    public void CapabilitiesPreserveClosedSetsAndNullableLimits()
    {
        var original = new LlmCapabilities
        {
            PromptCache = new() { Automatic = true, AcceptsHints = true, Retentions = new HashSet<LlmPromptCacheRetention> { LlmPromptCacheRetention.Default, LlmPromptCacheRetention.Extended } },
            ReasoningLevels = new HashSet<LlmReasoningLevel> { LlmReasoningLevel.None, LlmReasoningLevel.High },
            Tools = new() { ChoiceModes = new HashSet<LlmToolChoiceMode> { LlmToolChoiceMode.Auto, LlmToolChoiceMode.Required } }
        };
        LlmCapabilities restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(original, InferenceJson.LlmCapabilities), InferenceJson.LlmCapabilities)!;
        Assert.True(restored.ReasoningLevels.SetEquals(original.ReasoningLevels));
        Assert.True(restored.Tools.ChoiceModes.SetEquals(original.Tools.ChoiceModes));
        Assert.True(restored.PromptCache.Automatic);
        Assert.True(restored.PromptCache.AcceptsHints);
        Assert.True(restored.PromptCache.Retentions.SetEquals(original.PromptCache.Retentions));
        DecisionCapabilities decisions = JsonSerializer.Deserialize("{\"SupportedQuestionKinds\":[0,1,2]}", InferenceJson.DecisionCapabilities)!;
        Assert.Equal(3, decisions.SupportedQuestionKinds.Count);
        Assert.Null(decisions.MaximumQuestions);
    }

    [Fact]
    public void ImageBytesAndProgressRetainEstablishedShape()
    {
        var original = new ImageGenerationResult
        {
            Images = [new() { ContentType = "image/png", Data = new byte[] { 1, 2, 3 }, Seed = 42 }],
            Provenance = new() { ImplementationId = "fixture", ModelId = "model" }
        };
        string json = JsonSerializer.Serialize(original, InferenceJson.ImageResult);
        Assert.Contains("\"Data\":\"AQID\"", json);
        Assert.Equal(new byte[] { 1, 2, 3 }, JsonSerializer.Deserialize(json, InferenceJson.ImageResult)!.Images[0].Data.ToArray());
        Assert.Equal("{\"Stage\":2,\"Value\":1,\"Maximum\":4}",
            JsonSerializer.Serialize(new ImageGenerationProgress(ImageGenerationStage.Generating, 1, 4), InferenceJson.ImageProgress));
    }

    [Fact]
    public void ConcreteStreamMetadataDoesNotInventABaseDiscriminator()
    {
        Assert.Equal("{\"Delta\":\"text\"}", JsonSerializer.Serialize(new LlmTextDeltaEvent("text"), InferenceJson.TextDelta));
        Assert.Equal("{\"Delta\":\"reason\",\"IsSummary\":true}",
            JsonSerializer.Serialize(new LlmReasoningDeltaEvent("reason", true), InferenceJson.ReasoningDelta));
        Assert.Null(InferenceJson.LlmRequest.Options.TypeInfoResolver!.GetTypeInfo(typeof(LlmStreamEvent), InferenceJson.LlmRequest.Options));
    }

    [Fact]
    public void DecisionMetadataDoesNotAcceptNewDiscriminators() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "{\"type\":\"unknown\",\"Id\":\"question\",\"Instructions\":\"text\"}", InferenceJson.DecisionQuestion));

    [Fact]
    public void DecisionMetadataDoesNotInventMissingDiscriminators() =>
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize(
            "{\"Id\":\"question\",\"Instructions\":\"text\"}", InferenceJson.DecisionQuestion));

    public static TheoryData<string, string> CollectibleFamilies => new()
    {
        { "LlmUsage", HostedUsageJson },
        { "LlmHostedToolKind", "7" },
        { "Provenance", ProvenanceJson },
        { "LlmRequest", "{\"ModelId\":\"model\",\"Messages\":[{\"Role\":\"assistant\",\"Content\":\"text\",\"ContinuationState\":" + ContinuationJson + "}]}" },
        { "LlmRequest", "{\"ModelId\":\"model\",\"PromptCache\":{\"Scope\":0,\"Retention\":1}}" },
        { "LlmResult", ResultJson },
        { "LlmMessage", "{\"Role\":\"user\",\"Content\":\"text\"}" },
        { "ContinuationState", ContinuationJson },
        { "LlmCapabilities", "{\"ReasoningLevels\":[0,4],\"Tools\":{\"ChoiceModes\":[0,2]}}" },
        { "LlmCapabilities", "{\"PromptCache\":{\"Automatic\":true,\"AcceptsHints\":true,\"Retentions\":[0,1]}}" },
        { "LlmModel", "{\"Id\":\"model\",\"DisplayName\":\"Model\"}" },
        { "ToolDefinition", "{\"Name\":\"tool\",\"InputSchema\":{\"type\":\"object\"}}" },
        { "ToolOutput", "{\"Parts\":[{\"type\":\"text\",\"Text\":\"text\"},{\"type\":\"binary\",\"Kind\":0,\"MediaType\":\"image/png\",\"Data\":\"AQID\"},{\"type\":\"resource\",\"Uri\":\"urn:test\",\"Data\":\"\"}]}" },
        { "ToolOutputPart", "{\"type\":\"text\",\"Text\":\"text\"}" },
        { "TextDelta", "{\"Delta\":\"text\"}" },
        { "StructuredOutputSnapshot", "{\"Content\":{\"key\":true}}" },
        { "ReasoningDelta", "{\"Delta\":\"reason\",\"IsSummary\":true}" },
        { "RefusalDelta", "{\"Delta\":\"refusal\"}" },
        { "Activity", "{\"Activity\":\"waiting\"}" },
        { "ToolCallStarted", "{\"ToolCallId\":\"call\",\"Name\":\"tool\",\"Index\":0}" },
        { "ToolCallArgumentsDelta", "{\"ToolCallId\":\"call\",\"Delta\":\"{}\"}" },
        { "ToolCallCompleted", "{\"ToolCall\":{\"Id\":\"call\",\"Name\":\"tool\",\"ArgumentsJson\":\"{}\"}}" },
        { "UsageDelta", "{\"Cumulative\":{\"InputTokens\":1,\"OutputTokens\":2,\"TotalTokens\":3}}" },
        { "UsageDelta", "{\"Cumulative\":{\"InputTokens\":null,\"OutputTokens\":0,\"TotalTokens\":null,\"ProviderReportedCostUsd\":0.0000000001,\"HostedToolCalls\":{\"WebSearch\":0},\"HostedToolCallCount\":5,\"HostedSourcesUsed\":0}}" },
        { "Completion", "{\"Result\":" + ResultJson + "}" },
        { "DecisionQuestion", "{\"type\":\"choice\",\"Id\":\"question\",\"Instructions\":\"text\",\"Choices\":[{\"Id\":\"yes\"}]}" },
        { "DecisionAnswer", "{\"type\":\"boolean\",\"QuestionId\":\"question\",\"Confidence\":0.8,\"Value\":true,\"TrueProbability\":0.8}" },
        { "DecisionRequest", "{\"ModelId\":\"model\",\"State\":{},\"Questions\":[{\"type\":\"score\",\"Id\":\"question\",\"Instructions\":\"text\",\"Levels\":[]}]}" },
        { "DecisionResult", "{\"Answers\":{\"question\":{\"type\":\"choice\",\"QuestionId\":\"question\",\"Confidence\":0.8,\"ChoiceId\":\"yes\"}},\"Usage\":{\"InputTokens\":1,\"OutputTokens\":2,\"TotalTokens\":3},\"Provenance\":" + ProvenanceJson + "}" },
        { "DecisionCapabilities", "{\"SupportedQuestionKinds\":[0,1,2]}" },
        { "DecisionMeasurement", "{\"StateTokens\":1,\"StateTokenBudgets\":{\"question\":4}}" },
        { "ImageRequest", "{\"ModelId\":\"model\",\"Prompt\":\"draw\"}" },
        { "ImageResult", "{\"Images\":[{\"ContentType\":\"image/png\",\"Data\":\"AQID\"}],\"Provenance\":" + ProvenanceJson + "}" },
        { "ImageProgress", "{\"Stage\":2,\"Value\":1,\"Maximum\":4}" },
        { "SetupStatus", "{\"Ready\":{\"IsApplicable\":true,\"Value\":null}}" },
        { "SetupValidation", "{\"Issues\":[{\"Code\":\"invalid\",\"Message\":\"invalid\"}]}" },
        { "SetupIdentity", "{\"Intent\":0,\"Id\":\"entry\",\"ExpectedVersion\":null}" },
        { "SetupCommit", "{\"State\":5,\"Version\":null,\"Issues\":[]}" },
        { "SetupEdit", "{\"State\":0,\"Issues\":[]}" },
        { "SetupAction", "{\"Id\":\"check\",\"DisplayName\":\"Check\",\"Purpose\":3,\"SideEffects\":32}" },
        { "SetupProgress", "{\"Stage\":\"check\",\"Message\":null,\"Completed\":1,\"Total\":2}" },
        { "SetupChoices", "{\"Choices\":[{\"Value\":\"model\",\"DisplayName\":\"Model\"}],\"State\":0}" }
    };

    [Theory]
    [MemberData(nameof(CollectibleFamilies))]
    public void ActualCommonGenerationCollectsAfterItsOwnedMetadataRetires(string family, string json)
    {
        WeakReference generation = ExerciseGeneration(family, json);
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive, "The actual Common generation remains rooted after its owned metadata retires.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseGeneration(string family, string json)
    {
        var generation = new AssemblyLoadContext("Common serializer fixture", isCollectible: true);
        var weak = new WeakReference(generation);
        try
        {
            Assembly assembly = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assert.NotSame(typeof(InferenceJson).Assembly, assembly);
            Assert.Equal(typeof(InferenceJson).Assembly.ManifestModule.ModuleVersionId, assembly.ManifestModule.ModuleVersionId);
            Type owner = assembly.GetType("Darkspyre.Inference.Common.InferenceJson", throwOnError: true)!;
            var metadata = (JsonTypeInfo)owner.GetProperty(family)!.GetValue(null)!;
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Type.Assembly));
            Assert.Equal("InferenceJsonContext", metadata.Options.TypeInfoResolver!.GetType().Name);
            object value = JsonSerializer.Deserialize(json, metadata)!;
            string written = JsonSerializer.Serialize(value, metadata);
            Assert.NotNull(JsonSerializer.Deserialize(written, metadata));
            if (json.Contains("\"ProviderReportedCostUsd\"", StringComparison.Ordinal))
            {
                object usage = family == "LlmUsage" ? value : value.GetType().GetProperty("Cumulative")!.GetValue(value)!;
                Assert.Same(assembly, usage.GetType().Assembly);
                var map = (System.Collections.IEnumerable)usage.GetType().GetProperty("HostedToolCalls")!.GetValue(usage)!;
                object entry = Assert.Single(map.Cast<object>());
                Assert.Same(assembly, entry.GetType().GetProperty("Key")!.GetValue(entry)!.GetType().Assembly);
                using JsonDocument expected = JsonDocument.Parse(json);
                using JsonDocument actual = JsonDocument.Parse(written);
                JsonElement expectedUsage = family == "LlmUsage" ? expected.RootElement : expected.RootElement.GetProperty("Cumulative");
                JsonElement actualUsage = family == "LlmUsage" ? actual.RootElement : actual.RootElement.GetProperty("Cumulative");
                foreach (string name in new[] { "ProviderReportedCostUsd", "HostedToolCalls", "HostedToolCallCount", "HostedSourcesUsed" })
                    Assert.True(JsonElement.DeepEquals(expectedUsage.GetProperty(name), actualUsage.GetProperty(name)), name);
            }
            if (json.Contains("\"PromptCache\"", StringComparison.Ordinal))
            {
                object cache = value.GetType().GetProperty("PromptCache")!.GetValue(value)!;
                Assert.Same(assembly, cache.GetType().Assembly);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(cache.GetType().Assembly));
                using JsonDocument expected = JsonDocument.Parse(json);
                using JsonDocument actual = JsonDocument.Parse(written);
                Assert.Equal(expected.RootElement.GetProperty("PromptCache").GetRawText(), actual.RootElement.GetProperty("PromptCache").GetRawText());
            }
        }
        finally
        {
            generation.Unload();
        }
        return weak;
    }

    private static JsonElement Json(string value)
    {
        using JsonDocument document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
