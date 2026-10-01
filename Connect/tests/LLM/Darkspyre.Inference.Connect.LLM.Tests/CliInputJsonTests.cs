using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class CliInputJsonTests
{
    private const string AttachmentRequest = """
        {"ModelId":"model","Messages":[{"Role":"user","Content":"first","Attachments":[{"Kind":0,"FileName":"image.png","MediaType":"image/png","Data":"AQID"}]},{"Role":"user","Content":"second","Attachments":[{"Kind":1,"FileName":"notes.txt","MediaType":"text/plain","Data":"bm90ZXM="}]}]}
        """;
    private const string AttachmentWire = """
        {"type":"user","message":{"role":"user","content":[{"type":"text","text":"prompt"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AQID"}},{"type":"document","source":{"type":"text","media_type":"text/plain","data":"notes"},"title":"notes.txt"}]}}
        """ + "\n";
    private const string ToolFrame = """
        {"event":"step_update","step_update":{"step_index":0,"state":"DONE","step_type":"tool","tool_info":{"name":"view_file"}}}
        """;

    public static TheoryData<string?, string> Prompts => new()
    {
        { "plain", "\"plain\"" },
        { "", "\"\"" },
        { null, "null" },
        { "\"\\\n<&>\u00e9", "\"\\u0022\\\\\\n\\u003C\\u0026\\u003E\\u00E9\"" }
    };

    [Theory]
    [MemberData(nameof(Prompts))]
    public void CliInputRetainsExactNamesOrderEscapingNullsAndOneFinalNewline(string? prompt, string encoded)
    {
        Assert.Equal("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":" + encoded + "}]}}\n",
            ClaudeInput(new() { ModelId = "model", Messages = [] }, prompt));
        string expected = "{\"event\":\"user\",\"message\":{\"content\":" + encoded + "}}\n";
        Assert.Equal(expected, AntigravityProtocol.WriteUserInput(prompt!));
        Assert.Equal(expected, AntigravityInput(prompt));
    }

    [Fact]
    public void ClaudeInputRetainsTextThenAttachmentsInMessageOrder()
    {
        LlmChatRequest request = JsonSerializer.Deserialize(AttachmentRequest, ConnectLlmJson.ChatRequest)!;
        Assert.Equal(AttachmentWire, ClaudeInput(request, "prompt"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"parameters\":null")]
    [InlineData(",\"parameters\":[]")]
    [InlineData(",\"parameters\":{\"path\":\"notes.txt\"}")]
    public void AntigravityToolParametersRetainObjectDataOrTheEmptyObjectFallback(string parameters)
    {
        string frame = ToolFrame.Replace("\"view_file\"", "\"view_file\"" + parameters, StringComparison.Ordinal);
        AntigravityStep step = Assert.IsType<AntigravityStep>(AntigravityProtocol.Parse(frame));
        Assert.Equal(parameters.Contains("notes.txt", StringComparison.Ordinal) ? "{\"path\":\"notes.txt\"}" : "{}",
            step.Tool!.Parameters.GetRawText());
    }

    [Theory]
    [InlineData("ClaudeInput")]
    [InlineData("AntigravityInput")]
    [InlineData("AntigravityTool")]
    public void ActualCliWritersAndParserReleaseTheirCollectibleConnectCommonGeneration(string caller)
    {
        WeakReference generation = ExerciseGeneration(caller);
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive, "The settled CLI JSON caller retains its Connect/Common generation.");
    }

    private static string ClaudeInput(LlmChatRequest request, string? prompt) => (string)typeof(ClaudeCliLlmClient)
        .GetMethod("BuildClaudeStandardInput", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [request, prompt])!;

    private static string AntigravityInput(string? prompt) => (string)typeof(AntigravityCliLlmClient)
        .GetMethod("BuildStandardInput", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [prompt])!;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseGeneration(string caller)
    {
        string path = typeof(ConnectLlmJson).Assembly.Location;
        var generation = new ConnectGeneration(Path.GetDirectoryName(path)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly connect = generation.LoadFromAssemblyPath(path);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(common));
            if (caller == "ClaudeInput")
            {
                Type metadataOwner = connect.GetType("Darkspyre.Inference.Connect.LLM.ConnectLlmJson", true)!;
                var metadata = (JsonTypeInfo)metadataOwner.GetProperty("ChatRequest")!.GetValue(null)!;
                object request = JsonSerializer.Deserialize(AttachmentRequest, metadata)!;
                var messages = (System.Collections.IEnumerable)request.GetType().GetProperty("Messages")!.GetValue(request)!;
                object first = messages.Cast<object>().First();
                var attachments = (System.Collections.IEnumerable)first.GetType().GetProperty("Attachments")!.GetValue(first)!;
                Assert.Same(common, Assert.Single(attachments.Cast<object>()).GetType().Assembly);
                Type client = connect.GetType("Darkspyre.Inference.Connect.LLM.Providers.ClaudeCliLlmClient", true)!;
                Assert.Equal(AttachmentWire, client.GetMethod("BuildClaudeStandardInput", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [request, "prompt"]));
            }
            else
            {
                Type protocol = connect.GetType("Darkspyre.Inference.Connect.LLM.Providers.AntigravityProtocol", true)!;
                if (caller == "AntigravityInput")
                {
                    Type client = connect.GetType("Darkspyre.Inference.Connect.LLM.Providers.AntigravityCliLlmClient", true)!;
                    const string expected = "{\"event\":\"user\",\"message\":{\"content\":\"prompt\"}}\n";
                    Assert.Equal(expected, client.GetMethod("BuildStandardInput", BindingFlags.NonPublic | BindingFlags.Static)!
                        .Invoke(null, ["prompt"]));
                    Assert.Equal(expected, protocol.GetMethod("WriteUserInput")!.Invoke(null, ["prompt"]));
                }
                else
                {
                    object step = protocol.GetMethod("Parse")!.Invoke(null, [ToolFrame])!;
                    Assert.Same(generation, AssemblyLoadContext.GetLoadContext(step.GetType().Assembly));
                    object tool = step.GetType().GetProperty("Tool")!.GetValue(step)!;
                    var parameters = (JsonElement)tool.GetType().GetProperty("Parameters")!.GetValue(tool)!;
                    Assert.Equal("{}", parameters.GetRawText());
                }
            }
        }
        finally
        {
            generation.Unload();
        }
        return weak;
    }

    private sealed class ConnectGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
