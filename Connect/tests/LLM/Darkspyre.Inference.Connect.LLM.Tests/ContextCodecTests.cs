using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ContextCodecTests
{
    [Fact]
    public void ActualChatRequestResourcesUseCommonRefusalWithoutCallbacks() =>
        Assert.Equal("refused-without-callbacks", ContextResourceAuthorityFixture.Exercise());

    private const string LegacyJson = """
        {"FormatVersion":2,"Revision":17,"Entries":[
        {"Id":"user","TurnId":"turn","Message":{"Role":"user","Content":"Read","Attachments":[{"Kind":1,"FileName":"reference.txt","MediaType":"text/plain","Data":"AQID"}],"OmittedAttachments":[{"Kind":0,"FileName":"omitted.png","Index":1}]}},
        {"Id":"call","TurnId":"turn","Message":{"Role":"assistant","Content":"","ToolCalls":[{"Id":"actual-call","Name":"create","ArgumentsJson":"{\"a\":1}"}],"ContinuationState":{"FormatVersion":2,"ImplementationId":"provider","ModelId":"model","ScopeId":"scope","Payload":"opaque unchanged"}}},
        {"Id":"result","TurnId":"turn","Message":{"Role":"user","Content":"","ToolResults":[{"ToolCallId":"actual-call","Output":{"Parts":[{"type":"text","Text":"done"},{"type":"binary","Kind":1,"MediaType":"application/json","Data":"BAU=","FileName":"generated.json"},{"type":"resource","Uri":"urn:existing","Name":"linked","MediaType":"text/plain","Text":"existing body","Data":null}],"StructuredContent":{"actual":true},"IsError":true}}]}}]}
        """;

    [Fact]
    public void LegacyTwoUpgradesOnlyItsClosedGraphAndPreservesEveryPortableFact()
    {
        LlmAgentContext context = JsonSerializer.Deserialize(LegacyJson, ConnectLlmJson.AgentContext)!;
        Assert.Equal(3, context.FormatVersion);
        Assert.Equal(17, context.Revision);
        Assert.Empty(context.SelectedResources);
        Assert.Null(context.ResourceSelectionVersion);
        Assert.Equal(["user", "call", "result"], context.Entries.Select(static entry => entry.Id).ToArray());
        Assert.All(context.Entries, static entry => Assert.Equal("turn", entry.TurnId));
        var binary = Assert.IsType<LlmAttachment>(Assert.Single(context.Entries[0].Message.Attachments));
        Assert.Equal(new byte[] { 1, 2, 3 }, binary.Data.ToArray());
        Assert.Equal("reference.txt", binary.FileName);
        Assert.Equal("text/plain", binary.MediaType);
        Assert.Equal(new LlmOmittedAttachment(LlmBinaryKind.Image, "omitted.png", 1), Assert.Single(context.Entries[0].Message.OmittedAttachments));
        Assert.Equal(new LlmToolCall("actual-call", "create", "{\"a\":1}"), Assert.Single(context.Entries[1].Message.ToolCalls));
        Assert.Equal("opaque unchanged", context.Entries[1].Message.ContinuationState!.Payload);
        Assert.Equal("scope", context.Entries[1].Message.ContinuationState!.ScopeId);
        LlmToolResult result = Assert.IsType<LlmToolResult>(Assert.Single(context.Entries[2].Message.ToolResults));
        Assert.Equal("actual-call", result.ToolCallId);
        Assert.True(result.Output.IsError);
        Assert.True(result.Output.StructuredContent!.Value.GetProperty("actual").GetBoolean());
        Assert.Equal("done", Assert.IsType<LlmToolOutputText>(result.Output.Parts[0]).Text);
        Assert.Equal(new byte[] { 4, 5 }, Assert.IsType<LlmToolOutputBinary>(result.Output.Parts[1]).Data.ToArray());
        Assert.Equal("existing body", Assert.IsType<LlmToolOutputResource>(result.Output.Parts[2]).Text);
        string emitted = JsonSerializer.Serialize(context, ConnectLlmJson.AgentContext);
        using JsonDocument document = JsonDocument.Parse(emitted);
        Assert.Equal(3, document.RootElement.GetProperty("FormatVersion").GetInt32());
        Assert.Equal("binary", document.RootElement.GetProperty("Entries")[0].GetProperty("Message").GetProperty("Attachments")[0].GetProperty("type").GetString());
        Assert.Equal(emitted, JsonSerializer.Serialize(JsonSerializer.Deserialize(emitted, ConnectLlmJson.AgentContext)!, ConnectLlmJson.AgentContext));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"FormatVersion\":null}")]
    [InlineData("{\"FormatVersion\":\"3\"}")]
    [InlineData("{\"FormatVersion\":4,\"SelectedResources\":[]}")]
    [InlineData("{\"FormatVersion\":3}")]
    [InlineData("{\"FormatVersion\":3,\"SelectedResources\":null}")]
    [InlineData("{\"FormatVersion\":2,\"SelectedResources\":[]}")]
    [InlineData("{\"FormatVersion\":2,\"ResourceSelectionVersion\":null}")]
    [InlineData("{\"FormatVersion\":2,\"FormatVersion\":3,\"SelectedResources\":[]}")]
    [InlineData("{\"FormatVersion\":3,\"SelectedResources\":[],\"Entries\":null}")]
    [InlineData("{\"FormatVersion\":3,\"SelectedResources\":[],\"Revision\":-1}")]
    public void InvalidUnknownOrAmbiguousContextFormatsRefuse(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, ConnectLlmJson.AgentContext));

    [Theory]
    [InlineData("entry")]
    [InlineData("message")]
    [InlineData("attachments")]
    [InlineData("attachment")]
    [InlineData("calls")]
    [InlineData("call")]
    [InlineData("results")]
    [InlineData("result")]
    [InlineData("output")]
    [InlineData("parts")]
    [InlineData("part")]
    [InlineData("omissions")]
    [InlineData("omission")]
    public void NullNestedGraphsRejectInBothFormats(string component)
    {
        foreach (bool legacy in new[] { true, false })
        {
            JsonObject context = JsonNode.Parse(legacy ? LegacyJson : JsonSerializer.Serialize(
                JsonSerializer.Deserialize(LegacyJson, ConnectLlmJson.AgentContext)!, ConnectLlmJson.AgentContext))!.AsObject();
            switch (component)
            {
                case "entry": context["Entries"]![0] = null; break;
                case "message": context["Entries"]![0]!["Message"] = null; break;
                case "attachments": context["Entries"]![0]!["Message"]!["Attachments"] = null; break;
                case "attachment": context["Entries"]![0]!["Message"]!["Attachments"]![0] = null; break;
                case "calls": context["Entries"]![1]!["Message"]!["ToolCalls"] = null; break;
                case "call": context["Entries"]![1]!["Message"]!["ToolCalls"]![0] = null; break;
                case "results": context["Entries"]![2]!["Message"]!["ToolResults"] = null; break;
                case "result": context["Entries"]![2]!["Message"]!["ToolResults"]![0] = null; break;
                case "output": context["Entries"]![2]!["Message"]!["ToolResults"]![0]!["Output"] = null; break;
                case "parts": context["Entries"]![2]!["Message"]!["ToolResults"]![0]!["Output"]!["Parts"] = null; break;
                case "part": context["Entries"]![2]!["Message"]!["ToolResults"]![0]!["Output"]!["Parts"]![0] = null; break;
                case "omissions": context["Entries"]![0]!["Message"]!["OmittedAttachments"] = null; break;
                case "omission": context["Entries"]![0]!["Message"]!["OmittedAttachments"]![0] = null; break;
            }
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(context.ToJsonString(), ConnectLlmJson.AgentContext));
        }
    }

    [Theory]
    [InlineData("file")]
    [InlineData("workspace-resource")]
    [InlineData("binary")]
    public void LegacyAttachmentsCannotAdmitNewUnionVariants(string kind)
    {
        JsonObject context = JsonNode.Parse(LegacyJson)!.AsObject();
        context["Entries"]![0]!["Message"]!["Attachments"]![0]!["type"] = kind;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(context.ToJsonString(), ConnectLlmJson.AgentContext));
    }

    [Fact]
    public void LegacyToolsRejectNewResourcePartsAndClientToolDiscriminators()
    {
        JsonObject context = JsonNode.Parse(LegacyJson)!.AsObject();
        context["Entries"]![1]!["Message"]!["ToolCalls"]![0]!["type"] = "shell";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(context.ToJsonString(), ConnectLlmJson.AgentContext));
        context = JsonNode.Parse(LegacyJson)!.AsObject();
        context["Entries"]![2]!["Message"]!["ToolResults"]![0]!["Output"]!["Parts"]![0] =
            JsonNode.Parse("{\"type\":\"workspace-resource\",\"Resource\":{\"WorkspaceId\":\"ws\",\"Id\":\"created\"}}");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(context.ToJsonString(), ConnectLlmJson.AgentContext));
    }

    [Theory]
    [InlineData("duplicate-entry")]
    [InlineData("missing-turn")]
    [InlineData("orphan-result")]
    [InlineData("invalid-arguments")]
    [InlineData("invalid-omission")]
    public void LegacyStructuralFailuresStayFailuresDuringUpgrade(string failure)
    {
        JsonObject context = JsonNode.Parse(LegacyJson)!.AsObject();
        switch (failure)
        {
            case "duplicate-entry": context["Entries"]![1]!["Id"] = "user"; break;
            case "missing-turn": context["Entries"]![0]!.AsObject().Remove("TurnId"); break;
            case "orphan-result": context["Entries"]![2]!["Message"]!["ToolResults"]![0]!["ToolCallId"] = "not-actual"; break;
            case "invalid-arguments": context["Entries"]![1]!["Message"]!["ToolCalls"]![0]!["ArgumentsJson"] = "[]"; break;
            case "invalid-omission": context["Entries"]![0]!["Message"]!["OmittedAttachments"]![0]!["Index"] = -1; break;
        }
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(context.ToJsonString(), ConnectLlmJson.AgentContext));
    }

    [Fact]
    public void CurrentSelectionIsCopiedScopedOrderedAndIndependentOfTranscriptRevision()
    {
        InferenceResourceReference[] references = [new("ws", "original"), new("ws", "generated")];
        var version = new InferenceResourceSelectionVersion("ws", "logical-session", 42);
        var context = new LlmAgentContext { Revision = 7, SelectedResources = references, ResourceSelectionVersion = version };
        references[0] = new("ws", "replacement");
        Assert.Equal(["original", "generated"], context.SelectedResources.Select(static reference => reference.Id).ToArray());
        Assert.NotSame(version, context.ResourceSelectionVersion);
        Assert.Throws<NotSupportedException>(() => ((IList<InferenceResourceReference>)context.SelectedResources)[0] = references[0]);
        LlmAgentContext restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(context, ConnectLlmJson.AgentContext), ConnectLlmJson.AgentContext)!;
        Assert.Equal(7, restored.Revision);
        Assert.Equal(version, restored.ResourceSelectionVersion);
        Assert.Equal(context.SelectedResources, restored.SelectedResources);
        Assert.Throws<NotSupportedException>(() => ((IList<InferenceResourceReference>)restored.SelectedResources).Clear());
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("foreign-reference")]
    [InlineData("foreign-version")]
    [InlineData("null-reference")]
    [InlineData("empty-id")]
    [InlineData("negative-version")]
    public void CurrentInvalidSelectionRefusesWithoutAnyResourceAuthority(string failure)
    {
        JsonObject context = JsonNode.Parse("""
            {"FormatVersion":3,"SelectedResources":[{"WorkspaceId":"ws","Id":"original"},{"WorkspaceId":"ws","Id":"generated"}],
             "ResourceSelectionVersion":{"WorkspaceId":"ws","SessionId":"logical-session","Revision":5}}
            """)!.AsObject();
        switch (failure)
        {
            case "duplicate": context["SelectedResources"]![1]!["Id"] = "original"; break;
            case "foreign-reference": context["SelectedResources"]![1]!["WorkspaceId"] = "other"; break;
            case "foreign-version": context["ResourceSelectionVersion"]!["WorkspaceId"] = "other"; break;
            case "null-reference": context["SelectedResources"]![0] = null; break;
            case "empty-id": context["SelectedResources"]![0]!["Id"] = ""; break;
            case "negative-version": context["ResourceSelectionVersion"]!["Revision"] = -1; break;
        }
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(context.ToJsonString(), ConnectLlmJson.AgentContext));
    }

    [Fact]
    public void OnlyOwnedContextMetadataPerformsNarrowMigrationAndEmitsThree()
    {
        Assert.Equal(3, new LlmAgentContext().FormatVersion);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(new LlmAgentContext { FormatVersion = 2 }, ConnectLlmJson.AgentContext));
        LlmAgentContext manual = new() { SelectedResources = [new("ws", "manual")] };
        Assert.Null(JsonSerializer.Deserialize(JsonSerializer.Serialize(manual, ConnectLlmJson.AgentContext), ConnectLlmJson.AgentContext)!.ResourceSelectionVersion);
    }

    [Fact]
    public void FormatThreePreservesOneOrderedUnionAndIndependentSelectedMembership()
    {
        var reference = new InferenceResourceReference("ws", "created-json");
        var context = new LlmAgentContext
        {
            Revision = 9,
            SelectedResources = [reference],
            Entries =
            [
                new("original", "turn", new("user", "Read")
                {
                    Attachments =
                    [
                        new LlmFileReference(new ProviderFileReference("native-id")),
                        new LlmAttachment { Kind = LlmBinaryKind.Document, FileName = "original.txt", Data = new byte[] { 7, 8 } },
                        new LlmResourceAttachment(reference),
                        new LlmFileReference(new ProviderFileReference("native-id"))
                    ]
                }),
                new("call", "turn", new("assistant", "") { ToolCalls = [new LlmToolCall("actual", "create", "{}")] }),
                new("result", "turn", new("user", "")
                {
                    ToolResults = [new LlmToolResult("actual", new() { Parts = [new LlmToolOutputSessionResource(reference)] })]
                })
            ]
        };
        LlmAgentContext restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(context, ConnectLlmJson.AgentContext), ConnectLlmJson.AgentContext)!;
        Assert.Equal(9, restored.Revision);
        Assert.Equal(reference, Assert.Single(restored.SelectedResources));
        IReadOnlyList<LlmMessageAttachment> attachments = restored.Entries[0].Message.Attachments;
        Assert.Equal(4, attachments.Count);
        Assert.Equal("native-id", Assert.IsType<LlmFileReference>(attachments[0]).File.FileId);
        Assert.Equal(new byte[] { 7, 8 }, Assert.IsType<LlmAttachment>(attachments[1]).Data.ToArray());
        Assert.Equal(reference, Assert.IsType<LlmResourceAttachment>(attachments[2]).Resource);
        Assert.Equal("native-id", Assert.IsType<LlmFileReference>(attachments[3]).File.FileId);
        Assert.Equal(reference, Assert.IsType<LlmToolOutputSessionResource>(Assert.Single(Assert.IsType<LlmToolResult>(restored.Entries[2].Message.ToolResults[0]).Output.Parts)).Resource);
        LlmAgentContext excluded = ManagedAgentContext.Copy(restored, false);
        LlmAgentContext byteExcluded = JsonSerializer.Deserialize(JsonSerializer.Serialize(excluded, ConnectLlmJson.AgentContext), ConnectLlmJson.AgentContext)!;
        Assert.Equal(reference, Assert.Single(byteExcluded.SelectedResources));
        Assert.Equal(3, byteExcluded.Entries[0].Message.Attachments.Count);
        Assert.Equal(new LlmOmittedAttachment(LlmBinaryKind.Document, "original.txt", 1),
            Assert.Single(byteExcluded.Entries[0].Message.OmittedAttachments));
        Assert.Equal(reference, Assert.IsType<LlmResourceAttachment>(byteExcluded.Entries[0].Message.Attachments[1]).Resource);
        Assert.Equal(reference, Assert.IsType<LlmToolOutputSessionResource>(Assert.Single(Assert.IsType<LlmToolResult>(byteExcluded.Entries[2].Message.ToolResults[0]).Output.Parts)).Resource);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualOwnedForeignCodecMigratesAndRetires(bool legacy)
    {
        WeakReference generation = ExerciseForeign(legacy);
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive, "The actual context codec generation remains rooted after its metadata retires.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign(bool legacy)
    {
        string connectPath = typeof(ConnectLlmJson).Assembly.Location;
        var generation = new ContextGeneration(Path.GetDirectoryName(connectPath)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly connect = generation.LoadFromAssemblyPath(connectPath);
            Assembly common = generation.LoadFromAssemblyName(typeof(InferenceJson).Assembly.GetName());
            Assembly fixtureAssembly = generation.LoadFromAssemblyPath(typeof(ContextResourceAuthorityFixture).Assembly.Location);
            Assert.Equal(typeof(ConnectLlmJson).Assembly.ManifestModule.ModuleVersionId, connect.ManifestModule.ModuleVersionId);
            Assert.Equal(typeof(InferenceJson).Assembly.ManifestModule.ModuleVersionId, common.ManifestModule.ModuleVersionId);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(connect));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(common));
            Assert.Equal(typeof(ContextResourceAuthorityFixture).Assembly.ManifestModule.ModuleVersionId, fixtureAssembly.ManifestModule.ModuleVersionId);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixtureAssembly));
            MethodInfo exercise = fixtureAssembly.GetType(typeof(ContextResourceAuthorityFixture).FullName!, throwOnError: true)!.GetMethod("Exercise")!;
            Assert.Equal(fixtureAssembly.ManifestModule.ModuleVersionId, exercise.Module.ModuleVersionId);
            Assert.Equal("refused-without-callbacks", exercise.Invoke(null, null));
            Type owner = connect.GetType("Darkspyre.Inference.Connect.LLM.ConnectLlmJson", throwOnError: true)!;
            PropertyInfo getter = owner.GetProperty("AgentContext")!;
            Assert.Equal(connect.ManifestModule.ModuleVersionId, getter.GetMethod!.Module.ModuleVersionId);
            var metadata = (JsonTypeInfo)getter.GetValue(null)!;
            Assert.Same(connect, metadata.Converter.GetType().Assembly);
            string input = legacy ? LegacyJson : """
                {"FormatVersion":3,"Revision":11,"SelectedResources":[{"WorkspaceId":"ws","Id":"generated"}],
                 "ResourceSelectionVersion":{"WorkspaceId":"ws","SessionId":"logical","Revision":19},
                 "Entries":[{"Id":"actual-entry","TurnId":"actual-turn","Message":{"Role":"user","Content":"Inspect",
                   "Attachments":[{"type":"file","File":{"FileId":"actual-native"}},
                     {"type":"workspace-resource","Resource":{"WorkspaceId":"ws","Id":"generated"}}]}}]}
                """;
            object value = JsonSerializer.Deserialize(input, metadata)!;
            Assert.Same(connect, value.GetType().Assembly);
            string written = JsonSerializer.Serialize(value, metadata);
            using JsonDocument document = JsonDocument.Parse(written);
            Assert.Equal(3, document.RootElement.GetProperty("FormatVersion").GetInt32());
            Assert.Equal(legacy ? 17 : 11, document.RootElement.GetProperty("Revision").GetInt64());
            JsonElement selected = document.RootElement.GetProperty("SelectedResources");
            Assert.Equal(legacy ? 0 : 1, selected.GetArrayLength());
            if (!legacy)
            {
                object stamp = value.GetType().GetProperty("ResourceSelectionVersion")!.GetValue(value)!;
                Assert.Same(common, stamp.GetType().Assembly);
                Assert.Equal("generated", selected[0].GetProperty("Id").GetString());
                Assert.Equal(19, document.RootElement.GetProperty("ResourceSelectionVersion").GetProperty("Revision").GetInt64());
                JsonElement attachments = document.RootElement.GetProperty("Entries")[0].GetProperty("Message").GetProperty("Attachments");
                Assert.Equal("file", attachments[0].GetProperty("type").GetString());
                Assert.Equal("actual-native", attachments[0].GetProperty("File").GetProperty("FileId").GetString());
                Assert.Equal("workspace-resource", attachments[1].GetProperty("type").GetString());
                object entry = ((System.Collections.IEnumerable)value.GetType().GetProperty("Entries")!.GetValue(value)!).Cast<object>().Single();
                object message = entry.GetType().GetProperty("Message")!.GetValue(entry)!;
                object[] foreignAttachments = ((System.Collections.IEnumerable)message.GetType().GetProperty("Attachments")!.GetValue(message)!).Cast<object>().ToArray();
                Assert.Same(common, foreignAttachments[0].GetType().Assembly);
                Assert.Same(common, foreignAttachments[1].GetType().Assembly);
                Assert.Same(common, foreignAttachments[0].GetType().GetProperty("File")!.GetValue(foreignAttachments[0])!.GetType().Assembly);
                Assert.Same(common, foreignAttachments[1].GetType().GetProperty("Resource")!.GetValue(foreignAttachments[1])!.GetType().Assembly);
            }
            Assert.NotNull(JsonSerializer.Deserialize(written, metadata));
        }
        finally { generation.Unload(); }
        return weak;
    }

    private sealed class ContextGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}

