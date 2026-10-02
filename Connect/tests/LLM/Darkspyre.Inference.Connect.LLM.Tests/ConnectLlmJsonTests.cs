using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ConnectLlmJsonTests
{
    private const string CredentialJson = """
        {"apiKey":"key","oAuth":{"accessToken":"token","refreshToken":"refresh","expiresAtUnixSeconds":42,"idToken":"id","accountId":"account","planType":"plan","email":"fixture@example.invalid","scopes":[]},"aws":{"region":"region","profile":"profile","accessKeyId":"access","secretAccessKey":"secret","sessionToken":"session"},"google":{"serviceAccountJson":"protected-json"},"secretHeaders":{"X-Key":"value"}}
        """;
    private const string ConnectionJson = "{\"Version\":\"1\",\"Transport\":0,\"Endpoint\":\"https://provider.invalid\"}";
    private const string ChatConfigurationJson = "{\"OutputLimitField\":1,\"UsePromptCacheKey\":false}";
    private const string ChatConnectionJson = "{\"Version\":\"1\",\"Transport\":0,\"Endpoint\":\"https://provider.invalid\",\"ChatCompletions\":" + ChatConfigurationJson + "}";
    private const string VertexAdcJson = "{\"ProjectId\":\"project\",\"Location\":\"us-east5\",\"AuthenticationMode\":0}";
    private const string VertexServiceAccountJson = "{\"ProjectId\":\"project\",\"Location\":\"global\",\"AuthenticationMode\":1}";
    private const string VertexExpressJson = "{\"ProjectId\":null,\"Location\":null,\"AuthenticationMode\":2}";
    private const string AzureProfileJson = "{\"ApiMode\":0,\"AuthenticationMode\":0,\"DeploymentId\":\"alias\",\"ModelProfileId\":\"gpt-4o\"}";
    private const string AzureUnknownProfileJson = "{\"ApiMode\":0,\"AuthenticationMode\":0,\"DeploymentId\":\"alias\",\"ModelProfileId\":null}";
    private const string AzureFutureModesJson = "{\"ApiMode\":999,\"AuthenticationMode\":999,\"DeploymentId\":\"alias\",\"ModelProfileId\":null}";
    private const string XaiGlobalJson = "{\"Region\":0,\"ServiceTier\":0,\"MultiAgent\":null}";
    private const string XaiUnitedStatesPriorityJson = "{\"Region\":1,\"ServiceTier\":1,\"MultiAgent\":null}";
    private const string XaiFutureModesJson = "{\"Region\":999,\"ServiceTier\":999,\"MultiAgent\":null}";
    private const string StateJson = "{\"FormatVersion\":2,\"ImplementationId\":\"fixture\",\"ModelId\":\"model\",\"ScopeId\":\"scope\",\"Payload\":\"opaque\"}";
    private const string MessageJson = "{\"Role\":\"user\",\"Content\":\"text\",\"Attachments\":[{\"type\":\"binary\",\"Kind\":0,\"FileName\":\"image.png\",\"Data\":\"AQID\"}],\"ToolResults\":[{\"type\":\"function\",\"ToolCallId\":\"call\",\"Output\":{\"Parts\":[{\"type\":\"text\",\"Text\":\"text\"},{\"type\":\"binary\",\"Kind\":2,\"MediaType\":\"audio/wav\",\"Data\":\"BA==\"},{\"type\":\"resource\",\"Uri\":\"urn:test\",\"Data\":\"\"}]}}]}";
    private const string EntryJson = "{\"Id\":\"entry\",\"TurnId\":\"turn\",\"Message\":" + MessageJson + "}";

    [Fact]
    public void CredentialApiPreservesExactWebWireBytesAndNullPolicy()
    {
        LlmCredential credential = LlmCredential.Parse(CredentialJson)!;
        Assert.Equal(CredentialJson, credential.Serialize());
        Assert.Equal(CredentialJson, JsonSerializer.Serialize(credential, ConnectLlmJson.Credential));
        Assert.Equal("{\"secretHeaders\":{}}", new LlmCredential().Serialize());
        Assert.Equal("{\"oAuth\":{\"accessToken\":\"token\"},\"secretHeaders\":{}}",
            new LlmCredential { OAuth = new() { AccessToken = "token" } }.Serialize());
        Assert.Equal("{\"apiKey\":\"\\u003C\\u0026\\u003E\",\"secretHeaders\":{}}", new LlmCredential { ApiKey = "<&>" }.Serialize());
    }

    [Fact]
    public void CredentialParsingRetainsWebCaseInsensitivityQuotedNumbersAndUnknownDataBehavior()
    {
        LlmCredential credential = LlmCredential.Parse("""{"APIKEY":"key","OAuth":{"AccessToken":"token","ExpiresAtUnixSeconds":"42"},"future":true}""")!;
        Assert.Equal("key", credential.ApiKey);
        Assert.Equal(42, credential.OAuth!.ExpiresAtUnixSeconds);
        Assert.Null(LlmCredential.Parse("not-json"));
        Assert.Null(LlmCredential.Parse("{\"oAuth\":{}}"));
        Assert.Null(LlmCredential.Parse(" "));
    }

    [Fact]
    public void ScopeIdentityRetainsCanonicalPropertyOrderNullsNumericTransportAndSortedHeaders()
    {
        LlmConnection connection = new()
        {
            Version = "ignored-version",
            Transport = LlmTransport.OpenAiResponses,
            Endpoint = "https://provider.invalid",
            Credential = new LlmCredential
            {
                ApiKey = "key",
                OAuth = new() { AccessToken = "rotating-token", AccountId = "account" },
                SecretHeaders = new Dictionary<string, string> { ["Z-Key"] = "z", ["A-Key"] = "a" }
            }.Serialize(),
            AdditionalHeaders = new Dictionary<string, string> { ["Z-Header"] = "z", ["A-Header"] = "a" }
        };
        const string identity = """
            {"Transport":0,"Endpoint":"https://provider.invalid","ApiKey":"key","OAuthIdentity":"account","Headers":[{"Name":"a-header","Value":"a"},{"Name":"z-header","Value":"z"}],"SecretHeaders":[{"Name":"a-key","Value":"a"},{"Name":"z-key","Value":"z"}]}
            """;
        Assert.Equal(Hash(identity), ProviderContinuationStateCodec.Scope(connection));
        Assert.Equal(Hash(identity), ProviderContinuationStateCodec.Scope(connection with
        {
            Version = "another-version",
            Credential = (LlmCredential.Parse(connection.Credential)! with
            { OAuth = new() { AccessToken = "new-token", AccountId = "account" } }).Serialize()
        }));
    }

    [Fact]
    public void ScopeIdentityDistinguishesAbsentCredentialFromEmptyCredentialAndUsesTokenFallback()
    {
        LlmConnection connection = JsonSerializer.Deserialize(ConnectionJson, ConnectLlmJson.Connection)!;
        Assert.Equal(Hash("""{"Transport":0,"Endpoint":"https://provider.invalid","ApiKey":null,"OAuthIdentity":null,"Headers":[],"SecretHeaders":null}"""),
            ProviderContinuationStateCodec.Scope(connection));
        Assert.Equal(Hash("""{"Transport":0,"Endpoint":"https://provider.invalid","ApiKey":null,"OAuthIdentity":null,"Headers":[],"SecretHeaders":[]}"""),
            ProviderContinuationStateCodec.Scope(connection with { Credential = new LlmCredential().Serialize() }));
        Assert.Equal(Hash("""{"Transport":0,"Endpoint":"https://provider.invalid","ApiKey":null,"OAuthIdentity":"token","Headers":[],"SecretHeaders":[]}"""),
            ProviderContinuationStateCodec.Scope(connection with { Credential = new LlmCredential { OAuth = new() { AccessToken = "token" } }.Serialize() }));
        Assert.Equal("resolved", ProviderContinuationStateCodec.Scope(connection with { ResolvedContinuationScope = "resolved" }));
    }

    [Fact]
    public void ContextEquivalenceRetainsJsonObjectSemanticsAndDistinguishesOrderedBinaryParts()
    {
        var entry = new LlmAgentContextEntry("entry", "turn", new("user", "text")
        {
            ToolResults = [new LlmToolResult("call", new()
            {
                StructuredContent = JsonElement.Parse("{\"a\":1,\"b\":true}"),
                Parts = [new LlmToolOutputText("text"), new LlmToolOutputBinary(LlmBinaryKind.Audio, "audio/wav", new byte[] { 1 })]
            })]
        });
        LlmToolOutput output = Assert.IsType<LlmToolResult>(entry.Message.ToolResults[0]).Output;
        Assert.True(ManagedAgentContext.Equivalent(entry, entry with
        {
            Message = entry.Message with { ToolResults = [new LlmToolResult("call", output with { StructuredContent = JsonElement.Parse("{\"b\":true,\"a\":1}") })] }
        }));
        Assert.False(ManagedAgentContext.Equivalent(entry, entry with { Id = "another" }));
        Assert.False(ManagedAgentContext.Equivalent(entry, entry with
        {
            Message = entry.Message with { ToolResults = [new LlmToolResult("call", output with { Parts = output.Parts.Reverse().ToArray() })] }
        }));
        Assert.False(ManagedAgentContext.Equivalent(entry, entry with
        {
            Message = entry.Message with { ToolResults = [new LlmToolResult("call", output with { Parts = [new LlmToolOutputText("text"), new LlmToolOutputBinary(LlmBinaryKind.Audio, "audio/wav", new byte[] { 2 })] })] }
        }));
    }

    [Fact]
    public void ClosedMessageMetadataPreservesCommonDiscriminatorsBinaryBytesAndEmptyBodies()
    {
        LlmChatMessage message = JsonSerializer.Deserialize(MessageJson, ConnectLlmJson.ChatMessage)!;
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<LlmAttachment>(message.Attachments[0]).Data.ToArray());
        Assert.IsType<LlmToolOutputText>(Assert.IsType<LlmToolResult>(message.ToolResults[0]).Output.Parts[0]);
        Assert.Equal(new byte[] { 4 }, Assert.IsType<LlmToolOutputBinary>(Assert.IsType<LlmToolResult>(message.ToolResults[0]).Output.Parts[1]).Data.ToArray());
        Assert.True(Assert.IsType<LlmToolOutputResource>(Assert.IsType<LlmToolResult>(message.ToolResults[0]).Output.Parts[2]).Data.HasValue);
        string written = JsonSerializer.Serialize(message, ConnectLlmJson.ChatMessage);
        Assert.Contains("\"type\":\"binary\"", written);
        Assert.Contains("\"Data\":\"AQID\"", written);
        Assert.Contains("\"Data\":\"\"", written);
        Assert.NotNull(JsonSerializer.Deserialize(written, ConnectLlmJson.ChatMessage));
    }

    [Fact]
    public void ContextMetadataPreservesVersionAndUnmappedMemberRules()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Entries\":[]}", ConnectLlmJson.AgentContext));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"FormatVersion\":2,\"future\":true}", ConnectLlmJson.AgentContext));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Role\":\"user\",\"Content\":\"text\",\"future\":true}", ConnectLlmJson.ChatMessage));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Role\":\"assistant\",\"Content\":\"text\",\"ContinuationState\":{\"ImplementationId\":\"fixture\",\"ModelId\":\"model\",\"ScopeId\":\"scope\",\"Payload\":\"opaque\"}}", ConnectLlmJson.ChatMessage));
    }

    [Fact]
    public void ConnectionMetadataExcludesResolvedServicesAndRetainsUnknownNumericTransportData()
    {
        LlmConnection connection = JsonSerializer.Deserialize(ConnectionJson.Replace("\"Transport\":0", "\"Transport\":999,\"future\":true", StringComparison.Ordinal), ConnectLlmJson.Connection)!;
        Assert.Equal((LlmTransport)999, connection.Transport);
        string written = JsonSerializer.Serialize(connection with { ResolvedContinuationScope = "internal-scope" }, ConnectLlmJson.Connection);
        Assert.Contains("\"Transport\":999", written);
        Assert.DoesNotContain("internal-scope", written, StringComparison.Ordinal);
        Assert.DoesNotContain("ContinuationCodec", written, StringComparison.Ordinal);
        Assert.DoesNotContain("Catalog", written, StringComparison.Ordinal);
        Assert.Null(ConnectLlmJson.AgentContext.Options.TypeInfoResolver!.GetTypeInfo(typeof(LlmAgentEvent), ConnectLlmJson.AgentContext.Options));
        Assert.Null(ConnectLlmJson.AgentContext.Options.TypeInfoResolver!.GetTypeInfo(typeof(LlmSettings), ConnectLlmJson.AgentContext.Options));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionAndSetupMetadataRetainChatCompletionsConfiguration(bool configured)
    {
        string json = configured ? ChatConnectionJson : ConnectionJson;
        LlmConnection connection = JsonSerializer.Deserialize(json, ConnectLlmJson.Connection)!;
        string expected = configured ? ChatConfigurationJson : "null";
        using JsonDocument written = JsonDocument.Parse(JsonSerializer.Serialize(connection, ConnectLlmJson.Connection));
        Assert.Equal(expected, written.RootElement.GetProperty("ChatCompletions").GetRawText());
        var setup = new LlmSetupConfiguration(connection, "model");
        string setupJson = JsonSerializer.Serialize(setup, ConnectLlmJson.SetupConfiguration);
        using JsonDocument setupWritten = JsonDocument.Parse(setupJson);
        Assert.Equal(expected, setupWritten.RootElement.GetProperty("Connection").GetProperty("ChatCompletions").GetRawText());
        LlmSetupConfiguration restored = JsonSerializer.Deserialize(setupJson, ConnectLlmJson.SetupConfiguration)!;
        Assert.Equal(connection.ChatCompletions, restored.Connection.ChatCompletions);
        if (configured)
        {
            Assert.Equal(LlmChatCompletionsOutputLimitField.MaxCompletionTokens, restored.Connection.ChatCompletions!.OutputLimitField);
            Assert.False(restored.Connection.ChatCompletions.UsePromptCacheKey);
        }
    }

    [Theory]
    [InlineData(VertexAdcJson)]
    [InlineData(VertexServiceAccountJson)]
    [InlineData(VertexExpressJson)]
    public void ConnectionAndSetupMetadataRetainVertexScopeAndNumericAuthentication(string configuration)
    {
        LlmConnection connection = JsonSerializer.Deserialize(VertexConnectionJson(configuration), ConnectLlmJson.Connection)!;
        Assert.Equal(LlmTransport.GoogleVertexAi, connection.Transport);
        using JsonDocument written = JsonDocument.Parse(JsonSerializer.Serialize(connection, ConnectLlmJson.Connection));
        Assert.Equal(configuration, written.RootElement.GetProperty("Vertex").GetRawText());
        var setup = new LlmSetupConfiguration(connection, "google/gemini-3-flash-preview");
        string setupJson = JsonSerializer.Serialize(setup, ConnectLlmJson.SetupConfiguration);
        using JsonDocument setupWritten = JsonDocument.Parse(setupJson);
        Assert.Equal(configuration, setupWritten.RootElement.GetProperty("Connection").GetProperty("Vertex").GetRawText());
        LlmSetupConfiguration restored = JsonSerializer.Deserialize(setupJson, ConnectLlmJson.SetupConfiguration)!;
        Assert.Equal(connection.Vertex, restored.Connection.Vertex);
        Assert.Equal(setup.ModelId, restored.ModelId);
    }

    private static string VertexConnectionJson(string configuration) =>
        "{\"Version\":\"1\",\"Transport\":11,\"Endpoint\":\"https://aiplatform.googleapis.com\",\"Vertex\":" + configuration + "}";

    private static string AzureConnectionJson(string configuration) =>
        "{\"Version\":\"1\",\"Transport\":14,\"Endpoint\":\"https://resource.openai.azure.com\",\"Azure\":" + configuration + "}";

    private static string XaiConnectionJson(string configuration) =>
        "{\"Version\":\"1\",\"Transport\":13,\"Endpoint\":\"https://api.x.ai/v1\",\"Xai\":" + configuration + "}";

    [Theory]
    [InlineData(XaiGlobalJson)]
    [InlineData(XaiUnitedStatesPriorityJson)]
    [InlineData(XaiFutureModesJson)]
    public void ConnectionAndSetupMetadataRetainXaiRegionTierAndNumericModes(string configuration)
    {
        LlmConnection connection = JsonSerializer.Deserialize(XaiConnectionJson(configuration), ConnectLlmJson.Connection)!;
        using JsonDocument written = JsonDocument.Parse(JsonSerializer.Serialize(connection, ConnectLlmJson.Connection));
        Assert.Equal(configuration, written.RootElement.GetProperty("Xai").GetRawText());
        string setupJson = JsonSerializer.Serialize(new LlmSetupConfiguration(connection, "grok-4.7"), ConnectLlmJson.SetupConfiguration);
        LlmSetupConfiguration restored = JsonSerializer.Deserialize(setupJson, ConnectLlmJson.SetupConfiguration)!;
        Assert.Equal(connection.Xai, restored.Connection.Xai);
        Assert.Equal("grok-4.7", restored.ModelId);
    }

    [Theory]
    [InlineData(AzureProfileJson)]
    [InlineData(AzureUnknownProfileJson)]
    [InlineData(AzureFutureModesJson)]
    public void ConnectionAndSetupMetadataRetainAzureDeploymentProfileAndNumericModes(string configuration)
    {
        LlmConnection connection = JsonSerializer.Deserialize(AzureConnectionJson(configuration), ConnectLlmJson.Connection)!;
        using JsonDocument written = JsonDocument.Parse(JsonSerializer.Serialize(connection, ConnectLlmJson.Connection));
        Assert.Equal(configuration, written.RootElement.GetProperty("Azure").GetRawText());
        string setupJson = JsonSerializer.Serialize(new LlmSetupConfiguration(connection, "alias"), ConnectLlmJson.SetupConfiguration);
        LlmSetupConfiguration restored = JsonSerializer.Deserialize(setupJson, ConnectLlmJson.SetupConfiguration)!;
        Assert.Equal(connection.Azure, restored.Connection.Azure);
        Assert.Equal("alias", restored.ModelId);
    }

    public static TheoryData<string, string> CollectibleFamilies => new()
    {
        { "Credential", CredentialJson },
        { "Connection", ConnectionJson },
        { "Connection", ChatConnectionJson },
        { "Connection", VertexConnectionJson(VertexAdcJson) },
        { "Connection", VertexConnectionJson(VertexServiceAccountJson) },
        { "Connection", VertexConnectionJson(VertexExpressJson) },
        { "Connection", AzureConnectionJson(AzureProfileJson) },
        { "Connection", AzureConnectionJson(AzureUnknownProfileJson) },
        { "Connection", AzureConnectionJson(AzureFutureModesJson) },
        { "Connection", XaiConnectionJson(XaiGlobalJson) },
        { "Connection", XaiConnectionJson(XaiUnitedStatesPriorityJson) },
        { "Connection", XaiConnectionJson(XaiFutureModesJson) },
        { "SetupConfiguration", "{\"Connection\":" + ConnectionJson + ",\"ModelId\":\"model\"}" },
        { "SetupConfiguration", "{\"Connection\":" + ChatConnectionJson + ",\"ModelId\":\"model\"}" },
        { "SetupConfiguration", "{\"Connection\":" + VertexConnectionJson(VertexAdcJson) + ",\"ModelId\":\"model\"}" },
        { "SetupConfiguration", "{\"Connection\":" + VertexConnectionJson(VertexServiceAccountJson) + ",\"ModelId\":\"model\"}" },
        { "SetupConfiguration", "{\"Connection\":" + VertexConnectionJson(VertexExpressJson) + ",\"ModelId\":\"model\"}" },
        { "SetupConfiguration", "{\"Connection\":" + AzureConnectionJson(AzureProfileJson) + ",\"ModelId\":\"alias\"}" },
        { "SetupConfiguration", "{\"Connection\":" + AzureConnectionJson(AzureUnknownProfileJson) + ",\"ModelId\":\"alias\"}" },
        { "SetupConfiguration", "{\"Connection\":" + AzureConnectionJson(AzureFutureModesJson) + ",\"ModelId\":\"alias\"}" },
        { "SetupConfiguration", "{\"Connection\":" + XaiConnectionJson(XaiGlobalJson) + ",\"ModelId\":\"grok-4.7\"}" },
        { "SetupConfiguration", "{\"Connection\":" + XaiConnectionJson(XaiUnitedStatesPriorityJson) + ",\"ModelId\":\"grok-4.7\"}" },
        { "SetupConfiguration", "{\"Connection\":" + XaiConnectionJson(XaiFutureModesJson) + ",\"ModelId\":\"grok-4.7\"}" },
        { "ChatRequest", "{\"ModelId\":\"model\",\"Messages\":[" + MessageJson + "]}" },
        { "ChatRequest", "{\"ModelId\":\"model\",\"PromptCache\":{\"Scope\":0,\"Retention\":1},\"Messages\":[" + MessageJson + "]}" },
        { "ChatResult", "{\"Text\":\"text\",\"Status\":0,\"ContinuationState\":" + StateJson + ",\"Diagnostics\":{\"HttpStatusCode\":200}}" },
        { "ChatMessage", MessageJson },
        { "ChatRequest", """{"ModelId":"model","Tools":[{"type":"function","Name":"lookup","InputSchema":{}},{"type":"shell","Skills":[]}],"Messages":[{"Role":"assistant","Content":"","ToolCalls":[{"type":"function","Id":"fn","Name":"lookup","ArgumentsJson":"{}"},{"type":"shell","Id":"sh","Commands":["echo actual"],"TimeoutMs":0,"MaxOutputLength":0}]},{"Role":"user","Content":"","ToolResults":[{"type":"shell","ToolCallId":"sh","Output":[{"Stdout":"","Stderr":null,"Outcome":{"type":"exit","ExitCode":0}}]},{"type":"function","ToolCallId":"fn","Output":{"Parts":[]}}]}]}""" },
        { "ChatResult", """{"Text":"","Status":2,"Diagnostics":{"HttpStatusCode":200},"ToolCalls":[{"type":"shell","Id":"sh","Commands":["echo actual"],"TimeoutMs":null,"MaxOutputLength":0},{"type":"function","Id":"fn","Name":"lookup","ArgumentsJson":"{}"}]}""" },
        { "LlmAgentResourceSelectionChangedEvent", """{"ThreadId":"actual-thread","TurnId":null,"ResourceSelectionRevision":0}""" },
        { "LlmAgentResourceSelectionChangedEvent", """{"ThreadId":"actual-thread","TurnId":"actual-turn","ResourceSelectionRevision":5}""" },
        { "LlmEmbeddingRequest", "{\"ModelId\":\"unknown-model\",\"Inputs\":[\"first\",\"second\"],\"Dimensions\":2,\"ProviderOptions\":{\"custom\":{\"nested\":true}}}" },
        { "LlmEmbeddingResult", "{\"Vectors\":[[0,1],[2,3]],\"Usage\":{\"InputTokens\":0,\"TotalTokens\":null},\"Diagnostics\":{\"HttpStatusCode\":200}}" },
        { "LlmEmbeddingUsage", "{\"InputTokens\":null,\"TotalTokens\":0}" },
        { "ChatResult", "{\"Text\":\"done\",\"Status\":0,\"Usage\":{\"InputTokens\":0,\"OutputTokens\":null,\"TotalTokens\":null,\"ProviderReportedCostUsd\":0.0000000001,\"HostedToolCalls\":{\"WebSearch\":2},\"HostedToolCallCount\":5,\"HostedSourcesUsed\":0},\"Diagnostics\":{\"HttpStatusCode\":200}}" },
        { "AgentContext", "{\"FormatVersion\":3,\"SelectedResources\":[],\"Entries\":[{\"Id\":\"entry\",\"TurnId\":\"turn\",\"Message\":{\"Role\":\"user\",\"Content\":\"text\",\"Attachments\":[{\"type\":\"binary\",\"Kind\":0,\"FileName\":\"image.png\",\"Data\":\"AQID\"}]}}]}" },
        { "AgentContextEntry", EntryJson },
        { "AgentContext", """{"FormatVersion":3,"SelectedResources":[],"Entries":[{"Id":"user","TurnId":"turn","Message":{"Role":"user","Content":"go"}},{"Id":"calls","TurnId":"turn","Message":{"Role":"assistant","Content":"","ToolCalls":[{"type":"function","Id":"fn","Name":"lookup","ArgumentsJson":"{}"},{"type":"shell","Id":"sh","Commands":["echo actual"],"TimeoutMs":0,"MaxOutputLength":0}]}},{"Id":"results","TurnId":"turn","Message":{"Role":"user","Content":"","ToolResults":[{"type":"shell","ToolCallId":"sh","Output":[{"Stdout":"","Stderr":null,"Outcome":{"type":"exit","ExitCode":0}}]},{"type":"function","ToolCallId":"fn","Output":{"Parts":[]}}]}}]}""" },
        { "AgentContextStatus", "{\"ModelId\":\"model\",\"ContextRevision\":1,\"Source\":0}" },
        { "ConnectContinuationScopeIdentity", "{\"Transport\":0,\"Endpoint\":\"https://provider.invalid\",\"ApiKey\":null,\"OAuthIdentity\":null,\"Headers\":[],\"SecretHeaders\":null}" }
    };

    [Theory]
    [MemberData(nameof(CollectibleFamilies))]
    public void ActualConnectAndCommonGenerationCollectsAfterOwnedMetadataAndCallersRetire(string family, string json)
    {
        WeakReference generation = ExerciseGeneration(family, json);
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive, "The Connect/Common generation remains rooted after the owned family and callers retire.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseGeneration(string family, string json)
    {
        string path = typeof(ConnectLlmJson).Assembly.Location;
        var generation = new ConnectGeneration(Path.GetDirectoryName(path)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly connect = generation.LoadFromAssemblyPath(path);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assert.Equal(typeof(ConnectLlmJson).Assembly.ManifestModule.ModuleVersionId, connect.ManifestModule.ModuleVersionId);
            Assert.Equal(typeof(InferenceJson).Assembly.ManifestModule.ModuleVersionId, common.ManifestModule.ModuleVersionId);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(connect));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(common));
            Type owner = connect.GetType("Darkspyre.Inference.Connect.LLM.ConnectLlmJson", throwOnError: true)!;
            if (family.StartsWith("LlmEmbedding", StringComparison.Ordinal)) Assert.NotNull(owner.GetProperty(family));
            JsonTypeInfo metadata;
            if (owner.GetProperty(family) is { } exported) metadata = (JsonTypeInfo)exported.GetValue(null)!;
            else
            {
                Type context = connect.GetType("Darkspyre.Inference.Connect.LLM.ConnectLlmJsonContext", throwOnError: true)!;
                object instance = context.GetProperty("Default")!.GetValue(null)!;
                metadata = (JsonTypeInfo)context.GetProperty(family)!.GetValue(instance)!;
            }
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Type.Assembly));
            Assembly resolverOwner = metadata.Options.TypeInfoResolver!.GetType().Assembly;
            Assert.Same(connect, resolverOwner);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(resolverOwner));
            Assert.Equal(typeof(ConnectLlmJson).Assembly.ManifestModule.ModuleVersionId, resolverOwner.ManifestModule.ModuleVersionId);
            object value = JsonSerializer.Deserialize(json, metadata)!;
            string written = JsonSerializer.Serialize(value, metadata);
            Assert.NotNull(JsonSerializer.Deserialize(written, metadata));
            if (json.Contains("\"ProviderReportedCostUsd\"", StringComparison.Ordinal))
            {
                object usage = value.GetType().GetProperty("Usage")!.GetValue(value)!;
                Assert.Same(common, usage.GetType().Assembly);
                var map = (System.Collections.IEnumerable)usage.GetType().GetProperty("HostedToolCalls")!.GetValue(usage)!;
                object entry = Assert.Single(map.Cast<object>());
                Assert.Same(common, entry.GetType().GetProperty("Key")!.GetValue(entry)!.GetType().Assembly);
                using JsonDocument expected = JsonDocument.Parse(json);
                using JsonDocument actual = JsonDocument.Parse(written);
                foreach (string name in new[] { "ProviderReportedCostUsd", "HostedToolCalls", "HostedToolCallCount", "HostedSourcesUsed" })
                    Assert.True(JsonElement.DeepEquals(expected.RootElement.GetProperty("Usage").GetProperty(name), actual.RootElement.GetProperty("Usage").GetProperty(name)), name);
            }
            if (json.Contains("\"PromptCache\"", StringComparison.Ordinal))
            {
                object hint = value.GetType().GetProperty("PromptCache")!.GetValue(value)!;
                Assert.Same(common, hint.GetType().Assembly);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(hint.GetType().Assembly));
                using JsonDocument expected = JsonDocument.Parse(json);
                using JsonDocument actual = JsonDocument.Parse(written);
                Assert.Equal(expected.RootElement.GetProperty("PromptCache").GetRawText(), actual.RootElement.GetProperty("PromptCache").GetRawText());
            }
            if (json.Contains("\"ChatCompletions\"", StringComparison.Ordinal))
            {
                using JsonDocument document = JsonDocument.Parse(written);
                JsonElement connection = family == "Connection" ? document.RootElement : document.RootElement.GetProperty("Connection");
                Assert.Equal(ChatConfigurationJson, connection.GetProperty("ChatCompletions").GetRawText());
                object owningConnection = family == "Connection" ? value : value.GetType().GetProperty("Connection")!.GetValue(value)!;
                object configuration = owningConnection.GetType().GetProperty("ChatCompletions")!.GetValue(owningConnection)!;
                Assert.Same(connect, configuration.GetType().Assembly);
            }
            foreach (string configurationName in new[] { "Vertex", "Azure", "Xai" })
            {
                if (!json.Contains("\"" + configurationName + "\"", StringComparison.Ordinal)) continue;
                using JsonDocument original = JsonDocument.Parse(json);
                using JsonDocument document = JsonDocument.Parse(written);
                JsonElement expectedConnection = family == "Connection" ? original.RootElement : original.RootElement.GetProperty("Connection");
                JsonElement connection = family == "Connection" ? document.RootElement : document.RootElement.GetProperty("Connection");
                Assert.Equal(expectedConnection.GetProperty(configurationName).GetRawText(), connection.GetProperty(configurationName).GetRawText());
                object owningConnection = family == "Connection" ? value : value.GetType().GetProperty("Connection")!.GetValue(value)!;
                object configuration = owningConnection.GetType().GetProperty(configurationName)!.GetValue(owningConnection)!;
                Assert.Same(connect, configuration.GetType().Assembly);
            }
            if (family == "ChatMessage")
            {
                var attachments = (System.Collections.IEnumerable)value.GetType().GetProperty("Attachments")!.GetValue(value)!;
                object attachment = Assert.Single(attachments.Cast<object>());
                Assert.Same(common, attachment.GetType().Assembly);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(attachment.GetType().Assembly));
            }
            if (family == "Credential")
            {
                Assert.Equal(CredentialJson, value.GetType().GetMethod("Serialize")!.Invoke(value, null));
                Assert.NotNull(value.GetType().GetMethod("Parse")!.Invoke(null, [written]));
            }
            if (family == "AgentContextEntry")
            {
                Type context = connect.GetType("Darkspyre.Inference.Connect.LLM.Internal.ManagedAgentContext", throwOnError: true)!;
                Assert.Equal(true, context.GetMethod("Equivalent")!.Invoke(null, [value, value]));
            }
            if (family == "Connection")
            {
                Type codec = connect.GetType("Darkspyre.Inference.Connect.LLM.Internal.ProviderContinuationStateCodec", throwOnError: true)!;
                Assert.IsType<string>(codec.GetMethod("Scope", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [value]));
            }
        }
        finally
        {
            generation.Unload();
        }
        return weak;
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

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
