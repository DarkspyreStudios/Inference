using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Image.Tests;

public sealed class ImageWorkflowOriginJsonTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void ActualOriginGetterReadRefusesUndefined(int value) => Assert.Throws<JsonException>(() =>
        JsonSerializer.Deserialize(value.ToString(CultureInfo.InvariantCulture), ConnectImageJson.ImageWorkflowOrigin));

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void ActualOriginGetterWriteRefusesUndefined(int value) => Assert.Throws<JsonException>(() =>
        JsonSerializer.Serialize((ImageWorkflowOrigin)value, ConnectImageJson.ImageWorkflowOrigin));

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void ActualSelectionGetterReadRefusesUndefined(int value) => Assert.Throws<JsonException>(() =>
        JsonSerializer.Deserialize("{\"Origin\":" + value.ToString(CultureInfo.InvariantCulture) + ",\"Id\":\"workflow\"}",
            ConnectImageJson.ImageWorkflowSelection));

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void ActualSelectionGetterWriteRefusesUndefined(int value) => Assert.Throws<JsonException>(() =>
        JsonSerializer.Serialize(new ImageWorkflowSelection((ImageWorkflowOrigin)value, "workflow"), ConnectImageJson.ImageWorkflowSelection));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualSelectionAndNestedSetupRefuseMissingOrigin(bool nested)
    {
        if (!nested)
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Id\":\"same\"}", ConnectImageJson.ImageWorkflowSelection));
            return;
        }
        var configuration = new ImageSetupConfiguration(ImageTransport.ComfyUi, new("http://localhost:8188"),
            TimeSpan.FromMinutes(5), new(ImageWorkflowOrigin.Host, "same"));
        JsonNode json = JsonNode.Parse(JsonSerializer.Serialize(configuration, ConnectImageJson.ImageSetupConfiguration))!;
        Assert.True(((JsonObject)json["Workflow"]!).Remove("Origin"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json.ToJsonString(), ConnectImageJson.ImageSetupConfiguration));
    }

    [Fact]
    public void EntireNullSelectionRemainsAbsent()
    {
        Assert.Null(JsonSerializer.Deserialize("null", ConnectImageJson.ImageWorkflowSelection));
        Assert.Equal("null", JsonSerializer.Serialize((object?)null, (JsonTypeInfo)ConnectImageJson.ImageWorkflowSelection));
    }

    [Theory]
    [InlineData(ImageWorkflowOrigin.Host, 0)]
    [InlineData(ImageWorkflowOrigin.BuiltIn, 1)]
    public void DefinedNumericValuesAndSelectionIdentityRemainExact(ImageWorkflowOrigin value, int number)
    {
        string json = number.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(json, JsonSerializer.Serialize(value, ConnectImageJson.ImageWorkflowOrigin));
        Assert.Equal(value, JsonSerializer.Deserialize(json, ConnectImageJson.ImageWorkflowOrigin));
        var selection = new ImageWorkflowSelection(value, "same/id % + \u00e9");
        string selectionJson = JsonSerializer.Serialize(selection, ConnectImageJson.ImageWorkflowSelection);
        Assert.Equal(selection, JsonSerializer.Deserialize(selectionJson, ConnectImageJson.ImageWorkflowSelection));
        using JsonDocument document = JsonDocument.Parse(selectionJson);
        Assert.Equal(number, document.RootElement.GetProperty("Origin").GetInt32());
        Assert.Equal(selection.Id, document.RootElement.GetProperty("Id").GetString());
    }

    public static int ExerciseClosedOriginPolicy()
    {
        var test = new ImageWorkflowOriginJsonTests();
        foreach (int value in new[] { -1, 99, int.MinValue, int.MaxValue })
        {
            test.ActualOriginGetterReadRefusesUndefined(value);
            test.ActualOriginGetterWriteRefusesUndefined(value);
            test.ActualSelectionGetterReadRefusesUndefined(value);
            test.ActualSelectionGetterWriteRefusesUndefined(value);
        }
        test.DefinedNumericValuesAndSelectionIdentityRemainExact(ImageWorkflowOrigin.Host, 0);
        test.DefinedNumericValuesAndSelectionIdentityRemainExact(ImageWorkflowOrigin.BuiltIn, 1);
        test.ActualSelectionAndNestedSetupRefuseMissingOrigin(false);
        test.ActualSelectionAndNestedSetupRefuseMissingOrigin(true);
        test.EntireNullSelectionRemainsAbsent();
        return 21;
    }

    [Fact]
    public void ActualForeignGetterConverterAndExecutedFixtureRetire()
    {
        WeakReference[] roots = ExerciseForeign();
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10);
        }
        foreach (WeakReference root in roots) Assert.False(root.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeign()
    {
        var generation = new Generation();
        var roots = new List<WeakReference> { new(generation) };
        try
        {
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assembly llm = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assembly image = generation.LoadFromAssemblyPath(typeof(ConnectImageJson).Assembly.Location);
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(ImageWorkflowOriginJsonTests).Assembly.Location);
            foreach ((Assembly source, Assembly actual) in new[]
            {
                (typeof(InferenceJson).Assembly, common), (typeof(ConnectLlmJson).Assembly, llm),
                (typeof(ConnectImageJson).Assembly, image), (typeof(ImageWorkflowOriginJsonTests).Assembly, fixture)
            })
            {
                Assert.NotSame(source, actual);
                Assert.Equal(source.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
                roots.Add(new(actual));
            }
            Type facade = image.GetType(typeof(ConnectImageJson).FullName!, true)!;
            roots.Add(new(facade));
            foreach (string name in new[] { nameof(ConnectImageJson.ImageWorkflowOrigin), nameof(ConnectImageJson.ImageWorkflowSelection) })
            {
                PropertyInfo getter = facade.GetProperty(name)!;
                Assert.Same(image, getter.GetMethod!.Module.Assembly);
                var metadata = (JsonTypeInfo)getter.GetValue(null)!;
                Assert.Same(image, metadata.Type.Assembly);
                Assert.Same(image, metadata.Options.TypeInfoResolver!.GetType().Assembly);
                roots.Add(new(metadata)); roots.Add(new(metadata.Type)); roots.Add(new(metadata.Options.TypeInfoResolver));
                Type origin = image.GetType(typeof(ImageWorkflowOrigin).FullName!, true)!;
                var converter = metadata.Options.GetConverter(origin);
                Assert.Same(image, converter.GetType().Assembly);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(converter.GetType().Assembly));
                roots.Add(new(converter));
            }
            Type proofType = fixture.GetType(typeof(ImageWorkflowOriginJsonTests).FullName!, true)!;
            MethodInfo proof = proofType.GetMethod(nameof(ExerciseClosedOriginPolicy))!;
            Assert.Same(fixture, proof.Module.Assembly);
            Assert.Equal(21, proof.Invoke(null, null));
            roots.Add(new(proofType)); roots.Add(new(proof));
        }
        finally { generation.Unload(); }
        return roots.ToArray();
    }

    private sealed class Generation() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()))
            ?? Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()));
    }
}
