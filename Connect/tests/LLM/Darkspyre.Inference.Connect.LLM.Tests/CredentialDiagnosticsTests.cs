using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class CredentialDiagnosticsTests
{
    private const string CredentialDiagnostic = "LlmCredential { [redacted] }";
    private const string OAuthDiagnostic = "LlmOAuthTokens { [redacted] }";
    private const string Wire = """
        {"apiKey":"fixture-api-key","oAuth":{"accessToken":"fixture-access","refreshToken":"fixture-refresh","expiresAtUnixSeconds":42,"idToken":"fixture-id","accountId":"fixture-account","planType":"fixture-plan","email":"fixture@example.invalid","scopes":["fixture-scope"]},"aws":{"region":"fixture-region","profile":"fixture-profile","accessKeyId":"fixture-aws-access","secretAccessKey":"fixture-aws-secret","sessionToken":"fixture-aws-session"},"google":{"serviceAccountJson":"fixture-google-json"},"secretHeaders":{"X-Fixture":"fixture-header"}}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CredentialDiagnosticIsConstantForEmptyAndPopulatedValues(bool populated)
    {
        LlmCredential value = populated ? LlmCredential.Parse(Wire)! : new();
        Assert.Equal(CredentialDiagnostic, value.ToString());
        Assert.Equal(CredentialDiagnostic, $"{value}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OAuthDiagnosticIsConstantForEmptyAndPopulatedValues(bool populated)
    {
        LlmOAuthTokens value = populated ? LlmCredential.Parse(Wire)!.OAuth! : new() { AccessToken = "" };
        Assert.Equal(OAuthDiagnostic, value.ToString());
        Assert.Equal(OAuthDiagnostic, $"{value}");
    }

    [Fact]
    public void DiagnosticsPreserveValuesRecordCopiesAndExactProtectedStoreWire()
    {
        LlmCredential value = LlmCredential.Parse(Wire)!;
        Assert.Equal("fixture-api-key", value.ApiKey);
        LlmOAuthTokens oauth = value.OAuth!;
        Assert.Equal("fixture-access", oauth.AccessToken);
        Assert.Equal("fixture-refresh", oauth.RefreshToken);
        Assert.Equal(42L, oauth.ExpiresAtUnixSeconds);
        Assert.Equal("fixture-id", oauth.IdToken);
        Assert.Equal("fixture-account", oauth.AccountId);
        Assert.Equal("fixture-plan", oauth.PlanType);
        Assert.Equal("fixture@example.invalid", oauth.Email);
        Assert.Equal(["fixture-scope"], oauth.Scopes);
        Assert.Equal("fixture-aws-secret", value.Aws!.SecretAccessKey);
        Assert.Equal("fixture-google-json", value.Google!.ServiceAccountJson);
        Assert.Equal("fixture-header", value.SecretHeaders["X-Fixture"]);
        Assert.Equal(value, value with { });
        Assert.Equal(oauth, oauth with { });
        Assert.Equal(value.GetHashCode(), (value with { }).GetHashCode());
        Assert.Equal(oauth.GetHashCode(), (oauth with { }).GetHashCode());
        LlmCredential changed = value with { ApiKey = "fixture-other-key", OAuth = oauth with { AccountId = "fixture-other-account" } };
        Assert.NotEqual(value, changed);
        Assert.NotEqual(oauth, changed.OAuth);
        Assert.Same(value.SecretHeaders, changed.SecretHeaders);
        Assert.Same(value.Aws, changed.Aws);
        Assert.Same(value.Google, changed.Google);
        Assert.Same(oauth.Scopes, changed.OAuth!.Scopes);
        Assert.Equal("fixture-api-key", value.ApiKey);
        Assert.Equal("fixture-account", oauth.AccountId);
        Assert.Equal(Wire, value.Serialize());
        Assert.Equal(Wire, JsonSerializer.Serialize(value, ConnectLlmJson.Credential));
        Assert.Equal(Wire, LlmCredential.Parse(value.Serialize())!.Serialize());
        Assert.Equal("{\"secretHeaders\":{}}", new LlmCredential().Serialize());
        Assert.Equal("{\"oAuth\":{\"accessToken\":\"\"},\"secretHeaders\":{}}",
            new LlmCredential { OAuth = new() { AccessToken = "" } }.Serialize());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualForeignCredentialCallersAndOwnedMetadataRetire(bool populated)
    {
        (WeakReference generation, WeakReference[] roots) = ExerciseGeneration(populated);
        for (int attempt = 0; generation.IsAlive && attempt < 30; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.All(roots, root => Assert.False(root.IsAlive, "A credential or owned metadata graph remains rooted."));
        Assert.False(generation.IsAlive, "The actual Connect/Common credential generation remains rooted.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Generation, WeakReference[] Roots) ExerciseGeneration(bool populated)
    {
        var generation = new CredentialGeneration(Path.GetDirectoryName(typeof(LlmCredential).Assembly.Location)!);
        var weak = new WeakReference(generation);
        WeakReference[] roots;
        try
        {
            Assembly connect = generation.LoadFromAssemblyPath(typeof(LlmCredential).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            foreach (var pair in new[] { (connect, typeof(LlmCredential).Assembly), (common, typeof(InferenceJson).Assembly) })
            {
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(pair.Item1));
                Assert.Equal(pair.Item2.ManifestModule.ModuleVersionId, pair.Item1.ManifestModule.ModuleVersionId);
            }
            Type owner = connect.GetType(typeof(ConnectLlmJson).FullName!, true)!;
            var metadata = (JsonTypeInfo)owner.GetProperty(nameof(ConnectLlmJson.Credential))!.GetValue(null)!;
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Type.Assembly));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Options.TypeInfoResolver!.GetType().Assembly));
            Assert.Equal("LlmCredentialJsonContext", metadata.Options.TypeInfoResolver.GetType().Name);
            string wire = populated ? Wire : "{\"oAuth\":{\"accessToken\":\"\"},\"secretHeaders\":{}}";
            object credential = metadata.Type.GetMethod(nameof(LlmCredential.Parse))!.Invoke(null, [wire])!;
            Assert.Equal(CredentialDiagnostic, credential.ToString());
            object oauth = metadata.Type.GetProperty(nameof(LlmCredential.OAuth))!.GetValue(credential)!;
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(oauth.GetType().Assembly));
            Assert.Equal(OAuthDiagnostic, oauth.ToString());
            Assert.Equal(populated ? "fixture-access" : "", oauth.GetType().GetProperty(nameof(LlmOAuthTokens.AccessToken))!.GetValue(oauth));
            Assert.Equal(wire, credential.GetType().GetMethod(nameof(LlmCredential.Serialize))!.Invoke(credential, null));
            Assert.Equal(wire, JsonSerializer.Serialize(credential, metadata));
            object roundTrip = JsonSerializer.Deserialize(wire, metadata)!;
            Assert.Equal(CredentialDiagnostic, roundTrip.ToString());
            Type commonOwner = common.GetType(typeof(InferenceJson).FullName!, true)!;
            var commonMetadata = (JsonTypeInfo)commonOwner.GetProperty(nameof(InferenceJson.LlmMessage))!.GetValue(null)!;
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(commonMetadata.Type.Assembly));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(commonMetadata.Options.TypeInfoResolver!.GetType().Assembly));
            object message = JsonSerializer.Deserialize("{\"Role\":\"user\",\"Content\":\"fixture\"}", commonMetadata)!;
            Assert.Contains("\"Content\":\"fixture\"", JsonSerializer.Serialize(message, commonMetadata), StringComparison.Ordinal);
            roots = [new(credential), new(oauth), new(roundTrip), new(metadata), new(commonMetadata), new(message)];
        }
        finally { generation.Unload(); }
        return (weak, roots);
    }

    private sealed class CredentialGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
