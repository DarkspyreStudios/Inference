using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AppleProtocolJsonTests
{
    private const string GenerationJson = """
        {"instructions":"<&>","messages":[{"role":"user","content":"text","calls":[{"id":"call","name":"lookup","arguments":"{}"}],"results":[{"id":"call","name":"lookup","text":"result"}]}],"tools":[{"name":"lookup","schema":{"kind":"object","properties":[{"name":"name","required":true,"schema":{"kind":"string","choices":[]}}]}}],"schema":{"kind":"array","items":{"kind":"integer","minimum":"1","maximum":"9"},"minItems":0,"maxItems":2},"temperature":0.5,"maximumResponseTokens":7,"stream":false,"topP":0.8,"topK":3,"seed":42}
        """;

    [Fact]
    public void CommandWireBytesPreserveWebNamesNullOmissionEscapingAndEverySamplingField()
    {
        AppleGeneration generation = JsonSerializer.Deserialize(GenerationJson, AppleProtocolJsonContext.Default.AppleGeneration)!;
        string expected = "{\"command\":\"generate\",\"id\":\"request\",\"request\":"
            + GenerationJson.Replace("<&>", "\\u003C\\u0026\\u003E", StringComparison.Ordinal) + "}";
        Assert.Equal(expected, JsonSerializer.Serialize(new AppleCommand("generate", "request", generation), AppleProtocolJsonContext.Default.AppleCommand));
        Assert.Equal("{\"command\":\"cancel\",\"id\":\"request\"}",
            JsonSerializer.Serialize(new AppleCommand("cancel", "request"), AppleProtocolJsonContext.Default.AppleCommand));
        Assert.Equal("{\"instructions\":\"\",\"messages\":[],\"tools\":[],\"stream\":true}",
            JsonSerializer.Serialize(new AppleGeneration("", [], [], null, null, null, true, null, null, null), AppleProtocolJsonContext.Default.AppleGeneration));
    }

    [Fact]
    public void WebReadPolicyPreservesCaseInsensitivityQuotedNumbersAndUnknownMembers()
    {
        AppleGeneration generation = JsonSerializer.Deserialize("""
            {"Messages":[],"Tools":[],"Stream":false,"Temperature":"0.5","MaximumResponseTokens":"7","TopP":"0.8","TopK":"3","Seed":"42","future":true}
            """, AppleProtocolJsonContext.Default.AppleGeneration)!;
        Assert.Equal(.5, generation.Temperature);
        Assert.Equal(7, generation.MaximumResponseTokens);
        Assert.Equal(.8, generation.TopP);
        Assert.Equal(3, generation.TopK);
        Assert.Equal(42, generation.Seed);
        Assert.False(generation.Stream);
    }

    [Fact]
    public void RecursiveSchemaMetadataRetainsDepthLimitWithoutReflectionFallback()
    {
        Assert.Equal(128, AppleProtocolJsonContext.Default.Options.MaxDepth);
        AppleSchemaNode schema = Nested(100);
        string json = JsonSerializer.Serialize(schema, AppleProtocolJsonContext.Default.AppleSchemaNode);
        Assert.NotNull(JsonSerializer.Deserialize(json, AppleProtocolJsonContext.Default.AppleSchemaNode));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(Nested(140), AppleProtocolJsonContext.Default.AppleSchemaNode));
        Assert.Null(AppleProtocolJsonContext.Default.GetTypeInfo(typeof(LlmConnection)));
    }

    public static TheoryData<string, string> CollectibleFamilies => new()
    {
        { "AppleCommand", "{\"command\":\"generate\",\"id\":\"request\",\"request\":" + GenerationJson + "}" },
        { "AppleGeneration", GenerationJson },
        { "AppleMessage", "{\"role\":\"user\",\"content\":\"text\",\"calls\":[],\"results\":[]}" },
        { "AppleCall", "{\"id\":\"call\",\"name\":\"lookup\",\"arguments\":\"{}\"}" },
        { "AppleResult", "{\"id\":\"call\",\"name\":\"lookup\",\"text\":\"result\"}" },
        { "AppleTool", "{\"name\":\"lookup\",\"schema\":{\"kind\":\"object\",\"properties\":[]}}" },
        { "AppleSchemaNode", "{\"kind\":\"array\",\"items\":{\"kind\":\"string\",\"choices\":[]}}" },
        { "AppleSchemaProperty", "{\"name\":\"name\",\"required\":true,\"schema\":{\"kind\":\"string\"}}" }
    };

    [Theory]
    [MemberData(nameof(CollectibleFamilies))]
    public void ActualCollectibleConnectProtocolMetadataRetires(string family, string json)
    {
        WeakReference generation = ExerciseGeneration(family, json);
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive, "The private Apple protocol metadata still roots its collectible Connect generation.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseGeneration(string family, string json)
    {
        string path = typeof(ConnectLlmJson).Assembly.Location;
        var generation = new ProtocolGeneration(Path.GetDirectoryName(path)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly connect = generation.LoadFromAssemblyPath(path);
            Type owner = connect.GetType("Darkspyre.Inference.Connect.LLM.Providers.AppleProtocolJsonContext", throwOnError: true)!;
            object context = owner.GetProperty("Default")!.GetValue(null)!;
            JsonTypeInfo metadata = (JsonTypeInfo)owner.GetProperty(family)!.GetValue(context)!;
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Type.Assembly));
            Assert.Same(context, metadata.Options.TypeInfoResolver);
            object value = JsonSerializer.Deserialize(json, metadata)!;
            string written = JsonSerializer.Serialize(value, metadata);
            Assert.NotNull(JsonSerializer.Deserialize(written, metadata));
            if (family == "AppleCommand")
            {
                object request = value.GetType().GetProperty("Request")!.GetValue(value)!;
                Assert.Same(connect, request.GetType().Assembly);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(request.GetType().Assembly));
            }
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static AppleSchemaNode Nested(int count)
    {
        var schema = new AppleSchemaNode("string");
        for (int index = 0; index < count; index++) schema = new("array") { Items = schema };
        return schema;
    }

    private sealed class ProtocolGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
