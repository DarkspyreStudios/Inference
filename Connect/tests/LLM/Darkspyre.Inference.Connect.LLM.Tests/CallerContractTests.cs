using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class CallerContractTests
{
    [Theory]
    [InlineData("/workspace/a%20b/../raw")]
    [InlineData("C:\\workspace\\a%20b\\..\\raw")]
    [InlineData("C:/workspace/a%20b/../raw")]
    [InlineData("\\\\server\\share\\a%20b\\..\\raw")]
    public void AbsolutePathTextRoundTripsAcrossHostGrammars(string path)
    {
        var scope = new LlmAgentCallbackScope("thread", "session", null, path, null);
        var read = new LlmAgentTextFileReadRequest(scope, path, 1, 0);
        var write = new LlmAgentTextFileWriteRequest(scope, path, "text");
        var terminal = new LlmAgentTerminalCreateRequest(scope, "command", [], [], path, null);
        var location = new LlmAgentToolLocation(path, null);
        var diff = new LlmAgentToolPresentedDiff(path, null, "text");
        Assert.Equal(path, JsonSerializer.Deserialize(JsonSerializer.Serialize(read, ConnectLlmJson.LlmAgentTextFileReadRequest), ConnectLlmJson.LlmAgentTextFileReadRequest)!.Path);
        Assert.Equal(path, JsonSerializer.Deserialize(JsonSerializer.Serialize(write, ConnectLlmJson.LlmAgentTextFileWriteRequest), ConnectLlmJson.LlmAgentTextFileWriteRequest)!.Path);
        Assert.Equal(path, JsonSerializer.Deserialize(JsonSerializer.Serialize(terminal, ConnectLlmJson.LlmAgentTerminalCreateRequest), ConnectLlmJson.LlmAgentTerminalCreateRequest)!.WorkingDirectory);
        Assert.Equal(path, JsonSerializer.Deserialize(JsonSerializer.Serialize(location, ConnectLlmJson.LlmAgentToolLocation), ConnectLlmJson.LlmAgentToolLocation)!.Path);
        Assert.Equal(path, JsonSerializer.Deserialize(JsonSerializer.Serialize(diff, ConnectLlmJson.LlmAgentToolPresentedDiff), ConnectLlmJson.LlmAgentToolPresentedDiff)!.Path);
        Assert.Equal(path, JsonSerializer.Deserialize(JsonSerializer.Serialize(scope, ConnectLlmJson.LlmAgentCallbackScope), ConnectLlmJson.LlmAgentCallbackScope)!.WorkingDirectory);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("C:relative")]
    [InlineData("\\relative")]
    [InlineData("\\\\server")]
    [InlineData("\\\\server\\")]
    [InlineData("file:///workspace/file")]
    [InlineData("FILE:///C:/workspace/file")]
    public void RelativePathsAndFileUrlsRefuseWithoutNormalization(string path)
    {
        var scope = new LlmAgentCallbackScope("thread", "session", null, null, null);
        Assert.Throws<ArgumentException>(() => new LlmAgentTextFileReadRequest(scope, path, 1, 0));
        Assert.Throws<ArgumentException>(() => scope with { WorkingDirectory = path });
    }
    public static TheoryData<string, string> Shapes => new()
    {
        { "LlmAgentShellRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Call\":{\"Id\":\"shell\",\"Commands\":[\"raw\\ncommand\"],\"TimeoutMs\":-1,\"MaxOutputLength\":0}}" },
        { "LlmAgentCallerServiceKind", "4" },
        { "LlmAgentPermissionChoiceKind", "3" },
        { "LlmAgentCallbackScope", "{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null}" },
        { "LlmAgentCallbackScope", "{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":\"/workspace\",\"RuntimePolicy\":{\"type\":\"host-controlled\",\"Kind\":0,\"WritableRoots\":[\"/workspace\"],\"NetworkAllowed\":false,\"Sandbox\":0,\"ApprovalPolicy\":1,\"AllowRuntimeBackgroundWork\":false}}" },
        { "LlmAgentPermissionChoice", "{\"Id\":\"choice\",\"Label\":\"\",\"Kind\":0}" },
        { "LlmAgentPermissionRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Tool\":{\"CallId\":\"call\",\"RawInput\":{\"nested\":{\"secret\":\"raw\"}},\"RawOutput\":null,\"Content\":[],\"Locations\":[]},\"Choices\":[{\"Id\":\"choice\",\"Label\":\"\",\"Kind\":2}]}" },
        { "LlmAgentPermissionResult", "{\"type\":\"cancelled\"}" },
        { "LlmAgentPermissionResult", "{\"type\":\"selected\",\"ChoiceId\":\"choice\"}" },
        { "LlmAgentPermissionCancelled", "{}" },
        { "LlmAgentPermissionSelected", "{\"ChoiceId\":\"choice\"}" },
        { "LlmAgentToolPresentation", "{\"CallId\":\"call\",\"Name\":null,\"Content\":[{\"type\":\"diff\",\"Path\":\"/workspace/file\",\"OldText\":null,\"NewText\":\"raw\"}],\"Locations\":[{\"Path\":\"/workspace/file\",\"Line\":4294967295}]}" },
        { "LlmAgentToolLocation", "{\"Path\":\"/workspace/file\",\"Line\":0}" },
        { "LlmAgentToolPresentationContent", "{\"type\":\"content\",\"Content\":{\"type\":\"text\",\"Text\":\"raw\"}}" },
        { "LlmAgentToolPresentationContent", "{\"type\":\"diff\",\"Path\":\"/workspace/file\",\"OldText\":null,\"NewText\":\"\"}" },
        { "LlmAgentToolPresentationContent", "{\"type\":\"terminal\",\"TerminalId\":\"terminal\"}" },
        { "LlmAgentToolPresentedContent", "{\"Content\":{\"type\":\"text\",\"Text\":\"raw\"}}" },
        { "LlmAgentToolPresentedDiff", "{\"Path\":\"/workspace/file\",\"OldText\":\"old\",\"NewText\":\"new\"}" },
        { "LlmAgentToolPresentedTerminal", "{\"TerminalId\":\"terminal\"}" },
        { "LlmAgentContentAnnotations", "{\"Audience\":[],\"Priority\":0.75,\"LastModified\":\"2026-10-01T12:00:00+00:00\"}" },
        { "LlmAgentPresentedContentBlock", "{\"type\":\"text\",\"Text\":\"\"}" },
        { "LlmAgentPresentedContentBlock", "{\"type\":\"image\",\"Data\":\"AQID\",\"MediaType\":\"image/png\",\"Uri\":null}" },
        { "LlmAgentPresentedContentBlock", "{\"type\":\"audio\",\"Data\":\"AAEC\",\"MediaType\":\"audio/wav\"}" },
        { "LlmAgentPresentedContentBlock", "{\"type\":\"resource-link\",\"Uri\":\"opaque:informational\",\"Name\":\"\",\"Title\":null,\"Description\":null,\"MediaType\":null,\"SizeBytes\":null}" },
        { "LlmAgentPresentedContentBlock", "{\"type\":\"embedded-resource\",\"Resource\":{\"type\":\"binary\",\"Uri\":\"opaque:resource\",\"MediaType\":null,\"Data\":\"AAE=\"}}" },
        { "LlmAgentPresentedText", "{\"Text\":\"raw\"}" },
        { "LlmAgentPresentedImage", "{\"Data\":\"\",\"MediaType\":\"image/png\",\"Uri\":null}" },
        { "LlmAgentPresentedAudio", "{\"Data\":\"AQ==\",\"MediaType\":\"audio/wav\"}" },
        { "LlmAgentPresentedResourceLink", "{\"Uri\":\"opaque:resource\",\"Name\":\"name\",\"Title\":null,\"Description\":null,\"MediaType\":null,\"SizeBytes\":0}" },
        { "LlmAgentPresentedResourceBody", "{\"type\":\"text\",\"Uri\":\"opaque:resource\",\"MediaType\":null,\"Text\":\"raw\"}" },
        { "LlmAgentPresentedResourceBody", "{\"type\":\"binary\",\"Uri\":\"opaque:resource\",\"MediaType\":null,\"Data\":\"AAE=\"}" },
        { "LlmAgentPresentedResourceText", "{\"Uri\":\"opaque:resource\",\"MediaType\":null,\"Text\":\"\"}" },
        { "LlmAgentPresentedResourceBytes", "{\"Uri\":\"opaque:resource\",\"MediaType\":null,\"Data\":\"AQ==\"}" },
        { "LlmAgentPresentedEmbeddedResource", "{\"Resource\":{\"type\":\"text\",\"Uri\":\"opaque:resource\",\"MediaType\":null,\"Text\":\"raw\"}}" },
        { "LlmAgentTextFileReadRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Path\":\"/workspace/file\",\"StartLine\":1,\"MaxLines\":0}" },
        { "LlmAgentTextFileReadRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Path\":\"/workspace/file\",\"StartLine\":4294967295,\"MaxLines\":4294967295}" },
        { "LlmAgentTextFileWriteRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Path\":\"/workspace/file\",\"Text\":\"raw\"}" },
        { "LlmAgentTextFileWriteResult", "{\"RegisteredResources\":[{\"Reference\":{\"WorkspaceId\":\"workspace\",\"Id\":\"resource\"},\"SessionId\":\"logical-session\",\"FileName\":\"generated.json\",\"ContentType\":\"application/json\",\"Bytes\":3}]}" },
        { "LlmAgentTextFileWriteResult", "{\"RegisteredResources\":[]}" },
        { "LlmAgentEnvironmentVariable", "{\"Name\":\"SECRET\",\"Value\":\"never-log\"}" },
        { "LlmAgentTerminalCreateRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Command\":\"opaque-command\",\"Arguments\":[\"\",\"a\\nb\"],\"Environment\":[{\"Name\":\"SECRET\",\"Value\":\"never-log\"}],\"WorkingDirectory\":\"/workspace\",\"OutputByteLimit\":0}" },
        { "LlmAgentTerminalCreateRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Command\":\"opaque-command\",\"Arguments\":[],\"Environment\":[],\"WorkingDirectory\":null,\"OutputByteLimit\":18446744073709551615}" },
        { "LlmAgentTerminalExit", "{\"ExitCode\":null,\"Signal\":null}" },
        { "LlmAgentTerminalExit", "{\"ExitCode\":-2147483648,\"Signal\":\"actual-signal\"}" },
        { "LlmAgentTerminalOutput", "{\"Output\":\"\",\"Truncated\":false,\"Exit\":null}" },
        { "LlmAgentRuntimePolicy", "{\"type\":\"provider-configured\",\"Kind\":1}" },
        { "LlmAgentRuntimePolicy", "{\"type\":\"host-controlled\",\"Kind\":0,\"WritableRoots\":[],\"NetworkAllowed\":true,\"Sandbox\":1,\"ApprovalPolicy\":1,\"AllowRuntimeBackgroundWork\":false}" },
        { "LlmAgentHostRuntimePolicy", "{\"Kind\":0,\"WritableRoots\":[],\"NetworkAllowed\":true,\"Sandbox\":1,\"ApprovalPolicy\":1,\"AllowRuntimeBackgroundWork\":false}" },
        { "LlmAgentProviderRuntimePolicy", "{\"Kind\":1}" },
        { "LlmAgentRuntimePolicyKind", "1" },
    };
    public static TheoryData<string, string> Malformed => new()
    {
        { "LlmAgentShellRequest", "{\"Call\":{\"Id\":\"shell\",\"Commands\":[\"raw\\ncommand\"],\"TimeoutMs\":-1,\"MaxOutputLength\":0}}" },
        { "LlmAgentShellRequest", "{\"Scope\":null,\"Call\":{\"Id\":\"shell\",\"Commands\":[\"raw\\ncommand\"],\"TimeoutMs\":-1,\"MaxOutputLength\":0}}" },
        { "LlmAgentShellRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null}}" },
        { "LlmAgentShellRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Call\":null}" },
        { "LlmAgentCallbackScope", "{\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null}" },
        { "LlmAgentCallbackScope", "{\"ThreadId\":null,\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null}" },
        { "LlmAgentCallbackScope", "{\"ThreadId\":\"thread\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null}" },
        { "LlmAgentCallbackScope", "{\"ThreadId\":\"thread\",\"SessionId\":null,\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null}" },
        { "LlmAgentPermissionChoice", "{\"Label\":\"\",\"Kind\":0}" },
        { "LlmAgentPermissionChoice", "{\"Id\":null,\"Label\":\"\",\"Kind\":0}" },
        { "LlmAgentPermissionChoice", "{\"Id\":\"choice\",\"Kind\":0}" },
        { "LlmAgentPermissionChoice", "{\"Id\":\"choice\",\"Label\":null,\"Kind\":0}" },
        { "LlmAgentPermissionChoice", "{\"Id\":\"choice\",\"Label\":\"\"}" },
        { "LlmAgentPermissionChoice", "{\"Id\":\"choice\",\"Label\":\"\",\"Kind\":null}" },
        { "LlmAgentPermissionRequest", "{\"Tool\":{\"CallId\":\"call\",\"RawInput\":{\"nested\":{\"secret\":\"raw\"}},\"RawOutput\":null,\"Content\":[],\"Locations\":[]},\"Choices\":[{\"Id\":\"choice\",\"Label\":\"\",\"Kind\":2}]}" },
        { "LlmAgentPermissionRequest", "{\"Scope\":null,\"Tool\":{\"CallId\":\"call\",\"RawInput\":{\"nested\":{\"secret\":\"raw\"}},\"RawOutput\":null,\"Content\":[],\"Locations\":[]},\"Choices\":[{\"Id\":\"choice\",\"Label\":\"\",\"Kind\":2}]}" },
        { "LlmAgentPermissionRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Choices\":[{\"Id\":\"choice\",\"Label\":\"\",\"Kind\":2}]}" },
        { "LlmAgentPermissionRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Tool\":null,\"Choices\":[{\"Id\":\"choice\",\"Label\":\"\",\"Kind\":2}]}" },
        { "LlmAgentPermissionRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Tool\":{\"CallId\":\"call\",\"RawInput\":{\"nested\":{\"secret\":\"raw\"}},\"RawOutput\":null,\"Content\":[],\"Locations\":[]}}" },
        { "LlmAgentPermissionRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Tool\":{\"CallId\":\"call\",\"RawInput\":{\"nested\":{\"secret\":\"raw\"}},\"RawOutput\":null,\"Content\":[],\"Locations\":[]},\"Choices\":null}" },
        { "LlmAgentPermissionSelected", "{}" },
        { "LlmAgentPermissionSelected", "{\"ChoiceId\":null}" },
        { "LlmAgentToolPresentation", "{\"Name\":null,\"Content\":[{\"type\":\"diff\",\"Path\":\"/workspace/file\",\"OldText\":null,\"NewText\":\"raw\"}],\"Locations\":[{\"Path\":\"/workspace/file\",\"Line\":4294967295}]}" },
        { "LlmAgentToolPresentation", "{\"CallId\":null,\"Name\":null,\"Content\":[{\"type\":\"diff\",\"Path\":\"/workspace/file\",\"OldText\":null,\"NewText\":\"raw\"}],\"Locations\":[{\"Path\":\"/workspace/file\",\"Line\":4294967295}]}" },
        { "LlmAgentToolLocation", "{\"Line\":0}" },
        { "LlmAgentToolLocation", "{\"Path\":null,\"Line\":0}" },
        { "LlmAgentToolPresentedContent", "{}" },
        { "LlmAgentToolPresentedContent", "{\"Content\":null}" },
        { "LlmAgentToolPresentedDiff", "{\"OldText\":\"old\",\"NewText\":\"new\"}" },
        { "LlmAgentToolPresentedDiff", "{\"Path\":null,\"OldText\":\"old\",\"NewText\":\"new\"}" },
        { "LlmAgentToolPresentedDiff", "{\"Path\":\"/workspace/file\",\"OldText\":\"old\"}" },
        { "LlmAgentToolPresentedDiff", "{\"Path\":\"/workspace/file\",\"OldText\":\"old\",\"NewText\":null}" },
        { "LlmAgentToolPresentedTerminal", "{}" },
        { "LlmAgentToolPresentedTerminal", "{\"TerminalId\":null}" },
        { "LlmAgentPresentedText", "{}" },
        { "LlmAgentPresentedText", "{\"Text\":null}" },
        { "LlmAgentPresentedImage", "{\"MediaType\":\"image/png\",\"Uri\":null}" },
        { "LlmAgentPresentedImage", "{\"Data\":null,\"MediaType\":\"image/png\",\"Uri\":null}" },
        { "LlmAgentPresentedImage", "{\"Data\":\"\",\"Uri\":null}" },
        { "LlmAgentPresentedImage", "{\"Data\":\"\",\"MediaType\":null,\"Uri\":null}" },
        { "LlmAgentPresentedAudio", "{\"MediaType\":\"audio/wav\"}" },
        { "LlmAgentPresentedAudio", "{\"Data\":null,\"MediaType\":\"audio/wav\"}" },
        { "LlmAgentPresentedAudio", "{\"Data\":\"AQ==\"}" },
        { "LlmAgentPresentedAudio", "{\"Data\":\"AQ==\",\"MediaType\":null}" },
        { "LlmAgentPresentedResourceLink", "{\"Name\":\"name\",\"Title\":null,\"Description\":null,\"MediaType\":null,\"SizeBytes\":0}" },
        { "LlmAgentPresentedResourceLink", "{\"Uri\":null,\"Name\":\"name\",\"Title\":null,\"Description\":null,\"MediaType\":null,\"SizeBytes\":0}" },
        { "LlmAgentPresentedResourceLink", "{\"Uri\":\"opaque:resource\",\"Title\":null,\"Description\":null,\"MediaType\":null,\"SizeBytes\":0}" },
        { "LlmAgentPresentedResourceLink", "{\"Uri\":\"opaque:resource\",\"Name\":null,\"Title\":null,\"Description\":null,\"MediaType\":null,\"SizeBytes\":0}" },
        { "LlmAgentPresentedResourceText", "{\"MediaType\":null,\"Text\":\"\"}" },
        { "LlmAgentPresentedResourceText", "{\"Uri\":null,\"MediaType\":null,\"Text\":\"\"}" },
        { "LlmAgentPresentedResourceText", "{\"Uri\":\"opaque:resource\",\"MediaType\":null}" },
        { "LlmAgentPresentedResourceText", "{\"Uri\":\"opaque:resource\",\"MediaType\":null,\"Text\":null}" },
        { "LlmAgentPresentedResourceBytes", "{\"MediaType\":null,\"Data\":\"AQ==\"}" },
        { "LlmAgentPresentedResourceBytes", "{\"Uri\":null,\"MediaType\":null,\"Data\":\"AQ==\"}" },
        { "LlmAgentPresentedResourceBytes", "{\"Uri\":\"opaque:resource\",\"MediaType\":null}" },
        { "LlmAgentPresentedResourceBytes", "{\"Uri\":\"opaque:resource\",\"MediaType\":null,\"Data\":null}" },
        { "LlmAgentPresentedEmbeddedResource", "{}" },
        { "LlmAgentPresentedEmbeddedResource", "{\"Resource\":null}" },
        { "LlmAgentTextFileReadRequest", "{\"Path\":\"/workspace/file\",\"StartLine\":1,\"MaxLines\":0}" },
        { "LlmAgentTextFileReadRequest", "{\"Scope\":null,\"Path\":\"/workspace/file\",\"StartLine\":1,\"MaxLines\":0}" },
        { "LlmAgentTextFileReadRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"StartLine\":1,\"MaxLines\":0}" },
        { "LlmAgentTextFileReadRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Path\":null,\"StartLine\":1,\"MaxLines\":0}" },
        { "LlmAgentTextFileWriteRequest", "{\"Path\":\"/workspace/file\",\"Text\":\"raw\"}" },
        { "LlmAgentTextFileWriteRequest", "{\"Scope\":null,\"Path\":\"/workspace/file\",\"Text\":\"raw\"}" },
        { "LlmAgentTextFileWriteRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Text\":\"raw\"}" },
        { "LlmAgentTextFileWriteRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Path\":null,\"Text\":\"raw\"}" },
        { "LlmAgentTextFileWriteRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Path\":\"/workspace/file\"}" },
        { "LlmAgentTextFileWriteRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Path\":\"/workspace/file\",\"Text\":null}" },
        { "LlmAgentTextFileWriteResult", "{}" },
        { "LlmAgentTextFileWriteResult", "{\"RegisteredResources\":null}" },
        { "LlmAgentEnvironmentVariable", "{\"Value\":\"never-log\"}" },
        { "LlmAgentEnvironmentVariable", "{\"Name\":null,\"Value\":\"never-log\"}" },
        { "LlmAgentEnvironmentVariable", "{\"Name\":\"SECRET\"}" },
        { "LlmAgentEnvironmentVariable", "{\"Name\":\"SECRET\",\"Value\":null}" },
        { "LlmAgentTerminalCreateRequest", "{\"Command\":\"opaque-command\",\"Arguments\":[\"\",\"a\\nb\"],\"Environment\":[{\"Name\":\"SECRET\",\"Value\":\"never-log\"}],\"WorkingDirectory\":\"/workspace\",\"OutputByteLimit\":0}" },
        { "LlmAgentTerminalCreateRequest", "{\"Scope\":null,\"Command\":\"opaque-command\",\"Arguments\":[\"\",\"a\\nb\"],\"Environment\":[{\"Name\":\"SECRET\",\"Value\":\"never-log\"}],\"WorkingDirectory\":\"/workspace\",\"OutputByteLimit\":0}" },
        { "LlmAgentTerminalCreateRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Arguments\":[\"\",\"a\\nb\"],\"Environment\":[{\"Name\":\"SECRET\",\"Value\":\"never-log\"}],\"WorkingDirectory\":\"/workspace\",\"OutputByteLimit\":0}" },
        { "LlmAgentTerminalCreateRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Command\":null,\"Arguments\":[\"\",\"a\\nb\"],\"Environment\":[{\"Name\":\"SECRET\",\"Value\":\"never-log\"}],\"WorkingDirectory\":\"/workspace\",\"OutputByteLimit\":0}" },
        { "LlmAgentTerminalCreateRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Command\":\"opaque-command\",\"Environment\":[{\"Name\":\"SECRET\",\"Value\":\"never-log\"}],\"WorkingDirectory\":\"/workspace\",\"OutputByteLimit\":0}" },
        { "LlmAgentTerminalCreateRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Command\":\"opaque-command\",\"Arguments\":null,\"Environment\":[{\"Name\":\"SECRET\",\"Value\":\"never-log\"}],\"WorkingDirectory\":\"/workspace\",\"OutputByteLimit\":0}" },
        { "LlmAgentTerminalCreateRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Command\":\"opaque-command\",\"Arguments\":[\"\",\"a\\nb\"],\"WorkingDirectory\":\"/workspace\",\"OutputByteLimit\":0}" },
        { "LlmAgentTerminalCreateRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Command\":\"opaque-command\",\"Arguments\":[\"\",\"a\\nb\"],\"Environment\":null,\"WorkingDirectory\":\"/workspace\",\"OutputByteLimit\":0}" },
        { "LlmAgentTerminalOutput", "{\"Truncated\":false,\"Exit\":null}" },
        { "LlmAgentTerminalOutput", "{\"Output\":null,\"Truncated\":false,\"Exit\":null}" },
        { "LlmAgentTerminalOutput", "{\"Output\":\"\",\"Exit\":null}" },
        { "LlmAgentTerminalOutput", "{\"Output\":\"\",\"Truncated\":null,\"Exit\":null}" },
        { "LlmAgentPermissionResult", "{\"type\":\"unknown\"}" },
        { "LlmAgentToolPresentationContent", "{\"type\":\"unknown\"}" },
        { "LlmAgentPresentedContentBlock", "{\"type\":\"unknown\"}" },
        { "LlmAgentPresentedResourceBody", "{\"type\":\"unknown\"}" },
        { "LlmAgentRuntimePolicy", "{\"type\":\"unknown\"}" },
        { "LlmAgentTextFileReadRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Path\":\"/x\",\"StartLine\":0,\"MaxLines\":0}" },
        { "LlmAgentTextFileWriteRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Path\":\"relative\",\"Text\":\"raw\"}" },
        { "LlmAgentPermissionChoice", "{\"Id\":\"id\",\"Label\":\"label\",\"Kind\":99}" },
        { "LlmAgentTerminalCreateRequest", "{\"Scope\":{\"ThreadId\":\"thread\",\"SessionId\":\"native-session\",\"TurnId\":null,\"WorkingDirectory\":null,\"RuntimePolicy\":null},\"Command\":\"x\",\"Arguments\":[null],\"Environment\":[],\"WorkingDirectory\":null,\"OutputByteLimit\":null}" },
        { "LlmAgentTextFileWriteResult", "{\"RegisteredResources\":[null]}" },
    };

    [Theory, MemberData(nameof(Shapes))]
    public void ExactGettersPreserveEverySuppliedFact(string name, string json) => RoundTrip(json, Metadata(typeof(ConnectLlmJson), name));

    [Theory, MemberData(nameof(Malformed))]
    public void MissingNullOrMalformedFactsRefuse(string name, string json)
        => Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(json, Metadata(typeof(ConnectLlmJson), name))));

    [Fact]
    public void PositionalWithAndMutableInputsAreCopiedWithoutEffects()
    {
        List<string> roots = ["/workspace"];
        var scope = new LlmAgentCallbackScope(ThreadId: "thread", SessionId: "session", TurnId: null,
            WorkingDirectory: null, RuntimePolicy: new LlmAgentHostRuntimePolicy { WritableRoots = roots });
        roots.Clear();
        Assert.Equal("/workspace", Assert.Single(Assert.IsType<LlmAgentHostRuntimePolicy>(scope.RuntimePolicy).WritableRoots));
        byte[] bytes = [1, 2, 3];
        var image = new LlmAgentPresentedImage(Data: bytes, MediaType: "image/png", Uri: null);
        bytes[0] = 99; Assert.Equal(1, image.Data.Span[0]);
        var replacement = new byte[] { 4 };
        var changed = image with { Data = replacement }; replacement[0] = 99; Assert.Equal(4, changed.Data.Span[0]);
        List<LlmAgentToolPresentationContent> content = [new LlmAgentToolPresentedTerminal("terminal")];
        using JsonDocument document = JsonDocument.Parse("{\"secret\":\"raw\"}");
        var presentation = new LlmAgentToolPresentation { CallId = "call", RawInput = document.RootElement, Content = content };
        document.Dispose(); content.Clear();
        Assert.Single(presentation.Content); Assert.Equal("raw", presentation.RawInput!.Value.GetProperty("secret").GetString());
        var choices = new List<LlmAgentPermissionChoice> { new("choice", "label", LlmAgentPermissionChoiceKind.AllowOnce) };
        var request = new LlmAgentPermissionRequest(Scope: scope, Tool: presentation, Choices: choices); choices.Clear();
        Assert.Single(request.Choices);
        request.Deconstruct(out var copiedScope, out var copiedTool, out var copiedChoices);
        Assert.Same(scope, copiedScope); Assert.Same(presentation, copiedTool); Assert.Same(request.Choices, copiedChoices);
        Assert.Throws<ArgumentNullException>(() => request with { Choices = null! });
        Assert.Throws<ArgumentNullException>(() => image with { MediaType = null! });
        Assert.Throws<ArgumentException>(() => scope with { SessionId = "" });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmAgentTextFileReadRequest(scope, "/file", 1, 0) with { StartLine = 0 });
        Assert.Throws<ArgumentException>(() => request with { Choices = [new("a", "", 0), new("a", "", 0)] });
        Assert.Equal("LlmAgentCallerServices { <borrowed> }", new LlmAgentCallerServices().ToString());
        Assert.DoesNotContain("secret", presentation.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/workspace", scope.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("never-log", new LlmAgentEnvironmentVariable("KEY", "never-log").ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LlmAgentCallerServiceKind")]
    [InlineData("LlmAgentPermissionChoiceKind")]
    public void NewEnumRootsRefuseUndefinedValuesBothWays(string name)
    {
        JsonTypeInfo metadata = Metadata(typeof(ConnectLlmJson), name);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("99", metadata));
        object invalid = Enum.ToObject(metadata.Type, 99);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(invalid, metadata));
    }

    [Fact]
    public void RuntimeAuthoritiesHaveNoExportAndRefuseWithoutCallbacksOrDiagnostics()
    {
        var authority = new ThrowingAuthority();
        var services = new LlmAgentCallerServices
        {
            Permissions = authority,
            TextFileReader = authority,
            TextFileWriter = authority,
            Terminals = authority,
            ShellExecutor = authority
        };
        JsonSerializerOptions options = ConnectLlmJson.LlmAgentCallbackScope.Options;
        foreach (Type type in new[] { typeof(ILlmAgentPermissionSelector), typeof(ILlmAgentTextFileReader), typeof(ILlmAgentTextFileWriter),
                     typeof(ILlmAgentTerminalFactory), typeof(ILlmAgentTerminal), typeof(ILlmAgentShellExecutor), typeof(LlmAgentCallerServices) })
        {
            Assert.Null(typeof(ConnectLlmJson).GetProperty(type.Name));
            JsonTypeInfo metadata = options.GetTypeInfo(type);
            Assert.Equal("null", JsonSerializer.Serialize((object?)null, metadata));
            Assert.Null(JsonSerializer.Deserialize("null", metadata));
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{}", metadata));
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(type == typeof(LlmAgentCallerServices) ? services : authority, metadata));
        }
        Assert.Equal(0, authority.Effects);
        Assert.Null(options.TypeInfoResolver!.GetTypeInfo(typeof(LlmAgentSessionOptions), options));
        Assert.Null(options.TypeInfoResolver.GetTypeInfo(typeof(LlmAgentResumeOptions), options));
    }

    [Theory, MemberData(nameof(Shapes))]
    public void ActualForeignOwnedGraphRetires(string name, string json)
    {
        WeakReference weak = Foreign(name, json);
        for (int attempt = 0; weak.IsAlive && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Foreign(string name, string json)
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
            Type facade = connect.GetType(typeof(ConnectLlmJson).FullName!, true)!;
            JsonTypeInfo metadata = Metadata(facade, name);
            Assert.Same(connect, metadata.Type.Assembly);
            object value = RoundTrip(json, metadata);
            Assert.Same(connect, value.GetType().Assembly);
            var written = RoundTrip("{\"RegisteredResources\":[{\"Reference\":{\"WorkspaceId\":\"workspace\",\"Id\":\"resource\"},\"SessionId\":null,\"FileName\":null,\"ContentType\":null,\"Bytes\":null}]}",
                Metadata(facade, "LlmAgentTextFileWriteResult"));
            object list = written.GetType().GetProperty("RegisteredResources")!.GetValue(written)!;
            object descriptor = ((System.Collections.IEnumerable)list).GetEnumerator().MoveNext() ? ((System.Collections.IList)list)[0]! : throw new InvalidOperationException();
            Assert.Same(common, descriptor.GetType().Assembly);
            RoundTrip("{\"WorkspaceId\":\"workspace\",\"Id\":\"resource\"}",
                Metadata(common.GetType(typeof(InferenceJson).FullName!, true)!, "InferenceResourceReference"));
            Type services = connect.GetType(typeof(LlmAgentCallerServices).FullName!, true)!;
            JsonTypeInfo refusal = metadata.Options.GetTypeInfo(services);
            Assert.Same(connect, refusal.Converter.GetType().Assembly);
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(Activator.CreateInstance(services), refusal));
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static JsonTypeInfo Metadata(Type owner, string name) => Assert.IsAssignableFrom<JsonTypeInfo>(owner.GetProperty(name)!.GetValue(null));
    private static object RoundTrip(string json, JsonTypeInfo metadata)
    {
        object value = JsonSerializer.Deserialize(json, metadata)!;
        string written = JsonSerializer.Serialize(value, metadata);
        using JsonDocument expected = JsonDocument.Parse(json);
        using JsonDocument actual = JsonDocument.Parse(written);
        Preserve(expected.RootElement, actual.RootElement);
        Assert.Equal(written, JsonSerializer.Serialize(JsonSerializer.Deserialize(written, metadata), metadata));
        return value;
    }
    private static void Preserve(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in expected.EnumerateObject())
            { Assert.True(actual.TryGetProperty(property.Name, out JsonElement stored)); Preserve(property.Value, stored); }
        else if (expected.ValueKind == JsonValueKind.Array)
        {
            Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength());
            for (int index = 0; index < expected.GetArrayLength(); index++) Preserve(expected[index], actual[index]);
        }
        else Assert.True(JsonElement.DeepEquals(expected, actual), expected.GetRawText());
    }

    private sealed class ThrowingAuthority : ILlmAgentPermissionSelector, ILlmAgentTextFileReader, ILlmAgentTextFileWriter,
        ILlmAgentTerminalFactory, ILlmAgentTerminal, ILlmAgentShellExecutor
    {
        public int Effects { get; private set; }
        private InvalidOperationException Effect() { Effects++; return new InvalidOperationException("Authority must not be invoked."); }
        public override string ToString() => throw Effect();
        public Task<LlmAgentPermissionResult> RequestAsync(LlmAgentPermissionRequest request, CancellationToken cancellationToken = default) => throw Effect();
        public Task<string> ReadAsync(LlmAgentTextFileReadRequest request, CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmAgentTextFileWriteResult> WriteAsync(LlmAgentTextFileWriteRequest request, CancellationToken cancellationToken = default) => throw Effect();
        public Task<ILlmAgentTerminal> CreateAsync(LlmAgentTerminalCreateRequest request, CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmAgentTerminalOutput> GetOutputAsync(CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmAgentTerminalExit> WaitForExitAsync(CancellationToken cancellationToken = default) => throw Effect();
        public Task KillAsync(CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmShellCallResult> ExecuteAsync(LlmAgentShellRequest request, CancellationToken cancellationToken = default) => throw Effect();
        public ValueTask DisposeAsync() => throw Effect();
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
