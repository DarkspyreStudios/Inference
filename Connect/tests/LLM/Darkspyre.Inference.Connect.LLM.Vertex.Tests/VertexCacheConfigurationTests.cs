using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class VertexCacheConfigurationTests
{
    [Fact]
    public void ExactStagingRootRetainsPositionalApiAndValidatedWith()
    {
        var staging = new LlmVertexCacheStaging(Bucket: "cache-bucket", ObjectPrefix: "prefix/+%/");
        var (bucket, prefix) = staging;
        Assert.Equal("cache-bucket", bucket); Assert.Equal("prefix/+%/", prefix);
        var copied = staging with { ObjectPrefix = "" };
        Assert.Equal("", copied.ObjectPrefix); Assert.Equal("prefix/+%/", staging.ObjectPrefix);
        Assert.Throws<ArgumentException>(() => staging with { Bucket = "https://bucket" });
        Assert.Throws<ArgumentException>(() => staging with { ObjectPrefix = "bad\n" });
        Assert.Throws<ArgumentException>(() => staging with { ObjectPrefix = "bad#value" });
        Assert.Throws<ArgumentException>(() => staging with { ObjectPrefix = "bad?value" });
        Assert.DoesNotContain("cache-bucket", staging.ToString(), StringComparison.Ordinal);
        string json = JsonSerializer.Serialize(staging, ConnectLlmJson.LlmVertexCacheStaging);
        Assert.Equal(staging, JsonSerializer.Deserialize(json, ConnectLlmJson.LlmVertexCacheStaging));
        var connection = Connection(staging);
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(connection, ConnectLlmJson.Connection), ConnectLlmJson.Connection)!;
        Assert.Equal(staging, restored.Vertex!.CacheStaging);
        var setup = new LlmSetupConfiguration(connection, "google/gemini-2.5-flash");
        Assert.Equal(staging, JsonSerializer.Deserialize(JsonSerializer.Serialize(setup, ConnectLlmJson.SetupConfiguration), ConnectLlmJson.SetupConfiguration)!.Connection.Vertex!.CacheStaging);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Bucket\":\"cache-bucket\"}")]
    [InlineData("{\"ObjectPrefix\":\"\"}")]
    [InlineData("{\"Bucket\":null,\"ObjectPrefix\":\"\"}")]
    [InlineData("{\"Bucket\":\"cache-bucket\",\"ObjectPrefix\":null}")]
    [InlineData("{\"Bucket\":\"https://bucket\",\"ObjectPrefix\":\"\"}")]
    public void MissingOrContradictoryStagingFactsRefuse(string json) =>
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize(json, ConnectLlmJson.LlmVertexCacheStaging));

    [Fact]
    public void OlderConnectionWithoutStagingKeepsUnknownOptionalConfiguration()
    {
        const string json = "{\"Version\":\"1\",\"Transport\":11,\"Endpoint\":\"https://aiplatform.googleapis.com\",\"Vertex\":{\"ProjectId\":\"project\",\"Location\":\"global\",\"AuthenticationMode\":0}}";
        var connection = JsonSerializer.Deserialize(json, ConnectLlmJson.Connection)!;
        Assert.Equal("project", connection.Vertex!.ProjectId);
        Assert.Equal("global", connection.Vertex.Location);
        Assert.Null(connection.Vertex.CacheStaging);
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(connection, ConnectLlmJson.Connection), ConnectLlmJson.Connection)!;
        Assert.Equal(connection.Vertex, restored.Vertex);
    }

    [Fact]
    public void SettingsRetainEditClearAndValidateExplicitStagingWithoutEffects()
    {
        var existing = Connection(new("cache-bucket", "original/"));
        var settings = new LlmSettings(VertexProviderDefinition.Value, LlmTransport.GoogleVertexAi,
            new(SetupChangeIntent.Edit, "connection", "1"), existing, "google/gemini-2.5-flash", new Environment());
        SetupTextField Field(string id) => Assert.Single(settings.Steps.SelectMany(step => step.Sections)
            .SelectMany(section => section.Fields).OfType<SetupTextField>(), field => field.Description.Id == id);
        Assert.Equal("cache-bucket", Field("google-cache-bucket").Value);
        Assert.Equal("original/", Field("google-cache-prefix").Value);
        Assert.Equal(existing.Vertex!.CacheStaging, settings.CreateChanges().Configuration.Connection.Vertex!.CacheStaging);
        Field("google-cache-prefix").SetValue("changed/+%/");
        Assert.Equal("original/", existing.Vertex.CacheStaging!.ObjectPrefix);
        Assert.Equal(new LlmVertexCacheStaging("cache-bucket", "changed/+%/"), settings.CreateChanges().Configuration.Connection.Vertex!.CacheStaging);
        Assert.True(settings.CreateChanges().RequiresRetest); Assert.True(settings.CreateChanges().RequiresReauthentication);
        Field("google-cache-bucket").SetValue("https://bad");
        Assert.False(settings.Validate().IsValid);
        Field("google-cache-bucket").SetValue(""); Field("google-cache-prefix").SetValue("");
        Assert.Null(settings.CreateChanges().Configuration.Connection.Vertex!.CacheStaging);
        Assert.True(settings.Validate().IsValid);
    }

    [Fact]
    public void ActualForeignStagingMetadataAndNestedGraphRetire()
    {
        WeakReference[] roots = ExerciseForeign();
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(roots, root => Assert.False(root.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeign()
    {
        var context = new Generation(Path.GetDirectoryName(typeof(VertexCacheConfigurationTests).Assembly.Location)!);
        try
        {
            Assembly common = context.LoadFromAssemblyPath(typeof(SetupChangeIntent).Assembly.Location);
            Assembly connect = context.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assembly vertex = context.LoadFromAssemblyPath(typeof(VertexLlmClient).Assembly.Location);
            Assembly fixture = context.LoadFromAssemblyPath(typeof(VertexCacheConfigurationTests).Assembly.Location);
            foreach (var pair in new[] { (common, typeof(SetupChangeIntent).Assembly), (connect, typeof(ConnectLlmJson).Assembly),
                (vertex, typeof(VertexLlmClient).Assembly), (fixture, typeof(VertexCacheConfigurationTests).Assembly) })
            { Assert.Equal(pair.Item2.ManifestModule.ModuleVersionId, pair.Item1.ManifestModule.ModuleVersionId); Assert.Same(context, AssemblyLoadContext.GetLoadContext(pair.Item1)); }
            Type owner = connect.GetType(typeof(ConnectLlmJson).FullName!, true)!;
            PropertyInfo getter = owner.GetProperty(nameof(ConnectLlmJson.LlmVertexCacheStaging))!;
            Assert.Same(connect, getter.GetMethod!.Module.Assembly);
            JsonTypeInfo metadata = (JsonTypeInfo)getter.GetValue(null)!;
            Assert.Same(connect, metadata.Type.Assembly);
            object resolver = metadata.Options.TypeInfoResolver!;
            Assert.Same(connect, resolver.GetType().Assembly);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(resolver.GetType().Assembly));
            object converter = metadata.Converter;
            object staging = JsonSerializer.Deserialize("{\"Bucket\":\"cache-bucket\",\"ObjectPrefix\":\"prefix/\"}", metadata)!;
            Assert.Same(connect, staging.GetType().Assembly);
            Assert.Equal("{\"Bucket\":\"cache-bucket\",\"ObjectPrefix\":\"prefix/\"}", JsonSerializer.Serialize(staging, metadata));
            JsonTypeInfo setupMetadata = (JsonTypeInfo)owner.GetProperty(nameof(ConnectLlmJson.SetupConfiguration))!.GetValue(null)!;
            var setup = new LlmSetupConfiguration(Connection(new("cache-bucket", "prefix/")), "google/gemini-2.5-flash");
            object restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(setup, ConnectLlmJson.SetupConfiguration), setupMetadata)!;
            object connection = restored.GetType().GetProperty("Connection")!.GetValue(restored)!;
            object config = connection.GetType().GetProperty("Vertex")!.GetValue(connection)!;
            object nested = config.GetType().GetProperty("CacheStaging")!.GetValue(config)!;
            Assert.Same(connect, nested.GetType().Assembly);
            Assert.Equal("cache-bucket", nested.GetType().GetProperty("Bucket")!.GetValue(nested));
            return [new(context), new(common), new(connect), new(vertex), new(fixture), new(owner), new(getter), new(metadata),
                new(metadata.Type), new(staging), new(setupMetadata), new(restored), new(nested), new(resolver), new(converter)];
        }
        finally { context.Unload(); }
    }

    private static LlmConnection Connection(LlmVertexCacheStaging staging) => new()
    {
        Transport = LlmTransport.GoogleVertexAi,
        Version = "1",
        Endpoint = "https://aiplatform.googleapis.com",
        Vertex = new() { ProjectId = "project-id", Location = "global", CacheStaging = staging }
    };
    private sealed class Environment : ILlmSetupEnvironment
    {
        public LlmTransport Transport => LlmTransport.GoogleVertexAi;
        public Task<IReadOnlyList<SetupChoice>> GetProfilesAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<IReadOnlyList<SetupChoice>> GetRegionsAsync(string? profile, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public SetupValidation Validate(LlmConnection connection) => new([]);
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
