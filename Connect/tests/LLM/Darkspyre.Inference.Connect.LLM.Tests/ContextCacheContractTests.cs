using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ContextCacheContractTests
{
    private const string ReferenceJson = "{\"ResourceName\":\"cachedContents/1\",\"ModelId\":\"model\",\"ScopeId\":\"scope\"}";
    private const string ReceiptJson = "{\"Reference\":" + ReferenceJson + ",\"ExpiresAt\":\"2030-01-01T00:00:00.0000001+00:00\"}";

    public static TheoryData<bool, string, string> Shapes => new()
    {
        { false, "LlmExplicitCacheReference", ReferenceJson },
        { true, "LlmContextCacheCreate", "{\"ModelId\":\"m\",\"Correlation\":{\"OperationId\":\"op\",\"ReconciliationDisplayName\":\"same\"},\"TimeToLive\":\"00:00:01.0000001\",\"Resources\":null,\"Tools\":[{\"type\":\"shell\",\"Skills\":[]}],\"Messages\":[{\"Role\":\"user\",\"Content\":\"prefix\"}]}" },
        { true, "LlmContextCacheCreateCorrelation", "{\"OperationId\":\"op\",\"ReconciliationDisplayName\":\" caller supplied \"}" },
        { true, "LlmContextCache", ReceiptJson },
        { true, "LlmContextCacheUsage", "{\"TotalTokens\":null,\"TextCharacters\":0,\"ImageCount\":0,\"AudioDuration\":\"00:00:00\",\"VideoDuration\":null}" },
        { true, "LlmContextCacheList", "{\"PageSize\":2147483647,\"PageToken\":\"opaque+/=\"}" },
        { true, "LlmContextCachePage", "{\"Items\":[" + ReceiptJson + "],\"NextPageToken\":\"continue\"}" },
        { true, "LlmContextCachePage", "{\"Items\":[],\"NextPageToken\":\"still-continue\"}" },
        { true, "LlmContextCacheDeleteResult", "{\"AlreadyAbsent\":false}" }
    };

    [Theory, MemberData(nameof(Shapes))]
    public void EveryOwnedRootPreservesSuppliedFacts(bool connect, string name, string json)
        => RoundTrip(json, Metadata(connect ? typeof(ConnectLlmJson) : typeof(InferenceJson), name));

    [Theory]
    [InlineData(false, "LlmExplicitCacheReference", "{}")]
    [InlineData(false, "LlmExplicitCacheReference", "{\"ModelId\":\"m\",\"ScopeId\":\"s\"}")]
    [InlineData(false, "LlmExplicitCacheReference", "{\"ResourceName\":\"n\",\"ScopeId\":\"s\"}")]
    [InlineData(false, "LlmExplicitCacheReference", "{\"ResourceName\":\"n\",\"ModelId\":\"m\"}")]
    [InlineData(false, "LlmExplicitCacheReference", "{\"ResourceName\":null,\"ModelId\":\"m\",\"ScopeId\":\"s\"}")]
    [InlineData(false, "LlmExplicitCacheReference", "{\"ResourceName\":\"\",\"ModelId\":\"m\",\"ScopeId\":\"s\"}")]
    [InlineData(true, "LlmContextCacheCreate", "{}")]
    [InlineData(true, "LlmContextCacheCreate", "{\"ModelId\":\"m\",\"Correlation\":null}")]
    [InlineData(true, "LlmContextCacheCreateCorrelation", "{\"OperationId\":\"op\"}")]
    [InlineData(true, "LlmContextCacheCreateCorrelation", "{\"ReconciliationDisplayName\":\"name\"}")]
    [InlineData(true, "LlmContextCacheCreateCorrelation", "{\"OperationId\":\"\",\"ReconciliationDisplayName\":\"name\"}")]
    [InlineData(true, "LlmContextCacheCreateCorrelation", "{\"OperationId\":\"op\",\"ReconciliationDisplayName\":null}")]
    [InlineData(true, "LlmContextCache", "{\"Reference\":" + ReferenceJson + "}")]
    [InlineData(true, "LlmContextCache", "{\"ExpiresAt\":\"2030-01-01T00:00:00Z\"}")]
    [InlineData(true, "LlmContextCache", "{\"Reference\":null,\"ExpiresAt\":\"2030-01-01T00:00:00Z\"}")]
    [InlineData(true, "LlmContextCacheList", "{}")]
    [InlineData(true, "LlmContextCacheList", "{\"PageSize\":0}")]
    [InlineData(true, "LlmContextCacheList", "{\"PageSize\":-1}")]
    [InlineData(true, "LlmContextCachePage", "{}")]
    [InlineData(true, "LlmContextCachePage", "{\"Items\":null}")]
    [InlineData(true, "LlmContextCachePage", "{\"Items\":[null]}")]
    [InlineData(true, "LlmContextCacheDeleteResult", "{}")]
    public void MissingOrInvalidRequiredFactsRefuse(bool connect, string name, string json)
        => Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(json,
            Metadata(connect ? typeof(ConnectLlmJson) : typeof(InferenceJson), name))));

    [Fact]
    public void PositionalShapesAndWithAssignmentsPreserveIdentityAndGuardRequiredFacts()
    {
        var reference = new LlmExplicitCacheReference(ResourceName: "native", ModelId: "m", ScopeId: "s");
        (string name, string model, string scope, DateTimeOffset? expiry) = reference;
        Assert.Equal(("native", "m", "s", (DateTimeOffset?)null), (name, model, scope, expiry));
        Assert.Throws<ArgumentNullException>(() => reference with { ResourceName = null! });
        Assert.Throws<ArgumentException>(() => reference with { ScopeId = "" });
        Assert.Throws<ArgumentException>(() => reference with { ModelId = " " });
        var correlation = new LlmContextCacheCreateCorrelation("op", "display");
        Assert.Throws<ArgumentNullException>(() => correlation with { ReconciliationDisplayName = null! });
        Assert.Throws<ArgumentException>(() => correlation with { OperationId = "" });
        var receipt = new LlmContextCache(reference, null, null, null, DateTimeOffset.UnixEpoch, null);
        Assert.Throws<ArgumentNullException>(() => receipt with { Reference = null! });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmContextCacheList(1) with { PageSize = 0 });
        List<LlmContextCache> items = [receipt];
        var page = new LlmContextCachePage(items, "next");
        items.Clear(); Assert.Same(receipt, Assert.Single(page.Items));
        var replaced = page with { Items = items }; items.Add(receipt); Assert.Empty(replaced.Items);
        Assert.Throws<ArgumentNullException>(() => page with { Items = null! });
        Assert.Equal("next", replaced.NextPageToken);
    }

    [Fact]
    public void CreateCopiesListsAndKeepsLifetimeAndOptionalFactsUnchanged()
    {
        var messages = new List<LlmChatMessage> { new("user", "prefix") };
        var tools = new List<LlmClientToolDefinition> { new LlmShellTool([]) };
        var request = new LlmContextCacheCreate
        {
            ModelId = "m",
            Correlation = new("op", "display"),
            Messages = messages,
            Tools = tools,
            TimeToLive = TimeSpan.FromTicks(1)
        };
        messages.Clear(); tools.Clear(); Assert.Single(request.Messages); Assert.Single(request.Tools);
        Assert.Equal(TimeSpan.FromTicks(1), request.TimeToLive); Assert.Null(request.ExpiresAt);
        Assert.Null(request.DisplayName); Assert.Null(request.Resources); Assert.Null(request.ToolChoice);
        var changed = request with { Messages = messages, Tools = tools };
        messages.Add(new("user", "late")); tools.Add(new LlmShellTool([]));
        Assert.Empty(changed.Messages); Assert.Empty(changed.Tools);
        Assert.Throws<ArgumentNullException>(() => request with { Correlation = null! });
        Assert.Throws<ArgumentNullException>(() => request with { Tools = null! });
        Assert.Throws<ArgumentException>(() => request with { ModelId = "" });
    }

    [Fact]
    public void RuntimeResourcesRefuseSerializationWithoutInvokingAuthority()
    {
        var authority = new UncallableResources();
        var request = new LlmContextCacheCreate { ModelId = "m", Correlation = new("op", "name"), Resources = authority };
        Assert.Equal("LlmContextCacheCreate { <request> }", request.ToString());
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(request, ConnectLlmJson.LlmContextCacheCreate));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize(
            "{\"ModelId\":\"m\",\"Correlation\":{\"OperationId\":\"op\",\"ReconciliationDisplayName\":\"n\"},\"Resources\":{}}",
            ConnectLlmJson.LlmContextCacheCreate));
        Assert.Same(authority, (request with { DisplayName = "name" }).Resources);
        Assert.Equal(0, authority.Calls);
    }

    [Fact]
    public void UncertainExceptionsPreserveEvidenceAndCancellationWithoutClaimingSuccess()
    {
        var correlation = new LlmContextCacheCreateCorrelation("op", "display");
        var receipt = new LlmContextCache(new("resource", "model", "scope"), null, null, null, DateTimeOffset.UnixEpoch, null);
        using var source = new CancellationTokenSource(); source.Cancel();
        var inner = new InvalidOperationException("sanitized");
        var cancelled = new LlmContextCacheCreateCancelledException(correlation, source.Token, inner, "request", receipt);
        Assert.IsAssignableFrom<OperationCanceledException>(cancelled);
        Assert.Equal(source.Token, cancelled.CancellationToken); Assert.Same(inner, cancelled.InnerException);
        Assert.Same(correlation, cancelled.Correlation); Assert.Same(receipt, cancelled.ObservedReceipt);
        Assert.Equal("request", cancelled.RequestId);
        var uncertain = new LlmContextCacheCreateUncertainException(correlation, inner);
        Assert.Same(inner, uncertain.InnerException); Assert.Same(correlation, uncertain.Correlation);
        Assert.Null(uncertain.RequestId); Assert.Null(uncertain.ObservedReceipt);
        Assert.Equal("Cache creation may have committed; reconcile explicitly.", uncertain.Message);
        Assert.Equal(uncertain.Message, cancelled.Message);
        Assert.Null(typeof(ConnectLlmJson).GetProperty(nameof(LlmContextCacheCreateUncertainException)));
        Assert.Null(typeof(ConnectLlmJson).GetProperty(nameof(ILlmContextCacheClient)));
    }

    [Theory, MemberData(nameof(Shapes))]
    public void ActualForeignCommonAndConnectMetadataCollect(bool connect, string name, string json)
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
                Assert.NotSame(original, actual); Assert.Equal(original.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
            }
            Assembly owner = useConnect ? connect : common;
            Type facade = owner.GetType(useConnect ? typeof(ConnectLlmJson).FullName! : typeof(InferenceJson).FullName!, true)!;
            JsonTypeInfo metadata = Metadata(facade, name);
            Assert.Same(owner, metadata.Type.Assembly);
            Assert.Same(owner, RoundTrip(json, metadata).GetType().Assembly);
            JsonTypeInfo create = Metadata(connect.GetType(typeof(ConnectLlmJson).FullName!, true)!, "LlmContextCacheCreate");
            Type authority = common.GetType(typeof(IInferenceResourceAccess).FullName!, true)!;
            Assert.Same(common, create.Options.GetConverter(authority).GetType().Assembly);
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize(
                "{\"ModelId\":\"m\",\"Correlation\":{\"OperationId\":\"o\",\"ReconciliationDisplayName\":\"n\"},\"Resources\":{}}", create));
            RoundTrip(ReferenceJson, Metadata(common.GetType(typeof(InferenceJson).FullName!, true)!, "LlmExplicitCacheReference"));
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
            foreach (JsonProperty field in supplied.EnumerateObject())
            { Assert.True(actual.TryGetProperty(field.Name, out JsonElement stored), field.Name); PreserveFields(field.Value, stored); }
        else if (supplied.ValueKind == JsonValueKind.Array)
        {
            Assert.Equal(supplied.GetArrayLength(), actual.GetArrayLength());
            for (int i = 0; i < supplied.GetArrayLength(); i++) PreserveFields(supplied[i], actual[i]);
        }
        else Assert.True(JsonElement.DeepEquals(supplied, actual), supplied.GetRawText());
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

    private sealed class UncallableResources : IInferenceResourceAccess
    {
        public int Calls { get; private set; }
        private InvalidOperationException Called() { Calls++; return new InvalidOperationException("Authority was invoked."); }
        public override string ToString() => throw Called();
        public string WorkspaceId => throw Called();
        public string SessionId => throw Called();
        public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw Called();
        public Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw Called();
        public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default) => throw Called();
        public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) => throw Called();
        public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => throw Called();
        public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => throw Called();
    }
}
