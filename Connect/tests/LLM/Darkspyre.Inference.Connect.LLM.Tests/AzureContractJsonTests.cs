using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AzureContractJsonTests
{
    private const string SecretIdentity = """{"type":"client-secret","TenantId":"tenant","ClientId":"client"}""";
    private const string CertificateIdentity = """{"type":"client-certificate","TenantId":"tenant","ClientId":"client"}""";
    private const string WorkloadIdentity = """{"type":"workload","TenantId":"tenant","ClientId":"client","FederatedTokenFile":"/explicit/assertion"}""";
    private const string UserIdentity = """{"type":"user-managed","Id":{"type":"resource-id","Value":"/subscriptions/selected/resourceGroups/group/providers/Microsoft.ManagedIdentity/userAssignedIdentities/identity"}}""";

    public static TheoryData<string, string> Shapes => new()
    {
        { "LlmAzureConfiguration", """{"ApiMode":1,"AuthenticationMode":1,"DeploymentId":"deployment","ModelProfileId":"gpt-4.1","ApiVersion":"2025-01-01","Entra":{"Cloud":0,"Audience":0,"Identity":""" + SecretIdentity + "}}" },
        { "LlmAzureApiMode", "1" }, { "LlmAzureAuthenticationMode", "1" },
        { "LlmAzureCloud", "2" }, { "LlmAzureTokenAudience", "1" },
        { "LlmAzureEntraConfiguration", """{"Cloud":0,"Audience":1,"Identity":""" + WorkloadIdentity + "}" },
        { "LlmAzureIdentitySelection", SecretIdentity }, { "LlmAzureIdentitySelection", CertificateIdentity },
        { "LlmAzureIdentitySelection", WorkloadIdentity }, { "LlmAzureIdentitySelection", """{"type":"system-managed"}""" },
        { "LlmAzureIdentitySelection", UserIdentity },
        { "ServicePrincipalSecret", """{"TenantId":"tenant","ClientId":"client"}""" },
        { "ServicePrincipalCertificate", """{"TenantId":"tenant","ClientId":"client"}""" },
        { "WorkloadIdentity", """{"TenantId":"tenant","ClientId":"client","FederatedTokenFile":"/explicit/assertion"}""" },
        { "SystemAssignedManagedIdentity", "{}" },
        { "UserAssignedManagedIdentity", """{"Id":{"type":"client-id","Value":"11111111-1111-1111-1111-111111111111"}}""" },
        { "LlmAzureManagedIdentityId", """{"type":"client-id","Value":"11111111-1111-1111-1111-111111111111"}""" },
        { "LlmAzureManagedIdentityId", """{"type":"object-id","Value":"22222222-2222-2222-2222-222222222222"}""" },
        { "LlmAzureManagedIdentityId", """{"type":"resource-id","Value":"/subscriptions/selected/resourceGroups/group/providers/Microsoft.ManagedIdentity/userAssignedIdentities/identity"}""" },
        { "ClientId", """{"Value":"11111111-1111-1111-1111-111111111111"}""" },
        { "ObjectId", """{"Value":"22222222-2222-2222-2222-222222222222"}""" },
        { "ResourceId", """{"Value":"/subscriptions/selected/resourceGroups/group/providers/Microsoft.ManagedIdentity/userAssignedIdentities/identity"}""" },
        { "LlmAzureSecret", """{"type":"client-secret","value":"  exact secret  "}""" },
        { "LlmAzureSecret", """{"type":"certificate-pkcs12","base64":"  opaque protected bytes  ","password":"  exact password  "}""" },
        { "ClientSecret", """{"value":"  exact secret  "}""" },
        { "CertificatePkcs12", """{"base64":"opaque bytes","password":""}""" }
    };

    [Theory, MemberData(nameof(Shapes))]
    public void EveryOwnedGetterPreservesExactSelectedData(string name, string json)
        => RoundTrip(Metadata(typeof(ConnectLlmJson), name), json);

    [Theory]
    [InlineData("LlmAzureApiMode")]
    [InlineData("LlmAzureAuthenticationMode")]
    [InlineData("LlmAzureCloud")]
    [InlineData("LlmAzureTokenAudience")]
    public void ClosedSelectionsRefuseUnknownValues(string name)
    {
        JsonTypeInfo metadata = Metadata(typeof(ConnectLlmJson), name);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("99", metadata));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(Enum.ToObject(metadata.Type, 99), metadata));
    }

    [Theory]
    [InlineData("LlmAzureIdentitySelection")]
    [InlineData("LlmAzureManagedIdentityId")]
    [InlineData("LlmAzureSecret")]
    public void ClosedUnionsRefuseUnknownSelection(string name)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"type\":\"ambient\"}", Metadata(typeof(ConnectLlmJson), name)));

    [Fact]
    public void ProtectedAzureCredentialsDoNotDisappearIntoApiKeyCredentials()
    {
        const string json = """{"azure":{"type":"certificate-pkcs12","base64":"exact-protected-bytes","password":" exact password "}}""";
        LlmCredential credential = JsonSerializer.Deserialize(json, ConnectLlmJson.Credential)!;
        Assert.NotNull(typeof(LlmCredential).GetProperty("Azure"));
        using JsonDocument written = JsonDocument.Parse(credential.Serialize());
        using JsonDocument expected = JsonDocument.Parse(json);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.RootElement.GetProperty("azure").GetRawText()),
            JsonNode.Parse(written.RootElement.GetProperty("azure").GetRawText())));
        Assert.False(written.RootElement.TryGetProperty("apiKey", out _));
        Assert.Equal("{}", written.RootElement.GetProperty("secretHeaders").GetRawText());
        Assert.Equal("LlmCredential { [redacted] }", credential.ToString());
        object secret = typeof(LlmCredential).GetProperty("Azure")!.GetValue(credential)!;
        Assert.DoesNotContain("exact-protected-bytes", secret.ToString());
        Assert.DoesNotContain("password", secret.ToString());
    }

    [Theory]
    [InlineData("LlmAzureEntraConfiguration", "{}")]
    [InlineData("LlmAzureEntraConfiguration", "{\"Cloud\":0,\"Audience\":0}")]
    [InlineData("LlmAzureIdentitySelection", "{\"type\":\"system-managed\",\"ClientId\":\"ignored\"}")]
    [InlineData("LlmAzureManagedIdentityId", "{\"type\":\"client-id\",\"Value\":\"id\",\"ObjectId\":\"ignored\"}")]
    [InlineData("LlmAzureSecret", "{\"type\":\"certificate-pkcs12\",\"base64\":\"opaque\",\"file\":\"ignored\"}")]
    [InlineData("LlmAzureSecret", "{\"type\":\"client-secret\"}")]
    public void MissingOrForeignIdentityFieldsRefuse(string name, string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, Metadata(typeof(ConnectLlmJson), name)));

    [Fact]
    public void OptionalPasswordAndDefaultsRemainExactWithoutOpeningFiles()
    {
        Assert.Equal(LlmAzureApiMode.V1, new LlmAzureConfiguration().ApiMode);
        Assert.Equal(LlmAzureAuthenticationMode.ApiKey, new LlmAzureConfiguration().AuthenticationMode);
        Assert.Null(JsonSerializer.Deserialize("{\"base64\":\"opaque\"}", ConnectLlmJson.CertificatePkcs12)!.Password);
        Assert.Equal(string.Empty, JsonSerializer.Deserialize("{\"base64\":\"opaque\",\"password\":\"\"}", ConnectLlmJson.CertificatePkcs12)!.Password);
        var identity = new LlmAzureIdentitySelection.WorkloadIdentity("tenant", "client", "/not-opened/assertion");
        Assert.Equal("/not-opened/assertion", identity.FederatedTokenFile);
        Assert.DoesNotContain("assertion", identity.ToString());
        Assert.Throws<ArgumentException>(() => identity with { TenantId = " tenant " });
        Assert.Throws<ArgumentException>(() => identity with { FederatedTokenFile = "relative/assertion" });
    }

    [Fact]
    public void ConnectionAndSetupMetadataRefuseUnknownAzureModes()
    {
        const string json = "{\"Transport\":14,\"Azure\":{\"ApiMode\":999}}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, ConnectLlmJson.Connection));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Connection\":" + json + ",\"ModelId\":\"deployment\"}", ConnectLlmJson.SetupConfiguration));
    }

    [Fact]
    public void DiagnosticRedactionIncludesEveryProtectedAzureValue()
    {
        const string secret = "explicit-client-secret";
        const string certificate = "opaque-pkcs12-material";
        const string password = "explicit-pfx-password";
        Assert.Equal("echo [REDACTED]", SecretRedactor.Sanitize("echo " + secret,
            new() { Azure = new LlmAzureSecret.ClientSecret(secret) }));
        Assert.Equal("echo [REDACTED] [REDACTED]", SecretRedactor.Sanitize("echo " + certificate + " " + password,
            new() { Azure = new LlmAzureSecret.CertificatePkcs12(certificate, password) }));
    }

    [Theory]
    [InlineData(LlmTransport.AzureOpenAiResponses)]
    [InlineData(LlmTransport.AzureOpenAiChat)]
    public async Task ApiKeySetterCannotReplaceAnEntraCredential(LlmTransport transport)
    {
        var source = new CredentialSource(new LlmConnection
        {
            Transport = transport, Version = "current", Endpoint = "https://selected.openai.azure.com",
            Azure = new() { AuthenticationMode = LlmAzureAuthenticationMode.Entra },
            Credential = new LlmCredential { Azure = new LlmAzureSecret.ClientSecret("original") }.Serialize()
        });
        await using var service = new LlmProviderService(source, [], []);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.SetApiKeyAsync("selected", "replacement", "current"));
        Assert.Equal(0, source.Saves);
        Assert.Equal("original", Assert.IsType<LlmAzureSecret.ClientSecret>(LlmCredential.Parse(source.Connection.Credential)!.Azure).Value);
    }

    [Theory, MemberData(nameof(Shapes))]
    public void ActualForeignMetadataAndNestedSelectionsRetire(string name, string json)
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
            foreach ((Assembly expected, Assembly actual) in new[] { (typeof(ConnectLlmJson).Assembly, connect), (typeof(InferenceJson).Assembly, common) })
            {
                Assert.NotSame(expected, actual);
                Assert.Equal(expected.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
            }
            Type facade = connect.GetType(typeof(ConnectLlmJson).FullName!, true)!;
            JsonTypeInfo metadata = Metadata(facade, name);
            Assert.Same(connect, metadata.Type.Assembly);
            Assert.Same(connect, metadata.Options.TypeInfoResolver!.GetType().Assembly);
            object value = RoundTrip(metadata, json);
            object? nested = name switch
            {
                "LlmAzureConfiguration" => value.GetType().GetProperty("Entra")!.GetValue(value),
                "LlmAzureEntraConfiguration" => value.GetType().GetProperty("Identity")!.GetValue(value),
                "UserAssignedManagedIdentity" => value.GetType().GetProperty("Id")!.GetValue(value),
                "LlmAzureIdentitySelection" when value.GetType().Name == "UserAssignedManagedIdentity"
                    => value.GetType().GetProperty("Id")!.GetValue(value),
                _ => null
            };
            if (nested is not null)
            {
                Assert.Same(connect, nested.GetType().Assembly);
                object? identity = nested.GetType().GetProperty("Identity")?.GetValue(nested);
                if (identity is not null) Assert.Same(connect, identity.GetType().Assembly);
            }
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static object RoundTrip(JsonTypeInfo metadata, string json)
    {
        object value = JsonSerializer.Deserialize(json, metadata)!;
        using JsonDocument original = JsonDocument.Parse(json);
        using JsonDocument written = JsonDocument.Parse(JsonSerializer.Serialize(value, metadata));
        if (original.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in original.RootElement.EnumerateObject())
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(property.Value.GetRawText()), JsonNode.Parse(written.RootElement.GetProperty(property.Name).GetRawText())));
        }
        else
            Assert.Equal(original.RootElement.GetRawText(), written.RootElement.GetRawText());
        return value;
    }

    private static JsonTypeInfo Metadata(Type facade, string name)
    {
        PropertyInfo? getter = facade.GetProperty(name);
        Assert.NotNull(getter);
        Assert.Same(facade.Assembly, getter.GetMethod!.Module.Assembly);
        return Assert.IsAssignableFrom<JsonTypeInfo>(getter.GetValue(null));
    }

    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => name.Name is { } id && id.StartsWith("Darkspyre.Inference", StringComparison.Ordinal)
            ? LoadFromAssemblyPath(Path.Combine(directory, id + ".dll")) : null;
    }

    private sealed class CredentialSource(LlmConnection connection) : ILlmConnectionSource
    {
        public LlmConnection Connection { get; } = connection;
        public int Saves { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult<LlmConnection?>(Connection);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default)
        {
            Saves++;
            return Task.CompletedTask;
        }
    }
}
