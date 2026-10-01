using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class MixedToolContractTests
{
    private static readonly string[] OriginalCommands = ["a", "b"];
    public static TheoryData<bool, string, string> Shapes => new()
    {
        { false, "LlmClientToolDefinition", "{\"type\":\"function\",\"Name\":\"f\",\"InputSchema\":{}}" },
        { false, "LlmClientToolDefinition", "{\"type\":\"shell\",\"Skills\":[]}" },
        { false, "LlmShellSkill", "{\"Name\":\"n\",\"Description\":\"\",\"Path\":\"../caller/path\"}" },
        { false, "LlmShellSkill", "{\"Name\":\"\",\"Description\":\"\",\"Path\":\"\"}" },
        { false, "LlmShellTool", "{\"Skills\":[{\"Name\":\"n\",\"Description\":\"d\",\"Path\":\"p\"}]}" },
        { false, "LlmClientToolCall", "{\"type\":\"function\",\"Id\":\"f\",\"Name\":\"n\",\"ArgumentsJson\":\"{}\"}" },
        { false, "LlmToolCall", "{\"Id\":\"f\",\"Name\":\"n\",\"ArgumentsJson\":\"{}\"}" },
        { false, "LlmShellCall", "{\"Id\":\"s\",\"Commands\":[\"\",\"a\\nb\"],\"TimeoutMs\":9223372036854775807,\"MaxOutputLength\":-1}" },
        { false, "LlmClientToolCall", "{\"type\":\"shell\",\"Id\":\"s\",\"Commands\":[],\"TimeoutMs\":-1,\"MaxOutputLength\":0}" },
        { false, "LlmClientToolResult", "{\"type\":\"function\",\"ToolCallId\":\"f\",\"Output\":{\"Parts\":[],\"StructuredContent\":null,\"IsError\":false}}" },
        { false, "LlmToolResult", "{\"ToolCallId\":\"f\",\"Output\":{\"Parts\":[],\"StructuredContent\":null,\"IsError\":false}}" },
        { false, "LlmShellCommandResult", "{\"Stdout\":\"raw\\u0000\\n\",\"Stderr\":null,\"Outcome\":{\"type\":\"exit\",\"ExitCode\":-2147483648}}" },
        { false, "LlmShellCallResult", "{\"ToolCallId\":\"s\",\"Output\":[]}" },
        { false, "LlmClientToolResult", "{\"type\":\"shell\",\"ToolCallId\":\"s\",\"Output\":[{\"Outcome\":{\"type\":\"timeout\"}},{\"Stdout\":\"\",\"Stderr\":\"e\",\"Outcome\":{\"type\":\"exit\",\"ExitCode\":0}}]}" },
        { false, "LlmShellOutcome", "{\"type\":\"exit\",\"ExitCode\":2147483647}" },
        { false, "LlmShellOutcome", "{\"type\":\"timeout\"}" },
        { false, "LlmShellExitedOutcome", "{\"ExitCode\":0}" },
        { false, "LlmShellTimedOutOutcome", "{}" },
        { false, "LlmShellCallEvent", "{\"Call\":{\"Id\":\"s\",\"Commands\":[\"a\"],\"TimeoutMs\":null,\"MaxOutputLength\":null}}" },
        { true, "LlmAgentClientToolDefinition", "{\"type\":\"function\",\"Name\":\"n\",\"InputSchema\":{},\"Strict\":true,\"DeferLoading\":true,\"RequiresSequentialExecution\":true}" },
        { true, "LlmAgentClientToolDefinition", "{\"type\":\"shell\",\"Tool\":{\"Skills\":[]}}" },
        { true, "LlmAgentToolDefinition", "{\"Name\":\"n\",\"InputSchema\":{},\"DeferLoading\":true,\"RequiresSequentialExecution\":true}" },
        { true, "LlmAgentShellTool", "{\"Tool\":{\"Skills\":[{\"Name\":\"n\",\"Description\":\"d\",\"Path\":\"p\"}]}}" }
    };

    [Theory, MemberData(nameof(Shapes))]
    public void EveryExactOwnedGetterRoundTrips(bool connect, string name, string json)
        => RoundTrip(json, Metadata(connect ? typeof(ConnectLlmJson) : typeof(InferenceJson), name));

    [Theory]
    [InlineData("LlmShellSkill", "{\"Description\":\"d\",\"Path\":\"p\"}")]
    [InlineData("LlmShellSkill", "{\"Name\":\"n\",\"Path\":\"p\"}")]
    [InlineData("LlmShellSkill", "{\"Name\":\"n\",\"Description\":\"d\"}")]
    [InlineData("LlmShellTool", "{}")]
    [InlineData("LlmShellTool", "{\"Skills\":null}")]
    [InlineData("LlmShellTool", "{\"Skills\":[null]}")]
    [InlineData("LlmShellCall", "{\"Commands\":[]}")]
    [InlineData("LlmShellCall", "{\"Id\":\"s\"}")]
    [InlineData("LlmShellCall", "{\"Id\":\"s\",\"Commands\":[null]}")]
    [InlineData("LlmShellCall", "{\"Id\":\"s\",\"Commands\":[],\"TimeoutMs\":9223372036854775808}")]
    [InlineData("LlmShellCall", "{\"Id\":\"s\",\"Commands\":[],\"MaxOutputLength\":2147483648}")]
    [InlineData("LlmShellCommandResult", "{}")]
    [InlineData("LlmShellCommandResult", "{\"Outcome\":null}")]
    [InlineData("LlmShellCallResult", "{\"Output\":[]}")]
    [InlineData("LlmShellCallResult", "{\"ToolCallId\":\"s\"}")]
    [InlineData("LlmShellCallResult", "{\"ToolCallId\":\"s\",\"Output\":[null]}")]
    [InlineData("LlmShellExitedOutcome", "{}")]
    [InlineData("LlmShellTimedOutOutcome", "{\"ExitCode\":0}")]
    [InlineData("LlmShellCallEvent", "{}")]
    [InlineData("LlmClientToolDefinition", "{\"type\":\"unknown\"}")]
    [InlineData("LlmClientToolCall", "{\"type\":\"unknown\"}")]
    [InlineData("LlmClientToolResult", "{\"type\":\"unknown\"}")]
    [InlineData("LlmShellOutcome", "{\"type\":\"unknown\"}")]
    public void MissingMalformedOrUnknownFactsRefuse(string name, string json)
        => Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(json, Metadata(typeof(InferenceJson), name))));

    [Theory]
    [InlineData("LlmAgentShellTool", "{}")]
    [InlineData("LlmAgentShellTool", "{\"Tool\":null}")]
    [InlineData("LlmAgentClientToolDefinition", "{\"type\":\"unknown\"}")]
    public void AgentMalformedFactsRefuse(string name, string json)
        => Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(json, Metadata(typeof(ConnectLlmJson), name))));

    [Fact]
    public void PositionalApiSnapshotsAndWithValidationPreserveRawValues()
    {
        var skills = new List<LlmShellSkill> { new(Name: "n", Description: "", Path: "../not-read") };
        var tool = new LlmShellTool(Skills: skills);
        skills.Clear(); Assert.Single(tool.Skills);
        List<string> commands = ["a", "b"];
        var call = new LlmShellCall(Id: "s", Commands: commands, TimeoutMs: long.MinValue, MaxOutputLength: int.MinValue);
        commands.Clear(); Assert.Equal(OriginalCommands, call.Commands);
        (string id, var ordered, long? timeout, int? length) = call;
        Assert.Equal(("s", long.MinValue, int.MinValue), (id, timeout, length));
        Assert.Same(call.Commands, ordered);
        var replacement = new List<string> { "c" };
        var changed = call with { Commands = replacement }; replacement.Clear(); Assert.Equal("c", Assert.Single(changed.Commands));
        var output = new List<LlmShellCommandResult> { new(Stdout: null, Stderr: "", Outcome: new LlmShellTimedOutOutcome()) };
        var result = new LlmShellCallResult(ToolCallId: "s", Output: output); output.Clear(); Assert.Single(result.Output);
        (string resultId, var orderedOutput) = result;
        Assert.Equal("s", resultId); Assert.Same(result.Output, orderedOutput);
        var newOutput = new List<LlmShellCommandResult>();
        var changedResult = result with { Output = newOutput }; newOutput.Add(new("late", null, new LlmShellExitedOutcome(0)));
        Assert.Empty(changedResult.Output);
        (string skillName, string description, string path) = tool.Skills[0];
        Assert.Equal(("n", "", "../not-read"), (skillName, description, path));
        var shellEvent = new LlmShellCallEvent(Call: call); shellEvent.Deconstruct(out LlmShellCall eventCall);
        Assert.Same(call, eventCall);
        Assert.Throws<ArgumentException>(() => call with { Id = "" });
        Assert.Throws<ArgumentNullException>(() => tool with { Skills = null! });
        Assert.Throws<ArgumentNullException>(() => result with { Output = null! });
        Assert.Throws<ArgumentNullException>(() => result.Output[0] with { Outcome = null! });
        Assert.Throws<ArgumentNullException>(() => new LlmAgentShellTool(tool) with { Tool = null! });
    }

    [Fact]
    public void FunctionConcreteJsonOrderNullAndMetadataRemainUnchanged()
    {
        var call = new LlmToolCall(Id: "id", Name: "n", ArgumentsJson: "{}");
        Assert.Equal("{\"Id\":\"id\",\"Name\":\"n\",\"ArgumentsJson\":\"{}\"}", JsonSerializer.Serialize(call, InferenceJson.LlmToolCall));
        (string id, string name, string arguments) = call;
        Assert.Equal(("id", "n", "{}"), (id, name, arguments));
        Assert.Equal("{\"ToolCallId\":\"id\",\"Output\":{\"Parts\":[],\"StructuredContent\":null,\"IsError\":false}}", JsonSerializer.Serialize(new LlmToolResult("id", new()), InferenceJson.LlmToolResult));
        using JsonDocument schema = JsonDocument.Parse("{}");
        Assert.Equal("{\"Name\":\"n\",\"Description\":null,\"InputSchema\":{},\"Strict\":false}", JsonSerializer.Serialize(new LlmToolDefinition { Name = "n", InputSchema = schema.RootElement }, InferenceJson.ToolDefinition));
        var function = new LlmAgentToolDefinition { Name = "n", InputSchema = schema.RootElement, DeferLoading = true, RequiresSequentialExecution = true };
        var changed = function with { Strict = true };
        Assert.True(changed.DeferLoading); Assert.True(changed.RequiresSequentialExecution);
        Assert.DoesNotContain("\"type\"", JsonSerializer.Serialize(changed, ConnectLlmJson.LlmAgentToolDefinition), StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(Shapes))]
    public void ActualForeignCommonAndConnectMetadataRetires(bool connect, string name, string json)
    {
        WeakReference weak = ExerciseForeign(connect, name, json);
        for (int attempt = 0; weak.IsAlive && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign(bool useConnect, string name, string json)
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(ConnectLlmJson).Assembly.Location)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly connect = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            foreach ((Assembly original, Assembly actual) in new[] { (typeof(ConnectLlmJson).Assembly, connect), (typeof(InferenceJson).Assembly, common) })
            {
                Assert.NotSame(original, actual);
                Assert.Equal(original.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
            }
            Assembly owner = useConnect ? connect : common;
            Type facade = owner.GetType(useConnect ? typeof(ConnectLlmJson).FullName! : typeof(InferenceJson).FullName!, true)!;
            JsonTypeInfo metadata = Metadata(facade, name);
            Assert.Same(owner, metadata.Type.Assembly);
            object value = RoundTrip(json, metadata);
            Assert.Same(owner, value.GetType().Assembly);
            // Execute both owners even when this row's root belongs to only one assembly.
            RoundTrip("{\"Tool\":{\"Skills\":[]}}", Metadata(connect.GetType(typeof(ConnectLlmJson).FullName!, true)!, "LlmAgentShellTool"));
            RoundTrip("{\"type\":\"exit\",\"ExitCode\":-1}", Metadata(common.GetType(typeof(InferenceJson).FullName!, true)!, "LlmShellOutcome"));
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static JsonTypeInfo Metadata(Type owner, string name) => Assert.IsAssignableFrom<JsonTypeInfo>(owner.GetProperty(name)!.GetValue(null));
    private static object RoundTrip(string json, JsonTypeInfo metadata)
    {
        object value = JsonSerializer.Deserialize(json, metadata)!;
        string written = JsonSerializer.Serialize(value, metadata);
        using JsonDocument supplied = JsonDocument.Parse(json);
        using JsonDocument actual = JsonDocument.Parse(written);
        PreserveFields(supplied.RootElement, actual.RootElement);
        Assert.Equal(written, JsonSerializer.Serialize(JsonSerializer.Deserialize(written, metadata), metadata));
        return value;
    }

    private static void PreserveFields(JsonElement supplied, JsonElement actual)
    {
        if (supplied.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty field in supplied.EnumerateObject())
            { Assert.True(actual.TryGetProperty(field.Name, out JsonElement stored), field.Name); PreserveFields(field.Value, stored); }
        }
        else if (supplied.ValueKind == JsonValueKind.Array)
        {
            Assert.Equal(supplied.GetArrayLength(), actual.GetArrayLength());
            for (int i = 0; i < supplied.GetArrayLength(); i++) PreserveFields(supplied[i], actual[i]);
        }
        else { Assert.True(JsonElement.DeepEquals(supplied, actual), supplied.GetRawText()); }
    }

    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = System.IO.Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
