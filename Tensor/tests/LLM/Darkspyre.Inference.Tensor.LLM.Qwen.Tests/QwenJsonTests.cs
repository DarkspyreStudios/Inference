using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class QwenJsonTests
{
    private const string ArgumentsJson = """
        {"text":"hello","nested":{"items":[1,true,null]},"empty":[],"number":1.25}
        """;

    [Fact]
    public void ParsedArgumentsPreserveNestedJsonAndNullValues()
    {
        Dictionary<string, object?> arguments = QwenJson.ReadArguments(ArgumentsJson);
        Assert.All(arguments.Values, value => Assert.IsType<JsonElement>(value));
        Assert.Equal(ArgumentsJson, QwenJson.WriteArguments(arguments));
        Assert.Empty(QwenJson.ReadArguments("null"));
        Assert.Equal("{}", QwenJson.WriteArguments([]));
    }

    [Fact]
    public void SupplierStringsAndStringArraysRemainJsonData()
    {
        Dictionary<string, object?> arguments = new()
        {
            ["program"] = "echo",
            ["args"] = new[] { "hello", "world" },
            ["missing"] = null
        };
        Assert.Equal("{\"program\":\"echo\",\"args\":[\"hello\",\"world\"],\"missing\":null}", QwenJson.WriteArguments(arguments));
    }

    [Fact]
    public void PrimitiveAndNestedProgrammaticArgumentsUseOwnedMetadata()
    {
        Dictionary<string, object?> arguments = new()
        {
            ["boolean"] = true,
            ["byte"] = (byte)1,
            ["sbyte"] = (sbyte)-1,
            ["short"] = (short)-2,
            ["ushort"] = (ushort)2,
            ["int"] = -3,
            ["uint"] = 3U,
            ["long"] = long.MinValue,
            ["ulong"] = ulong.MaxValue,
            ["float"] = 0.5F,
            ["double"] = 0.75D,
            ["decimal"] = 1.25M,
            ["array"] = new object?[] { "text", false, null },
            ["list"] = new List<object?> { 1, "text" },
            ["dictionary"] = new Dictionary<string, object?> { ["nested"] = "value" }
        };
        using JsonDocument json = JsonDocument.Parse(QwenJson.WriteArguments(arguments));
        Assert.True(json.RootElement.GetProperty("boolean").GetBoolean());
        Assert.Equal(long.MinValue, json.RootElement.GetProperty("long").GetInt64());
        Assert.Equal(ulong.MaxValue, json.RootElement.GetProperty("ulong").GetUInt64());
        Assert.Equal(1.25M, json.RootElement.GetProperty("decimal").GetDecimal());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("array")[2].ValueKind);
        Assert.Equal("value", json.RootElement.GetProperty("dictionary").GetProperty("nested").GetString());
        Assert.Equal("text", json.RootElement.GetProperty("list")[1].GetString());
    }

    [Fact]
    public void ToolsPreserveSchemaNamesOrderAndMissingDescription()
    {
        using JsonDocument schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}");
        LlmToolDefinition[] tools = [new() { Name = "function", InputSchema = schema.RootElement },
            new() { Name = "second", Description = "description", InputSchema = schema.RootElement, Strict = true }];
        Assert.Equal("[{\"name\":\"function\",\"description\":\"\",\"parameters\":{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}},"
            + "{\"name\":\"second\",\"description\":\"description\",\"parameters\":{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}}]",
            QwenJson.WriteTools(tools));
        Assert.Equal("[]", QwenJson.WriteTools([]));
    }

    [Theory]
    [InlineData("[")]
    [InlineData("[]")]
    [InlineData("true")]
    public void InvalidArgumentShapeFailsWithoutReflectionFallback(string json) => Assert.Throws<JsonException>(() => QwenJson.ReadArguments(json));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QwenOwnedSerializerGenerationCollects(bool tools)
    {
        WeakReference generation = ExerciseGeneration(tools);
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive, "The Qwen-owned JSON generation remains rooted after its metadata retires.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseGeneration(bool tools)
    {
        string assemblyPath = typeof(QwenLlmInference).Assembly.Location;
        var generation = new QwenGeneration(Path.GetDirectoryName(assemblyPath)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly qwen = generation.LoadFromAssemblyPath(assemblyPath);
            if (!tools)
            {
                Type owner = qwen.GetType("Darkspyre.Inference.Tensor.LLM.Qwen.QwenJson", throwOnError: true)!;
                object arguments = owner.GetMethod("ReadArguments")!.Invoke(null, [ArgumentsJson])!;
                Assert.Equal(ArgumentsJson, owner.GetMethod("WriteArguments")!.Invoke(null, [arguments]));
                Dictionary<string, object?> supplier = new() { ["program"] = "echo", ["args"] = new[] { "text" } };
                Assert.Contains("echo", (string)owner.GetMethod("WriteArguments")!.Invoke(null, [supplier])!, StringComparison.Ordinal);
            }
            else
            {
                Assembly fixture = generation.LoadFromAssemblyPath(typeof(QwenJsonTests).Assembly.Location);
                Type caller = fixture.GetType(typeof(QwenJsonTests).FullName!, throwOnError: true)!;
                const string expected = "[{\"name\":\"function\",\"description\":\"\",\"parameters\":{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}},"
                    + "{\"name\":\"second\",\"description\":\"description\",\"parameters\":{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}}]";
                Assert.Equal(expected, caller.GetMethod(nameof(WriteForeignTools), BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null));
            }
        }
        finally
        {
            generation.Unload();
        }
        return weak;
    }

    private static string WriteForeignTools()
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(QwenJsonTests).Assembly)!;
        Assert.True(generation.IsCollectible);
        Assert.NotSame(AssemblyLoadContext.Default, generation);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(QwenJson).Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(LlmToolDefinition).Assembly));
        JsonTypeInfo metadata = QwenJsonContext.Default.QwenToolJsonArray;
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Type.Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Options.TypeInfoResolver!.GetType().Assembly));
        using JsonDocument schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}");
        LlmToolDefinition[] tools = [new() { Name = "function", InputSchema = schema.RootElement },
            new() { Name = "second", Description = "description", InputSchema = schema.RootElement, Strict = true }];
        Assert.All(tools, tool => Assert.Same(generation, AssemblyLoadContext.GetLoadContext(tool.GetType().Assembly)));
        Assert.Equal("[]", QwenJson.WriteTools([]));
        return QwenJson.WriteTools(tools);
    }

    private sealed class QwenGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