public static class ContextResourceAuthorityFixture
{
    public static string Exercise()
    {
        var access = new ThrowingAccess();
        var metadata = ConnectLlmJson.ChatRequest;
        var converter = metadata.Options.Converters.Single(value => value.CanConvert(typeof(IInferenceResourceAccess)));
        if (!ReferenceEquals(converter.GetType().Assembly, typeof(InferenceJson).Assembly))
            throw new InvalidOperationException("Chat request metadata did not use the actual Common authority converter.");
        var empty = new LlmChatRequest { ModelId = "model" };
        string json = JsonSerializer.Serialize(empty, metadata);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.GetProperty("Resources").ValueKind != JsonValueKind.Null
            || JsonSerializer.Deserialize(json, metadata)!.Resources is not null)
            throw new InvalidOperationException("Null resource authority changed shape.");
        try { JsonSerializer.Serialize(empty with { Resources = access }, metadata); throw new InvalidOperationException("Live authority serialized."); }
        catch (NotSupportedException) { }
        try { JsonSerializer.Deserialize("""{"ModelId":"model","Resources":{}}""", metadata); throw new InvalidOperationException("Live authority deserialized."); }
        catch (NotSupportedException) { }
        _ = (empty with { Resources = access }).ToString();
        if (access.Touched != 0) throw new InvalidOperationException("Request serialization or diagnostics invoked borrowed authority.");
        return "refused-without-callbacks";
    }

    private sealed class ThrowingAccess : IInferenceResourceAccess
    {
        public int Touched { get; private set; }
        public string WorkspaceId => Forbidden<string>();
        public string SessionId => Forbidden<string>();
        public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => Forbidden<Task<InferenceResourceDescriptor?>>();
        public Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => Forbidden<Task<IInferenceResourceReadHandle>>();
        public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default) => Forbidden<Task<IInferenceResourceWriteHandle>>();
        public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) => Forbidden<Task<InferenceResourceSelection>>();
        public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => Forbidden<Task<InferenceResourceSelection>>();
        public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden<Task<InferenceResourceSelection>>();
        public override string ToString() => Forbidden<string>();
        private T Forbidden<T>()
        {
            Touched++;
            throw new InvalidOperationException("Borrowed resource access was touched.");
        }
    }
}
