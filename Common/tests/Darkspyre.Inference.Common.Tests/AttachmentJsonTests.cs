using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Common.Tests;

public sealed class AttachmentJsonTests
{
    public static TheoryData<string, string> Roots => new()
    {
        { "LlmMessageAttachment", """{"type":"binary","Kind":0,"FileName":"a.png","MediaType":null,"Data":"AQ=="}""" },
        { "LlmMessageAttachment", """{"type":"file","File":{"FileId":"native"}}""" },
        { "LlmMessageAttachment", """{"type":"workspace-resource","Resource":{"WorkspaceId":"workspace","Id":"original"}}""" },
        { "LlmAttachment", """{"Kind":0,"FileName":"a.png","MediaType":null,"Data":"AQ=="}""" },
        { "LlmFileReference", """{"File":{"FileId":"native"}}""" },
        { "LlmResourceAttachment", """{"Resource":{"WorkspaceId":"workspace","Id":"original"}}""" },
        { "LlmToolOutputSessionResource", """{"Resource":{"WorkspaceId":"workspace","Id":"generated"}}""" }
    };

    [Theory]
    [MemberData(nameof(Roots))]
    public void ExactMetadataRoundTripsAllVariants(string name, string json)
    {
        JsonTypeInfo metadata = Metadata(typeof(InferenceJson), name);
        Assert.Equal("Darkspyre.Inference.Common.LLM." + name, metadata.Type.FullName);
        Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize(json, metadata), metadata));
    }

    [Fact]
    public void ActualRequestAndToolOutputGraphsPreserveReferenceIdentityAndOrder()
    {
        var request = new LlmInferenceRequest
        {
            ModelId = "model",
            Messages = [new("user", "inspect")
            {
                Attachments = [new LlmFileReference(new("native")), new LlmResourceAttachment(new("workspace", "original"))],
                ToolResults = [new LlmToolResult("call", new() { Parts = [new LlmToolOutputSessionResource(new("workspace", "generated"))] })]
            }]
        };
        string json = JsonSerializer.Serialize(request, InferenceJson.LlmRequest);
        var copy = JsonSerializer.Deserialize(json, InferenceJson.LlmRequest)!;
        Assert.Equal(request.Messages[0].Attachments, copy.Messages[0].Attachments);
        Assert.Equal(Assert.IsType<LlmToolResult>(request.Messages[0].ToolResults[0]).Output.Parts, Assert.IsType<LlmToolResult>(copy.Messages[0].ToolResults[0]).Output.Parts);
        Assert.Contains("\"type\":\"workspace-resource\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Data\"", json, StringComparison.Ordinal);
        Assert.Null(copy.Resources);
    }

    [Theory]
    [InlineData("""{"type":"other"}""")]
    [InlineData("""{"type":"file"}""")]
    [InlineData("""{"type":"workspace-resource"}""")]
    [InlineData("""{"type":"file","File":{"FileId":"native"},"Data":"AQ=="}""")]
    [InlineData("""{"type":"binary","Kind":0,"FileName":"a.png","Data":"AQ==","Resource":{"WorkspaceId":"workspace","Id":"hidden"}}""")]
    public void UnknownOrIncompleteAttachmentShapesRefuse(string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, InferenceJson.LlmMessageAttachment));

    [Theory]
    [MemberData(nameof(Roots))]
    public void ActualForeignMetadataRetires(string name, string json)
    {
        WeakReference generation = ExerciseForeign(name, json);
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign(string name, string json)
    {
        var generation = new AssemblyLoadContext("attachment metadata", isCollectible: true);
        var weak = new WeakReference(generation);
        try
        {
            Assembly assembly = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assert.NotSame(typeof(InferenceJson).Assembly, assembly);
            Assert.Equal(typeof(InferenceJson).Assembly.ManifestModule.ModuleVersionId, assembly.ManifestModule.ModuleVersionId);
            JsonTypeInfo metadata = Metadata(assembly.GetType(typeof(InferenceJson).FullName!, true)!, name);
            Assert.Same(assembly, metadata.Type.Assembly);
            object value = JsonSerializer.Deserialize(json, metadata)!;
            Assert.Same(assembly, value.GetType().Assembly);
            foreach (string property in new[] { "File", "Resource" })
                if (value.GetType().GetProperty(property)?.GetValue(value) is { } nested)
                    Assert.Same(assembly, nested.GetType().Assembly);
            Assert.Equal(json, JsonSerializer.Serialize(value, metadata));
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static JsonTypeInfo Metadata(Type owner, string name)
        => Assert.IsAssignableFrom<JsonTypeInfo>(owner.GetProperty(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null));
}
