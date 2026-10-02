using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class RuntimeAuthenticationContractTests
{
    public static TheoryData<string> DataRoots => new()
    {
        "LlmAgentRuntimeProfile", "LlmAgentRuntimeStateConfiguration", "LlmAgentIsolatedRuntimeState", "LlmAgentAdoptedRuntimeState",
        "LlmAcpConfiguration", "LlmAcpToolRelayConfiguration", "LlmCopilotEndpointMode", "LlmCopilotConfiguration",
        "LlmAcpAuthenticationConfiguration", "LlmAcpExternalAuthentication", "LlmAcpAgentAuthentication", "LlmAcpTerminalAuthentication",
        "LlmAcpAuthenticationFlow", "LlmAcpAuthenticationMethod", "LlmCopilotAuthenticationConfiguration",
        "LlmCopilotExplicitGitHubToken", "LlmCopilotRuntimeAccount", "LlmAgentRuntimeLoginScope",
        "LlmAgentRuntimeAuthenticationSnapshot", "LlmAgentRuntimeLoginOptions", "LlmAgentStagedLoginState",
        "LlmAgentRuntimeLoginResult", "LlmAgentStagedStateDisposition", "LlmAgentStagedStateSnapshot", "LlmAgentStagedStateCommitResult",
        "LlmCopilotCredentials", "LlmCopilotRuntimeConnectionCredential", "LlmAgentCapabilities"
    };

    [Theory, MemberData(nameof(DataRoots))]
    public void EveryExactDataRootHasItsOwnedNamedMetadata(string name)
    {
        Type? type = typeof(ConnectLlmJson).Assembly.GetType("Darkspyre.Inference.Connect.LLM." + name);
        Assert.NotNull(type);
        var getter = typeof(ConnectLlmJson).GetProperty(name);
        Assert.NotNull(getter);
        JsonTypeInfo metadata = Assert.IsAssignableFrom<JsonTypeInfo>(getter.GetValue(null));
        Assert.Equal(type, metadata.Type);
        Assert.Same(typeof(ConnectLlmJson).Assembly, metadata.Type.Assembly);
    }

    [Fact]
    public void NewTransportIdentitiesAppendWithoutReclaimingFive()
    {
        Assert.True(Enum.TryParse("GitHubCopilot", out LlmTransport copilot));
        Assert.True(Enum.TryParse("AgentClientProtocol", out LlmTransport acp));
        Assert.Equal(16, (int)copilot); Assert.Equal(17, (int)acp);
        Assert.False(Enum.IsDefined((LlmTransport)5)); Assert.Equal(15, (int)LlmTransport.AzureOpenAiChat);
    }

    private const string Profile = """{"Id":"profile","DeclaredRuntimeVersion":"candidate","DeclaredAdapterVersion":"adapter","Arguments":["literal argument"],"EnvironmentAllowlist":["EXPLICIT"]}""";
    private const string Identity = """{"Intent":1,"Id":"connection","ExpectedVersion":"version"}""";
    private const string Receipt = """{"AttemptId":"11111111-1111-1111-1111-111111111111","Identity":""" + Identity + ""","ProfileId":"profile","RootDirectory":"/selected/root","Directory":"/selected/root/staging"}""";
    private const string Observation = """{"Transport":17,"ProfileId":"profile","Authenticated":null,"AccountId":null,"AcpMethods":[{"Id":"method","Name":"Selected method","Description":null,"Flow":1}],"UnavailableReason":null}""";

    public static TheoryData<string, string> Shapes => new()
    {
        { "LlmAgentRuntimeProfile", Profile },
        { "LlmAgentRuntimeStateConfiguration", """{"type":"isolated","RootDirectory":"/selected/root","ActivatedDirectory":null}""" },
        { "LlmAgentIsolatedRuntimeState", """{"RootDirectory":"/selected/root","ActivatedDirectory":"/selected/root/active"}""" },
        { "LlmAgentAdoptedRuntimeState", """{"Directory":"/selected/adopted"}""" },
        { "LlmAcpConfiguration", """{"Profile":""" + Profile + ""","State":{"type":"adopted","Directory":"/selected/adopted"},"Authentication":{"type":"terminal","MethodId":"method"},"ToolRelay":{"ManagedHostExecutable":"/selected/dotnet"},"ProtocolVersion":1}""" },
        { "LlmAcpToolRelayConfiguration", """{"ManagedHostExecutable":"/selected/dotnet"}""" },
        { "LlmCopilotEndpointMode", "0" },
        { "LlmCopilotConfiguration", """{"Profile":""" + Profile + ""","EndpointMode":0,"State":{"type":"isolated","RootDirectory":"/selected/root","ActivatedDirectory":null},"Authentication":{"type":"runtime-account"},"RequiredSdkVersion":"candidate"}""" },
        { "LlmAcpAuthenticationConfiguration", """{"type":"agent","MethodId":"method"}""" },
        { "LlmAcpExternalAuthentication", "{}" },
        { "LlmAcpAgentAuthentication", """{"MethodId":"method"}""" },
        { "LlmAcpTerminalAuthentication", """{"MethodId":"method"}""" },
        { "LlmAcpAuthenticationFlow", "1" },
        { "LlmAcpAuthenticationMethod", """{"Id":"method","Name":"Selected method","Description":null,"Flow":1}""" },
        { "LlmCopilotAuthenticationConfiguration", """{"type":"explicit-github-token"}""" },
        { "LlmCopilotExplicitGitHubToken", "{}" },
        { "LlmCopilotRuntimeAccount", "{}" },
        { "LlmAgentRuntimeLoginScope", """{"Identity":""" + Identity + ""","Transport":16,"ProfileId":"profile","WorkingDirectory":"/selected/work"}""" },
        { "LlmAgentRuntimeAuthenticationSnapshot", Observation },
        { "LlmAgentRuntimeLoginOptions", """{"WorkingDirectory":"/selected/work","Services":null}""" },
        { "LlmAgentStagedLoginState", Receipt },
        { "LlmAgentRuntimeLoginResult", """{"Observation":""" + Observation + ""","RequiresConfigurationCommit":true,"StagedState":""" + Receipt + "}" },
        { "LlmAgentStagedStateDisposition", "2" },
        { "LlmAgentStagedStateSnapshot", """{"AttemptId":"11111111-1111-1111-1111-111111111111","Disposition":2}""" },
        { "LlmAgentStagedStateCommitResult", """{"Commit":{"State":5,"Version":null,"Issues":null},"State":{"AttemptId":"11111111-1111-1111-1111-111111111111","Disposition":2}}""" },
        { "LlmCopilotCredentials", """{"accessToken":"  exact-protected-token  ","expiresAt":"2030-01-01T00:00:00+00:00"}""" },
        { "LlmCopilotRuntimeConnectionCredential", """{"connectionToken":"  separate-runtime-token  "}""" },
        { "LlmAgentCapabilities", """{"HostedTools":[0],"SupportsShellTools":false,"SupportedCallerServices":[],"ContextOwnership":1,"ContextImport":1,"SupportsContextCapture":true,"SupportsContextReplacement":true,"SupportsContextStatus":true,"SupportsCompactionHandoff":true,"SupportsNativeResume":false,"NativeFork":0,"CompactionOwnership":1,"Steering":1,"SupportsInterrupt":true,"ResourceObservation":0,"ToolExecutionOwnership":1,"RichContentSupported":{"Images":1,"ImageMediaTypes":["image/png"],"MaxBinaryBytes":null},"SupportedRuntimePolicies":[]}""" }
    };

    [Theory, MemberData(nameof(Shapes))]
    public void EveryDataRootRoundTripsActualPopulatedFacts(string name, string json)
        => RoundTrip(json, Metadata(typeof(ConnectLlmJson), name));

    [Theory]
    [InlineData("LlmAgentRuntimeProfile", "{}")]
    [InlineData("LlmAgentIsolatedRuntimeState", "{}")]
    [InlineData("LlmAgentAdoptedRuntimeState", "{}")]
    [InlineData("LlmAcpConfiguration", "{}")]
    [InlineData("LlmAcpAgentAuthentication", "{}")]
    [InlineData("LlmAcpTerminalAuthentication", "{}")]
    [InlineData("LlmAcpAuthenticationMethod", "{\"Id\":\"method\",\"Name\":\"name\"}")]
    [InlineData("LlmAgentRuntimeLoginScope", "{}")]
    [InlineData("LlmAgentStagedLoginState", "{}")]
    [InlineData("LlmAgentRuntimeLoginResult", "{\"Observation\":{\"Transport\":17,\"ProfileId\":\"profile\"}}")]
    [InlineData("LlmAgentStagedStateSnapshot", "{\"AttemptId\":\"11111111-1111-1111-1111-111111111111\"}")]
    [InlineData("LlmAgentStagedStateCommitResult", "{}")]
    [InlineData("LlmCopilotCredentials", "{}")]
    [InlineData("LlmCopilotRuntimeConnectionCredential", "{}")]
    public void MissingRequiredFactsRefuse(string name, string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, Metadata(typeof(ConnectLlmJson), name)));

    [Theory]
    [InlineData("LlmAgentRuntimeStateConfiguration")]
    [InlineData("LlmAcpAuthenticationConfiguration")]
    [InlineData("LlmCopilotAuthenticationConfiguration")]
    public void ClosedVariantsRefuseUnknownDiscriminator(string name)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"type\":\"unknown\"}", Metadata(typeof(ConnectLlmJson), name)));

    [Theory]
    [InlineData("LlmCopilotEndpointMode")]
    [InlineData("LlmAcpAuthenticationFlow")]
    [InlineData("LlmAgentStagedStateDisposition")]
    public void ClosedEnumRootsRefuseUnknownValuesBothWays(string name)
    {
        JsonTypeInfo metadata = Metadata(typeof(ConnectLlmJson), name);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("99", metadata));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(Enum.ToObject(metadata.Type, 99), metadata));
    }

    [Theory]
    [InlineData("LlmAgentRuntimeProfile", "{\"Id\":\"profile\",\"DeclaredRuntimeVersion\":\"candidate\",\"ExecutablePath\":\"/other\"}")]
    [InlineData("LlmAcpAuthenticationConfiguration", "{\"type\":\"external\",\"token\":\"sentinel\"}")]
    [InlineData("LlmCopilotAuthenticationConfiguration", "{\"type\":\"runtime-account\",\"token\":\"sentinel\"}")]
    [InlineData("LlmAcpAuthenticationMethod", "{\"Id\":\"method\",\"Name\":\"name\",\"Flow\":0,\"Arguments\":[\"secret\"]}")]
    public void ClosedDataDoesNotSilentlyDropUnknownInputs(string name, string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, Metadata(typeof(ConnectLlmJson), name)));

    [Theory]
    [InlineData("LlmAgentRuntimeProfile", "{\"Id\":null,\"DeclaredRuntimeVersion\":\"candidate\"}")]
    [InlineData("LlmAcpAgentAuthentication", "{\"MethodId\":null}")]
    [InlineData("LlmAgentRuntimeLoginOptions", "{\"WorkingDirectory\":null}")]
    [InlineData("LlmAgentStagedStateCommitResult", "{\"Commit\":null,\"State\":{\"AttemptId\":\"11111111-1111-1111-1111-111111111111\",\"Disposition\":0}}")]
    public void ExplicitNullRequiredFactsRefuse(string name, string json)
        => Assert.ThrowsAny<ArgumentException>(() => JsonSerializer.Deserialize(json, Metadata(typeof(ConnectLlmJson), name)));

    [Fact]
    public void ConnectionOwnsTypedRuntimeConfigurationAndNoLaunchAuthority()
    {
        var acp = JsonSerializer.Deserialize("""{"Profile":""" + Profile + ""","State":{"type":"adopted","Directory":"/selected"},"Authentication":{"type":"external"}}""", ConnectLlmJson.LlmAcpConfiguration)!;
        var connection = new LlmConnection { Transport = LlmTransport.AgentClientProtocol, Version = "version", Endpoint = "/selected/executable", Acp = acp };
        string json = JsonSerializer.Serialize(connection, ConnectLlmJson.Connection);
        LlmConnection copied = JsonSerializer.Deserialize(json, ConnectLlmJson.Connection)!;
        Assert.Equal("profile", copied.Acp!.Profile.Id); Assert.IsType<LlmAcpExternalAuthentication>(copied.Acp.Authentication);
        Assert.Null(copied.RuntimeLaunchInputs);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize(json.Replace("\"RuntimeLaunchInputs\":null", "\"RuntimeLaunchInputs\":{}", StringComparison.Ordinal), ConnectLlmJson.Connection));
    }

    [Fact]
    public void ConfigurationCopiesInputsAndWithCannotEraseRequiredFacts()
    {
        string[] arguments = ["literal argument"];
        var names = new HashSet<string>(StringComparer.Ordinal) { "EXPLICIT" };
        var profile = new LlmAgentRuntimeProfile { Id = "profile", DeclaredRuntimeVersion = "candidate", Arguments = arguments, EnvironmentAllowlist = names };
        arguments[0] = "changed"; names.Clear();
        Assert.Equal("literal argument", Assert.Single(profile.Arguments)); Assert.Contains("EXPLICIT", profile.EnvironmentAllowlist);
        var environment = new[] { new LlmAgentEnvironmentVariable("EXPLICIT", "sentinel-secret") };
        var launch = new LlmAgentRuntimeLaunchInputs { Environment = environment };
        environment[0] = new("CHANGED", "changed");
        Assert.Equal("sentinel-secret", Assert.Single(launch.Environment).Value);
        Assert.Throws<ArgumentNullException>(() => profile with { Arguments = null! });
        Assert.Throws<ArgumentNullException>(() => profile with { EnvironmentAllowlist = null! });
        Assert.Throws<ArgumentException>(() => profile with { Id = "" });
        Assert.Throws<ArgumentException>(() => new LlmAgentAdoptedRuntimeState("relative"));
        Assert.Throws<ArgumentException>(() => new LlmAgentRuntimeLaunchInputs { Environment = [new("EXPLICIT", "a"), new("EXPLICIT", "b")] });
        Assert.Throws<ArgumentException>(() => profile with { EnvironmentAllowlist = new HashSet<string> { "INVALID=NAME" } });
        Assert.Throws<ArgumentNullException>(() => new LlmAcpConfiguration { Profile = profile, State = new LlmAgentAdoptedRuntimeState("/selected"), Authentication = new LlmAcpExternalAuthentication() } with { State = null! });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmAgentStagedStateSnapshot(Guid.NewGuid(), (LlmAgentStagedStateDisposition)99));
        Assert.DoesNotContain("sentinel-secret", launch.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("literal argument", profile.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProtectedCredentialsPreserveWireAndInvalidParsingPolicy()
    {
        var credential = new LlmCredential
        {
            Copilot = new() { AccessToken = "  protected-secret  " },
            CopilotRuntime = new() { ConnectionToken = "  separate-secret  " }
        };
        string json = credential.Serialize();
        Assert.Contains("\"copilot\":{\"accessToken\":\"  protected-secret  \"}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("expiresAt", json, StringComparison.Ordinal);
        Assert.Equal(credential.Copilot, LlmCredential.Parse(json)!.Copilot);
        Assert.Equal(credential.CopilotRuntime, LlmCredential.Parse(json)!.CopilotRuntime);
        Assert.Null(LlmCredential.Parse("{\"copilot\":{}}"));
        Assert.Null(LlmCredential.Parse("{\"copilot\":{\"accessToken\":null}}"));
        Assert.Null(LlmCredential.Parse("{\"copilotRuntime\":{\"connectionToken\":null}}"));
        Assert.Equal("valid", LlmCredential.Parse("{\"copilot\":{\"accessToken\":\"valid\",\"futureField\":true}}")!.Copilot!.AccessToken);
        Assert.DoesNotContain("protected-secret", credential.Copilot.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("separate-secret", credential.CopilotRuntime.ToString(), StringComparison.Ordinal);
        Assert.Same(ConnectLlmJson.Credential.Options, ConnectLlmJson.LlmCopilotCredentials.Options);
        Assert.Same(ConnectLlmJson.Credential.Options, ConnectLlmJson.LlmCopilotRuntimeConnectionCredential.Options);
    }

    [Fact]
    public void LiveRuntimeInputsRefuseWithoutBorrowedCallbacksAndNullRemainsData()
    {
        var authority = new ThrowingLoginAuthority();
        var services = new LlmAgentRuntimeLoginServices { DevicePrompt = authority, Terminals = authority };
        var scope = new LlmAgentRuntimeLoginScope(new(SetupChangeIntent.Edit, "connection", "version"), LlmTransport.AgentClientProtocol, "profile", "/selected");
        object[] values = [new LlmAgentRuntimeLaunchInputs(), services, authority, authority,
            new LlmAgentRuntimeLoginTerminalRequest(scope, "/selected/executable", [], []), authority];
        Type[] types = [typeof(LlmAgentRuntimeLaunchInputs), typeof(LlmAgentRuntimeLoginServices),
            typeof(ILlmAgentRuntimeLoginTerminalFactory), typeof(ILlmAgentRuntimeAuthentication),
            typeof(LlmAgentRuntimeLoginTerminalRequest), typeof(ILlmAuthPrompt)];
        for (int index = 0; index < types.Length; index++)
        {
            Assert.Null(typeof(ConnectLlmJson).GetProperty(types[index].Name));
            JsonTypeInfo metadata = ConnectLlmJson.Connection.Options.GetTypeInfo(types[index]);
            Assert.Equal("null", JsonSerializer.Serialize((object?)null, metadata)); Assert.Null(JsonSerializer.Deserialize("null", metadata));
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(values[index], metadata));
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{}", metadata));
        }
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new LlmAgentRuntimeLoginOptions { WorkingDirectory = "/selected", Services = services }, ConnectLlmJson.LlmAgentRuntimeLoginOptions));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"WorkingDirectory\":\"/selected\",\"Services\":{}}", ConnectLlmJson.LlmAgentRuntimeLoginOptions));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new LlmConnection { Version = "version", Endpoint = "/selected/executable", Transport = LlmTransport.AgentClientProtocol, RuntimeLaunchInputs = new() }, ConnectLlmJson.Connection));
        Assert.Equal(0, authority.Effects);
    }

    [Fact]
    public void ExternalGeneratedEnvelopeComposesShippingRuntimeRefusal()
    {
        var options = new JsonSerializerOptions(ConnectLlmJson.Connection.Options)
        { TypeInfoResolver = JsonTypeInfoResolver.Combine(ConnectLlmJson.Connection.Options.TypeInfoResolver, RuntimeLoginConsumerContext.Default) };
        JsonTypeInfo metadata = options.GetTypeInfo<RuntimeLoginConsumerEnvelope>();
        string json = JsonSerializer.Serialize(new RuntimeLoginConsumerEnvelope(new() { WorkingDirectory = "/selected" }, null), metadata);
        Assert.NotNull(JsonSerializer.Deserialize(json, metadata));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new RuntimeLoginConsumerEnvelope(
            new() { WorkingDirectory = "/selected", Services = new() }, new()), metadata));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"Options\":{\"WorkingDirectory\":\"/selected\",\"Services\":{}},\"Launch\":null}", metadata));
    }

    [Theory, MemberData(nameof(Shapes))]
    public void ActualForeignDataAndOwnedConvertersRetire(string name, string json)
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
                Assert.NotSame(original, actual); Assert.Equal(original.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
            }
            Type facade = connect.GetType(typeof(ConnectLlmJson).FullName!, true)!;
            JsonTypeInfo metadata = Metadata(facade, name);
            Assert.Same(connect, metadata.Type.Assembly);
            object value = RoundTrip(json, metadata); Assert.Same(connect, value.GetType().Assembly);
            object scope = RoundTrip("""{"Identity":""" + Identity + ""","Transport":17,"ProfileId":"profile","WorkingDirectory":"/selected"}""", Metadata(facade, "LlmAgentRuntimeLoginScope"));
            Assert.Same(common, scope.GetType().GetProperty("Identity")!.GetValue(scope)!.GetType().Assembly);
            Type services = connect.GetType(typeof(LlmAgentRuntimeLoginServices).FullName!, true)!;
            JsonTypeInfo refusal = Metadata(facade, "LlmAgentRuntimeLoginOptions").Options.GetTypeInfo(services);
            Assert.Same(connect, refusal.Converter.GetType().Assembly);
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(Activator.CreateInstance(services), refusal));
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static JsonTypeInfo Metadata(Type facade, string name) => Assert.IsAssignableFrom<JsonTypeInfo>(facade.GetProperty(name)!.GetValue(null));
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

    private sealed class ThrowingLoginAuthority : ILlmAgentRuntimeLoginTerminalFactory, ILlmAuthPrompt, ILlmAgentRuntimeAuthentication
    {
        public int Effects { get; private set; }
        private InvalidOperationException Effect() { Effects++; return new InvalidOperationException("Must not invoke live authority."); }
        public override string ToString() => throw Effect();
        public Task<ILlmAgentTerminal> CreateAsync(LlmAgentRuntimeLoginTerminalRequest request, CancellationToken cancellationToken = default) => throw Effect();
        public Task ShowDeviceCodeAsync(LlmDeviceCode code, CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmAgentRuntimeAuthenticationSnapshot> InspectAsync(CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmAgentRuntimeLoginResult> SignInAsync(LlmAgentRuntimeLoginOptions options, CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmAgentStagedStateCommitResult> CommitStagedStateAsync(LlmAgentStagedLoginState receipt, LlmSetupChanges changes, ISetupCommitTarget<LlmSetupChanges> target, CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmAgentStagedStateSnapshot> ReconcileStagedStateAsync(LlmAgentStagedLoginState receipt, CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmAgentStagedStateSnapshot> AbandonStagedStateAsync(LlmAgentStagedLoginState receipt, CancellationToken cancellationToken = default) => throw Effect();
    }

    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}

internal sealed record RuntimeLoginConsumerEnvelope(LlmAgentRuntimeLoginOptions Options, LlmAgentRuntimeLaunchInputs? Launch);
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RuntimeLoginConsumerEnvelope))]
internal sealed partial class RuntimeLoginConsumerContext : JsonSerializerContext;
